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
    /// Turns the fitting set-out plan into fabrication dimensions.
    ///
    /// Fabrication rules in this pass:
    /// - Dimensions stay close to the feature.
    /// - Horizontal dimensions use ABOVE first; vertical dimensions use LEFT first.
    /// - Hole-based chains are placed outside geometry/set-out chains where both exist.
    /// - Bottom-flange longitudinal set-out is projected BELOW the member to avoid
    ///   running dimension lines through the member.
    /// - End-plate horizontal set-out uses the member centreline.
    /// - End-plate vertical set-out uses the top flange edge as the primary datum.
    /// - Top/bottom attachments use the relevant flange edge for vertical set-out.
    /// - Longitudinal set-out includes both member ends, giving a closing dimension.
    /// - Similar fittings on the same fabrication face share one dimension chain.
    /// </summary>
    public sealed class FittingDimensioner
    {
        private const double CoordinateTolerance = 0.5;
        private const int PrimaryLaneCapacity = 6;
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
            messages?.Add("DIM policy: dimensions sit close to the feature; ABOVE/LEFT are preferred before BELOW/RIGHT.");
            messages?.Add("DIM policy: hole-centre chains are kept outside fitting-geometry chains.");
            messages?.Add("DIM policy: end plates use member centreline horizontally and flange edge vertically.");
            messages?.Add("DIM policy: bottom-flange longitudinal set-out projects below the member and includes a closing dimension to the far end.");

            var groups = tasks
                .GroupBy(task => task.GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.ToList())
                .OrderBy(group => GetViewKey(group[0].Owner))
                .ThenBy(group => IsHorizontal(group[0].Axis2D) ? 0 : 1)
                .ThenBy(group => group.Any(task => task.UsesHoles) ? 1 : 0)
                .ThenBy(group => group[0].Family)
                .ThenBy(group => group[0].Axis)
                .ToList();

            foreach (var items in groups)
            {
                var first = items[0];
                var points = items.SelectMany(item => item.TargetPoints).ToList();
                var partBounds = items
                    .Where(item => item.PartBounds != null)
                    .Select(item => item.PartBounds)
                    .ToList();
                var usesHoles = items.Any(item => item.UsesHoles);

                var count = CreateGroupedDimension(
                    analysis,
                    first.Owner,
                    first.Axis,
                    first.Axis2D,
                    first.Family,
                    points,
                    partBounds,
                    usesHoles,
                    lanes);

                created += count;

                var partIds = string.Join(",", items.Select(item => item.PartId).Distinct().OrderBy(id => id));
                messages?.Add(
                    "DIM " + FriendlyViewName(first.Owner) +
                    ": " + first.Family +
                    " / " + first.Axis +
                    " / parts [" + partIds + "]" +
                    " / " + (usesHoles ? "holes" : "geometry") +
                    (count > 0 ? " -> one grouped chain created." : " -> skipped (no measurable separation)."));
            }

            analysis.Drawing.CommitChanges();
            messages?.Add("DIMENSIONING ==============================================");
            messages?.Add("DIM total straight dimension sets created: " + created + ".");
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

                foreach (var requirement in fitting.Requirements.OrderBy(item => item.Axis))
                {
                    var owner = ResolveViewForPart(analysis, requirement, part, family);
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
                    var usesHoles = points.Count > 0;
                    var targetDescription = usesHoles
                        ? "holes " + string.Join(",", holeGroups.Select(group => group.ModelIdentifierId).Distinct())
                        : "fitting geometry";

                    if (!usesHoles && partBounds != null)
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
                        UsesHoles = usesHoles,
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
            bool usesHoles,
            IDictionary<string, int> lanes)
        {
            if (view == null || view.View == null || view.MainPartBounds == null ||
                targetPoints == null || targetPoints.Count == 0)
                return 0;

            var uniqueTargets = UniqueAlongAxis(targetPoints, axis, CoordinateTolerance);
            if (uniqueTargets.Count == 0)
                return 0;

            var horizontalMeasurement = IsHorizontal(axis);
            var lane = AllocateLane(view, horizontalMeasurement, memberAxis, family, lanes);

            var dimensionPoints = BuildDimensionPoints(
                analysis.MainPart,
                view,
                memberAxis,
                axis,
                family,
                uniqueTargets,
                fittingBounds,
                lane.Side);

            dimensionPoints = UniqueAlongAxis(dimensionPoints, axis, CoordinateTolerance)
                .OrderBy(point => Dot(point, axis))
                .ToList();

            if (dimensionPoints.Count < 2)
                return 0;

            var firstValue = Dot(dimensionPoints.First(), axis);
            var lastValue = Dot(dimensionPoints.Last(), axis);
            if (Math.Abs(lastValue - firstValue) <= CoordinateTolerance)
                return 0;

            var direction = GetDimensionDirection(axis, lane.Side);
            var offset = DimensionLayout.GetBaseOffset(view) + lane.LaneIndex * DimensionLayout.GetLaneSpacing(view);

            var points = new PointList();
            foreach (var point in dimensionPoints)
                points.Add(point);

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view.View, points, direction, offset);
            return dimension == null ? 0 : 1;
        }

        private List<Point> BuildDimensionPoints(
            ModelPart mainPart,
            ViewAnalysis view,
            FittingSetoutAxis memberAxis,
            Axis2D axis,
            FittingFamily family,
            IList<Point> targets,
            IList<ViewBounds> fittingBounds,
            DimensionSide side)
        {
            var result = new List<Point>();
            var main = view.MainPartBounds;

            if (memberAxis == FittingSetoutAxis.MemberX)
            {
                // Any longitudinal set-out receives both main-member ends. This gives
                // the closing dimension to the far end as standard fabrication practice.
                result.Add(GetMainEndPoint(main, axis, side, true));
                result.AddRange(targets);
                result.Add(GetMainEndPoint(main, axis, side, false));
                return result;
            }

            if (IsEndFamily(family))
            {
                if (memberAxis == FittingSetoutAxis.MemberY)
                {
                    // End-plate horizontal dimensions originate at the main-member centreline.
                    result.Add(GetCentrelineDatum(main, axis, side));
                    result.AddRange(targets);
                    return result;
                }

                if (memberAxis == FittingSetoutAxis.MemberZ)
                {
                    // Use the top flange as the default vertical fabrication datum in
                    // an end view, matching the usual 90 / pitch / pitch style chain.
                    result.Add(GetTopFlangeDatum(main, side));
                    result.AddRange(targets);
                    return result;
                }
            }

            if (memberAxis == FittingSetoutAxis.MemberZ && family == FittingFamily.BottomFlange)
            {
                result.Add(GetBottomFlangeDatum(main, side));
                result.AddRange(targets);
                return result;
            }

            if (memberAxis == FittingSetoutAxis.MemberZ && family == FittingFamily.TopFlange)
            {
                result.Add(GetTopFlangeDatum(main, side));
                result.AddRange(targets);
                return result;
            }

            // Generic fallback: locate from the nearest real main-member edge on the
            // dimensioned axis. Never invent a free-floating projected datum.
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

        private static Point GetCentrelineDatum(ViewBounds bounds, Axis2D axis, DimensionSide side)
        {
            if (IsHorizontal(axis))
            {
                var y = side == DimensionSide.Below ? bounds.MinY : bounds.MaxY;
                return new Point(bounds.CentreX, y, 0.0);
            }

            var x = side == DimensionSide.Right ? bounds.MaxX : bounds.MinX;
            return new Point(x, bounds.CentreY, 0.0);
        }

        private static Point GetTopFlangeDatum(ViewBounds bounds, DimensionSide side)
        {
            var x = side == DimensionSide.Right ? bounds.MaxX : bounds.MinX;
            return new Point(x, bounds.MaxY, 0.0);
        }

        private static Point GetBottomFlangeDatum(ViewBounds bounds, DimensionSide side)
        {
            var x = side == DimensionSide.Right ? bounds.MaxX : bounds.MinX;
            return new Point(x, bounds.MinY, 0.0);
        }

        private static Point GetNearestMainEdgeDatum(
            ViewBounds bounds,
            Axis2D axis,
            IEnumerable<Point> targets,
            DimensionSide side)
        {
            var average = targets.Average(point => Dot(point, axis));
            var sideCorners = GetCornersOnSide(bounds, side);
            if (sideCorners.Count == 0)
                sideCorners = GetCorners(bounds);

            var minimum = sideCorners.OrderBy(point => Dot(point, axis)).First();
            var maximum = sideCorners.OrderByDescending(point => Dot(point, axis)).First();

            return Math.Abs(average - Dot(minimum, axis)) <= Math.Abs(average - Dot(maximum, axis))
                ? minimum
                : maximum;
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

        private static LanePlacement AllocateLane(
            ViewAnalysis view,
            bool horizontalMeasurement,
            FittingSetoutAxis axis,
            FittingFamily family,
            IDictionary<string, int> lanes)
        {
            var preferred = horizontalMeasurement ? DimensionSide.Above : DimensionSide.Left;

            // Bottom-flange longitudinal set-out belongs below the member. Projecting
            // upward runs extension lines through the main member and is harder to read.
            if (horizontalMeasurement && axis == FittingSetoutAxis.MemberX && family == FittingFamily.BottomFlange)
                preferred = DimensionSide.Below;

            var alternate = GetOpposite(preferred);
            var preferredKey = GetViewKey(view) + "|" + preferred;
            int preferredCount;
            if (!lanes.TryGetValue(preferredKey, out preferredCount))
                preferredCount = 0;

            if (preferredCount < PrimaryLaneCapacity)
            {
                lanes[preferredKey] = preferredCount + 1;
                return new LanePlacement(preferred, preferredCount);
            }

            var alternateKey = GetViewKey(view) + "|" + alternate;
            int alternateCount;
            if (!lanes.TryGetValue(alternateKey, out alternateCount))
                alternateCount = 0;
            lanes[alternateKey] = alternateCount + 1;
            return new LanePlacement(alternate, alternateCount);
        }

        private static DimensionSide GetOpposite(DimensionSide side)
        {
            switch (side)
            {
                case DimensionSide.Above: return DimensionSide.Below;
                case DimensionSide.Below: return DimensionSide.Above;
                case DimensionSide.Left: return DimensionSide.Right;
                case DimensionSide.Right: return DimensionSide.Left;
                default: return DimensionSide.Above;
            }
        }

        private ViewAnalysis ResolveViewForPart(
            DrawingAnalysisResult analysis,
            FittingSetoutRequirement requirement,
            ModelPart part,
            FittingFamily family)
        {
            if (family == FittingFamily.EndA)
            {
                var a = analysis.Views.FirstOrDefault(view => NameEquals(view, "A-A"));
                if (a != null && IsPartVisible(a, part.Identifier.ID))
                    return a;
            }

            if (family == FittingFamily.EndB)
            {
                var b = analysis.Views.FirstOrDefault(view => NameEquals(view, "B-B"));
                if (b != null && IsPartVisible(b, part.Identifier.ID))
                    return b;
            }

            var resolved = ResolveView(analysis, requirement.ViewName, requirement.ViewKind);
            if (resolved != null && IsPartVisible(resolved, part.Identifier.ID))
                return resolved;

            if (requirement.ViewKind == ViewKind.End ||
                (requirement.ViewName ?? string.Empty).IndexOf("END", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var visibleEnd = analysis.Views.FirstOrDefault(view =>
                    view.Kind == ViewKind.End && IsPartVisible(view, part.Identifier.ID));
                if (visibleEnd != null)
                    return visibleEnd;
            }

            return resolved;
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
                if (drawingPart != null && drawingPart.ModelIdentifier != null &&
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

        private static bool IsEndFamily(FittingFamily family)
        {
            return family == FittingFamily.EndA || family == FittingFamily.EndB;
        }

        private static bool IsHorizontal(Axis2D axis)
        {
            return Math.Abs(axis.X) >= Math.Abs(axis.Y);
        }

        private static List<Point> UniqueAlongAxis(IEnumerable<Point> points, Axis2D axis, double tolerance)
        {
            var result = new List<Point>();
            foreach (var point in points.OrderBy(point => Dot(point, axis)))
            {
                var coordinate = Dot(point, axis);
                if (result.All(existing => Math.Abs(Dot(existing, axis) - coordinate) > tolerance))
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
            var z = Normalize(GeometryMath.Cross(x, y));
            return new MainAxes(x, y, z);
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
                string.Equals(view.View == null ? string.Empty : view.View.Name ?? string.Empty,
                    name, StringComparison.OrdinalIgnoreCase));
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
            if (!string.IsNullOrWhiteSpace(name))
                return name;
            return FriendlyViewName(view);
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
            public bool UsesHoles { get; set; }
            public string GroupKey { get; set; }
        }

        private sealed class LanePlacement
        {
            public LanePlacement(DimensionSide side, int laneIndex)
            {
                Side = side;
                LaneIndex = laneIndex;
            }

            public DimensionSide Side { get; }
            public int LaneIndex { get; }
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
