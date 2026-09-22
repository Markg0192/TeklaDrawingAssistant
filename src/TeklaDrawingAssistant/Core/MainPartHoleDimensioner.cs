using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingView = Tekla.Structures.Drawing.View;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Dedicated main-member hole dimensioner.
    ///
    /// Structural ownership:
    /// - bolt axis through section WIDTH => WEB holes;
    /// - bolt axis through section DEPTH => FLANGE holes;
    /// - flange holes are split TOP/BOTTOM by their position across section depth.
    /// </summary>
    public sealed class MainPartHoleDimensioner
    {
        private const double CoordinateTolerance = 0.5;

        private readonly Model _model;

        public MainPartHoleDimensioner(Model model)
        {
            _model = model;
        }

        public int Dimension(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null)
                return 0;

            var axes = GetStructuralAxes(analysis.MainPart);
            var ownership = ClassifyGroups(analysis.MainPart, axes, messages);
            var created = 0;

            created += DimensionFace(
                analysis,
                MainFace.TopFlange,
                FindView(analysis, "TDA_TOP"),
                ownership,
                axes,
                messages);

            created += DimensionFace(
                analysis,
                MainFace.BottomFlange,
                FindView(analysis, "TDA_BOTTOM"),
                ownership,
                axes,
                messages);

            created += DimensionFace(
                analysis,
                MainFace.Web,
                FindBaseWebView(analysis),
                ownership,
                axes,
                messages);

            analysis.Drawing.CommitChanges();
            messages?.Add("MAIN HOLES V2: created " + created + " hole dimension set(s).");
            return created;
        }

        private int DimensionFace(
            DrawingAnalysisResult analysis,
            MainFace face,
            ViewAnalysis view,
            IDictionary<int, MainFace> ownership,
            StructuralAxes axes,
            IList<string> messages)
        {
            if (view == null || view.View == null || view.MainPartBounds == null)
            {
                messages?.Add("MAIN HOLES V2 " + FaceName(face) + ": owning view missing.");
                return 0;
            }

            var ids = ownership
                .Where(item => item.Value == face)
                .Select(item => item.Key)
                .Distinct()
                .ToList();

            if (ids.Count == 0)
            {
                messages?.Add("MAIN HOLES V2 " + FaceName(face) + ": no groups classified to this face.");
                return 0;
            }

            var groups = ProjectGroupsToView(view.View, ids);
            if (groups.Count == 0)
            {
                messages?.Add("MAIN HOLES V2 " + FaceName(face) + ": classified groups could not be projected into owning view.");
                return 0;
            }

            var memberX = ProjectAxis(axes.LengthAxis, view.View.DisplayCoordinateSystem);
            var transverse = ProjectAxis(
                face == MainFace.Web ? axes.DepthAxis : axes.WidthAxis,
                view.View.DisplayCoordinateSystem);

            if (memberX.Length < 0.25)
            {
                messages?.Add("MAIN HOLES V2 " + FaceName(face) + ": member length axis collapses in owning view.");
                return 0;
            }

            var created = 0;
            var allPoints = groups.SelectMany(group => group.Points).ToList();

            // Longitudinal set-out for the whole fabrication face, including closing dimension.
            var longitudinal = UniqueAlongAxis(allPoints, memberX);
            if (longitudinal.Count > 0)
            {
                var points = new List<Point>
                {
                    GetMainEndPoint(view.MainPartBounds, memberX, true)
                };
                points.AddRange(longitudinal);
                points.Add(GetMainEndPoint(view.MainPartBounds, memberX, false));

                var side = face == MainFace.BottomFlange
                    ? DimensionSide.Below
                    : DimensionSide.Above;

                created += CreateDimension(
                    view.View,
                    points,
                    memberX,
                    side,
                    DimensionLayout.GetBaseOffset(view));
            }

            if (transverse.Length >= 0.25)
            {
                foreach (var group in groups)
                {
                    var stations = UniqueAlongAxis(group.Points, transverse);
                    if (stations.Count == 0)
                        continue;

                    var points = new List<Point>();

                    if (face == MainFace.Web)
                    {
                        // For a horizontal fabrication elevation, an upper hole run is
                        // always dimensioned from the TOP flange corner; a lower run from
                        // the BOTTOM flange corner. Do not let the sign of the projected
                        // member-depth vector invert that choice.
                        var averageY = group.Points.Average(point => point.Y);
                        var useTop = averageY >= view.MainPartBounds.CentreY;
                        var leftMostX = group.Points.Min(point => point.X);
                        var datum = new Point(
                            leftMostX,
                            useTop ? view.MainPartBounds.MaxY : view.MainPartBounds.MinY,
                            0.0);

                        points.Add(datum);

                        messages?.Add(
                            "MAIN HOLES V2 WEB group " + group.ModelIdentifierId +
                            ": " + (useTop ? "upper" : "lower") +
                            " run -> " + (useTop ? "TOP" : "BOTTOM") + " flange corner datum.");
                    }
                    else
                    {
                        // Flange holes: transverse gauge from member centreline.
                        points.Add(GetCentreLineDatum(view.MainPartBounds, transverse, group.Points));
                    }

                    points.AddRange(stations);

                    created += CreateDimension(
                        view.View,
                        points,
                        transverse,
                        DimensionSide.Left,
                        DimensionLayout.GetLocalFeatureOffset(view));
                }
            }

            messages?.Add(
                "MAIN HOLES V2 " + FaceName(face) +
                " -> " + FriendlyViewName(view) +
                ": groups [" + string.Join(",", ids.OrderBy(id => id)) + "] dimensioned only in this owning view.");

            return created;
        }

        private Dictionary<int, MainFace> ClassifyGroups(
            ModelPart mainPart,
            StructuralAxes axes,
            IList<string> messages)
        {
            var result = new Dictionary<int, MainFace>();
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane());
                var mainOrigin = mainPart.GetCoordinateSystem().Origin;
                var bolts = mainPart.GetBolts();

                while (bolts.MoveNext())
                {
                    var group = bolts.Current as BoltGroup;
                    if (group == null || result.ContainsKey(group.Identifier.ID))
                        continue;

                    var cs = group.GetCoordinateSystem();
                    var boltAxis = Normalize(Cross(new Vector(cs.AxisX), new Vector(cs.AxisY)));

                    var alongLength = Math.Abs(Dot(boltAxis, axes.LengthAxis));
                    var throughWidth = Math.Abs(Dot(boltAxis, axes.WidthAxis));
                    var throughDepth = Math.Abs(Dot(boltAxis, axes.DepthAxis));

                    MainFace face;
                    if (throughWidth >= throughDepth && throughWidth >= alongLength)
                    {
                        face = MainFace.Web;
                    }
                    else if (throughDepth >= throughWidth && throughDepth >= alongLength)
                    {
                        var depthPositions = new List<double>();
                        foreach (var item in group.BoltPositions)
                        {
                            var point = item as Point;
                            if (point == null)
                                continue;

                            var relative = new Vector(
                                point.X - mainOrigin.X,
                                point.Y - mainOrigin.Y,
                                point.Z - mainOrigin.Z);
                            depthPositions.Add(Dot(relative, axes.DepthAxis));
                        }

                        var averageDepth = depthPositions.Count == 0 ? axes.DepthMid : depthPositions.Average();
                        face = averageDepth >= axes.DepthMid
                            ? MainFace.TopFlange
                            : MainFace.BottomFlange;
                    }
                    else
                    {
                        face = MainFace.End;
                    }

                    result[group.Identifier.ID] = face;
                    messages?.Add(
                        "MAIN HOLE V2 ownership: group " + group.Identifier.ID +
                        " | length=" + alongLength.ToString("0.###") +
                        " width=" + throughWidth.ToString("0.###") +
                        " depth=" + throughDepth.ToString("0.###") +
                        " -> " + FaceName(face) + ".");
                }
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }

            return result;
        }

        private StructuralAxes GetStructuralAxes(ModelPart mainPart)
        {
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                var cs = mainPart.GetCoordinateSystem();
                var x = Normalize(new Vector(cs.AxisX));
                var y = Normalize(new Vector(cs.AxisY));
                var z = Normalize(Cross(x, y));

                handler.SetCurrentTransformationPlane(new TransformationPlane(cs));
                var solid = mainPart.GetSolid();

                var spanY = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                var spanZ = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);

                if (spanY >= spanZ)
                {
                    return new StructuralAxes(
                        x,
                        widthAxis: z,
                        depthAxis: y,
                        depthMid: (solid.MinimumPoint.Y + solid.MaximumPoint.Y) * 0.5,
                        widthSpan: spanZ,
                        depthSpan: spanY);
                }

                return new StructuralAxes(
                    x,
                    widthAxis: y,
                    depthAxis: z,
                    depthMid: (solid.MinimumPoint.Z + solid.MaximumPoint.Z) * 0.5,
                    widthSpan: spanY,
                    depthSpan: spanZ);
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }
        }

        private List<HoleGroup> ProjectGroupsToView(DrawingView view, IEnumerable<int> ids)
        {
            var result = new List<HoleGroup>();
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(view.DisplayCoordinateSystem));

                foreach (var id in ids)
                {
                    var group = _model.SelectModelObject(new Identifier(id)) as BoltGroup;
                    if (group == null)
                        continue;

                    var projected = new HoleGroup { ModelIdentifierId = id };
                    foreach (var item in group.BoltPositions)
                    {
                        var point = item as Point;
                        if (point != null)
                            projected.Points.Add(new Point(point.X, point.Y, 0.0));
                    }

                    if (projected.Points.Count > 0)
                        result.Add(projected);
                }
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }

            return result;
        }

        private static int CreateDimension(
            DrawingView view,
            IEnumerable<Point> points,
            Axis2D measuredAxis,
            DimensionSide side,
            double offset)
        {
            var unique = UniqueAlongAxis(points, measuredAxis);
            if (unique.Count < 2)
                return 0;

            var list = new PointList();
            foreach (var point in unique.OrderBy(point => Dot(point, measuredAxis)))
                list.Add(point);

            var attributes = new StraightDimensionSet.StraightDimensionSetAttributes(null, "standard");
            attributes.Placing.Placing = DimensionSetBaseAttributes.Placings.Free;

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(
                view,
                list,
                GetDimensionDirection(measuredAxis, side),
                offset,
                attributes);

            return dimension == null ? 0 : 1;
        }

        private static Point GetMainEndPoint(ViewBounds bounds, Axis2D axis, bool start)
        {
            var corners = GetCorners(bounds);
            return start
                ? corners.OrderBy(point => Dot(point, axis)).First()
                : corners.OrderByDescending(point => Dot(point, axis)).First();
        }

        private static Point GetCentreLineDatum(ViewBounds bounds, Axis2D axis, IEnumerable<Point> targets)
        {
            var averageX = targets.Average(point => point.X);
            var averageY = targets.Average(point => point.Y);

            return Math.Abs(axis.X) >= Math.Abs(axis.Y)
                ? new Point(bounds.CentreX, averageY, 0.0)
                : new Point(averageX, bounds.CentreY, 0.0);
        }

        private static Axis2D ProjectAxis(Vector globalAxis, CoordinateSystem viewCs)
        {
            var vx = Normalize(new Vector(viewCs.AxisX));
            var vy = Normalize(new Vector(viewCs.AxisY));
            return new Axis2D(Dot(globalAxis, vx), Dot(globalAxis, vy));
        }

        private static List<Point> UniqueAlongAxis(IEnumerable<Point> points, Axis2D axis)
        {
            var result = new List<Point>();
            foreach (var point in points.OrderBy(point => Dot(point, axis)))
            {
                if (result.All(existing => Math.Abs(Dot(existing, axis) - Dot(point, axis)) > CoordinateTolerance))
                    result.Add(point);
            }
            return result;
        }

        private static Vector GetDimensionDirection(Axis2D axis, DimensionSide side)
        {
            var x = -axis.Y;
            var y = axis.X;

            switch (side)
            {
                case DimensionSide.Above:
                    if (y < 0.0) { x *= -1.0; y *= -1.0; }
                    break;
                case DimensionSide.Below:
                    if (y > 0.0) { x *= -1.0; y *= -1.0; }
                    break;
                case DimensionSide.Left:
                    if (x > 0.0) { x *= -1.0; y *= -1.0; }
                    break;
                case DimensionSide.Right:
                    if (x < 0.0) { x *= -1.0; y *= -1.0; }
                    break;
            }

            return new Vector(x, y, 0.0);
        }

        private static ViewAnalysis FindView(DrawingAnalysisResult analysis, string name)
        {
            return analysis.Views.FirstOrDefault(view =>
                view.View != null &&
                string.Equals(view.View.Name ?? string.Empty, name, StringComparison.OrdinalIgnoreCase));
        }

        private static ViewAnalysis FindBaseWebView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(view => view.View != null && view.Kind == ViewKind.Web)
                .Where(view => !IsGenerated(view))
                .OrderByDescending(view => view.MainPartBounds == null ? 0.0 : view.MainPartBounds.Width * view.MainPartBounds.Height)
                .FirstOrDefault();
        }

        private static bool IsGenerated(ViewAnalysis view)
        {
            var name = view == null || view.View == null ? string.Empty : (view.View.Name ?? string.Empty).Trim().ToUpperInvariant();
            return name.StartsWith("TDA_") || name == "A-A" || name == "B-B";
        }

        private static string FriendlyViewName(ViewAnalysis view)
        {
            if (view == null || view.View == null)
                return "<none>";

            var name = (view.View.Name ?? string.Empty).Trim();
            if (name == "TDA_TOP") return "TOP flange";
            if (name == "TDA_BOTTOM") return "BOTTOM flange";
            if (!string.IsNullOrWhiteSpace(name)) return name;
            return view.Kind == ViewKind.Web ? "BASE / WEB" : view.Kind.ToString();
        }

        private static string FaceName(MainFace face)
        {
            switch (face)
            {
                case MainFace.TopFlange: return "TOP flange";
                case MainFace.BottomFlange: return "BOTTOM flange";
                case MainFace.Web: return "WEB";
                case MainFace.End: return "END";
                default: return "UNKNOWN";
            }
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

        private static double Dot(Point point, Axis2D axis)
        {
            return point.X * axis.X + point.Y * axis.Y;
        }

        private static double Dot(Vector a, Vector b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Vector Cross(Vector a, Vector b)
        {
            return new Vector(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private static Vector Normalize(Vector vector)
        {
            var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
            return length < 0.000001
                ? new Vector()
                : new Vector(vector.X / length, vector.Y / length, vector.Z / length);
        }

        private enum MainFace
        {
            Unknown,
            TopFlange,
            BottomFlange,
            Web,
            End
        }

        private enum DimensionSide
        {
            Above,
            Below,
            Left,
            Right
        }

        private sealed class StructuralAxes
        {
            public StructuralAxes(
                Vector lengthAxis,
                Vector widthAxis,
                Vector depthAxis,
                double depthMid,
                double widthSpan,
                double depthSpan)
            {
                LengthAxis = lengthAxis;
                WidthAxis = widthAxis;
                DepthAxis = depthAxis;
                DepthMid = depthMid;
                WidthSpan = widthSpan;
                DepthSpan = depthSpan;
            }

            public Vector LengthAxis { get; }
            public Vector WidthAxis { get; }
            public Vector DepthAxis { get; }
            public double DepthMid { get; }
            public double WidthSpan { get; }
            public double DepthSpan { get; }
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
