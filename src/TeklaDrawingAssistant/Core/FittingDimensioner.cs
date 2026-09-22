using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using TeklaDrawingAssistant.Tekla;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using ModelPart = Tekla.Structures.Model.Part;
using DrawingView = Tekla.Structures.Drawing.View;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// First-pass fitting dimension generator.
    ///
    /// The FittingSetoutPlanner decides WHICH view owns each independent fitting axis.
    /// This class only turns that plan into Tekla dimensions.
    ///
    /// Rules for this pass:
    /// - Prefer every visible bolt group attached to the fitting in the planned owner view.
    /// - A dimension chain from the main-member datum through all unique hole centres both
    ///   locates the fitting and defines pitch/gauge on that axis.
    /// - If no usable hole group exists in the owner view, fall back to the fitting bounds.
    /// - Dimension offsets are paper-space values from DimensionLayout; there is no user offset.
    /// </summary>
    public sealed class FittingDimensioner
    {
        private const double CoordinateTolerance = 0.5;

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
            var datum = analysis.MainPart.GetCoordinateSystem().Origin;
            var lanes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var created = 0;

            messages?.Add("DIMENSIONING ==============================================");
            messages?.Add("DIMENSION policy: dimensions follow fitting set-out ownership; holes are preferred over plate edges.");

            foreach (var fitting in plan.Fittings.OrderBy(item => item.PartId))
            {
                var part = _model.SelectModelObject(new Identifier(fitting.PartId)) as ModelPart;
                if (part == null)
                {
                    messages?.Add("DIM part " + fitting.PartId + ": model part not found.");
                    continue;
                }

                foreach (var requirement in fitting.Requirements.OrderBy(item => item.Axis))
                {
                    var owner = ResolveView(analysis, requirement.ViewName, requirement.ViewKind);
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

                    var holeGroups = GetVisibleAttachedHoleGroups(part, owner);
                    if (holeGroups.Count > 0)
                    {
                        foreach (var group in holeGroups)
                        {
                            var key = FriendlyViewName(owner) + "|H|" + group.ModelIdentifierId + "|" + requirement.Axis;
                            if (!completed.Add(key))
                                continue;

                            var count = CreateAxisDimension(
                                owner,
                                group.Points,
                                axis2d,
                                datum,
                                lanes,
                                "hole " + group.ModelIdentifierId);

                            created += count;
                            messages?.Add("DIM " + FriendlyViewName(owner) + ": part " + fitting.PartId +
                                          " " + requirement.Axis + " -> hole group " + group.ModelIdentifierId +
                                          (count > 0 ? " created." : " skipped (no measurable separation)."));
                        }

                        continue;
                    }

                    var partKey = FriendlyViewName(owner) + "|P|" + fitting.PartId + "|" + requirement.Axis;
                    if (!completed.Add(partKey))
                        continue;

                    var bounds = _geometryReader.GetPartBounds(owner.View, new Identifier(fitting.PartId));
                    if (bounds == null)
                    {
                        messages?.Add("DIM " + FriendlyViewName(owner) + ": part " + fitting.PartId +
                                      " has no projected bounds for " + requirement.Axis + ".");
                        continue;
                    }

                    var geometryPoints = new List<Point>
                    {
                        new Point(bounds.MinX, bounds.MinY, 0.0),
                        new Point(bounds.MinX, bounds.MaxY, 0.0),
                        new Point(bounds.MaxX, bounds.MinY, 0.0),
                        new Point(bounds.MaxX, bounds.MaxY, 0.0)
                    };

                    var geometryCount = CreateAxisDimension(
                        owner,
                        geometryPoints,
                        axis2d,
                        datum,
                        lanes,
                        "part " + fitting.PartId);

                    created += geometryCount;
                    messages?.Add("DIM " + FriendlyViewName(owner) + ": part " + fitting.PartId +
                                  " " + requirement.Axis + " -> fitting geometry" +
                                  (geometryCount > 0 ? " created." : " skipped (no measurable separation)."));
                }
            }

            analysis.Drawing.CommitChanges();
            messages?.Add("DIMENSIONING ==============================================");
            messages?.Add("DIM total straight dimension sets created: " + created + ".");
            return created;
        }

        private int CreateAxisDimension(
            ViewAnalysis view,
            IList<Point> targetPoints,
            Axis2D axis,
            Point globalDatum,
            IDictionary<string, int> lanes,
            string description)
        {
            if (view == null || view.View == null || view.MainPartBounds == null ||
                targetPoints == null || targetPoints.Count == 0)
                return 0;

            var unique = UniqueAlongAxis(targetPoints, axis, CoordinateTolerance);
            if (unique.Count == 0)
                return 0;

            var datumInView = ProjectPoint(globalDatum, view.View.DisplayCoordinateSystem);
            var perp = new Axis2D(-axis.Y, axis.X);
            var averageCross = unique.Average(point => Dot(point, perp));
            var datumAlong = Dot(datumInView, axis);
            var reference = new Point(
                axis.X * datumAlong + perp.X * averageCross,
                axis.Y * datumAlong + perp.Y * averageCross,
                0.0);

            if (unique.All(point => Math.Abs(Dot(point, axis) - datumAlong) <= CoordinateTolerance))
                return 0;

            var mainCentre = new Point(view.MainPartBounds.CentreX, view.MainPartBounds.CentreY, 0.0);
            var mainCross = Dot(mainCentre, perp);
            var side = averageCross >= mainCross ? 1.0 : -1.0;
            var direction = new Vector(perp.X * side, perp.Y * side, 0.0);

            var laneKey = FriendlyViewName(view) + "|" + (side > 0.0 ? "+" : "-");
            int lane;
            if (!lanes.TryGetValue(laneKey, out lane))
                lane = 0;
            lanes[laneKey] = lane + 1;

            var offset = DimensionLayout.GetBaseOffset(view) + lane * DimensionLayout.GetLaneSpacing(view);
            var points = new PointList();
            points.Add(reference);

            foreach (var point in unique.OrderBy(point => Dot(point, axis)))
                points.Add(point);

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view.View, points, direction, offset);
            return dimension == null ? 0 : 1;
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
