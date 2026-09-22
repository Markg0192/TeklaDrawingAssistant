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
    /// Important layout rules:
    /// - Dimension the fitting in the view that owns that set-out axis.
    /// - Hole centres are preferred to arbitrary plate edges.
    /// - Similar fittings on the same fabrication face share one dimension chain.
    /// - Horizontal chains go above the view first, then below when the upper lanes fill.
    /// - Vertical chains go left of the view first, then right when the left lanes fill.
    /// - Datums are real solid edges/corners; never a floating projected point.
    /// - Bottom/top attached fittings use the relevant flange edge for vertical set-out.
    /// - End fittings are forced to the end section that actually belongs to that end.
    /// </summary>
    public sealed class FittingDimensioner
    {
        private const double CoordinateTolerance = 0.5;
        private const int PrimaryLaneCapacity = 3;
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
            messages?.Add("DIM policy: first horizontal dimensions above, first vertical dimensions left; then use below/right lanes as required.");
            messages?.Add("DIM policy: similar fittings on the same fabrication face are combined on one chain.");
            messages?.Add("DIM policy: reference points are real member/fitting corners; bottom/top attachments use the relevant flange edge.");

            foreach (var group in tasks
                .GroupBy(task => task.GroupKey, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key))
            {
                var items = group.ToList();
                var first = items[0];
                var points = items.SelectMany(item => item.TargetPoints).ToList();
                var partBounds = items
                    .Where(item => item.PartBounds != null)
                    .Select(item => item.PartBounds)
                    .ToList();

                var count = CreateGroupedDimension(
                    analysis,
                    first.Owner,
                    first.Axis,
                    first.Axis2D,
                    first.Family,
                    points,
                    partBounds,
                    lanes);

                created += count;

                var partIds = string.Join(",", items.Select(item => item.PartId).Distinct().OrderBy(id => id));
                messages?.Add(
                    "DIM " + FriendlyViewName(first.Owner) +
                    ": " + first.Family +
                    " / " + first.Axis +
                    " / parts [" + partIds + "]" +
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
                    var targetDescription = holeGroups.Count > 0
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

            var unique = UniqueAlongAxis(targetPoints, axis, CoordinateTolerance);
            if (unique.Count == 0)
                return 0;

            var horizontalMeasurement = Math.Abs(axis.X) >= Math.Abs(axis.Y);
            var lane = AllocateLane(view, horizontalMeasurement, lanes);
            var referenceBounds = IsEndFamily(family) && fittingBounds.Count > 0
                ? CombineBounds(fittingBounds)
                : view.MainPartBounds;

            var reference = GetReferencePoint(
                analysis.MainPart,
                view,
                memberAxis,
                axis,
                family,
                unique,
                referenceBounds,
                lane.PrimarySide);

            var referenceAlong = Dot(reference, axis);
            if (unique.All(point => Math.Abs(Dot(point, axis) - referenceAlong) <= CoordinateTolerance))
                return 0;

            var direction = GetDimensionDirection(axis, horizontalMeasurement, lane.PrimarySide);
            var offset = DimensionLayout.GetBaseOffset(view) + lane.LaneIndex * DimensionLayout.GetLaneSpacing(view);

            var points = new PointList();
            points.Add(reference);
            foreach (var point in unique.OrderBy(point => Dot(point, axis)))
                points.Add(point);

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view.View, points, direction, offset);
            return dimension == null ? 0 : 1;
        }

        private Point GetReferencePoint(
            ModelPart mainPart,
            ViewAnalysis view,
            FittingSetoutAxis memberAxis,
            Axis2D axis,
            FittingFamily family,
            IList<Point> targets,
            ViewBounds datumBounds,
            bool primarySide)
        {
            var horizontalMeasurement = Math.Abs(axis.X) >= Math.Abs(axis.Y);
            var averageAlong = targets.Average(point => Dot(point, axis));

            if (horizontalMeasurement)
            {
                double x;

                if (memberAxis == FittingSetoutAxis.MemberX && !IsEndFamily(family))
                {
                    var start = ProjectPoint(mainPart.GetCoordinateSystem().Origin, view.View.DisplayCoordinateSystem);
                    x = Math.Abs(start.X - datumBounds.MinX) <= Math.Abs(start.X - datumBounds.MaxX)
                        ? datumBounds.MinX
                        : datumBounds.MaxX;
                }
                else
                {
                    x = Math.Abs(averageAlong - Dot(new Point(datumBounds.MinX, datumBounds.CentreY, 0.0), axis)) <=
                        Math.Abs(averageAlong - Dot(new Point(datumBounds.MaxX, datumBounds.CentreY, 0.0), axis))
                        ? datumBounds.MinX
                        : datumBounds.MaxX;
                }

                var y = primarySide ? datumBounds.MaxY : datumBounds.MinY;
                return new Point(x, y, 0.0);
            }

            double datumY;
            if (memberAxis == FittingSetoutAxis.MemberZ && family == FittingFamily.BottomFlange)
            {
                datumY = view.MainPartBounds.MinY;
            }
            else if (memberAxis == FittingSetoutAxis.MemberZ && family == FittingFamily.TopFlange)
            {
                datumY = view.MainPartBounds.MaxY;
            }
            else
            {
                var minAlong = Dot(new Point(datumBounds.CentreX, datumBounds.MinY, 0.0), axis);
                var maxAlong = Dot(new Point(datumBounds.CentreX, datumBounds.MaxY, 0.0), axis);
                datumY = Math.Abs(averageAlong - minAlong) <= Math.Abs(averageAlong - maxAlong)
                    ? datumBounds.MinY
                    : datumBounds.MaxY;
            }

            var xSide = primarySide ? datumBounds.MinX : datumBounds.MaxX;
            return new Point(xSide, datumY, 0.0);
        }

        private static Vector GetDimensionDirection(Axis2D axis, bool horizontalMeasurement, bool primarySide)
        {
            var perpX = -axis.Y;
            var perpY = axis.X;
            double sign;

            if (horizontalMeasurement)
            {
                // Primary horizontal dimensions live ABOVE the view; secondary below.
                sign = perpY >= 0.0 ? 1.0 : -1.0;
                if (!primarySide)
                    sign *= -1.0;
            }
            else
            {
                // Primary vertical dimensions live LEFT of the view; secondary right.
                sign = perpX <= 0.0 ? 1.0 : -1.0;
                if (!primarySide)
                    sign *= -1.0;
            }

            return new Vector(perpX * sign, perpY * sign, 0.0);
        }

        private static LanePlacement AllocateLane(
            ViewAnalysis view,
            bool horizontalMeasurement,
            IDictionary<string, int> lanes)
        {
            var key = GetViewKey(view) + "|" + (horizontalMeasurement ? "H" : "V");
            int count;
            if (!lanes.TryGetValue(key, out count))
                count = 0;
            lanes[key] = count + 1;

            var primary = count < PrimaryLaneCapacity;
            var laneIndex = primary ? count : count - PrimaryLaneCapacity;
            return new LanePlacement(primary, laneIndex);
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

            // If the planner selected a generic END SECTION, never blindly use the first end.
            // Choose the end section that actually contains this fitting.
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

                if (dA <= endZone)
                    return FittingFamily.EndA;
                if (dB <= endZone)
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

        private static ViewBounds CombineBounds(IEnumerable<ViewBounds> bounds)
        {
            var list = bounds.ToList();
            return new ViewBounds
            {
                MinX = list.Min(item => item.MinX),
                MaxX = list.Max(item => item.MaxX),
                MinY = list.Min(item => item.MinY),
                MaxY = list.Max(item => item.MaxY)
            };
        }

        private static bool IsEndFamily(FittingFamily family)
        {
            return family == FittingFamily.EndA || family == FittingFamily.EndB;
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

        private static Point ProjectPoint(Point globalPoint, CoordinateSystem viewCs)
        {
            var x = Normalize(new Vector(viewCs.AxisX));
            var y = Normalize(new Vector(viewCs.AxisY));
            var relative = new Vector(
                globalPoint.X - viewCs.Origin.X,
                globalPoint.Y - viewCs.Origin.Y,
                globalPoint.Z - viewCs.Origin.Z);

            return new Point(Dot(relative, x), Dot(relative, y), 0.0);
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

        private sealed class LanePlacement
        {
            public LanePlacement(bool primarySide, int laneIndex)
            {
                PrimarySide = primarySide;
                LaneIndex = laneIndex;
            }

            public bool PrimarySide { get; }
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
