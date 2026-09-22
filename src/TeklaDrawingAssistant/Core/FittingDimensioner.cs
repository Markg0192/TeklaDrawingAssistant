using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using TeklaDrawingAssistant.Tekla;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingPart = Tekla.Structures.Drawing.Part;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Generic fitting set-out dimensioner for non-end fittings.
    /// End plates are deliberately left to EndPlateDimensioner.
    ///
    /// Rules in this pass:
    /// - use the view selected by the fitting set-out planner;
    /// - prefer visible holes to fitting edges;
    /// - group like fittings on the same fabrication face into one chain;
    /// - top-flange longitudinal set-out projects above;
    /// - bottom-flange longitudinal set-out projects below;
    /// - longitudinal chains include both member ends for a closing dimension;
    /// - dimensions use FREE placing so Tekla cannot auto-move them to another side;
    /// - offsets are scale-aware and intentionally tight.
    /// </summary>
    public sealed class FittingDimensioner
    {
        private const double CoordinateTolerance = 0.5;
        private const double EndZoneMinimum = 100.0;
        private const double AttachmentTolerance = 45.0;

        private readonly Model _model;
        private readonly ViewGeometryReader _geometryReader;

        public FittingDimensioner(Model model)
        {
            _model = model;
            _geometryReader = new ViewGeometryReader(model);
        }

        public int Dimension(
            DrawingAnalysisResult analysis,
            FittingSetoutPlan plan,
            IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null || plan == null)
                return 0;

            DeleteExistingStraightDimensions(analysis);

            var axes = GetMainAxes(analysis.MainPart);
            var tasks = BuildTasks(analysis, plan, axes, messages);
            var lanes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var created = 0;

            messages?.Add("DIMENSIONING ==============================================");
            messages?.Add("DIM policy: FREE placing is forced so explicit side/distance rules win over Tekla automatic placing.");
            messages?.Add("DIM policy: bottom-flange longitudinal set-out is BELOW; top-flange set-out is ABOVE.");
            messages?.Add("DIM policy: longitudinal chains include both member ends for a closing dimension.");

            var groupedTasks = tasks
                .GroupBy(task => task.GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.ToList())
                .OrderBy(group => GetViewKey(group[0].Owner))
                .ThenBy(group => group[0].Axis)
                .ThenBy(group => group[0].Family)
                .ToList();

            foreach (var group in groupedTasks)
            {
                var first = group[0];
                var targetPoints = group.SelectMany(item => item.TargetPoints).ToList();
                var fittingBounds = group
                    .Where(item => item.PartBounds != null)
                    .Select(item => item.PartBounds)
                    .ToList();

                var count = CreateGroupedDimension(
                    analysis,
                    first.Owner,
                    first.Axis,
                    first.Axis2D,
                    first.Family,
                    targetPoints,
                    fittingBounds,
                    lanes);

                created += count;

                var partIds = string.Join(",", group.Select(item => item.PartId).Distinct().OrderBy(id => id));
                messages?.Add(
                    "DIM " + FriendlyViewName(first.Owner) +
                    ": " + first.Family +
                    " / " + first.Axis +
                    " / parts [" + partIds + "]" +
                    (count > 0 ? " -> grouped chain created." : " -> skipped."));
            }

            analysis.Drawing.CommitChanges();
            messages?.Add("DIM total generic straight dimension sets created: " + created + ".");
            messages?.Add("DIMENSIONING ==============================================");
            return created;
        }

        private List<DimensionTask> BuildTasks(
            DrawingAnalysisResult analysis,
            FittingSetoutPlan plan,
            MainAxes axes,
            IList<string> messages)
        {
            var result = new List<DimensionTask>();

            foreach (var fitting in plan.Fittings.OrderBy(item => item.PartId))
            {
                var part = _model.SelectModelObject(new Identifier(fitting.PartId)) as ModelPart;
                if (part == null)
                {
                    messages?.Add("DIM part " + fitting.PartId + ": model part not found.");
                    continue;
                }

                var family = ClassifyFamily(analysis.MainPart, part);

                // End plates are rebuilt later by EndPlateDimensioner using the dedicated
                // centreline/top-flange convention. Do not create generic dimensions here.
                if (family == FittingFamily.EndA || family == FittingFamily.EndB)
                    continue;

                foreach (var requirement in fitting.Requirements.OrderBy(item => item.Axis))
                {
                    var owner = ResolveViewForPart(analysis, requirement, part);
                    if (owner == null || owner.View == null || owner.MainPartBounds == null)
                    {
                        messages?.Add("DIM part " + fitting.PartId + " " + requirement.Axis +
                                      ": owner view '" + requirement.ViewName + "' not found/usable.");
                        continue;
                    }

                    var axis3d = GetAxis(axes, requirement.Axis);
                    var axis2d = ProjectAxis(axis3d, owner.View.DisplayCoordinateSystem);
                    if (axis2d.Length < 0.25)
                    {
                        messages?.Add("DIM part " + fitting.PartId + " " + requirement.Axis +
                                      ": planned axis collapses in " + FriendlyViewName(owner) + ".");
                        continue;
                    }

                    var partBounds = _geometryReader.GetPartBounds(owner.View, new Identifier(fitting.PartId));
                    var holeGroups = GetVisibleAttachedHoleGroups(part, owner);
                    var points = holeGroups.SelectMany(group => group.Points).ToList();
                    var targetDescription = points.Count > 0
                        ? "holes " + string.Join(",", holeGroups.Select(group => group.ModelIdentifierId).Distinct())
                        : "fitting geometry";

                    if (points.Count == 0 && partBounds != null)
                        points.AddRange(GetCorners(partBounds));

                    if (points.Count == 0)
                    {
                        messages?.Add("DIM " + FriendlyViewName(owner) + ": part " + fitting.PartId +
                                      " has no usable projected points for " + requirement.Axis + ".");
                        continue;
                    }

                    var familyKey = family == FittingFamily.Other
                        ? family + "_" + fitting.PartId
                        : family.ToString();

                    result.Add(new DimensionTask
                    {
                        PartId = fitting.PartId,
                        Owner = owner,
                        Axis = requirement.Axis,
                        Axis2D = axis2d,
                        Family = family,
                        PartBounds = partBounds,
                        TargetPoints = points,
                        GroupKey = GetViewKey(owner) + "|" + requirement.Axis + "|" + familyKey
                    });

                    messages?.Add("DIM PLAN part " + fitting.PartId + ": " + requirement.Axis +
                                  " -> " + FriendlyViewName(owner) + " using " + targetDescription +
                                  "; family=" + family + ".");
                }
            }

            return result;
        }

        private int CreateGroupedDimension(
            DrawingAnalysisResult analysis,
            ViewAnalysis view,
            FittingSetoutAxis memberAxis,
            Axis2D axis,
            FittingFamily family,
            IList<Point> targetPoints,
            IList<ViewBounds> fittingBounds,
            IDictionary<string, int> lanes)
        {
            if (view == null || view.View == null || view.MainPartBounds == null ||
                targetPoints == null || targetPoints.Count == 0)
                return 0;

            var uniqueTargets = UniqueAlongAxis(targetPoints, axis, CoordinateTolerance);
            if (uniqueTargets.Count == 0)
                return 0;

            var side = ChooseSide(view, memberAxis, axis, family, fittingBounds);
            var laneIndex = AllocateLane(view, side, lanes);

            var points = BuildDimensionPoints(
                view,
                memberAxis,
                axis,
                family,
                uniqueTargets,
                side);

            points = UniqueAlongAxis(points, axis, CoordinateTolerance)
                .OrderBy(point => Dot(point, axis))
                .ToList();

            if (points.Count < 2)
                return 0;

            if (Math.Abs(Dot(points.Last(), axis) - Dot(points.First(), axis)) <= CoordinateTolerance)
                return 0;

            var pointList = new PointList();
            foreach (var point in points)
                pointList.Add(point);

            var offset = DimensionLayout.GetBaseOffset(view) +
                         laneIndex * DimensionLayout.GetLaneSpacing(view);

            var attributes = new StraightDimensionSet.StraightDimensionSetAttributes(null, "standard");
            attributes.Placing.Placing = DimensionSetBaseAttributes.Placings.Free;

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(
                view.View,
                pointList,
                GetDimensionDirection(axis, side),
                offset,
                attributes);

            return dimension == null ? 0 : 1;
        }

        private static DimensionSide ChooseSide(
            ViewAnalysis view,
            FittingSetoutAxis memberAxis,
            Axis2D axis,
            FittingFamily family,
            IList<ViewBounds> fittingBounds)
        {
            var horizontalMeasurement = IsHorizontal(axis);

            if (horizontalMeasurement)
            {
                if (memberAxis == FittingSetoutAxis.MemberX)
                {
                    if (family == FittingFamily.BottomFlange)
                        return DimensionSide.Below;

                    if (family == FittingFamily.TopFlange)
                        return DimensionSide.Above;

                    // Extra geometric guard: if classification is imperfect but all of
                    // the projected fittings clearly sit under the member, keep the
                    // longitudinal dimension below rather than running it through steel.
                    if (fittingBounds != null && fittingBounds.Count > 0)
                    {
                        var averageY = fittingBounds.Average(bounds => bounds.CentreY);
                        if (averageY < view.MainPartBounds.CentreY)
                        {
                            var bottomDistance = fittingBounds.Min(bounds =>
                                Math.Min(
                                    Math.Abs(bounds.MinY - view.MainPartBounds.MinY),
                                    Math.Abs(bounds.MaxY - view.MainPartBounds.MinY)));

                            var topDistance = fittingBounds.Min(bounds =>
                                Math.Min(
                                    Math.Abs(bounds.MinY - view.MainPartBounds.MaxY),
                                    Math.Abs(bounds.MaxY - view.MainPartBounds.MaxY)));

                            if (bottomDistance <= topDistance)
                                return DimensionSide.Below;
                        }
                    }
                }

                return DimensionSide.Above;
            }

            return DimensionSide.Left;
        }

        private static List<Point> BuildDimensionPoints(
            ViewAnalysis view,
            FittingSetoutAxis memberAxis,
            Axis2D axis,
            FittingFamily family,
            IList<Point> targets,
            DimensionSide side)
        {
            var result = new List<Point>();
            var main = view.MainPartBounds;

            if (memberAxis == FittingSetoutAxis.MemberX)
            {
                result.Add(GetMainEndPoint(main, axis, side, true));
                result.AddRange(targets);
                result.Add(GetMainEndPoint(main, axis, side, false));
                return result;
            }

            if (memberAxis == FittingSetoutAxis.MemberZ && family == FittingFamily.BottomFlange)
            {
                result.Add(GetFlangeDatum(main, false, side));
                result.AddRange(targets);
                return result;
            }

            if (memberAxis == FittingSetoutAxis.MemberZ && family == FittingFamily.TopFlange)
            {
                result.Add(GetFlangeDatum(main, true, side));
                result.AddRange(targets);
                return result;
            }

            result.Add(GetNearestMainEdgeDatum(main, axis, targets, side));
            result.AddRange(targets);
            return result;
        }

        private static Point GetMainEndPoint(
            ViewBounds bounds,
            Axis2D axis,
            DimensionSide side,
            bool start)
        {
            var sideCorners = GetCornersOnSide(bounds, side);
            if (sideCorners.Count == 0)
                sideCorners = GetCorners(bounds);

            return start
                ? sideCorners.OrderBy(point => Dot(point, axis)).First()
                : sideCorners.OrderByDescending(point => Dot(point, axis)).First();
        }

        private static Point GetFlangeDatum(ViewBounds bounds, bool top, DimensionSide side)
        {
            var x = side == DimensionSide.Right ? bounds.MaxX : bounds.MinX;
            return new Point(x, top ? bounds.MaxY : bounds.MinY, 0.0);
        }

        private static Point GetNearestMainEdgeDatum(
            ViewBounds bounds,
            Axis2D axis,
            IEnumerable<Point> targets,
            DimensionSide side)
        {
            var average = targets.Average(point => Dot(point, axis));
            var candidates = GetCornersOnSide(bounds, side);
            if (candidates.Count == 0)
                candidates = GetCorners(bounds);

            return candidates
                .OrderBy(point => Math.Abs(average - Dot(point, axis)))
                .First();
        }

        private static List<Point> GetCornersOnSide(ViewBounds bounds, DimensionSide side)
        {
            switch (side)
            {
                case DimensionSide.Above:
                    return new List<Point>
                    {
                        new Point(bounds.MinX, bounds.MaxY, 0.0),
                        new Point(bounds.MaxX, bounds.MaxY, 0.0)
                    };

                case DimensionSide.Below:
                    return new List<Point>
                    {
                        new Point(bounds.MinX, bounds.MinY, 0.0),
                        new Point(bounds.MaxX, bounds.MinY, 0.0)
                    };

                case DimensionSide.Left:
                    return new List<Point>
                    {
                        new Point(bounds.MinX, bounds.MinY, 0.0),
                        new Point(bounds.MinX, bounds.MaxY, 0.0)
                    };

                case DimensionSide.Right:
                    return new List<Point>
                    {
                        new Point(bounds.MaxX, bounds.MinY, 0.0),
                        new Point(bounds.MaxX, bounds.MaxY, 0.0)
                    };

                default:
                    return new List<Point>();
            }
        }

        private static Vector GetDimensionDirection(Axis2D axis, DimensionSide side)
        {
            var perpX = -axis.Y;
            var perpY = axis.X;

            switch (side)
            {
                case DimensionSide.Above:
                    if (perpY < 0.0) { perpX *= -1.0; perpY *= -1.0; }
                    break;
                case DimensionSide.Below:
                    if (perpY > 0.0) { perpX *= -1.0; perpY *= -1.0; }
                    break;
                case DimensionSide.Left:
                    if (perpX > 0.0) { perpX *= -1.0; perpY *= -1.0; }
                    break;
                case DimensionSide.Right:
                    if (perpX < 0.0) { perpX *= -1.0; perpY *= -1.0; }
                    break;
            }

            return new Vector(perpX, perpY, 0.0);
        }

        private static int AllocateLane(
            ViewAnalysis view,
            DimensionSide side,
            IDictionary<string, int> lanes)
        {
            var key = GetViewKey(view) + "|" + side;
            int count;
            if (!lanes.TryGetValue(key, out count))
                count = 0;

            lanes[key] = count + 1;
            return count;
        }

        private ViewAnalysis ResolveViewForPart(
            DrawingAnalysisResult analysis,
            FittingSetoutRequirement requirement,
            ModelPart part)
        {
            var resolved = ResolveView(analysis, requirement.ViewName, requirement.ViewKind);
            if (resolved != null && IsPartVisible(resolved, part.Identifier.ID))
                return resolved;

            var visible = analysis.Views
                .Where(view => view.View != null && IsPartVisible(view, part.Identifier.ID))
                .OrderBy(view => view.Kind == requirement.ViewKind ? 0 : 1)
                .ThenBy(view => IsGenerated(view) ? 1 : 0)
                .FirstOrDefault();

            return visible ?? resolved;
        }

        private FittingFamily ClassifyFamily(ModelPart mainPart, ModelPart part)
        {
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(mainPart.GetCoordinateSystem()));

                var main = mainPart.GetSolid();
                var fitting = part.GetSolid();

                var mainLength = Math.Abs(main.MaximumPoint.X - main.MinimumPoint.X);
                var endZone = Math.Max(EndZoneMinimum, Math.Min(400.0, mainLength * 0.04));
                var centreX = (fitting.MinimumPoint.X + fitting.MaximumPoint.X) * 0.5;
                var dA = Math.Abs(centreX - main.MinimumPoint.X);
                var dB = Math.Abs(centreX - main.MaximumPoint.X);

                var sx = Math.Abs(fitting.MaximumPoint.X - fitting.MinimumPoint.X);
                var sy = Math.Abs(fitting.MaximumPoint.Y - fitting.MinimumPoint.Y);
                var sz = Math.Abs(fitting.MaximumPoint.Z - fitting.MinimumPoint.Z);
                var transverse = Math.Max(sy, sz);
                var transverseEndPlate = sx <= Math.Max(60.0, transverse * 0.65);

                if (transverseEndPlate && dA <= endZone + sx * 0.5)
                    return FittingFamily.EndA;
                if (transverseEndPlate && dB <= endZone + sx * 0.5)
                    return FittingFamily.EndB;

                var bottomDistance = Math.Min(
                    Math.Abs(fitting.MinimumPoint.Z - main.MinimumPoint.Z),
                    Math.Abs(fitting.MaximumPoint.Z - main.MinimumPoint.Z));

                var topDistance = Math.Min(
                    Math.Abs(fitting.MinimumPoint.Z - main.MaximumPoint.Z),
                    Math.Abs(fitting.MaximumPoint.Z - main.MaximumPoint.Z));

                if (bottomDistance <= AttachmentTolerance && bottomDistance <= topDistance)
                    return FittingFamily.BottomFlange;

                if (topDistance <= AttachmentTolerance)
                    return FittingFamily.TopFlange;

                return FittingFamily.Web;
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }
        }

        private static bool IsPartVisible(ViewAnalysis view, int partId)
        {
            if (view == null || view.View == null)
                return false;

            var parts = view.View.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
            {
                var drawingPart = parts.Current as DrawingPart;
                if (drawingPart != null &&
                    drawingPart.ModelIdentifier != null &&
                    drawingPart.ModelIdentifier.ID == partId)
                    return true;
            }

            return false;
        }

        private static List<HoleGroup> GetVisibleAttachedHoleGroups(ModelPart part, ViewAnalysis view)
        {
            var attached = new HashSet<int>();
            var bolts = part.GetBolts();

            while (bolts.MoveNext())
            {
                var bolt = bolts.Current as BoltGroup;
                if (bolt != null)
                    attached.Add(bolt.Identifier.ID);
            }

            return view.HoleGroups
                .Where(group => attached.Contains(group.ModelIdentifierId) && group.Points.Count > 0)
                .ToList();
        }

        private static ViewAnalysis ResolveView(
            DrawingAnalysisResult analysis,
            string friendlyName,
            ViewKind fallbackKind)
        {
            if (analysis == null)
                return null;

            var name = (friendlyName ?? string.Empty).Trim();

            if (name.Equals("TOP flange", StringComparison.OrdinalIgnoreCase))
                return analysis.Views.FirstOrDefault(view => NameEquals(view, "TDA_TOP"));

            if (name.Equals("BOTTOM flange", StringComparison.OrdinalIgnoreCase))
                return analysis.Views.FirstOrDefault(view => NameEquals(view, "TDA_BOTTOM"));

            if (name.StartsWith("A-A", StringComparison.OrdinalIgnoreCase))
                return analysis.Views.FirstOrDefault(view => NameEquals(view, "A-A"));

            if (name.StartsWith("B-B", StringComparison.OrdinalIgnoreCase))
                return analysis.Views.FirstOrDefault(view => NameEquals(view, "B-B"));

            if (name.Equals("BASE / WEB", StringComparison.OrdinalIgnoreCase))
            {
                return analysis.Views
                    .Where(view => view.Kind == ViewKind.Web)
                    .OrderBy(view => IsGenerated(view) ? 1 : 0)
                    .FirstOrDefault();
            }

            var exact = analysis.Views.FirstOrDefault(view =>
                string.Equals(
                    view.View == null ? string.Empty : view.View.Name ?? string.Empty,
                    name,
                    StringComparison.OrdinalIgnoreCase));

            if (exact != null)
                return exact;

            return analysis.Views
                .Where(view => view.Kind == fallbackKind)
                .OrderBy(view => IsGenerated(view) ? 1 : 0)
                .FirstOrDefault();
        }

        private static bool NameEquals(ViewAnalysis view, string name)
        {
            return view != null && view.View != null &&
                   string.Equals(view.View.Name ?? string.Empty, name, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGenerated(ViewAnalysis view)
        {
            var name = view == null || view.View == null
                ? string.Empty
                : (view.View.Name ?? string.Empty).Trim().ToUpperInvariant();

            return name.StartsWith("TDA_") || name == "A-A" || name == "B-B";
        }

        private static string FriendlyViewName(ViewAnalysis view)
        {
            if (view == null || view.View == null)
                return "<none>";

            var name = (view.View.Name ?? string.Empty).Trim();
            if (name.Equals("TDA_TOP", StringComparison.OrdinalIgnoreCase)) return "TOP flange";
            if (name.Equals("TDA_BOTTOM", StringComparison.OrdinalIgnoreCase)) return "BOTTOM flange";
            if (name.Equals("A-A", StringComparison.OrdinalIgnoreCase)) return "A-A end section";
            if (name.Equals("B-B", StringComparison.OrdinalIgnoreCase)) return "B-B end section";
            if (!string.IsNullOrWhiteSpace(name)) return name;

            switch (view.Kind)
            {
                case ViewKind.Web: return "BASE / WEB";
                case ViewKind.Flange: return "FLANGE";
                case ViewKind.End: return "END SECTION";
                default: return "UNNAMED VIEW";
            }
        }

        private static string GetViewKey(ViewAnalysis view)
        {
            if (view == null || view.View == null)
                return "<none>";

            var name = (view.View.Name ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(name) ? FriendlyViewName(view) : name;
        }

        private static List<Point> GetCorners(ViewBounds bounds)
        {
            return new List<Point>
            {
                new Point(bounds.MinX, bounds.MinY, 0.0),
                new Point(bounds.MinX, bounds.MaxY, 0.0),
                new Point(bounds.MaxX, bounds.MinY, 0.0),
                new Point(bounds.MaxX, bounds.MaxY, 0.0)
            };
        }

        private static bool IsHorizontal(Axis2D axis)
        {
            return Math.Abs(axis.X) >= Math.Abs(axis.Y);
        }

        private static List<Point> UniqueAlongAxis(
            IEnumerable<Point> points,
            Axis2D axis,
            double tolerance)
        {
            var result = new List<Point>();

            foreach (var point in points.OrderBy(point => Dot(point, axis)))
            {
                var coordinate = Dot(point, axis);
                if (result.All(existing =>
                    Math.Abs(Dot(existing, axis) - coordinate) > tolerance))
                    result.Add(point);
            }

            return result;
        }

        private static double Dot(Point point, Axis2D axis)
        {
            return point.X * axis.X + point.Y * axis.Y;
        }

        private static Axis2D ProjectAxis(Vector globalAxis, CoordinateSystem viewCs)
        {
            var axis = Normalize(globalAxis);
            var x = Normalize(new Vector(viewCs.AxisX));
            var y = Normalize(new Vector(viewCs.AxisY));
            return new Axis2D(Dot(axis, x), Dot(axis, y));
        }

        private static MainAxes GetMainAxes(ModelPart mainPart)
        {
            var cs = mainPart.GetCoordinateSystem();
            var x = Normalize(new Vector(cs.AxisX));
            var y = Normalize(new Vector(cs.AxisY));
            var z = Normalize(Cross(x, y));
            return new MainAxes(x, y, z);
        }

        private static Vector Cross(Vector a, Vector b)
        {
            return new Vector(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private static Vector GetAxis(MainAxes axes, FittingSetoutAxis axis)
        {
            switch (axis)
            {
                case FittingSetoutAxis.MemberX: return axes.X;
                case FittingSetoutAxis.MemberY: return axes.Y;
                case FittingSetoutAxis.MemberZ: return axes.Z;
                default: return axes.X;
            }
        }

        private static Vector Normalize(Vector vector)
        {
            var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
            return length < 0.000001
                ? new Vector()
                : new Vector(vector.X / length, vector.Y / length, vector.Z / length);
        }

        private static double Dot(Vector a, Vector b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static void DeleteExistingStraightDimensions(DrawingAnalysisResult analysis)
        {
            foreach (var view in analysis.Views)
            {
                if (view.View == null)
                    continue;

                var dimensions = view.View.GetObjects(new[] { typeof(StraightDimensionSet) });
                var delete = new List<StraightDimensionSet>();

                while (dimensions.MoveNext())
                {
                    var dimension = dimensions.Current as StraightDimensionSet;
                    if (dimension != null)
                        delete.Add(dimension);
                }

                foreach (var dimension in delete)
                    dimension.Delete();
            }
        }

        private enum FittingFamily
        {
            EndA,
            EndB,
            TopFlange,
            BottomFlange,
            Web,
            Other
        }

        private enum DimensionSide
        {
            Above,
            Below,
            Left,
            Right
        }

        private sealed class DimensionTask
        {
            public int PartId { get; set; }
            public ViewAnalysis Owner { get; set; }
            public FittingSetoutAxis Axis { get; set; }
            public Axis2D Axis2D { get; set; }
            public FittingFamily Family { get; set; }
            public ViewBounds PartBounds { get; set; }
            public List<Point> TargetPoints { get; set; }
            public string GroupKey { get; set; }
        }

        private sealed class MainAxes
        {
            public MainAxes(Vector x, Vector y, Vector z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public Vector X { get; }
            public Vector Y { get; }
            public Vector Z { get; }
        }

        private sealed class Axis2D
        {
            public Axis2D(double x, double y)
            {
                var length = Math.Sqrt(x * x + y * y);
                if (length < 0.000001)
                {
                    X = 0.0;
                    Y = 0.0;
                    Length = 0.0;
                }
                else
                {
                    X = x / length;
                    Y = y / length;
                    Length = length;
                }
            }

            public double X { get; }
            public double Y { get; }
            public double Length { get; }
        }
    }
}
