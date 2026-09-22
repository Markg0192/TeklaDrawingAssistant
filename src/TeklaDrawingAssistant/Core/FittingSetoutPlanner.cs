using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingBolt = Tekla.Structures.Drawing.Bolt;
using DrawingPart = Tekla.Structures.Drawing.Part;
using DrawingView = Tekla.Structures.Drawing.View;
using ModelBoltGroup = Tekla.Structures.Model.BoltGroup;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Plans fitting set-out before any dimensions are created.
    /// Every fitting must be located in two independent member axes.
    /// Hole centres are preferred whenever an attached bolt group is visible in the chosen view.
    /// The fitting's core/true-face view is strongly preferred over a section where the fitting
    /// is only seen edge-on.
    /// </summary>
    public sealed class FittingSetoutPlanner
    {
        private const double MinimumAxisProjection = 0.55;

        private readonly Model _model;

        public FittingSetoutPlanner(Model model)
        {
            _model = model;
        }

        public FittingSetoutPlan Build(DrawingAnalysisResult analysis)
        {
            if (analysis == null || analysis.MainPart == null)
                throw new InvalidOperationException("A resolved drawing and main part are required.");

            var plan = new FittingSetoutPlan();
            var mainAxes = GetMainAxes(analysis.MainPart);
            var secondaries = GetAssemblyParts(analysis.MainPart)
                .Where(part => part.Identifier.ID != analysis.MainPart.Identifier.ID)
                .ToList();

            foreach (var part in secondaries)
            {
                var item = BuildItem(analysis, part, mainAxes);
                plan.Fittings.Add(item);

                if (!item.IsComplete)
                {
                    plan.Warnings.Add(
                        "Part " + Describe(part) +
                        " is not fully set out in two independent directions. Review/create another view before dimensioning.");
                }
            }

            return plan;
        }

        private FittingSetoutItem BuildItem(
            DrawingAnalysisResult analysis,
            ModelPart part,
            MainAxes mainAxes)
        {
            var item = new FittingSetoutItem
            {
                PartId = part.Identifier.ID,
                Profile = part.Profile == null ? string.Empty : part.Profile.ProfileString
            };

            var normal = GetDominantFaceNormal(part);
            var requiredAxes = GetRequiredAxes(normal, mainAxes);
            var visibleViews = analysis.Views
                .Where(view => view.View != null && IsPartVisible(view.View, part.Identifier.ID))
                .ToList();

            item.CoreView = GetCoreViewName(visibleViews, normal);

            foreach (var axis in requiredAxes)
            {
                var requirement = SelectOwner(analysis, part, axis, normal, visibleViews);
                if (requirement != null)
                    item.Requirements.Add(requirement);
            }

            return item;
        }

        private FittingSetoutRequirement SelectOwner(
            DrawingAnalysisResult analysis,
            ModelPart part,
            AxisInfo axis,
            Vector fittingNormal,
            IList<ViewAnalysis> visibleViews)
        {
            ViewCandidate best = null;

            foreach (var view in visibleViews)
            {
                var candidate = ScoreView(analysis, part, axis, fittingNormal, view);
                if (candidate == null)
                    continue;

                if (best == null || candidate.Score > best.Score)
                    best = candidate;
            }

            if (best == null)
                return null;

            return new FittingSetoutRequirement
            {
                Axis = axis.Axis,
                AxisDescription = axis.Description,
                TargetType = best.HoleGroupId.HasValue
                    ? FittingSetoutTargetType.HoleGroup
                    : FittingSetoutTargetType.PartGeometry,
                HoleGroupId = best.HoleGroupId,
                ViewName = FriendlyViewName(best.View),
                ViewKind = best.View.Kind,
                AxisProjection = best.AxisProjection,
                CoreAlignment = best.CoreAlignment,
                SpaceScore = best.SpaceScore,
                Reason = BuildReason(axis, best)
            };
        }

        private ViewCandidate ScoreView(
            DrawingAnalysisResult analysis,
            ModelPart part,
            AxisInfo axis,
            Vector fittingNormal,
            ViewAnalysis view)
        {
            var viewNormal = GetViewNormal(view.View);
            var axisProjection = ProjectedMagnitude(axis.Vector, viewNormal);
            if (axisProjection < MinimumAxisProjection)
                return null;

            var coreAlignment = GeometryMath.AbsoluteDot(viewNormal, fittingNormal);
            var visibleBolt = GetVisibleBoltGroups(part, view.View).FirstOrDefault();
            var spaceScore = GetSpaceScore(analysis, view, axis.Axis);

            // Fabrication priority is deliberately ordered like this:
            // 1) the axis must read properly in the view;
            // 2) dimension the fitting in the view where most of its real face is visible;
            // 3) prefer useful face conventions (web/flange/end);
            // 4) use available paper space only as a tie-breaker.
            var score = axisProjection * 35.0;
            score += coreAlignment * 65.0;
            score += GetViewPolicyScore(axis.Axis, view.Kind);
            score += spaceScore * 0.25;

            if (coreAlignment >= 0.90)
                score += 30.0;

            if (visibleBolt != null)
                score += 35.0;

            return new ViewCandidate
            {
                View = view,
                HoleGroupId = visibleBolt == null ? (int?)null : visibleBolt.Identifier.ID,
                AxisProjection = axisProjection,
                CoreAlignment = coreAlignment,
                SpaceScore = spaceScore,
                Score = score
            };
        }

        private static double GetViewPolicyScore(FittingSetoutAxis axis, ViewKind kind)
        {
            // Fabrication preference:
            // X (along member) is clearest in a flange/top-bottom view when available.
            // Y is clearest in a flange or end view.
            // Z (member depth/vertical) is clearest in the web/base view, then an end section.
            switch (axis)
            {
                case FittingSetoutAxis.MemberX:
                    if (kind == ViewKind.Flange) return 30.0;
                    if (kind == ViewKind.Web) return 18.0;
                    return 0.0;

                case FittingSetoutAxis.MemberY:
                    if (kind == ViewKind.Flange) return 30.0;
                    if (kind == ViewKind.End) return 22.0;
                    return 0.0;

                case FittingSetoutAxis.MemberZ:
                    if (kind == ViewKind.Web) return 30.0;
                    if (kind == ViewKind.End) return 22.0;
                    return 0.0;

                default:
                    return 0.0;
            }
        }

        private static double GetSpaceScore(
            DrawingAnalysisResult analysis,
            ViewAnalysis view,
            FittingSetoutAxis axis)
        {
            if (analysis == null || analysis.Drawing == null || view == null || view.View == null)
                return 0.0;

            var sheet = analysis.Drawing.GetSheet();
            if (sheet == null || sheet.Width <= 0.0 || sheet.Height <= 0.0)
                return 0.0;

            var freePaper = axis == FittingSetoutAxis.MemberX
                ? Math.Max(0.0, sheet.Height - view.View.Height)
                : Math.Max(0.0, sheet.Width - view.View.Width);

            return Math.Min(20.0, freePaper / 15.0);
        }

        private static string BuildReason(AxisInfo axis, ViewCandidate candidate)
        {
            var target = candidate.HoleGroupId.HasValue
                ? "hole group " + candidate.HoleGroupId.Value
                : "fitting geometry";

            return axis.Description +
                   " set-out; use " + target +
                   "; axis projection=" + candidate.AxisProjection.ToString("0.00") +
                   "; core-face alignment=" + candidate.CoreAlignment.ToString("0.00") +
                   "; space=" + candidate.SpaceScore.ToString("0.0") + ".";
        }

        private static List<AxisInfo> GetRequiredAxes(Vector fittingNormal, MainAxes axes)
        {
            var candidates = new List<AxisInfo>
            {
                new AxisInfo(FittingSetoutAxis.MemberX, "LONGITUDINAL X (along member)", axes.X),
                new AxisInfo(FittingSetoutAxis.MemberY, "TRANSVERSE Y (across flange/member)", axes.Y),
                new AxisInfo(FittingSetoutAxis.MemberZ, "VERTICAL Z (member depth)", axes.Z)
            };

            return candidates
                .OrderBy(candidate => GeometryMath.AbsoluteDot(candidate.Vector, fittingNormal))
                .Take(2)
                .ToList();
        }

        private string GetCoreViewName(IList<ViewAnalysis> visibleViews, Vector fittingNormal)
        {
            var best = visibleViews
                .Select(view => new
                {
                    View = view,
                    Alignment = GeometryMath.AbsoluteDot(GetViewNormal(view.View), fittingNormal)
                })
                .OrderByDescending(item => item.Alignment)
                .FirstOrDefault();

            return best == null ? "<no visible view>" : FriendlyViewName(best.View);
        }

        private Vector GetDominantFaceNormal(ModelPart part)
        {
            var cs = part.GetCoordinateSystem();
            var x = Normalize(new Vector(cs.AxisX));
            var y = Normalize(new Vector(cs.AxisY));
            var z = Normalize(GeometryMath.Cross(x, y));

            if (part is ContourPlate)
                return z;

            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();
            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(cs));
                var solid = part.GetSolid();
                var dx = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
                var dy = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                var dz = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);

                if (dx <= dy && dx <= dz) return x;
                if (dy <= dx && dy <= dz) return y;
                return z;
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }
        }

        private static MainAxes GetMainAxes(ModelPart mainPart)
        {
            var cs = mainPart.GetCoordinateSystem();
            var x = Normalize(new Vector(cs.AxisX));
            var y = Normalize(new Vector(cs.AxisY));
            var z = Normalize(GeometryMath.Cross(x, y));
            return new MainAxes(x, y, z);
        }

        private static Vector GetViewNormal(DrawingView view)
        {
            var cs = view.DisplayCoordinateSystem;
            return Normalize(GeometryMath.Cross(new Vector(cs.AxisX), new Vector(cs.AxisY)));
        }

        private static double ProjectedMagnitude(Vector axis, Vector viewNormal)
        {
            var dot = GeometryMath.AbsoluteDot(axis, viewNormal);
            var value = 1.0 - dot * dot;
            return Math.Sqrt(Math.Max(0.0, value));
        }

        private static bool IsPartVisible(DrawingView view, int partId)
        {
            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
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

        private static List<ModelBoltGroup> GetVisibleBoltGroups(ModelPart part, DrawingView view)
        {
            var result = new List<ModelBoltGroup>();
            var seen = new HashSet<int>();
            var bolts = part.GetBolts();

            while (bolts.MoveNext())
            {
                var bolt = bolts.Current as ModelBoltGroup;
                if (bolt == null || !seen.Add(bolt.Identifier.ID))
                    continue;

                if (IsBoltVisible(view, bolt.Identifier.ID))
                    result.Add(bolt);
            }

            return result;
        }

        private static bool IsBoltVisible(DrawingView view, int boltId)
        {
            var bolts = view.GetObjects(new[] { typeof(DrawingBolt) });
            while (bolts.MoveNext())
            {
                var drawingBolt = bolts.Current as DrawingBolt;
                if (drawingBolt != null &&
                    drawingBolt.ModelIdentifier != null &&
                    drawingBolt.ModelIdentifier.ID == boltId)
                    return true;
            }

            return false;
        }

        private static List<ModelPart> GetAssemblyParts(ModelPart mainPart)
        {
            var result = new List<ModelPart> { mainPart };
            var assembly = mainPart.GetAssembly();
            if (assembly == null)
                return result;

            foreach (var item in assembly.GetSecondaries())
            {
                var part = item as ModelPart;
                if (part != null)
                    result.Add(part);
            }

            return result;
        }

        private static string FriendlyViewName(ViewAnalysis view)
        {
            if (view == null || view.View == null)
                return "<none>";

            var name = (view.View.Name ?? string.Empty).Trim();
            if (string.Equals(name, "TDA_TOP", StringComparison.OrdinalIgnoreCase))
                return "TOP flange";
            if (string.Equals(name, "TDA_BOTTOM", StringComparison.OrdinalIgnoreCase))
                return "BOTTOM flange";
            if (string.Equals(name, "A-A", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "B-B", StringComparison.OrdinalIgnoreCase))
                return name + " end section";

            if (!string.IsNullOrWhiteSpace(name))
                return name;

            switch (view.Kind)
            {
                case ViewKind.Web: return "BASE / WEB";
                case ViewKind.Flange: return "FLANGE";
                case ViewKind.End: return "END SECTION";
                default: return "UNNAMED VIEW";
            }
        }

        private static string Describe(ModelPart part)
        {
            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            return part.Identifier.ID + (string.IsNullOrWhiteSpace(profile) ? string.Empty : " (" + profile + ")");
        }

        private static Vector Normalize(Vector vector)
        {
            var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
            return length < 0.000001
                ? new Vector()
                : new Vector(vector.X / length, vector.Y / length, vector.Z / length);
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

        private sealed class AxisInfo
        {
            public AxisInfo(FittingSetoutAxis axis, string description, Vector vector)
            {
                Axis = axis;
                Description = description;
                Vector = vector;
            }

            public FittingSetoutAxis Axis { get; }
            public string Description { get; }
            public Vector Vector { get; }
        }

        private sealed class ViewCandidate
        {
            public ViewAnalysis View { get; set; }
            public int? HoleGroupId { get; set; }
            public double AxisProjection { get; set; }
            public double CoreAlignment { get; set; }
            public double SpaceScore { get; set; }
            public double Score { get; set; }
        }
    }
}
