using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingGrid = Tekla.Structures.Drawing.Grid;
using DrawingPart = Tekla.Structures.Drawing.Part;
using DrawingView = Tekla.Structures.Drawing.View;
using ModelBoltGroup = Tekla.Structures.Model.BoltGroup;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Gives each fabrication feature one owning face view.
    /// Basic member-face views are created with Tekla's native assembly drawing view
    /// methods so that they inherit the assembly drawing context instead of behaving
    /// like ad-hoc GA/model views.
    /// </summary>
    public sealed class FaceViewPlanner
    {
        private const double FaceParallelTolerance = 0.90;
        private const double ViewGap = 18.0;

        private readonly Model _model;

        public FaceViewPlanner(Model model)
        {
            _model = model;
        }

        public int EnsureRequiredViews(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null)
                throw new ArgumentNullException(nameof(analysis));

            var source = GetSourceView(analysis);
            if (source == null)
            {
                messages?.Add("No usable source view containing the main part was found.");
                return 0;
            }

            var features = GetFeatures(analysis.MainPart);
            var knownViews = GetKnownViews(analysis.Views);
            var allowedPartIds = new HashSet<int>(GetAssemblyParts(analysis.MainPart).Select(part => part.Identifier.ID));
            var created = 0;

            foreach (var faceGroup in features
                .Where(feature => feature.Face != FabricationFace.Custom)
                .GroupBy(feature => feature.Face))
            {
                var representative = faceGroup.First();
                if (FindBestView(knownViews, representative.RequiredNormal) != null)
                    continue;

                var view = CreateFaceView(
                    analysis,
                    source,
                    faceGroup.Key,
                    allowedPartIds,
                    knownViews,
                    messages);

                if (view == null)
                    continue;

                knownViews.Add(new KnownView(view, GetViewNormal(view)));
                created++;
            }

            foreach (var custom in features.Where(feature => feature.Face == FabricationFace.Custom))
            {
                if (FindBestView(knownViews, custom.RequiredNormal) == null)
                {
                    messages?.Add(
                        custom.Description +
                        " has a skew/custom dominant face. Leaving it for manual review instead of creating an unreliable view.");
                }
            }

            if (created > 0)
                analysis.Drawing.CommitChanges();

            return created;
        }

        public FaceViewOwnership BuildOwnership(DrawingAnalysisResult analysis)
        {
            if (analysis == null)
                throw new ArgumentNullException(nameof(analysis));

            var result = new FaceViewOwnership();
            var knownViews = GetKnownViews(analysis.Views);
            var features = GetFeatures(analysis.MainPart);

            foreach (var feature in features)
            {
                var owner = FindBestView(knownViews, feature.RequiredNormal);
                if (owner == null)
                {
                    result.Messages.Add("No suitable face view found for " + feature.Description + ".");
                    continue;
                }

                if (feature.Kind == FeatureKind.HoleGroup)
                    AddOwnership(result.HoleGroupsByView, owner.View, feature.IdentifierId);
                else if (feature.Kind == FeatureKind.SecondaryPart)
                    AddOwnership(result.PartsByView, owner.View, feature.IdentifierId);

                result.Messages.Add(feature.Description + " -> " + GetFriendlyViewName(owner.View));
            }

            return result;
        }

        private DrawingView CreateFaceView(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            FabricationFace face,
            HashSet<int> allowedPartIds,
            IList<KnownView> knownViews,
            IList<string> messages)
        {
            var insertionPoint = GetInitialInsertionPoint(source.View, face);
            var attributes = CreateGeneratedViewAttributes(source.View);
            DrawingView view = null;
            SectionMark sectionMark = null;
            var created = false;

            switch (face)
            {
                case FabricationFace.TopFlange:
                    created = DrawingView.CreateTopView(
                        analysis.Drawing,
                        insertionPoint,
                        attributes,
                        out view);
                    break;

                case FabricationFace.BottomFlange:
                    created = DrawingView.CreateBottomView(
                        analysis.Drawing,
                        insertionPoint,
                        attributes,
                        out view);
                    break;

                case FabricationFace.WebSideA:
                    created = DrawingView.CreateFrontView(
                        analysis.Drawing,
                        insertionPoint,
                        attributes,
                        out view);
                    break;

                case FabricationFace.WebSideB:
                    created = DrawingView.CreateBackView(
                        analysis.Drawing,
                        insertionPoint,
                        attributes,
                        out view);
                    break;

                case FabricationFace.StartEnd:
                case FabricationFace.FinishEnd:
                    created = CreateEndSectionView(
                        source,
                        face,
                        insertionPoint,
                        attributes,
                        out view,
                        out sectionMark);
                    break;
            }

            if (!created || view == null)
            {
                messages?.Add("Could not create " + GetViewName(face) + " view.");
                return null;
            }

            view.Name = GetViewName(face);
            view.Modify();
            analysis.Drawing.CommitChanges();

            CleanGeneratedView(view, allowedPartIds);
            analysis.Drawing.CommitChanges();

            if (!ContainsModelObject(view, analysis.MainPartIdentifier))
            {
                if (sectionMark != null)
                    sectionMark.Delete();

                view.Delete();
                analysis.Drawing.CommitChanges();
                messages?.Add(
                    "Rejected " + GetViewName(face) +
                    " because Tekla created it without the assembly main part.");
                return null;
            }

            PlaceGeneratedView(analysis.Drawing, source.View, view, face, knownViews);
            view.Modify();
            analysis.Drawing.CommitChanges();

            messages?.Add("Created " + GetViewName(face) + " view using Tekla assembly drawing view creation.");
            return view;
        }

        private static bool CreateEndSectionView(
            ViewAnalysis source,
            FabricationFace face,
            Point insertionPoint,
            DrawingView.ViewAttributes attributes,
            out DrawingView view,
            out SectionMark sectionMark)
        {
            view = null;
            sectionMark = null;

            if (source == null || source.View == null || source.MainPartBounds == null)
                return false;

            var bounds = source.MainPartBounds;
            var markAttributes = new SectionMarkBase.SectionMarkAttributes
            {
                MarkName = face == FabricationFace.StartEnd ? "END 1" : "END 2"
            };

            Point start;
            Point end;

            if (Math.Abs(bounds.Width) >= Math.Abs(bounds.Height))
            {
                var x = face == FabricationFace.StartEnd ? bounds.MinX : bounds.MaxX;
                var margin = Math.Max(25.0, Math.Abs(bounds.Height) * 0.50);
                start = new Point(x, bounds.MinY - margin, 0.0);
                end = new Point(x, bounds.MaxY + margin, 0.0);
            }
            else
            {
                var y = face == FabricationFace.StartEnd ? bounds.MinY : bounds.MaxY;
                var margin = Math.Max(25.0, Math.Abs(bounds.Width) * 0.50);
                start = new Point(bounds.MinX - margin, y, 0.0);
                end = new Point(bounds.MaxX + margin, y, 0.0);
            }

            return DrawingView.CreateSectionView(
                source.View,
                start,
                end,
                insertionPoint,
                500.0,
                500.0,
                attributes,
                markAttributes,
                out view,
                out sectionMark);
        }

        private static DrawingView.ViewAttributes CreateGeneratedViewAttributes(DrawingView source)
        {
            var attributes = new DrawingView.ViewAttributes();

            if (source != null && source.Attributes != null)
            {
                attributes.Scale = source.Attributes.Scale;
                attributes.Shortening = source.Attributes.Shortening;
                attributes.ReflectedView = source.Attributes.ReflectedView;
                attributes.UndeformedView = source.Attributes.UndeformedView;
            }

            attributes.FixedViewPlacing = true;
            attributes.ViewExtensionForNeighbourParts = 0.0;
            return attributes;
        }

        private static void CleanGeneratedView(DrawingView view, ISet<int> allowedPartIds)
        {
            if (view == null)
                return;

            var grids = view.GetObjects(new[] { typeof(DrawingGrid) });
            var gridsToDelete = new List<DrawingGrid>();
            while (grids.MoveNext())
            {
                var grid = grids.Current as DrawingGrid;
                if (grid != null)
                    gridsToDelete.Add(grid);
            }

            foreach (var grid in gridsToDelete)
                grid.Delete();

            if (allowedPartIds == null || allowedPartIds.Count == 0)
                return;

            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            var partsToDelete = new List<DrawingPart>();
            while (parts.MoveNext())
            {
                var part = parts.Current as DrawingPart;
                if (part == null || part.ModelIdentifier == null)
                    continue;

                if (!allowedPartIds.Contains(part.ModelIdentifier.ID))
                    partsToDelete.Add(part);
            }

            foreach (var part in partsToDelete)
                part.Delete();
        }

        private static void PlaceGeneratedView(
            Drawing drawing,
            DrawingView source,
            DrawingView view,
            FabricationFace face,
            IEnumerable<KnownView> knownViews)
        {
            if (drawing == null || source == null || view == null)
                return;

            var candidates = GetPlacementCandidates(source, view, face);
            var sheet = drawing.GetSheet();
            var occupied = knownViews == null
                ? new List<DrawingView>()
                : knownViews.Select(item => item.View).Where(item => item != null).ToList();

            foreach (var candidate in candidates)
            {
                if (!FitsOnSheet(sheet, view, candidate))
                    continue;

                if (occupied.All(existing => !Overlaps(existing, view, candidate, ViewGap)))
                {
                    view.Origin = candidate;
                    return;
                }
            }

            view.Origin = ClampToSheet(sheet, view, candidates.First());
        }

        private static List<Point> GetPlacementCandidates(
            DrawingView source,
            DrawingView view,
            FabricationFace face)
        {
            var horizontal = source.Width * 0.5 + view.Width * 0.5 + ViewGap;
            var vertical = source.Height * 0.5 + view.Height * 0.5 + ViewGap;
            var centre = source.Origin;

            var above = new Point(centre.X, centre.Y + vertical, 0.0);
            var below = new Point(centre.X, centre.Y - vertical, 0.0);
            var left = new Point(centre.X - horizontal, centre.Y, 0.0);
            var right = new Point(centre.X + horizontal, centre.Y, 0.0);
            var aboveRight = new Point(centre.X + horizontal, centre.Y + vertical, 0.0);
            var belowRight = new Point(centre.X + horizontal, centre.Y - vertical, 0.0);
            var aboveLeft = new Point(centre.X - horizontal, centre.Y + vertical, 0.0);
            var belowLeft = new Point(centre.X - horizontal, centre.Y - vertical, 0.0);

            switch (face)
            {
                case FabricationFace.TopFlange:
                    return new List<Point> { above, aboveRight, aboveLeft, right, left, below };

                case FabricationFace.BottomFlange:
                    return new List<Point> { below, belowRight, belowLeft, right, left, above };

                case FabricationFace.StartEnd:
                    return new List<Point> { left, aboveLeft, belowLeft, below, above, right };

                case FabricationFace.FinishEnd:
                    return new List<Point> { right, aboveRight, belowRight, below, above, left };

                case FabricationFace.WebSideB:
                    return new List<Point> { below, belowRight, belowLeft, right, left, above };

                default:
                    return new List<Point> { above, below, right, left, aboveRight, belowRight };
            }
        }

        private static Point GetInitialInsertionPoint(DrawingView source, FabricationFace face)
        {
            if (source == null)
                return new Point();

            const double initialGap = 35.0;
            var origin = source.Origin;

            switch (face)
            {
                case FabricationFace.TopFlange:
                    return new Point(origin.X, origin.Y + source.Height + initialGap, 0.0);
                case FabricationFace.BottomFlange:
                    return new Point(origin.X, origin.Y - source.Height - initialGap, 0.0);
                case FabricationFace.StartEnd:
                    return new Point(origin.X - source.Width * 0.5 - initialGap, origin.Y, 0.0);
                case FabricationFace.FinishEnd:
                    return new Point(origin.X + source.Width * 0.5 + initialGap, origin.Y, 0.0);
                case FabricationFace.WebSideB:
                    return new Point(origin.X, origin.Y - source.Height - initialGap, 0.0);
                default:
                    return new Point(origin.X, origin.Y + source.Height + initialGap, 0.0);
            }
        }

        private static bool FitsOnSheet(ContainerView sheet, DrawingView view, Point origin)
        {
            if (sheet == null || view == null || origin == null || sheet.Width <= 0.0 || sheet.Height <= 0.0)
                return true;

            var halfWidth = view.Width * 0.5 + ViewGap;
            var halfHeight = view.Height * 0.5 + ViewGap;

            return origin.X - halfWidth >= 0.0 &&
                   origin.Y - halfHeight >= 0.0 &&
                   origin.X + halfWidth <= sheet.Width &&
                   origin.Y + halfHeight <= sheet.Height;
        }

        private static Point ClampToSheet(ContainerView sheet, DrawingView view, Point desired)
        {
            if (sheet == null || view == null || desired == null || sheet.Width <= 0.0 || sheet.Height <= 0.0)
                return desired ?? new Point();

            var halfWidth = view.Width * 0.5 + ViewGap;
            var halfHeight = view.Height * 0.5 + ViewGap;

            var minX = Math.Min(halfWidth, sheet.Width * 0.5);
            var maxX = Math.Max(minX, sheet.Width - halfWidth);
            var minY = Math.Min(halfHeight, sheet.Height * 0.5);
            var maxY = Math.Max(minY, sheet.Height - halfHeight);

            return new Point(
                Math.Max(minX, Math.Min(maxX, desired.X)),
                Math.Max(minY, Math.Min(maxY, desired.Y)),
                0.0);
        }

        private static bool Overlaps(
            DrawingView existing,
            DrawingView candidate,
            Point candidateOrigin,
            double gap)
        {
            if (existing == null || candidate == null || candidateOrigin == null)
                return false;

            var existingLeft = existing.Origin.X - existing.Width * 0.5 - gap;
            var existingRight = existing.Origin.X + existing.Width * 0.5 + gap;
            var existingBottom = existing.Origin.Y - existing.Height * 0.5 - gap;
            var existingTop = existing.Origin.Y + existing.Height * 0.5 + gap;

            var candidateLeft = candidateOrigin.X - candidate.Width * 0.5;
            var candidateRight = candidateOrigin.X + candidate.Width * 0.5;
            var candidateBottom = candidateOrigin.Y - candidate.Height * 0.5;
            var candidateTop = candidateOrigin.Y + candidate.Height * 0.5;

            return candidateLeft < existingRight &&
                   candidateRight > existingLeft &&
                   candidateBottom < existingTop &&
                   candidateTop > existingBottom;
        }

        private static bool ContainsModelObject(DrawingView view, Identifier identifier)
        {
            if (view == null || identifier == null)
                return false;

            var objects = view.GetModelObjects(identifier);
            return objects != null && objects.MoveNext();
        }

        private static ViewAnalysis GetSourceView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(view => view.View != null && view.ContainsMainPart && view.MainPartBounds != null)
                .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                .FirstOrDefault();
        }

        private List<FeatureSpec> GetFeatures(ModelPart mainPart)
        {
            var result = new List<FeatureSpec>();
            var originalPlane = _model.GetWorkPlaneHandler().GetCurrentTransformationPlane();

            try
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane());

                var mainCs = mainPart.GetCoordinateSystem();
                var mainX = Normalize(new Vector(mainCs.AxisX));
                var mainY = Normalize(new Vector(mainCs.AxisY));
                var mainZ = Normalize(GeometryMath.Cross(mainX, mainY));
                var mainCentre = GetSolidCentre(mainPart);

                var parts = GetAssemblyParts(mainPart);
                foreach (var part in parts)
                {
                    if (part.Identifier.ID == mainPart.Identifier.ID)
                        continue;

                    var centre = GetSolidCentre(part);
                    var dominantNormal = GetDominantFaceNormal(part);
                    var requiredNormal = GetRequiredNormal(dominantNormal, centre, mainCentre, mainX, mainY, mainZ);

                    result.Add(new FeatureSpec
                    {
                        IdentifierId = part.Identifier.ID,
                        Kind = FeatureKind.SecondaryPart,
                        Description = "part " + GetPartDescription(part),
                        RequiredNormal = requiredNormal,
                        Face = ClassifyFace(requiredNormal, mainX, mainY, mainZ)
                    });
                }

                var seenBoltGroups = new HashSet<int>();
                foreach (var part in parts)
                {
                    var bolts = part.GetBolts();
                    while (bolts.MoveNext())
                    {
                        var boltGroup = bolts.Current as ModelBoltGroup;
                        if (boltGroup == null || !seenBoltGroups.Add(boltGroup.Identifier.ID))
                            continue;

                        var boltCs = boltGroup.GetCoordinateSystem();
                        var boltNormal = Normalize(GeometryMath.Cross(
                            new Vector(boltCs.AxisX),
                            new Vector(boltCs.AxisY)));
                        var centre = GetBoltGroupCentre(boltGroup);
                        var requiredNormal = GetRequiredNormal(boltNormal, centre, mainCentre, mainX, mainY, mainZ);

                        result.Add(new FeatureSpec
                        {
                            IdentifierId = boltGroup.Identifier.ID,
                            Kind = FeatureKind.HoleGroup,
                            Description = "hole group " + boltGroup.Identifier.ID,
                            RequiredNormal = requiredNormal,
                            Face = ClassifyFace(requiredNormal, mainX, mainY, mainZ)
                        });
                    }
                }
            }
            finally
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(originalPlane);
            }

            return result;
        }

        private Vector GetDominantFaceNormal(ModelPart part)
        {
            var partCs = part.GetCoordinateSystem();
            var axisX = Normalize(new Vector(partCs.AxisX));
            var axisY = Normalize(new Vector(partCs.AxisY));
            var axisZ = Normalize(GeometryMath.Cross(axisX, axisY));

            if (part is ContourPlate)
                return axisZ;

            var originalPlane = _model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
            try
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane(partCs));
                var solid = part.GetSolid();
                var dx = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
                var dy = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                var dz = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);

                if (dx <= dy && dx <= dz)
                    return axisX;

                if (dy <= dx && dy <= dz)
                    return axisY;

                return axisZ;
            }
            finally
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(originalPlane);
            }
        }

        private static Vector GetRequiredNormal(
            Vector featureNormal,
            Point featureCentre,
            Point mainCentre,
            Vector mainX,
            Vector mainY,
            Vector mainZ)
        {
            var normal = Normalize(featureNormal);
            var xScore = GeometryMath.AbsoluteDot(normal, mainX);
            var yScore = GeometryMath.AbsoluteDot(normal, mainY);
            var zScore = GeometryMath.AbsoluteDot(normal, mainZ);
            var fromMain = new Vector(
                featureCentre.X - mainCentre.X,
                featureCentre.Y - mainCentre.Y,
                featureCentre.Z - mainCentre.Z);

            if (xScore >= FaceParallelTolerance && xScore >= yScore && xScore >= zScore)
                return Dot(fromMain, mainX) >= 0.0 ? mainX : Multiply(mainX, -1.0);

            if (yScore >= FaceParallelTolerance && yScore >= xScore && yScore >= zScore)
                return Dot(fromMain, mainY) >= 0.0 ? mainY : Multiply(mainY, -1.0);

            if (zScore >= FaceParallelTolerance && zScore >= xScore && zScore >= yScore)
                return Dot(fromMain, mainZ) >= 0.0 ? mainZ : Multiply(mainZ, -1.0);

            return Dot(fromMain, normal) >= 0.0 ? normal : Multiply(normal, -1.0);
        }

        private static FabricationFace ClassifyFace(Vector normal, Vector mainX, Vector mainY, Vector mainZ)
        {
            var x = Dot(normal, mainX);
            var y = Dot(normal, mainY);
            var z = Dot(normal, mainZ);

            if (Math.Abs(x) >= FaceParallelTolerance)
                return x >= 0.0 ? FabricationFace.FinishEnd : FabricationFace.StartEnd;

            if (Math.Abs(y) >= FaceParallelTolerance)
                return y >= 0.0 ? FabricationFace.WebSideA : FabricationFace.WebSideB;

            if (Math.Abs(z) >= FaceParallelTolerance)
                return z >= 0.0 ? FabricationFace.TopFlange : FabricationFace.BottomFlange;

            return FabricationFace.Custom;
        }

        private static KnownView FindBestView(IEnumerable<KnownView> views, Vector requiredNormal)
        {
            KnownView best = null;
            var bestScore = FaceParallelTolerance;

            foreach (var view in views)
            {
                var score = Dot(Normalize(view.Normal), Normalize(requiredNormal));
                if (score < bestScore)
                    continue;

                best = view;
                bestScore = score;
            }

            return best;
        }

        private static List<KnownView> GetKnownViews(IEnumerable<ViewAnalysis> views)
        {
            return views
                .Where(view => view.View != null)
                .Select(view => new KnownView(view.View, GetViewNormal(view.View)))
                .ToList();
        }

        private static Vector GetViewNormal(DrawingView view)
        {
            var cs = view.DisplayCoordinateSystem;
            return Normalize(GeometryMath.Cross(new Vector(cs.AxisX), new Vector(cs.AxisY)));
        }

        private static List<ModelPart> GetAssemblyParts(ModelPart mainPart)
        {
            var parts = new List<ModelPart> { mainPart };
            var assembly = mainPart.GetAssembly();
            if (assembly == null)
                return parts;

            var secondaries = assembly.GetSecondaries();
            foreach (var item in secondaries)
            {
                var part = item as ModelPart;
                if (part != null)
                    parts.Add(part);
            }

            return parts;
        }

        private static Point GetSolidCentre(ModelPart part)
        {
            var solid = part.GetSolid();
            return new Point(
                (solid.MinimumPoint.X + solid.MaximumPoint.X) * 0.5,
                (solid.MinimumPoint.Y + solid.MaximumPoint.Y) * 0.5,
                (solid.MinimumPoint.Z + solid.MaximumPoint.Z) * 0.5);
        }

        private static Point GetBoltGroupCentre(ModelBoltGroup boltGroup)
        {
            var points = new List<Point>();
            foreach (var item in boltGroup.BoltPositions)
            {
                var point = item as Point;
                if (point != null)
                    points.Add(point);
            }

            if (points.Count == 0)
                return new Point(boltGroup.GetCoordinateSystem().Origin);

            return new Point(
                points.Average(point => point.X),
                points.Average(point => point.Y),
                points.Average(point => point.Z));
        }

        private static string GetPartDescription(ModelPart part)
        {
            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            return string.IsNullOrWhiteSpace(profile)
                ? part.Identifier.ID.ToString()
                : part.Identifier.ID + " (" + profile + ")";
        }

        private static string GetViewName(FabricationFace face)
        {
            switch (face)
            {
                case FabricationFace.TopFlange:
                    return "TOP";
                case FabricationFace.BottomFlange:
                    return "BOTTOM";
                case FabricationFace.WebSideA:
                    return "SIDE A";
                case FabricationFace.WebSideB:
                    return "SIDE B";
                case FabricationFace.StartEnd:
                    return "END 1";
                case FabricationFace.FinishEnd:
                    return "END 2";
                default:
                    return "DETAIL";
            }
        }

        private static string GetFriendlyViewName(DrawingView view)
        {
            return view == null || string.IsNullOrWhiteSpace(view.Name)
                ? "ORIGINAL"
                : view.Name;
        }

        private static void AddOwnership(
            Dictionary<DrawingView, HashSet<int>> map,
            DrawingView view,
            int objectId)
        {
            HashSet<int> ids;
            if (!map.TryGetValue(view, out ids))
            {
                ids = new HashSet<int>();
                map.Add(view, ids);
            }

            ids.Add(objectId);
        }

        private static Vector Normalize(Vector vector)
        {
            var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
            if (length < 0.000001)
                return new Vector();

            return new Vector(vector.X / length, vector.Y / length, vector.Z / length);
        }

        private static double Dot(Vector a, Vector b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Vector Multiply(Vector vector, double factor)
        {
            return new Vector(vector.X * factor, vector.Y * factor, vector.Z * factor);
        }

        private sealed class KnownView
        {
            public KnownView(DrawingView view, Vector normal)
            {
                View = view;
                Normal = normal;
            }

            public DrawingView View { get; }
            public Vector Normal { get; }
        }

        private sealed class FeatureSpec
        {
            public int IdentifierId { get; set; }
            public FeatureKind Kind { get; set; }
            public string Description { get; set; }
            public Vector RequiredNormal { get; set; }
            public FabricationFace Face { get; set; }
        }

        private enum FeatureKind
        {
            SecondaryPart,
            HoleGroup
        }
    }

    public sealed class FaceViewOwnership
    {
        public Dictionary<DrawingView, HashSet<int>> HoleGroupsByView { get; } =
            new Dictionary<DrawingView, HashSet<int>>();

        public Dictionary<DrawingView, HashSet<int>> PartsByView { get; } =
            new Dictionary<DrawingView, HashSet<int>>();

        public List<string> Messages { get; } = new List<string>();

        public HashSet<int> GetHoleGroups(DrawingView view)
        {
            HashSet<int> result;
            return view != null && HoleGroupsByView.TryGetValue(view, out result)
                ? result
                : new HashSet<int>();
        }

        public HashSet<int> GetParts(DrawingView view)
        {
            HashSet<int> result;
            return view != null && PartsByView.TryGetValue(view, out result)
                ? result
                : new HashSet<int>();
        }
    }
}
