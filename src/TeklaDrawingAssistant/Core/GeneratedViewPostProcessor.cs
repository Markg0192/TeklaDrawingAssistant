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
    /// Important rule: NO layout decisions are made before annotation generation.
    /// Apply() only cleans generated views and matches their scale to the retained main view.
    /// FinaliseLayout() runs after dimensions/marks etc. and uses each view's final paper-space
    /// bounding box, including annotations, to pack the drawing without overlaps.
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
            if (analysis == null || analysis.Drawing == null)
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

            // 1. Build the horizontal row first: A-A | MAIN | B-B.
            // End sections share the main view's vertical centre and are packed by the
            // FINAL view bounding boxes, not by steel extents or guessed view width.
            var baseBox = GetFinalBox(baseView.View);
            var aBox = PlaceEndBesideBase(a, baseBox, true);
            var bBox = PlaceEndBesideBase(b, baseBox, false);

            // 2. The top/bottom flange views share the main view's horizontal centre.
            // Put them above/below the whole end/main/end row so no corner can overlap.
            var rowBoxes = new List<LayoutBox> { baseBox };
            if (aBox != null) rowBoxes.Add(aBox);
            if (bBox != null) rowBoxes.Add(bBox);

            var rowTop = rowBoxes.Max(box => box.MaxY);
            var rowBottom = rowBoxes.Min(box => box.MinY);
            var mainCentreX = baseBox.CentreX;

            var topBox = PlaceFlangeRelativeToRow(top, mainCentreX, rowTop, true);
            var bottomBox = PlaceFlangeRelativeToRow(bottom, mainCentreX, rowBottom, false);

            analysis.Drawing.CommitChanges();

            // 3. Preserve all relative spacing and, if needed, translate the complete
            // five-view cluster onto the sheet as one block. We never clamp one view on
            // its own because that was the cause of A-A overlap / B-B drifting away.
            var arranged = new List<ViewAnalysis> { baseView };
            if (a != null) arranged.Add(a);
            if (b != null) arranged.Add(b);
            if (top != null) arranged.Add(top);
            if (bottom != null) arranged.Add(bottom);

            TranslateClusterOntoSheet(analysis.Drawing, arranged);
            analysis.Drawing.CommitChanges();

            messages?.Add(
                "Final layout: used final paper-space view bounding boxes after annotation. " +
                "A-A/MAIN/B-B aligned vertically; TOP/BOTTOM aligned horizontally; " +
                "bounding boxes packed to " + ViewGap.ToString("0.#") + " mm without overlap.");
        }

        private static LayoutBox PlaceEndBesideBase(ViewAnalysis section, LayoutBox baseBox, bool startEnd)
        {
            if (section == null || section.View == null || baseBox == null)
                return null;

            var box = GetFinalBox(section.View);
            if (box == null)
                return null;

            var targetCentreY = baseBox.CentreY;
            var dx = startEnd
                ? baseBox.MinX - ViewGap - box.MaxX
                : baseBox.MaxX + ViewGap - box.MinX;
            var dy = targetCentreY - box.CentreY;

            Move(section.View, dx, dy);
            return box.Translate(dx, dy);
        }

        private static LayoutBox PlaceFlangeRelativeToRow(
            ViewAnalysis flange,
            double mainCentreX,
            double rowEdgeY,
            bool top)
        {
            if (flange == null || flange.View == null)
                return null;

            var box = GetFinalBox(flange.View);
            if (box == null)
                return null;

            var dx = mainCentreX - box.CentreX;
            var dy = top
                ? rowEdgeY + ViewGap - box.MinY
                : rowEdgeY - ViewGap - box.MaxY;

            Move(flange.View, dx, dy);
            return box.Translate(dx, dy);
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

            // Tekla returns the view's size in PAPER coordinates and includes the
            // annotation-expanded view frame. This is exactly what final layout needs.
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
