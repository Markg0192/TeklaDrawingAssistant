using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
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
    /// Builds only the extra flange views. End sections are deliberately handled by
    /// OutsideInEndSectionBuilder so there is one authority for end-view direction/cuts.
    /// </summary>
    public sealed class FlangeOnlyViewCreationPlanner
    {
        private const double ParallelTolerance = 0.90;
        private const double ViewGap = 12.0;
        private readonly Model _model;

        public FlangeOnlyViewCreationPlanner(Model model)
        {
            _model = model;
        }

        public int RebuildFlangeViews(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null)
                throw new InvalidOperationException("A resolved assembly main part is required.");

            var source = GetSourceView(analysis);
            if (source == null)
            {
                messages?.Add("No usable base view containing the main part was found.");
                return 0;
            }

            messages?.Add("Base view retained: " + DisplayName(source.View) + ".");

            var removed = RemoveAllOtherViews(analysis, source.View);
            RemoveExistingSectionMarks(source.View);
            analysis.Drawing.CommitChanges();

            if (removed > 0)
                messages?.Add("Removed " + removed + " non-base view(s) before rebuilding.");

            var requirements = AnalyseRequirements(analysis.MainPart);
            messages?.Add("Flange view requirements:");
            messages?.Add("  TOP flange features: " + requirements.Top);
            messages?.Add("  BOTTOM flange features: " + requirements.Bottom);
            messages?.Add("  WEB/end/custom features left to base/end logic: " + requirements.Other);

            var assemblyParts = GetAssemblyParts(analysis.MainPart);
            var partIds = new HashSet<int>(assemblyParts.Select(p => p.Identifier.ID));
            var boltIds = GetAssemblyBoltIds(assemblyParts);
            var created = 0;

            if (requirements.Top > 0 && CreateFlangeView(analysis, source, true, partIds, boltIds, messages) != null)
                created++;

            if (requirements.Bottom > 0 && CreateFlangeView(analysis, source, false, partIds, boltIds, messages) != null)
                created++;

            analysis.Drawing.CommitChanges();
            return created;
        }

        private DrawingView CreateFlangeView(
            DrawingAnalysisResult analysis,
            ViewAnalysis source,
            bool top,
            ISet<int> partIds,
            ISet<int> boltIds,
            IList<string> messages)
        {
            var attributes = CreateAttributes(source.View);
            var insertion = new Point(
                source.View.Origin.X,
                source.View.Origin.Y + (top ? 1.0 : -1.0) * (source.View.Height + 25.0),
                0.0);

            DrawingView view;
            var ok = top
                ? DrawingView.CreateTopView(analysis.Drawing, insertion, attributes, out view)
                : DrawingView.CreateBottomView(analysis.Drawing, insertion, attributes, out view);

            if (!ok || view == null)
            {
                messages?.Add("Could not create the " + (top ? "TOP" : "BOTTOM") + " flange view.");
                return null;
            }

            view.Name = top ? "TDA_TOP" : "TDA_BOTTOM";
            view.Modify();
            analysis.Drawing.CommitChanges();

            Clean(view, partIds, boltIds);
            PlaceFlangeView(analysis.Drawing, source.View, view, top);
            view.Modify();
            analysis.Drawing.CommitChanges();

            messages?.Add("Created " + (top ? "TOP" : "BOTTOM") + " flange view with no visible name, grids, marks or neighbouring steel.");
            return view;
        }

        private FlangeRequirements AnalyseRequirements(ModelPart mainPart)
        {
            var result = new FlangeRequirements();
            var original = _model.GetWorkPlaneHandler().GetCurrentTransformationPlane();

            try
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane());
                var cs = mainPart.GetCoordinateSystem();
                var axisX = Normalize(new Vector(cs.AxisX));
                var webNormal = Normalize(new Vector(cs.AxisY));
                var topNormal = Normalize(GeometryMath.Cross(axisX, webNormal));
                if (topNormal.Z < 0.0)
                    topNormal = Multiply(topNormal, -1.0);

                var mainCentre = GetSolidCentre(mainPart);
                var parts = GetAssemblyParts(mainPart);

                foreach (var part in parts)
                {
                    if (part.Identifier.ID == mainPart.Identifier.ID)
                        continue;

                    CountFace(GetDominantFaceNormal(part), GetSolidCentre(part), mainCentre, axisX, webNormal, topNormal, result);
                }

                var seen = new HashSet<int>();
                foreach (var part in parts)
                {
                    var bolts = part.GetBolts();
                    while (bolts.MoveNext())
                    {
                        var bolt = bolts.Current as ModelBoltGroup;
                        if (bolt == null || !seen.Add(bolt.Identifier.ID))
                            continue;

                        var boltCs = bolt.GetCoordinateSystem();
                        var normal = Normalize(GeometryMath.Cross(new Vector(boltCs.AxisX), new Vector(boltCs.AxisY)));
                        CountFace(normal, GetBoltCentre(bolt), mainCentre, axisX, webNormal, topNormal, result);
                    }
                }
            }
            finally
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(original);
            }

            return result;
        }

        private static void CountFace(
            Vector normal,
            Point centre,
            Point mainCentre,
            Vector axisX,
            Vector webNormal,
            Vector topNormal,
            FlangeRequirements result)
        {
            normal = Normalize(normal);
            var along = GeometryMath.AbsoluteDot(normal, axisX);
            var web = GeometryMath.AbsoluteDot(normal, webNormal);
            var flange = GeometryMath.AbsoluteDot(normal, topNormal);

            if (flange >= ParallelTolerance && flange >= along && flange >= web)
            {
                var fromMain = new Vector(centre.X - mainCentre.X, centre.Y - mainCentre.Y, centre.Z - mainCentre.Z);
                if (Dot(fromMain, topNormal) >= 0.0)
                    result.Top++;
                else
                    result.Bottom++;
                return;
            }

            result.Other++;
        }

        private Vector GetDominantFaceNormal(ModelPart part)
        {
            var cs = part.GetCoordinateSystem();
            var x = Normalize(new Vector(cs.AxisX));
            var y = Normalize(new Vector(cs.AxisY));
            var z = Normalize(GeometryMath.Cross(x, y));

            if (part is ContourPlate)
                return z;

            var original = _model.GetWorkPlaneHandler().GetCurrentTransformationPlane();
            try
            {
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(new TransformationPlane(cs));
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
                _model.GetWorkPlaneHandler().SetCurrentTransformationPlane(original);
            }
        }

        private static DrawingView.ViewAttributes CreateAttributes(DrawingView source)
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
            attributes.TagsAttributes = new DrawingView.ViewMarkTagsAttributes();
            return attributes;
        }

        private static void Clean(DrawingView view, ISet<int> partIds, ISet<int> boltIds)
        {
            var grids = view.GetObjects(new[] { typeof(DrawingGrid) });
            while (grids.MoveNext())
            {
                var grid = grids.Current as DrawingGrid;
                if (grid == null) continue;
                if (grid.Hideable != null) grid.Hideable.HideFromDrawingView();
                var lines = grid.GetObjects();
                while (lines.MoveNext())
                {
                    var line = lines.Current as DrawingGridLine;
                    if (line != null && line.Hideable != null)
                        line.Hideable.HideFromDrawingView();
                }
            }

            var parts = view.GetObjects(new[] { typeof(DrawingPart) });
            while (parts.MoveNext())
            {
                var part = parts.Current as DrawingPart;
                if (part == null || part.ModelIdentifier == null || partIds.Contains(part.ModelIdentifier.ID)) continue;
                if (part.Hideable != null) part.Hideable.HideFromDrawingView();
            }

            var bolts = view.GetObjects(new[] { typeof(DrawingBolt) });
            while (bolts.MoveNext())
            {
                var bolt = bolts.Current as DrawingBolt;
                if (bolt == null || bolt.ModelIdentifier == null || boltIds.Contains(bolt.ModelIdentifier.ID)) continue;
                if (bolt.Hideable != null) bolt.Hideable.HideFromDrawingView();
            }

            var marks = view.GetObjects(new[] { typeof(DrawingMark), typeof(DrawingWeldMark) });
            var delete = new List<DrawingObject>();
            while (marks.MoveNext())
            {
                var item = marks.Current as DrawingObject;
                if (item != null) delete.Add(item);
            }
            foreach (var item in delete) item.Delete();
        }

        private static void PlaceFlangeView(Drawing drawing, DrawingView source, DrawingView view, bool top)
        {
            var vertical = source.Height * 0.5 + view.Height * 0.5 + ViewGap;
            var desired = new Point(source.Origin.X, source.Origin.Y + (top ? vertical : -vertical), 0.0);
            var sheet = drawing.GetSheet();
            if (sheet != null && sheet.Width > 0.0 && sheet.Height > 0.0)
            {
                var hw = view.Width * 0.5 + ViewGap;
                var hh = view.Height * 0.5 + ViewGap;
                desired.X = Math.Max(hw, Math.Min(sheet.Width - hw, desired.X));
                desired.Y = Math.Max(hh, Math.Min(sheet.Height - hh, desired.Y));
            }
            view.Origin = desired;
        }

        private static int RemoveAllOtherViews(DrawingAnalysisResult analysis, DrawingView source)
        {
            var removed = 0;
            foreach (var item in analysis.Views)
            {
                if (item.View == null || ReferenceEquals(item.View, source)) continue;
                if (item.View.Delete()) removed++;
            }
            return removed;
        }

        private static void RemoveExistingSectionMarks(DrawingView source)
        {
            var marks = source.GetObjects(new[] { typeof(SectionMark) });
            var delete = new List<SectionMark>();
            while (marks.MoveNext())
            {
                var mark = marks.Current as SectionMark;
                if (mark != null) delete.Add(mark);
            }
            foreach (var mark in delete) mark.Delete();
        }

        private static ViewAnalysis GetSourceView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(v => v.View != null && v.ContainsMainPart && v.MainPartBounds != null)
                .Where(v => !IsGenerated(v.View))
                .OrderByDescending(v => Math.Abs(v.MainPartBounds.Width * v.MainPartBounds.Height))
                .FirstOrDefault()
                ?? analysis.Views
                    .Where(v => v.View != null && v.ContainsMainPart && v.MainPartBounds != null)
                    .OrderByDescending(v => Math.Abs(v.MainPartBounds.Width * v.MainPartBounds.Height))
                    .FirstOrDefault();
        }

        private static bool IsGenerated(DrawingView view)
        {
            var name = (view == null ? string.Empty : view.Name ?? string.Empty).Trim().ToUpperInvariant();
            return name.StartsWith("TDA_") || name.StartsWith("AUTO -");
        }

        private static List<ModelPart> GetAssemblyParts(ModelPart mainPart)
        {
            var result = new List<ModelPart> { mainPart };
            var assembly = mainPart.GetAssembly();
            if (assembly == null) return result;
            foreach (var item in assembly.GetSecondaries())
            {
                var part = item as ModelPart;
                if (part != null) result.Add(part);
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
                    if (bolt != null) result.Add(bolt.Identifier.ID);
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

        private static Point GetBoltCentre(ModelBoltGroup bolt)
        {
            var points = new List<Point>();
            foreach (var item in bolt.BoltPositions)
            {
                var point = item as Point;
                if (point != null) points.Add(point);
            }
            if (points.Count == 0) return new Point(bolt.GetCoordinateSystem().Origin);
            return new Point(points.Average(p => p.X), points.Average(p => p.Y), points.Average(p => p.Z));
        }

        private static string DisplayName(DrawingView view)
        {
            return view == null || string.IsNullOrWhiteSpace(view.Name) ? "unnamed original view" : view.Name;
        }

        private static Vector Normalize(Vector vector)
        {
            var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
            return length < 0.000001 ? new Vector() : new Vector(vector.X / length, vector.Y / length, vector.Z / length);
        }

        private static double Dot(Vector a, Vector b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Vector Multiply(Vector v, double f)
        {
            return new Vector(v.X * f, v.Y * f, v.Z * f);
        }

        private sealed class FlangeRequirements
        {
            public int Top { get; set; }
            public int Bottom { get; set; }
            public int Other { get; set; }
        }
    }
}
