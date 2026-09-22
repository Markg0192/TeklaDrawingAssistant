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
    /// Small deterministic cleanup pass applied after view creation.
    /// Keeps view generation logic focused on geometry while this class handles
    /// generated-view presentation needed before dimensions are added.
    /// </summary>
    public sealed class GeneratedViewPostProcessor
    {
        private const double ExtraEndGap = 8.0;

        public void Apply(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.Drawing == null)
                return;

            var removedGridViews = 0;

            foreach (var view in analysis.Views)
            {
                if (view.View == null)
                    continue;

                var name = (view.View.Name ?? string.Empty).Trim();
                if (name.Equals("TDA_TOP", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("TDA_BOTTOM", StringComparison.OrdinalIgnoreCase))
                {
                    if (RemoveGrids(view.View))
                        removedGridViews++;
                }
            }

            var baseView = analysis.Views
                .Where(view => view.View != null && view.ContainsMainPart && view.MainPartBounds != null)
                .Where(view => !IsGenerated(view.View))
                .OrderByDescending(view => Math.Abs(view.MainPartBounds.Width * view.MainPartBounds.Height))
                .FirstOrDefault();

            if (baseView != null)
            {
                MoveEndViewOutward(analysis.Drawing, analysis.Views, baseView, "A-A", true);
                MoveEndViewOutward(analysis.Drawing, analysis.Views, baseView, "B-B", false);
            }

            analysis.Drawing.CommitChanges();

            if (removedGridViews > 0)
                messages?.Add("Post-process: removed grids from " + removedGridViews + " flange view(s).");

            messages?.Add("Post-process: added a little extra paper-space clearance to the end views for dimensions.");
        }

        private static bool RemoveGrids(DrawingView view)
        {
            var changed = false;
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

                // A drawing-grid object is only the representation in this drawing view;
                // deleting it does not delete the model grid. If Tekla refuses the delete,
                // hide it as a fallback.
                if (!grid.Delete() && grid.Hideable != null)
                    grid.Hideable.HideFromDrawingView();

                changed = true;
            }

            if (changed)
                view.Modify();

            return changed;
        }

        private static void MoveEndViewOutward(
            Drawing drawing,
            IEnumerable<ViewAnalysis> views,
            ViewAnalysis baseView,
            string sectionName,
            bool startEnd)
        {
            var section = views.FirstOrDefault(view =>
                view.View != null &&
                string.Equals(view.View.Name ?? string.Empty, sectionName, StringComparison.OrdinalIgnoreCase));

            if (section == null || section.View == null)
                return;

            var desired = new Point(
                section.View.Origin.X + (startEnd ? -ExtraEndGap : ExtraEndGap),
                section.View.Origin.Y,
                0.0);

            var sheet = drawing.GetSheet();
            if (sheet != null && sheet.Width > 0.0 && sheet.Height > 0.0)
            {
                var halfWidth = section.View.Width * 0.5 + 2.0;
                desired.X = Math.Max(halfWidth, Math.Min(sheet.Width - halfWidth, desired.X));
            }

            section.View.Origin = desired;
            section.View.Modify();
        }

        private static bool IsGenerated(DrawingView view)
        {
            var name = (view == null ? string.Empty : view.Name ?? string.Empty).Trim().ToUpperInvariant();
            return name.StartsWith("TDA_") || name == "A-A" || name == "B-B";
        }
    }
}
