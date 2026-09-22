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
    public sealed class GeneratedViewPostProcessor
    {
        private const double PreDimensionEndGap = 4.0;
        private const double FinalFlangeGap = 58.0;
        private const double FinalEndGap = 8.0;
        private const double SheetMargin = 4.0;

        public void Apply(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.Drawing == null)
                return;

            var cleaned = CleanGeneratedFlangeGrids(analysis);
            var baseView = GetBaseView(analysis);

            if (baseView != null)
            {
                MatchGeneratedViewScales(analysis, baseView, messages);
                MoveEndViewOutward(analysis.Drawing, analysis.Views, "A-A", true, PreDimensionEndGap);
                MoveEndViewOutward(analysis.Drawing, analysis.Views, "B-B", false, PreDimensionEndGap);
            }

            analysis.Drawing.CommitChanges();

            if (cleaned > 0)
                messages?.Add("Post-process: removed/hid grids and grid lines from " + cleaned + " flange view(s).");

            messages?.Add("Post-process: generated top/bottom/end views forced to retained main-view scale.");
        }

        public void FinaliseLayout(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.Drawing == null)
                return;

            var baseView = GetBaseView(analysis);
            if (baseView == null)
                return;

            CleanGeneratedFlangeGrids(analysis);
            MatchGeneratedViewScales(analysis, baseView, messages);

            PlaceFlangeView(analysis.Drawing, baseView, FindView(analysis.Views, "TDA_TOP"), true);
            PlaceFlangeView(analysis.Drawing, baseView, FindView(analysis.Views, "TDA_BOTTOM"), false);
            PlaceEndView(analysis.Drawing, baseView, FindView(analysis.Views, "A-A"), true);
            PlaceEndView(analysis.Drawing, baseView, FindView(analysis.Views, "B-B"), false);

            analysis.Drawing.CommitChanges();
            messages?.Add("Final layout: end sections positioned from their actual steel extents, immediately beside the source member ends.");
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

        private static void PlaceFlangeView(
            Drawing drawing,
            ViewAnalysis baseView,
            ViewAnalysis generated,
            bool top)
        {
            if (drawing == null || baseView == null || baseView.View == null ||
                generated == null || generated.View == null)
                return;

            var desired = new Point(
                baseView.View.Origin.X,
                baseView.View.Origin.Y + (top ? 1.0 : -1.0) *
                (baseView.View.Height * 0.5 + generated.View.Height * 0.5 + FinalFlangeGap),
                0.0);

            generated.View.Origin = ClampToSheet(drawing, generated.View, desired);
            generated.View.Modify();
        }

        private static void PlaceEndView(
            Drawing drawing,
            ViewAnalysis baseView,
            ViewAnalysis section,
            bool startEnd)
        {
            if (drawing == null || baseView == null || baseView.View == null ||
                baseView.MainPartBounds == null || section == null || section.View == null ||
                section.MainPartBounds == null)
                return;

            var baseScale = GetScale(baseView.View);
            var sectionScale = GetScale(section.View);
            var baseBounds = baseView.MainPartBounds;
            var sectionBounds = section.MainPartBounds;
            var horizontal = Math.Abs(baseBounds.Width) >= Math.Abs(baseBounds.Height);
            Point desired;

            if (horizontal)
            {
                var baseEndOnSheet = baseView.View.Origin.X +
                    (startEnd ? baseBounds.MinX : baseBounds.MaxX) / baseScale;
                var baseCentreOnSheet = baseView.View.Origin.Y + baseBounds.CentreY / baseScale;

                // Position from the SECTION STEEL, not View.Width. Dimension strings can
                // make the view frame huge and were previously pushing B-B far away while
                // sheet clamping dragged A-A back over the main view.
                var desiredX = startEnd
                    ? baseEndOnSheet - FinalEndGap - sectionBounds.MaxX / sectionScale
                    : baseEndOnSheet + FinalEndGap - sectionBounds.MinX / sectionScale;
                var desiredY = baseCentreOnSheet - sectionBounds.CentreY / sectionScale;

                desired = new Point(desiredX, desiredY, 0.0);
            }
            else
            {
                var baseEndOnSheet = baseView.View.Origin.Y +
                    (startEnd ? baseBounds.MinY : baseBounds.MaxY) / baseScale;
                var baseCentreOnSheet = baseView.View.Origin.X + baseBounds.CentreX / baseScale;

                var desiredY = startEnd
                    ? baseEndOnSheet - FinalEndGap - sectionBounds.MaxY / sectionScale
                    : baseEndOnSheet + FinalEndGap - sectionBounds.MinY / sectionScale;
                var desiredX = baseCentreOnSheet - sectionBounds.CentreX / sectionScale;

                desired = new Point(desiredX, desiredY, 0.0);
            }

            section.View.Origin = ClampSteelToSheet(drawing, section, desired);
            section.View.Modify();
        }

        private static void MoveEndViewOutward(
            Drawing drawing,
            IEnumerable<ViewAnalysis> views,
            string sectionName,
            bool startEnd,
            double distance)
        {
            var section = views.FirstOrDefault(view =>
                view.View != null &&
                string.Equals(view.View.Name ?? string.Empty, sectionName, StringComparison.OrdinalIgnoreCase));

            if (section == null || section.View == null)
                return;

            var desired = new Point(
                section.View.Origin.X + (startEnd ? -distance : distance),
                section.View.Origin.Y,
                0.0);

            section.View.Origin = ClampToSheet(drawing, section.View, desired);
            section.View.Modify();
        }

        private static Point ClampSteelToSheet(Drawing drawing, ViewAnalysis view, Point desired)
        {
            var sheet = drawing.GetSheet();
            if (sheet == null || sheet.Width <= 0.0 || sheet.Height <= 0.0 ||
                view == null || view.View == null || view.MainPartBounds == null)
                return desired;

            var scale = GetScale(view.View);
            var bounds = view.MainPartBounds;

            var steelMinX = desired.X + bounds.MinX / scale;
            var steelMaxX = desired.X + bounds.MaxX / scale;
            var steelMinY = desired.Y + bounds.MinY / scale;
            var steelMaxY = desired.Y + bounds.MaxY / scale;

            if (steelMinX < SheetMargin)
                desired.X += SheetMargin - steelMinX;
            else if (steelMaxX > sheet.Width - SheetMargin)
                desired.X -= steelMaxX - (sheet.Width - SheetMargin);

            if (steelMinY < SheetMargin)
                desired.Y += SheetMargin - steelMinY;
            else if (steelMaxY > sheet.Height - SheetMargin)
                desired.Y -= steelMaxY - (sheet.Height - SheetMargin);

            return desired;
        }

        private static Point ClampToSheet(Drawing drawing, DrawingView view, Point desired)
        {
            var sheet = drawing.GetSheet();
            if (sheet == null || sheet.Width <= 0.0 || sheet.Height <= 0.0)
                return desired;

            var halfWidth = view.Width * 0.5 + SheetMargin;
            var halfHeight = view.Height * 0.5 + SheetMargin;

            desired.X = Math.Max(halfWidth, Math.Min(sheet.Width - halfWidth, desired.X));
            desired.Y = Math.Max(halfHeight, Math.Min(sheet.Height - halfHeight, desired.Y));
            return desired;
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
    }
}
