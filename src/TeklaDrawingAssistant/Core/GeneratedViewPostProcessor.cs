using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using DrawingGrid = Tekla.Structures.Drawing.Grid;
using DrawingGridLine = Tekla.Structures.Drawing.GridLine;
using DrawingView = Tekla.Structures.Drawing.View;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Generated-view presentation pass.
    ///
    /// Important rules:
    /// - NO placement decisions are made before annotation generation;
    /// - generated views are kept at the retained main-view scale;
    /// - final orthographic alignment comes from shared MODEL geometry, not view boxes;
    /// - final paper-space bounding boxes are used only afterwards to close gaps without overlap.
    /// </summary>
    public sealed class GeneratedViewPostProcessor
    {
        private const double ViewGap = 4.0;
        private const double SheetMargin = 4.0;

        public void Apply(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.Drawing == null)
                return;

            var cleaned = CleanGeneratedFlangeGrids(analysis);
            var baseView = GetBaseView(analysis);

            if (baseView != null)
                MatchGeneratedViewScales(analysis, baseView, messages);

            analysis.Drawing.CommitChanges();

            if (cleaned > 0)
                messages?.Add("Post-process: removed/hid grids and grid lines from " + cleaned + " flange view(s).");

            messages?.Add("Post-process: no view placement performed before annotation; generated views only cleaned and scale-matched.");
        }

        public void FinaliseLayout(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.Drawing == null || analysis.MainPart == null)
                return;

            var baseView = GetBaseView(analysis);
            if (baseView == null || baseView.View == null)
                return;

            CleanGeneratedFlangeGrids(analysis);
            MatchGeneratedViewScales(analysis, baseView, messages);
            analysis.Drawing.CommitChanges();

            var a = FindView(analysis.Views, "A-A");
            var b = FindView(analysis.Views, "B-B");
            var top = FindView(analysis.Views, "TDA_TOP");
            var bottom = FindView(analysis.Views, "TDA_BOTTOM");

            // Orthographic alignment is based on the SAME physical model point in each view.
            // If the views are at the same scale, aligning one common model datum also aligns
            // every other point along their common projected axis: holes, plate faces, etc.
            var modelDatum = analysis.MainPart.GetCoordinateSystem().Origin;

            AlignHorizontalFromModelDatum(baseView, top, modelDatum);
            AlignHorizontalFromModelDatum(baseView, bottom, modelDatum);
            AlignVerticalFromModelDatum(baseView, a, modelDatum);
            AlignVerticalFromModelDatum(baseView, b, modelDatum);
            analysis.Drawing.CommitChanges();

            // Now that physical geometry is aligned, use the FINAL annotated view boxes only
            // to close the perpendicular gaps until the views are as close as possible without
            // overlapping. Do not disturb the orthographic alignment axis.
            var baseBox = GetFinalBox(baseView.View);
            var aBox = PackEndBesideBase(a, baseBox, true);
            var bBox = PackEndBesideBase(b, baseBox, false);

            var rowBoxes = new List<LayoutBox> { baseBox };
            if (aBox != null) rowBoxes.Add(aBox);
            if (bBox != null) rowBoxes.Add(bBox);

            var rowTop = rowBoxes.Max(box => box.MaxY);
            var rowBottom = rowBoxes.Min(box => box.MinY);

            PackFlangeRelativeToRow(top, rowTop, true);
            PackFlangeRelativeToRow(bottom, rowBottom, false);
            analysis.Drawing.CommitChanges();

            // Preserve all alignment/spacing and translate the completed cluster as one block
            // if it needs to be brought back onto the sheet.
            var arranged = new List<ViewAnalysis> { baseView };
            if (a != null) arranged.Add(a);
            if (b != null) arranged.Add(b);
            if (top != null) arranged.Add(top);
            if (bottom != null) arranged.Add(bottom);

            TranslateClusterOntoSheet(analysis.Drawing, arranged);
            analysis.Drawing.CommitChanges();

            messages?.Add(
                "Final layout: physical model datum used for orthographic alignment first; " +
                "TOP/BOTTOM keep the same longitudinal X as MAIN and A-A/B-B keep the same vertical geometry as MAIN. " +
                "Final annotated bounding boxes are then packed to " + ViewGap.ToString("0.#") + " mm without overlap.");
        }

        private static void AlignHorizontalFromModelDatum(
            ViewAnalysis source,
            ViewAnalysis target,
            Point modelDatum)
        {
            if (source == null || source.View == null || target == null || target.View == null)
                return;

            var sourcePoint = ModelPointOnSheet(source.View, modelDatum);
            var targetPoint = ModelPointOnSheet(target.View, modelDatum);
            Move(target.View, sourcePoint.X - targetPoint.X, 0.0);
        }

        private static void AlignVerticalFromModelDatum(
            ViewAnalysis source,
            ViewAnalysis target,
            Point modelDatum)
        {
            if (source == null || source.View == null || target == null || target.View == null)
                return;

            var sourcePoint = ModelPointOnSheet(source.View, modelDatum);
            var targetPoint = ModelPointOnSheet(target.View, modelDatum);
            Move(target.View, 0.0, sourcePoint.Y - targetPoint.Y);
        }

        private static Point ModelPointOnSheet(DrawingView view, Point modelPoint)
        {
            var cs = view.DisplayCoordinateSystem;
            var xAxis = Normalize(new Vector(cs.AxisX));
            var yAxis = Normalize(new Vector(cs.AxisY));
            var relative = new Vector(
                modelPoint.X - cs.Origin.X,
                modelPoint.Y - cs.Origin.Y,
                modelPoint.Z - cs.Origin.Z);

            var viewX = Dot(relative, xAxis);
            var viewY = Dot(relative, yAxis);
            var scale = GetScale(view);

            return new Point(
                view.Origin.X + viewX / scale,
                view.Origin.Y + viewY / scale,
                0.0);
        }

        private static LayoutBox PackEndBesideBase(ViewAnalysis section, LayoutBox baseBox, bool startEnd)
        {
            if (section == null || section.View == null || baseBox == null)
                return null;

            var box = GetFinalBox(section.View);
            if (box == null)
                return null;

            // Horizontal movement only: vertical geometry alignment was fixed from the model datum.
            var dx = startEnd
                ? baseBox.MinX - ViewGap - box.MaxX
                : baseBox.MaxX + ViewGap - box.MinX;

            Move(section.View, dx, 0.0);
            return box.Translate(dx, 0.0);
        }

        private static LayoutBox PackFlangeRelativeToRow(
            ViewAnalysis flange,
            double rowEdgeY,
            bool top)
        {
            if (flange == null || flange.View == null)
                return null;

            var box = GetFinalBox(flange.View);
            if (box == null)
                return null;

            // Vertical movement only: longitudinal physical alignment with MAIN was fixed first.
            var dy = top
                ? rowEdgeY + ViewGap - box.MinY
                : rowEdgeY - ViewGap - box.MaxY;

            Move(flange.View, 0.0, dy);
            return box.Translate(0.0, dy);
        }

        private static void TranslateClusterOntoSheet(Drawing drawing, IList<ViewAnalysis> views)
        {
            if (drawing == null || views == null || views.Count == 0)
                return;

            var boxes = views
                .Where(view => view != null && view.View != null)
                .Select(view => GetFinalBox(view.View))
                .Where(box => box != null)
                .ToList();

            if (boxes.Count == 0)
                return;

            var minX = boxes.Min(box => box.MinX);
            var maxX = boxes.Max(box => box.MaxX);
            var minY = boxes.Min(box => box.MinY);
            var maxY = boxes.Max(box => box.MaxY);

            var sheet = drawing.GetSheet();
            if (sheet == null || sheet.Width <= 0.0 || sheet.Height <= 0.0)
                return;

            var availableWidth = sheet.Width - 2.0 * SheetMargin;
            var availableHeight = sheet.Height - 2.0 * SheetMargin;
            var clusterWidth = maxX - minX;
            var clusterHeight = maxY - minY;

            double dx;
            double dy;

            if (clusterWidth <= availableWidth)
            {
                dx = 0.0;
                if (minX < SheetMargin)
                    dx = SheetMargin - minX;
                else if (maxX > sheet.Width - SheetMargin)
                    dx = sheet.Width - SheetMargin - maxX;
            }
            else
            {
                dx = sheet.Width * 0.5 - (minX + maxX) * 0.5;
            }

            if (clusterHeight <= availableHeight)
            {
                dy = 0.0;
                if (minY < SheetMargin)
                    dy = SheetMargin - minY;
                else if (maxY > sheet.Height - SheetMargin)
                    dy = sheet.Height - SheetMargin - maxY;
            }
            else
            {
                dy = sheet.Height * 0.5 - (minY + maxY) * 0.5;
            }

            if (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001)
                return;

            foreach (var view in views.Where(item => item != null && item.View != null))
                Move(view.View, dx, dy);
        }

        private static LayoutBox GetFinalBox(DrawingView view)
        {
            if (view == null)
                return null;

            var box = view.GetAxisAlignedBoundingBox();
            if (box == null)
                return null;

            return new LayoutBox(
                box.LowerLeft.X,
                box.LowerLeft.Y,
                box.UpperRight.X,
                box.UpperRight.Y);
        }

        private static void Move(DrawingView view, double dx, double dy)
        {
            if (view == null || (Math.Abs(dx) < 0.001 && Math.Abs(dy) < 0.001))
                return;

            view.Origin = new Point(view.Origin.X + dx, view.Origin.Y + dy, 0.0);
            view.Modify();
        }

        private static void MatchGeneratedViewScales(
            DrawingAnalysisResult analysis,
            ViewAnalysis baseView,
            IList<string> messages)
        {
            if (baseView == null || baseView.View == null || baseView.View.Attributes == null)
                return;

            var scale = baseView.View.Attributes.Scale;
            if (scale <= 0.0)
                return;

            foreach (var view in analysis.Views)
            {
                if (view.View == null || view.View.Attributes == null || !IsGenerated(view.View))
                    continue;

                if (Math.Abs(view.View.Attributes.Scale - scale) <= 0.0001)
                    continue;

                var old = view.View.Attributes.Scale;
                view.View.Attributes.Scale = scale;
                view.View.Modify();

                messages?.Add(
                    "Scale corrected: " + FriendlyName(view.View) +
                    " " + old.ToString("0.###") + " -> " + scale.ToString("0.###") +
                    " to match retained main view.");
            }
        }

        private static int CleanGeneratedFlangeGrids(DrawingAnalysisResult analysis)
        {
            var cleaned = 0;
            foreach (var view in analysis.Views)
            {
                if (view.View == null)
                    continue;

                var name = (view.View.Name ?? string.Empty).Trim();
                if (!name.Equals("TDA_TOP", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("TDA_BOTTOM", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (RemoveGrids(view.View))
                    cleaned++;
            }

            return cleaned;
        }

        private static bool RemoveGrids(DrawingView view)
        {
            var changed = false;

            var directLines = view.GetObjects(new[] { typeof(DrawingGridLine) });
            while (directLines.MoveNext())
            {
                var line = directLines.Current as DrawingGridLine;
                if (line == null)
                    continue;

                if (line.Hideable != null)
                    line.Hideable.HideFromDrawingView();

                changed = true;
            }

            var grids = view.GetObjects(new[] { typeof(DrawingGrid) });
            var delete = new List<DrawingGrid>();
            while (grids.MoveNext())
            {
                var grid = grids.Current as DrawingGrid;
                if (grid != null)
                    delete.Add(grid);
            }

            foreach (var grid in delete)
            {
                var lines = grid.GetObjects();
                while (lines.MoveNext())
                {
                    var line = lines.Current as DrawingGridLine;
                    if (line != null && line.Hideable != null)
                        line.Hideable.HideFromDrawingView();
                }

                if (!grid.Delete() && grid.Hideable != null)
                    grid.Hideable.HideFromDrawingView();

                changed = true;
            }

            if (changed)
                view.Modify();

            return changed;
        }

        private static ViewAnalysis GetBaseView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(view => view.View != null && view.ContainsMainPart && view.MainPartBounds != null)
                .Where(view => !IsGenerated(view.View))
                .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                .FirstOrDefault()
                ?? analysis.Views
                    .Where(view => view.View != null && view.ContainsMainPart && view.MainPartBounds != null)
                    .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                    .FirstOrDefault();
        }

        private static ViewAnalysis FindView(IEnumerable<ViewAnalysis> views, string name)
        {
            return views.FirstOrDefault(view =>
                view.View != null &&
                string.Equals(view.View.Name ?? string.Empty, name, StringComparison.OrdinalIgnoreCase));
        }

        private static double GetScale(DrawingView view)
        {
            return view != null && view.Attributes != null && view.Attributes.Scale > 0.0
                ? view.Attributes.Scale
                : 1.0;
        }

        private static bool IsGenerated(DrawingView view)
        {
            var name = (view == null ? string.Empty : view.Name ?? string.Empty).Trim().ToUpperInvariant();
            return name.StartsWith("TDA_") || name == "A-A" || name == "B-B";
        }

        private static string FriendlyName(DrawingView view)
        {
            if (view == null)
                return "<null>";

            var name = (view.Name ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(name) ? "unnamed generated view" : name;
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

        private sealed class LayoutBox
        {
            public LayoutBox(double minX, double minY, double maxX, double maxY)
            {
                MinX = Math.Min(minX, maxX);
                MinY = Math.Min(minY, maxY);
                MaxX = Math.Max(minX, maxX);
                MaxY = Math.Max(minY, maxY);
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
            public double CentreX => (MinX + MaxX) * 0.5;
            public double CentreY => (MinY + MaxY) * 0.5;

            public LayoutBox Translate(double dx, double dy)
            {
                return new LayoutBox(MinX + dx, MinY + dy, MaxX + dx, MaxY + dy);
            }
        }
    }
}
