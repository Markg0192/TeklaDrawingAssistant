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
    /// Dimensions fabrication features belonging to the MAIN member.
    ///
    /// Hole ownership is based on the bolt direction relative to the main-part
    /// coordinate system, not on whichever drawing views happen to display the
    /// bolt symbol. This prevents web holes being dimensioned in flange views.
    ///
    /// TOP flange holes    -> TOP view
    /// BOTTOM flange holes -> BOTTOM view
    /// WEB holes           -> retained base/web view
    ///
    /// Longitudinal hole locations are chained from member start through the
    /// hole stations to member finish. Gauge/vertical dimensions stay local to
    /// each group and use centreline/flange datums as appropriate.
    /// </summary>
    public sealed class MainPartFeatureDimensioner
    {
        private const double CoordinateTolerance = 0.5;
        private const double FaceAlignmentTolerance = 0.55;
        private const double CutEdgeTolerance = 2.0;

        private readonly Model _model;

        public MainPartFeatureDimensioner(Model model)
        {
            _model = model;
        }

        public int Dimension(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null)
                return 0;

            var ownership = ClassifyMainPartBoltGroups(analysis.MainPart, messages);
            var created = 0;

            created += DimensionFaceHoles(
                analysis,
                MainFace.TopFlange,
                FindView(analysis, "TDA_TOP"),
                ownership,
                messages);

            created += DimensionFaceHoles(
                analysis,
                MainFace.BottomFlange,
                FindView(analysis, "TDA_BOTTOM"),
                ownership,
                messages);

            created += DimensionFaceHoles(
                analysis,
                MainFace.Web,
                FindBaseWebView(analysis),
                ownership,
                messages);

            created += DimensionLocalCuts(analysis, messages);

            analysis.Drawing.CommitChanges();
            messages?.Add("MAIN FEATURE DIM: created " + created + " main-part hole/cut dimension set(s).");
            return created;
        }

        private int DimensionFaceHoles(
            DrawingAnalysisResult analysis,
            MainFace face,
            ViewAnalysis view,
            IDictionary<int, MainFace> ownership,
            IList<string> messages)
        {
            if (view == null || view.View == null || view.MainPartBounds == null)
            {
                messages?.Add("MAIN HOLES " + FriendlyFace(face) + ": owning view not available.");
                return 0;
            }

            var ids = ownership
                .Where(item => item.Value == face)
                .Select(item => item.Key)
                .ToList();

            if (ids.Count == 0)
            {
                messages?.Add("MAIN HOLES " + FriendlyFace(face) + ": no main-part bolt groups classified to this face.");
                return 0;
            }

            // Read positions directly from the MODEL bolt groups in this view's
            // coordinate system. Do not rely on Drawing.Bolt visibility because a
            // bolt can be represented in more than one drawing view.
            var groups = GetProjectedHoleGroups(view, ids);
            if (groups.Count == 0)
            {
                messages?.Add("MAIN HOLES " + FriendlyFace(face) + ": classified groups exist but no projected bolt positions were read.");
                return 0;
            }

            var axes = GetProjectedAxes(analysis.MainPart, view.View);
            if (axes.MemberX.Length < 0.25)
            {
                messages?.Add("MAIN HOLES " + FriendlyFace(face) + ": member X axis collapses in owning view.");
                return 0;
            }

            var created = 0;

            // Common longitudinal chain for all holes on this fabrication face.
            // Start -> every hole station -> finish gives the required closing dim.
            var allPoints = groups.SelectMany(group => group.Points).ToList();
            var xStations = UniqueAlongAxis(allPoints, axes.MemberX);
            if (xStations.Count > 0)
            {
                var points = new List<Point>
                {
                    GetMainEndPoint(view.MainPartBounds, axes.MemberX, true)
                };
                points.AddRange(xStations);
                points.Add(GetMainEndPoint(view.MainPartBounds, axes.MemberX, false));

                var side = face == MainFace.BottomFlange
                    ? DimensionSide.Below
                    : DimensionSide.Above;

                created += CreateDimension(
                    view,
                    points,
                    axes.MemberX,
                    side,
                    DimensionLayout.GetBaseOffset(view));
            }

            // Local transverse definition per group.
            foreach (var group in groups)
            {
                var transverseAxis = face == MainFace.Web ? axes.MemberZ : axes.MemberY;
                if (transverseAxis.Length < 0.25)
                    continue;

                var stations = UniqueAlongAxis(group.Points, transverseAxis);
                if (stations.Count == 0)
                    continue;

                Point datum;
                DimensionSide side;

                if (face == MainFace.Web)
                {
                    // Web holes are located vertically from the nearest flange edge.
                    var average = stations.Average(point => Dot(point, transverseAxis));
                    var topPoint = new Point(view.MainPartBounds.CentreX, view.MainPartBounds.MaxY, 0.0);
                    var bottomPoint = new Point(view.MainPartBounds.CentreX, view.MainPartBounds.MinY, 0.0);
                    var top = Dot(topPoint, transverseAxis);
                    var bottom = Dot(bottomPoint, transverseAxis);
                    var useTop = Math.Abs(average - top) <= Math.Abs(average - bottom);

                    datum = GetWebFlangeDatum(view.MainPartBounds, transverseAxis, group, useTop);
                    side = DimensionSide.Left;
                }
                else
                {
                    // Flange holes use the member centreline for transverse gauge.
                    datum = GetCentreLineDatum(view.MainPartBounds, transverseAxis, group);
                    side = DimensionSide.Left;
                }

                var points = new List<Point> { datum };
                points.AddRange(stations);

                created += CreateDimension(
                    view,
                    points,
                    transverseAxis,
                    side,
                    DimensionLayout.GetLocalFeatureOffset(view));

                messages?.Add(
                    "MAIN HOLES " + FriendlyFace(face) +
                    ": group " + group.ModelIdentifierId +
                    " -> " + FriendlyViewName(view) +
                    "; " + group.Points.Count + " hole(s); longitudinal + transverse set-out created.");
            }

            return created;
        }

        private List<HoleGroup> GetProjectedHoleGroups(ViewAnalysis view, IEnumerable<int> ids)
        {
            var result = new List<HoleGroup>();
            var workPlane = _model.GetWorkPlaneHandler();
            var original = workPlane.GetCurrentTransformationPlane();

            try
            {
                workPlane.SetCurrentTransformationPlane(new TransformationPlane(view.View.DisplayCoordinateSystem));

                foreach (var id in ids.Distinct())
                {
                    var boltGroup = _model.SelectModelObject(new Identifier(id)) as BoltGroup;
                    if (boltGroup == null)
                        continue;

                    var group = new HoleGroup { ModelIdentifierId = id };
                    foreach (var item in boltGroup.BoltPositions)
                    {
                        var point = item as Point;
                        if (point != null)
                            group.Points.Add(new Point(point.X, point.Y, 0.0));
                    }

                    if (group.Points.Count > 0)
                        result.Add(group);
                }
            }
            finally
            {
                workPlane.SetCurrentTransformationPlane(original);
            }

            return result;
        }

        private Dictionary<int, MainFace> ClassifyMainPartBoltGroups(ModelPart mainPart, IList<string> messages)
        {
            var result = new Dictionary<int, MainFace>();
            var workPlane = _model.GetWorkPlaneHandler();
            var original = workPlane.GetCurrentTransformationPlane();

            try
            {
                // Make all coordinate systems/bolt positions deterministic regardless
                // of whatever work plane the user currently has active in Tekla.
                workPlane.SetCurrentTransformationPlane(new TransformationPlane());

                var mainCs = mainPart.GetCoordinateSystem();
                var mainOrigin = mainCs.Origin;
                var mainX = Normalize(new Vector(mainCs.AxisX));
                var mainY = Normalize(new Vector(mainCs.AxisY));
                var mainZ = Normalize(Cross(mainX, mainY));

                // Get the main member's local Z mid-plane for TOP/BOTTOM split.
                workPlane.SetCurrentTransformationPlane(new TransformationPlane(mainCs));
                var mainSolid = mainPart.GetSolid();
                var midZ = mainSolid == null
                    ? 0.0
                    : (mainSolid.MinimumPoint.Z + mainSolid.MaximumPoint.Z) * 0.5;

                workPlane.SetCurrentTransformationPlane(new TransformationPlane());

                var bolts = mainPart.GetBolts();
                while (bolts.MoveNext())
                {
                    var group = bolts.Current as BoltGroup;
                    if (group == null || result.ContainsKey(group.Identifier.ID))
                        continue;

                    var cs = group.GetCoordinateSystem();
                    var boltDirection = Normalize(Cross(new Vector(cs.AxisX), new Vector(cs.AxisY)));

                    var xAlignment = Math.Abs(Dot(boltDirection, mainX));
                    var yAlignment = Math.Abs(Dot(boltDirection, mainY));
                    var zAlignment = Math.Abs(Dot(boltDirection, mainZ));

                    MainFace face;
                    if (zAlignment >= FaceAlignmentTolerance && zAlignment >= yAlignment && zAlignment >= xAlignment)
                    {
                        // Bolt axis is through a flange. Split TOP/BOTTOM from actual
                        // bolt positions transformed into the main-part local Z axis.
                        var localZ = new List<double>();
                        foreach (var item in group.BoltPositions)
                        {
                            var point = item as Point;
                            if (point == null)
                                continue;

                            var relative = new Vector(
                                point.X - mainOrigin.X,
                                point.Y - mainOrigin.Y,
                                point.Z - mainOrigin.Z);
                            localZ.Add(Dot(relative, mainZ));
                        }

                        var averageZ = localZ.Count == 0 ? midZ : localZ.Average();
                        face = averageZ >= midZ ? MainFace.TopFlange : MainFace.BottomFlange;
                    }
                    else if (yAlignment >= FaceAlignmentTolerance && yAlignment >= xAlignment && yAlignment >= zAlignment)
                    {
                        // Bolt axis through the web thickness.
                        face = MainFace.Web;
                    }
                    else if (xAlignment >= FaceAlignmentTolerance)
                    {
                        face = MainFace.End;
                    }
                    else
                    {
                        face = MainFace.Unknown;
                    }

                    result[group.Identifier.ID] = face;
                    messages?.Add(
                        "MAIN HOLE ownership: group " + group.Identifier.ID +
                        " dir align X=" + xAlignment.ToString("0.###") +
                        " Y=" + yAlignment.ToString("0.###") +
                        " Z=" + zAlignment.ToString("0.###") +
                        " -> " + FriendlyFace(face) + ".");
                }
            }
            finally
            {
                workPlane.SetCurrentTransformationPlane(original);
            }

            return result;
        }

        private int DimensionLocalCuts(DrawingAnalysisResult analysis, IList<string> messages)
        {
            var cuts = GetBooleanCuts(analysis.MainPart);
            if (cuts.Count == 0)
                return 0;

            var candidateViews = analysis.Views
                .Where(view => view.View != null && view.MainPartBounds != null)
                .Where(view => !NameEquals(view, "A-A") && !NameEquals(view, "B-B"))
                .Where(view => !IsUnknownGeneratedView(view))
                .ToList();

            var created = 0;

            foreach (var cut in cuts)
            {
                var best = FindBestCutView(cut, candidateViews);
                if (best == null || best.View == null || best.CutBounds == null)
                    continue;

                var bounds = best.View.MainPartBounds;
                var cutBounds = best.CutBounds;
                var touchesLeft = Math.Abs(cutBounds.MinX - bounds.MinX) <= CutEdgeTolerance;
                var touchesRight = Math.Abs(cutBounds.MaxX - bounds.MaxX) <= CutEdgeTolerance;
                var touchesBottom = Math.Abs(cutBounds.MinY - bounds.MinY) <= CutEdgeTolerance;
                var touchesTop = Math.Abs(cutBounds.MaxY - bounds.MaxY) <= CutEdgeTolerance;

                if (!(touchesLeft || touchesRight || touchesBottom || touchesTop))
                    continue;

                var offset = DimensionLayout.GetLocalFeatureOffset(best.View);

                if ((touchesTop || touchesBottom) && cutBounds.Width > CoordinateTolerance)
                {
                    var y = touchesTop ? cutBounds.MinY : cutBounds.MaxY;
                    created += CreateRawDimension(
                        best.View.View,
                        new[]
                        {
                            new Point(cutBounds.MinX, y, 0.0),
                            new Point(cutBounds.MaxX, y, 0.0)
                        },
                        touchesTop ? new Vector(0.0, 1.0, 0.0) : new Vector(0.0, -1.0, 0.0),
                        offset);
                }

                if (touchesTop || touchesBottom)
                {
                    var x = Math.Abs(cutBounds.CentreX - bounds.MinX) <= Math.Abs(cutBounds.CentreX - bounds.MaxX)
                        ? cutBounds.MinX
                        : cutBounds.MaxX;
                    var outerY = touchesTop ? bounds.MaxY : bounds.MinY;
                    var innerY = touchesTop ? cutBounds.MinY : cutBounds.MaxY;

                    created += CreateRawDimension(
                        best.View.View,
                        new[] { new Point(x, outerY, 0.0), new Point(x, innerY, 0.0) },
                        x <= bounds.CentreX ? new Vector(-1.0, 0.0, 0.0) : new Vector(1.0, 0.0, 0.0),
                        offset);
                }

                if ((touchesLeft || touchesRight) && !touchesTop && !touchesBottom)
                {
                    var x = touchesLeft ? cutBounds.MaxX : cutBounds.MinX;
                    created += CreateRawDimension(
                        best.View.View,
                        new[]
                        {
                            new Point(x, cutBounds.MinY, 0.0),
                            new Point(x, cutBounds.MaxY, 0.0)
                        },
                        touchesLeft ? new Vector(-1.0, 0.0, 0.0) : new Vector(1.0, 0.0, 0.0),
                        offset);

                    var y = cutBounds.CentreY <= bounds.CentreY ? cutBounds.MinY : cutBounds.MaxY;
                    var outerX = touchesLeft ? bounds.MinX : bounds.MaxX;
                    var innerX = touchesLeft ? cutBounds.MaxX : cutBounds.MinX;
                    created += CreateRawDimension(
                        best.View.View,
                        new[] { new Point(outerX, y, 0.0), new Point(innerX, y, 0.0) },
                        y <= bounds.CentreY ? new Vector(0.0, -1.0, 0.0) : new Vector(0.0, 1.0, 0.0),
                        offset);
                }

                messages?.Add(
                    "MAIN CUT " + cut.Identifier.ID +
                    " -> " + FriendlyViewName(best.View) +
                    " local notch dimensions created.");
            }

            return created;
        }

        private List<BooleanPart> GetBooleanCuts(ModelPart mainPart)
        {
            var result = new List<BooleanPart>();
            var booleans = mainPart.GetBooleans();
            while (booleans != null && booleans.MoveNext())
            {
                var cut = booleans.Current as BooleanPart;
                if (cut != null && cut.Type == BooleanPart.BooleanTypeEnum.BOOLEAN_CUT && cut.OperativePart != null)
                    result.Add(cut);
            }
            return result;
        }

        private CutViewCandidate FindBestCutView(BooleanPart cut, IEnumerable<ViewAnalysis> views)
        {
            CutViewCandidate best = null;

            foreach (var view in views)
            {
                var operativeBounds = GetPartBoundsInView(cut.OperativePart, view.View);
                if (operativeBounds == null)
                    continue;

                var clipped = Intersect(operativeBounds, view.MainPartBounds);
                if (clipped == null || clipped.Width <= CoordinateTolerance || clipped.Height <= CoordinateTolerance)
                    continue;

                var touchesBoundary =
                    Math.Abs(clipped.MinX - view.MainPartBounds.MinX) <= CutEdgeTolerance ||
                    Math.Abs(clipped.MaxX - view.MainPartBounds.MaxX) <= CutEdgeTolerance ||
                    Math.Abs(clipped.MinY - view.MainPartBounds.MinY) <= CutEdgeTolerance ||
                    Math.Abs(clipped.MaxY - view.MainPartBounds.MaxY) <= CutEdgeTolerance;

                if (!touchesBoundary)
                    continue;

                var nx = clipped.Width / Math.Max(1.0, view.MainPartBounds.Width);
                var ny = clipped.Height / Math.Max(1.0, view.MainPartBounds.Height);
                var score = Math.Min(nx, ny) + Math.Max(nx, ny) * 0.15;

                if (best == null || score > best.Score)
                {
                    best = new CutViewCandidate
                    {
                        View = view,
                        CutBounds = clipped,
                        Score = score
                    };
                }
            }

            return best;
        }

        private ViewBounds GetPartBoundsInView(ModelPart part, DrawingView view)
        {
            if (part == null || view == null)
                return null;

            var workPlane = _model.GetWorkPlaneHandler();
            var original = workPlane.GetCurrentTransformationPlane();
            try
            {
                workPlane.SetCurrentTransformationPlane(new TransformationPlane(view.DisplayCoordinateSystem));
                var solid = part.GetSolid();
                if (solid == null)
                    return null;

                return new ViewBounds
                {
                    MinX = solid.MinimumPoint.X,
                    MaxX = solid.MaximumPoint.X,
                    MinY = solid.MinimumPoint.Y,
                    MaxY = solid.MaximumPoint.Y
                };
            }
            finally
            {
                workPlane.SetCurrentTransformationPlane(original);
            }
        }

        private static ViewBounds Intersect(ViewBounds a, ViewBounds b)
        {
            var minX = Math.Max(a.MinX, b.MinX);
            var maxX = Math.Min(a.MaxX, b.MaxX);
            var minY = Math.Max(a.MinY, b.MinY);
            var maxY = Math.Min(a.MaxY, b.MaxY);

            if (maxX <= minX || maxY <= minY)
                return null;

            return new ViewBounds { MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY };
        }

        private static ProjectedAxes GetProjectedAxes(ModelPart mainPart, DrawingView view)
        {
            var cs = mainPart.GetCoordinateSystem();
            var x = Normalize(new Vector(cs.AxisX));
            var y = Normalize(new Vector(cs.AxisY));
            var z = Normalize(Cross(x, y));

            return new ProjectedAxes(
                ProjectAxis(x, view.DisplayCoordinateSystem),
                ProjectAxis(y, view.DisplayCoordinateSystem),
                ProjectAxis(z, view.DisplayCoordinateSystem));
        }

        private static Axis2D ProjectAxis(Vector globalAxis, CoordinateSystem viewCs)
        {
            var x = Normalize(new Vector(viewCs.AxisX));
            var y = Normalize(new Vector(viewCs.AxisY));
            return new Axis2D(Dot(globalAxis, x), Dot(globalAxis, y));
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

        private static Point GetMainEndPoint(ViewBounds bounds, Axis2D axis, bool start)
        {
            var corners = GetCorners(bounds);
            return start
                ? corners.OrderBy(point => Dot(point, axis)).First()
                : corners.OrderByDescending(point => Dot(point, axis)).First();
        }

        private static Point GetCentreLineDatum(ViewBounds bounds, Axis2D axis, HoleGroup group)
        {
            var averageX = group.Points.Average(point => point.X);
            var averageY = group.Points.Average(point => point.Y);

            if (Math.Abs(axis.X) >= Math.Abs(axis.Y))
                return new Point(bounds.CentreX, averageY, 0.0);

            return new Point(averageX, bounds.CentreY, 0.0);
        }

        private static Point GetWebFlangeDatum(ViewBounds bounds, Axis2D axis, HoleGroup group, bool top)
        {
            var averageX = group.Points.Average(point => point.X);
            var averageY = group.Points.Average(point => point.Y);

            if (Math.Abs(axis.X) >= Math.Abs(axis.Y))
                return new Point(top ? bounds.MaxX : bounds.MinX, averageY, 0.0);

            return new Point(averageX, top ? bounds.MaxY : bounds.MinY, 0.0);
        }

        private static int CreateDimension(
            ViewAnalysis view,
            IEnumerable<Point> points,
            Axis2D measuredAxis,
            DimensionSide side,
            double offset)
        {
            return CreateRawDimension(view.View, points, GetDimensionDirection(measuredAxis, side), offset);
        }

        private static int CreateRawDimension(
            DrawingView view,
            IEnumerable<Point> points,
            Vector direction,
            double offset)
        {
            var unique = points.ToList();
            if (unique.Count < 2)
                return 0;

            var pointList = new PointList();
            foreach (var point in unique)
                pointList.Add(point);

            var attributes = new StraightDimensionSet.StraightDimensionSetAttributes(null, "standard");
            attributes.Placing.Placing = DimensionSetBaseAttributes.Placings.Free;

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view, pointList, direction, offset, attributes);
            return dimension == null ? 0 : 1;
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

        private static ViewAnalysis FindView(DrawingAnalysisResult analysis, string name)
        {
            return analysis.Views.FirstOrDefault(view => NameEquals(view, name));
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

        private static bool IsUnknownGeneratedView(ViewAnalysis view)
        {
            return view != null && view.View != null &&
                   (view.View.Name ?? string.Empty).StartsWith("AUTO -", StringComparison.OrdinalIgnoreCase);
        }

        private static bool NameEquals(ViewAnalysis view, string name)
        {
            return view != null && view.View != null &&
                   string.Equals(view.View.Name ?? string.Empty, name, StringComparison.OrdinalIgnoreCase);
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

        private static string FriendlyFace(MainFace face)
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

        private sealed class ProjectedAxes
        {
            public ProjectedAxes(Axis2D memberX, Axis2D memberY, Axis2D memberZ)
            {
                MemberX = memberX;
                MemberY = memberY;
                MemberZ = memberZ;
            }

            public Axis2D MemberX { get; }
            public Axis2D MemberY { get; }
            public Axis2D MemberZ { get; }
        }

        private sealed class CutViewCandidate
        {
            public ViewAnalysis View { get; set; }
            public ViewBounds CutBounds { get; set; }
            public double Score { get; set; }
        }
    }
}
