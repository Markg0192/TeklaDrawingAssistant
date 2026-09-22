using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingBolt = Tekla.Structures.Drawing.Bolt;
using DrawingGrid = Tekla.Structures.Drawing.Grid;
using DrawingGridLine = Tekla.Structures.Drawing.GridLine;
using DrawingMark = Tekla.Structures.Drawing.Mark;
using DrawingPart = Tekla.Structures.Drawing.Part;
using DrawingView = Tekla.Structures.Drawing.View;
using DrawingWeldMark = Tekla.Structures.Drawing.WeldMark;
using ModelBoltGroup = Tekla.Structures.Model.BoltGroup;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// View-only prototype.
    ///
    /// Current rule: the incoming fabrication drawing is treated as having one base view.
    /// That base view owns web-face information. We only add the extra orthogonal views
    /// needed for flange-face information and end-plate information.
    ///
    /// No dimensions are created here. Generated views are deliberately stripped back
    /// to assembly geometry so Tekla's standard view/grid/mark settings do not decide the
    /// appearance for us.
    /// </summary>
    public sealed class ViewCreationPlanner
    {
        private const double ParallelTolerance = 0.90;
        private const double ViewGap = 12.0;

        private readonly Model _model;

        public ViewCreationPlanner(Model model)
        {
            _model = model;
        }

        public int RebuildRequiredViews(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null)
                throw new ArgumentNullException(nameof(analysis));

            if (analysis.MainPart == null)
                throw new InvalidOperationException("The drawing does not have a resolved main part.");

            var source = GetSourceView(analysis);
            if (source == null)
            {
                messages?.Add("No usable base view containing the main part was found.");
                return 0;
            }

            messages?.Add("Base view retained: " + GetDisplayName(source.View) + ".");

            // During this phase we explicitly assume one incoming base view. Any other
            // views are removed before rebuilding so repeated tests cannot accumulate
            // duplicate top/bottom/side views from previous runs.
            var removed = RemoveAllOtherViews(analysis, source.View);
            RemoveExistingSectionMarks(source.View);
            analysis.Drawing.CommitChanges();

            if (removed > 0)
                messages?.Add("Removed " + removed + " non-base view(s) before rebuilding.");

            var requirements = AnalyseRequirements(analysis.MainPart);
            WriteRequirementSummary(requirements, messages);

            var assemblyParts = GetAssemblyParts(analysis.MainPart);
            var allowedPartIds = new HashSet<int>(assemblyParts.Select(part => part.Identifier.ID));
            var allowedBoltIds = GetAssemblyBoltIds(assemblyParts);
            var createdViews = new List<DrawingView>();
            var created = 0;

            // Web-side features stay in the original base view. We never make SIDE A / SIDE B
            // views in this phase.
            if (requirements.TopFlange > 0)
            {
                var top = CreateBasicView(
                    analysis,
                    source,
                    GeneratedViewType.Top,
                    allowedPartIds,
                    allowedBoltIds,
                    createdViews,
                    messages);

                if (top != null)
                {
                    createdViews.Add(top);
                    created++;
                }
            }

            if (requirements.BottomFlange > 0)
            {
                var bottom = CreateBasicView(
                    analysis,
                    source,
                    GeneratedViewType.Bottom,
                    allowedPartIds,
                    allowedBoltIds,
                    createdViews,
                    messages);

                if (bottom != null)
                {
                    createdViews.Add(bottom);
                    created++;
                }
            }

            if (requirements.StartEnd > 0)
            {
                var endA = CreateEndSection(
                    analysis,
                    source,
                    true,
                    allowedPartIds,
                    allowedBoltIds,
                    createdViews,
                    messages);

                if (endA != null)
                {
                    createdViews.Add(endA);
                    created++;
                }
            }

            if (requirements.FinishEnd > 0)
            {
                var endB = CreateEndSection(
                    analysis,
                    source,
                    false,
                    allowedPartIds,
                    allowedBoltIds,
                    createdViews,
                    messages);

                if (endB != null)
                {
                    createdViews.Add(endB);
                    created++;
                }
            }

            analysis.Drawing.CommitChanges();
            return created;
        }

        private DrawingView CreateBasicView(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            GeneratedViewType type,
            ISet<int> allowedPartIds,
            ISet<int> allowedBoltIds,
            IList<DrawingView> alreadyCreated,
            IList<string> messages)
        {
            var attributes = CreateGeneratedViewAttributes(source.View);
            var insertionPoint = GetInitialInsertionPoint(source.View, type);
            DrawingView view;
            bool created;

            if (type == GeneratedViewType.Top)
            {
                created = DrawingView.CreateTopView(
                    analysis.Drawing,
                    insertionPoint,
                    attributes,
                    out view);
            }
            else
            {
                created = DrawingView.CreateBottomView(
                    analysis.Drawing,
                    insertionPoint,
                    attributes,
                    out view);
            }

            if (!created || view == null)
            {
                messages?.Add("Could not create the " + GetFriendlyName(type) + " view.");
                return null;
            }

            // Internal name only. View mark tags are cleared so this is not shown on the drawing.
            view.Name = type == GeneratedViewType.Top ? "TDA_TOP" : "TDA_BOTTOM";
            view.Modify();
            analysis.Drawing.CommitChanges();

            CleanGeneratedView(view, allowedPartIds, allowedBoltIds);
            analysis.Drawing.CommitChanges();

            if (!ContainsAnyAssemblyPart(view, allowedPartIds))
            {
                view.Delete();
                analysis.Drawing.CommitChanges();
                messages?.Add("Rejected the " + GetFriendlyName(type) + " view because it contained no assembly steel.");
                return null;
            }

            PlaceView(analysis.Drawing, source.View, view, type, alreadyCreated);
            view.Modify();
            analysis.Drawing.CommitChanges();

            messages?.Add("Created " + GetFriendlyName(type) + " view with no visible view name, grids, marks or neighbouring steel.");
            return view;
        }

        private DrawingView CreateEndSection(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            bool startEnd,
            ISet<int> allowedPartIds,
            ISet<int> allowedBoltIds,
            IList<DrawingView> alreadyCreated,
            IList<string> messages)
        {
            if (source.MainPartBounds == null)
                return null;

            var type = startEnd ? GeneratedViewType.EndA : GeneratedViewType.EndB;
            var attributes = CreateGeneratedViewAttributes(source.View);
            var insertionPoint = GetInitialInsertionPoint(source.View, type);
            var markAttributes = new SectionMarkBase.SectionMarkAttributes
            {
                MarkName = startEnd ? "A" : "B"
            };

            Point lineStart;
            Point lineEnd;
            GetSectionLine(source.MainPartBounds, startEnd, out lineStart, out lineEnd);

            DrawingView sectionView;
            SectionMark sectionMark;
            var created = DrawingView.CreateSectionView(
                source.View,
                lineStart,
                lineEnd,
                insertionPoint,
                250.0,
                250.0,
                attributes,
                markAttributes,
                out sectionView,
                out sectionMark);

            if (!created || sectionView == null)
            {
                messages?.Add("Could not create section " + (startEnd ? "A-A" : "B-B") + " at the member end.");
                return null;
            }

            sectionView.Name = startEnd ? "TDA_SECTION_A" : "TDA_SECTION_B";
            sectionView.Modify();
            analysis.Drawing.CommitChanges();

            CleanGeneratedView(sectionView, allowedPartIds, allowedBoltIds);
            analysis.Drawing.CommitChanges();

            // An end section may legitimately cut only the end plate at the precise cut
            // plane, so requiring the main part itself was too strict. The view is valid
            // if it contains any part belonging to the assembly.
            if (!ContainsAnyAssemblyPart(sectionView, allowedPartIds))
            {
                if (sectionMark != null)
                    sectionMark.Delete();

                sectionView.Delete();
                analysis.Drawing.CommitChanges();
                messages?.Add("Rejected section " + (startEnd ? "A-A" : "B-B") + " because it contained no assembly steel.");
                return null;
            }

            PlaceView(analysis.Drawing, source.View, sectionView, type, alreadyCreated);
            sectionView.Modify();
            analysis.Drawing.CommitChanges();

            messages?.Add("Created end section " + (startEnd ? "A-A" : "B-B") + ".");
            return sectionView;
        }

        private static DrawingView.ViewAttributes CreateGeneratedViewAttributes(DrawingView source)
        {
            var attributes = new DrawingView.ViewAttributes();

            if (source != null && source.Attributes != null)
            {
                if (source.Attributes.Scale > 0.0)
                    attributes.Scale = source.Attributes.Scale;

                attributes.Shortening = source.Attributes.Shortening;
            }

            attributes.FixedViewPlacing = true;
            attributes.ViewExtensionForNeighbourParts = 0.0;

            // Do not inherit whatever view-label content the current Tekla environment has
            // in its standard attributes (for example KEY PLAN on the test drawing).
            attributes.TagsAttributes = new DrawingView.ViewMarkTagsAttributes();

            return attributes;
        }

        private static void CleanGeneratedView(
            DrawingView view,
            ISet<int> allowedPartIds,
            ISet<int> allowedBoltIds)
        {
            if (view == null)
                return;

            HideGrids(view);
            HideUnrelatedSteel(view, allowedPartIds, allowedBoltIds);
            DeleteAutomaticMarks(view);
        }

        private static void HideGrids(DrawingView view)
        {
            var grids = view.GetObjects(new[] { typeof(DrawingGrid) });
            while (grids.MoveNext())
            {
                var grid = grids.Current as DrawingGrid;
                if (grid == null)
                    continue;

                if (grid.Hideable != null)
                {
                    grid.Hideable.HideFromDrawingView();
                    grid.Modify();
                }

                var lines = grid.GetObjects();
                while (lines.MoveNext())
                {
                    var line = lines.Current as DrawingGridLine;
                    if (line == null || line.Hideable == null)
                        continue;

                    line.Hideable.HideFromDrawingView();
                    line.Modify();
                }
            }

            // GridLine is a child object, so also fetch all children directly. This catches
            // environments where the parent grid is not returned by View.GetObjects().
            var allGridLines = view.GetAllObjects(new[] { typeof(DrawingGridLine) });
            while (allGridLines.MoveNext())
            {
                var line = allGridLines.Current as DrawingGridLine;
                if (line == null || line.Hideable == null)
                    continue;

                line.Hideable.HideFromDrawingView();
                line.Modify();
            }
        }

        private static void HideUnrelatedSteel(
            DrawingView view,
            ISet<int> allowedPartIds,
            ISet<int> allowedBoltIds)
        {
            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
            {
                var part = parts.Current as DrawingPart;
                if (part == null || part.ModelIdentifier == null)
                    continue;

                if (allowedPartIds != null && allowedPartIds.Contains(part.ModelIdentifier.ID))
                    continue;

                if (part.Hideable != null)
                {
                    part.Hideable.HideFromDrawingView();
                    part.Modify();
                }
            }

            var bolts = view.GetObjects(new[] { typeof(DrawingBolt) });
            while (bolts.MoveNext())
            {
                var bolt = bolts.Current as DrawingBolt;
                if (bolt == null || bolt.ModelIdentifier == null)
                    continue;

                if (allowedBoltIds != null && allowedBoltIds.Contains(bolt.ModelIdentifier.ID))
                    continue;

                if (bolt.Hideable != null)
                {
                    bolt.Hideable.HideFromDrawingView();
                    bolt.Modify();
                }
            }
        }

        private static void DeleteAutomaticMarks(DrawingView view)
        {
            var marks = view.GetObjects(new[] { typeof(DrawingMark), typeof(DrawingWeldMark) });
            var toDelete = new List<DrawingObject>();

            while (marks.MoveNext())
            {
                var drawingObject = marks.Current as DrawingObject;
                if (drawingObject != null)
                    toDelete.Add(drawingObject);
            }

            foreach (var drawingObject in toDelete)
                drawingObject.Delete();
        }

        private static bool ContainsAnyAssemblyPart(DrawingView view, ISet<int> allowedPartIds)
        {
            if (view == null || allowedPartIds == null || allowedPartIds.Count == 0)
                return false;

            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
            {
                var part = parts.Current as DrawingPart;
                if (part != null &&
                    part.ModelIdentifier != null &&
                    allowedPartIds.Contains(part.ModelIdentifier.ID))
                {
                    return true;
                }
            }

            return false;
        }

        private static int RemoveAllOtherViews(DrawingAnalysisResult analysis, DrawingView source)
        {
            var removed = 0;

            foreach (var viewAnalysis in analysis.Views)
            {
                var view = viewAnalysis.View;
                if (view == null || ReferenceEquals(view, source))
                    continue;

                if (view.Delete())
                    removed++;
            }

            return removed;
        }

        private static void RemoveExistingSectionMarks(DrawingView source)
        {
            if (source == null)
                return;

            var marks = source.GetObjects(new[] { typeof(SectionMark) });
            var toDelete = new List<SectionMark>();
            while (marks.MoveNext())
            {
                var mark = marks.Current as SectionMark;
                if (mark != null)
                    toDelete.Add(mark);
            }

            foreach (var mark in toDelete)
                mark.Delete();
        }

        private ViewRequirements AnalyseRequirements(ModelPart mainPart)
        {
            var requirements = new ViewRequirements();
            var originalPlane = _model.GetWorkPlaneHandler().GetCurrentTransformationPlane();

            try
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane());

                var basis = GetMemberBasis(mainPart);
                var mainCentre = GetSolidCentre(mainPart);
                var assemblyParts = GetAssemblyParts(mainPart);

                foreach (var part in assemblyParts)
                {
                    if (part.Identifier.ID == mainPart.Identifier.ID)
                        continue;

                    var face = ClassifyFeature(
                        GetDominantFaceNormal(part),
                        GetSolidCentre(part),
                        mainCentre,
                        basis);

                    requirements.Add(face);
                }

                var seenBoltGroups = new HashSet<int>();
                foreach (var part in assemblyParts)
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

                        var face = ClassifyFeature(
                            boltNormal,
                            GetBoltGroupCentre(boltGroup),
                            mainCentre,
                            basis);

                        requirements.Add(face);
                    }
                }
            }
            finally
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(originalPlane);
            }

            return requirements;
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

        private static RequiredFace ClassifyFeature(
            Vector featureNormal,
            Point featureCentre,
            Point mainCentre,
            MemberBasis basis)
        {
            var normal = Normalize(featureNormal);
            var along = GeometryMath.AbsoluteDot(normal, basis.AxisX);
            var web = GeometryMath.AbsoluteDot(normal, basis.WebNormal);
            var flange = GeometryMath.AbsoluteDot(normal, basis.TopNormal);

            var fromMain = new Vector(
                featureCentre.X - mainCentre.X,
                featureCentre.Y - mainCentre.Y,
                featureCentre.Z - mainCentre.Z);

            if (along >= ParallelTolerance && along >= web && along >= flange)
                return Dot(fromMain, basis.AxisX) < 0.0 ? RequiredFace.StartEnd : RequiredFace.FinishEnd;

            if (flange >= ParallelTolerance && flange >= web && flange >= along)
                return Dot(fromMain, basis.TopNormal) >= 0.0 ? RequiredFace.TopFlange : RequiredFace.BottomFlange;

            if (web >= ParallelTolerance && web >= along && web >= flange)
                return RequiredFace.Web;

            return RequiredFace.Custom;
        }

        private static MemberBasis GetMemberBasis(ModelPart mainPart)
        {
            var cs = mainPart.GetCoordinateSystem();
            var axisX = Normalize(new Vector(cs.AxisX));
            var webNormal = Normalize(new Vector(cs.AxisY));
            var topNormal = Normalize(GeometryMath.Cross(axisX, webNormal));

            // Keep the semantic TOP direction pointing upward in the model whenever
            // possible, regardless of how the profile local coordinate system was created.
            if (topNormal.Z < 0.0)
                topNormal = Multiply(topNormal, -1.0);

            return new MemberBasis
            {
                AxisX = axisX,
                WebNormal = webNormal,
                TopNormal = topNormal
            };
        }

        private static void WriteRequirementSummary(ViewRequirements requirements, IList<string> messages)
        {
            if (messages == null)
                return;

            messages.Add("View requirements:");
            messages.Add("  TOP flange features: " + requirements.TopFlange);
            messages.Add("  BOTTOM flange features: " + requirements.BottomFlange);
            messages.Add("  END A features: " + requirements.StartEnd);
            messages.Add("  END B features: " + requirements.FinishEnd);
            messages.Add("  WEB features staying in base view: " + requirements.Web);

            if (requirements.Custom > 0)
                messages.Add("  Custom/skew features left for later: " + requirements.Custom);
        }

        private static void GetSectionLine(ViewBounds bounds, bool startEnd, out Point start, out Point end)
        {
            if (Math.Abs(bounds.Width) >= Math.Abs(bounds.Height))
            {
                var x = startEnd ? bounds.MinX : bounds.MaxX;
                var margin = Math.Max(40.0, Math.Abs(bounds.Height) * 0.25);
                start = new Point(x, bounds.MinY - margin, 0.0);
                end = new Point(x, bounds.MaxY + margin, 0.0);

                // Reverse the second end so the two sections look back toward the member
                // from opposite ends rather than both facing the same way.
                if (!startEnd)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }
            }
            else
            {
                var y = startEnd ? bounds.MinY : bounds.MaxY;
                var margin = Math.Max(40.0, Math.Abs(bounds.Width) * 0.25);
                start = new Point(bounds.MinX - margin, y, 0.0);
                end = new Point(bounds.MaxX + margin, y, 0.0);

                if (startEnd)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }
            }
        }

        private static ViewAnalysis GetSourceView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(view =>
                    view.View != null &&
                    view.ContainsMainPart &&
                    view.MainPartBounds != null)
                .OrderBy(view => IsToolGenerated(view.View) ? 1 : 0)
                .ThenByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                .FirstOrDefault();
        }

        private static bool IsToolGenerated(DrawingView view)
        {
            if (view == null)
                return false;

            var name = (view.Name ?? string.Empty).Trim().ToUpperInvariant();
            return name.StartsWith("TDA_") ||
                   name.StartsWith("AUTO -") ||
                   name == "TOP" ||
                   name == "BOTTOM" ||
                   name == "SIDE A" ||
                   name == "SIDE B" ||
                   name == "END 1" ||
                   name == "END 2";
        }

        private static List<ModelPart> GetAssemblyParts(ModelPart mainPart)
        {
            var result = new List<ModelPart> { mainPart };
            var assembly = mainPart.GetAssembly();
            if (assembly == null)
                return result;

            var secondaries = assembly.GetSecondaries();
            foreach (var item in secondaries)
            {
                var part = item as ModelPart;
                if (part != null)
                    result.Add(part);
            }

            return result;
        }

        private static HashSet<int> GetAssemblyBoltIds(IEnumerable<ModelPart> parts)
        {
            var result = new HashSet<int>();

            foreach (var part in parts)
            {
                var bolts = part.GetBolts();
                while (bolts.MoveNext())
                {
                    var bolt = bolts.Current as ModelBoltGroup;
                    if (bolt != null)
                        result.Add(bolt.Identifier.ID);
                }
            }

            return result;
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

        private static Point GetInitialInsertionPoint(DrawingView source, GeneratedViewType type)
        {
            var origin = source.Origin;
            const double gap = 25.0;

            switch (type)
            {
                case GeneratedViewType.Top:
                    return new Point(origin.X, origin.Y + source.Height + gap, 0.0);
                case GeneratedViewType.Bottom:
                    return new Point(origin.X, origin.Y - source.Height - gap, 0.0);
                case GeneratedViewType.EndA:
                    return new Point(origin.X - source.Width * 0.5 - gap, origin.Y, 0.0);
                default:
                    return new Point(origin.X + source.Width * 0.5 + gap, origin.Y, 0.0);
            }
        }

        private static void PlaceView(
            Drawing drawing,
            DrawingView source,
            DrawingView view,
            GeneratedViewType type,
            IEnumerable<DrawingView> alreadyCreated)
        {
            var candidates = GetPlacementCandidates(source, view, type);
            var occupied = new List<DrawingView> { source };
            if (alreadyCreated != null)
                occupied.AddRange(alreadyCreated.Where(item => item != null));

            var sheet = drawing.GetSheet();
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
            GeneratedViewType type)
        {
            var horizontal = source.Width * 0.5 + view.Width * 0.5 + ViewGap;
            var vertical = source.Height * 0.5 + view.Height * 0.5 + ViewGap;
            var centre = source.Origin;

            var above = new Point(centre.X, centre.Y + vertical, 0.0);
            var below = new Point(centre.X, centre.Y - vertical, 0.0);
            var left = new Point(centre.X - horizontal, centre.Y, 0.0);
            var right = new Point(centre.X + horizontal, centre.Y, 0.0);
            var aboveLeft = new Point(centre.X - horizontal, centre.Y + vertical, 0.0);
            var aboveRight = new Point(centre.X + horizontal, centre.Y + vertical, 0.0);
            var belowLeft = new Point(centre.X - horizontal, centre.Y - vertical, 0.0);
            var belowRight = new Point(centre.X + horizontal, centre.Y - vertical, 0.0);

            switch (type)
            {
                case GeneratedViewType.Top:
                    return new List<Point> { above, aboveRight, aboveLeft, right, left };
                case GeneratedViewType.Bottom:
                    return new List<Point> { below, belowRight, belowLeft, right, left };
                case GeneratedViewType.EndA:
                    return new List<Point> { left, aboveLeft, belowLeft, above, below };
                default:
                    return new List<Point> { right, aboveRight, belowRight, above, below };
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

        private static string GetFriendlyName(GeneratedViewType type)
        {
            return type == GeneratedViewType.Top ? "TOP" : "BOTTOM";
        }

        private static string GetDisplayName(DrawingView view)
        {
            if (view == null || string.IsNullOrWhiteSpace(view.Name))
                return "unnamed original view";

            return view.Name;
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

        private sealed class MemberBasis
        {
            public Vector AxisX { get; set; }
            public Vector WebNormal { get; set; }
            public Vector TopNormal { get; set; }
        }

        private sealed class ViewRequirements
        {
            public int TopFlange { get; private set; }
            public int BottomFlange { get; private set; }
            public int StartEnd { get; private set; }
            public int FinishEnd { get; private set; }
            public int Web { get; private set; }
            public int Custom { get; private set; }

            public void Add(RequiredFace face)
            {
                switch (face)
                {
                    case RequiredFace.TopFlange:
                        TopFlange++;
                        break;
                    case RequiredFace.BottomFlange:
                        BottomFlange++;
                        break;
                    case RequiredFace.StartEnd:
                        StartEnd++;
                        break;
                    case RequiredFace.FinishEnd:
                        FinishEnd++;
                        break;
                    case RequiredFace.Web:
                        Web++;
                        break;
                    default:
                        Custom++;
                        break;
                }
            }
        }

        private enum RequiredFace
        {
            TopFlange,
            BottomFlange,
            StartEnd,
            FinishEnd,
            Web,
            Custom
        }

        private enum GeneratedViewType
        {
            Top,
            Bottom,
            EndA,
            EndB
        }
    }
}
