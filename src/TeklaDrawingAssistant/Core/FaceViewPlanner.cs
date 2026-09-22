using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using TeklaDrawingAssistant.Tekla;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingView = Tekla.Structures.Drawing.View;
using ModelPart = Tekla.Structures.Model.Part;
using ModelBoltGroup = Tekla.Structures.Model.BoltGroup;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Decides which drawing view owns each fabrication feature.
    /// A feature is only dimensioned in the view which looks most directly at its working face.
    /// Missing face views are created before dimensioning starts.
    /// </summary>
    public sealed class FaceViewPlanner
    {
        private const double FaceParallelTolerance = 0.90;

        private readonly Model _model;

        public FaceViewPlanner(Model model)
        {
            _model = model;
        }

        public int EnsureRequiredViews(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null)
                throw new ArgumentNullException(nameof(analysis));

            var features = GetFeatures(analysis.MainPart);
            var knownViews = GetKnownViews(analysis.Views);
            var assemblyParts = GetAssemblyPartIdentifiers(analysis.MainPart);
            var created = 0;

            foreach (var feature in features)
            {
                if (FindBestView(knownViews, feature.RequiredNormal) != null)
                    continue;

                var name = "AUTO - " + GetViewName(feature);
                var view = CreateView(analysis.Drawing, analysis.MainPart, assemblyParts, feature.RequiredNormal, name);
                if (view == null)
                {
                    messages?.Add("Could not create a face view for " + feature.Description + ".");
                    continue;
                }

                knownViews.Add(new KnownView(view, GetViewNormal(view)));
                created++;
                messages?.Add("Created " + name + " for " + feature.Description + ".");
            }

            if (created > 0)
            {
                analysis.Drawing.CommitChanges();
                analysis.Drawing.PlaceViews();
                analysis.Drawing.CommitChanges();
            }

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

                result.Messages.Add(feature.Description + " -> " + owner.View.Name);
            }

            return result;
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

            // For a skew/custom fitting, look at the face from the side on which the fitting sits.
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

        private DrawingView CreateView(
            Drawing drawing,
            ModelPart mainPart,
            ArrayList assemblyParts,
            Vector requiredNormal,
            string name)
        {
            var cs = BuildViewCoordinateSystem(mainPart.GetCoordinateSystem(), requiredNormal);
            var view = new DrawingView(drawing.GetSheet(), cs, cs, assemblyParts)
            {
                Name = name
            };

            return view.Insert() ? view : null;
        }

        private static CoordinateSystem BuildViewCoordinateSystem(CoordinateSystem mainCs, Vector requiredNormal)
        {
            var normal = Normalize(requiredNormal);
            var mainX = Normalize(new Vector(mainCs.AxisX));
            var mainY = Normalize(new Vector(mainCs.AxisY));

            var xCandidate = Math.Abs(Dot(normal, mainX)) < 0.95 ? mainX : mainY;
            var projectedX = new Vector(
                xCandidate.X - normal.X * Dot(xCandidate, normal),
                xCandidate.Y - normal.Y * Dot(xCandidate, normal),
                xCandidate.Z - normal.Z * Dot(xCandidate, normal));
            var axisX = Normalize(projectedX);
            var axisY = Normalize(GeometryMath.Cross(normal, axisX));

            return new CoordinateSystem(
                new Point(mainCs.Origin),
                axisX,
                axisY);
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

        private static ArrayList GetAssemblyPartIdentifiers(ModelPart mainPart)
        {
            var identifiers = new ArrayList();
            foreach (var part in GetAssemblyParts(mainPart))
                identifiers.Add(part.Identifier);
            return identifiers;
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

        private static string GetViewName(FeatureSpec feature)
        {
            switch (feature.Face)
            {
                case FabricationFace.TopFlange:
                    return "Top flange";
                case FabricationFace.BottomFlange:
                    return "Bottom flange";
                case FabricationFace.WebSideA:
                    return "Web side A";
                case FabricationFace.WebSideB:
                    return "Web side B";
                case FabricationFace.StartEnd:
                    return "Start end";
                case FabricationFace.FinishEnd:
                    return "Finish end";
                default:
                    return "Fitting face " + feature.IdentifierId;
            }
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
