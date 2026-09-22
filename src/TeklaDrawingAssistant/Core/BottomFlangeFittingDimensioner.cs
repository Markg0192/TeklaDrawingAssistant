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

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Dedicated longitudinal set-out for fittings hanging from the bottom flange
    /// in the retained web/elevation view.
    ///
    /// These are intentionally grouped onto one chain BELOW the member so the
    /// dimensions do not run through the steel. Plate/gusset edges are used here;
    /// local hole-pattern dimensions remain owned by the normal fitting/hole passes.
    /// </summary>
    public sealed class BottomFlangeFittingDimensioner
    {
        private const double EdgeTolerance = 12.0;
        private const double CoordinateTolerance = 0.5;

        private readonly Model _model;
        private readonly ViewGeometryReader _geometryReader;

        public BottomFlangeFittingDimensioner(Model model)
        {
            _model = model;
            _geometryReader = new ViewGeometryReader(model);
        }

        public int Dimension(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null)
                return 0;

            var view = FindBaseWebView(analysis);
            if (view == null || view.View == null || view.MainPartBounds == null)
            {
                messages?.Add("BOTTOM FLANGE SETOUT: retained web view not available.");
                return 0;
            }

            var main = view.MainPartBounds;
            var fittings = new List<ViewBounds>();

            var assembly = analysis.MainPart.GetAssembly();
            if (assembly != null)
            {
                foreach (var item in assembly.GetSecondaries())
                {
                    var part = item as ModelPart;
                    if (part == null)
                        continue;

                    var bounds = _geometryReader.GetPartBounds(view.View, part.Identifier);
                    if (bounds == null)
                        continue;

                    // A bottom-flange fitting touches the lower edge of the main member
                    // and projects below it in the retained fabrication elevation.
                    var touchesBottom = bounds.MaxY >= main.MinY - EdgeTolerance &&
                                        bounds.MaxY <= main.MinY + EdgeTolerance;
                    var hangsBelow = bounds.MinY < main.MinY - CoordinateTolerance;

                    if (touchesBottom && hangsBelow)
                        fittings.Add(bounds);
                }
            }

            if (fittings.Count == 0)
            {
                messages?.Add("BOTTOM FLANGE SETOUT: no hanging bottom-flange fittings found in BASE / WEB.");
                return 0;
            }

            var points = new List<Point>
            {
                new Point(main.MinX, main.MinY, 0.0)
            };

            foreach (var fitting in fittings.OrderBy(bounds => bounds.MinX))
            {
                // Use both edges so the fitting width is included in the common chain.
                points.Add(new Point(fitting.MinX, fitting.MinY, 0.0));
                points.Add(new Point(fitting.MaxX, fitting.MinY, 0.0));
            }

            points.Add(new Point(main.MaxX, main.MinY, 0.0));

            var unique = new List<Point>();
            foreach (var point in points.OrderBy(point => point.X))
            {
                if (unique.All(existing => Math.Abs(existing.X - point.X) > CoordinateTolerance))
                    unique.Add(point);
            }

            if (unique.Count < 2)
                return 0;

            var list = new PointList();
            foreach (var point in unique)
                list.Add(point);

            var attributes = new StraightDimensionSet.StraightDimensionSetAttributes(null, "standard");
            attributes.Placing.Placing = DimensionSetBaseAttributes.Placings.Free;

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(
                view.View,
                list,
                new Vector(0.0, -1.0, 0.0),
                DimensionLayout.GetBaseOffset(view),
                attributes);

            if (dimension == null)
            {
                messages?.Add("BOTTOM FLANGE SETOUT: Tekla did not create the grouped lower chain.");
                return 0;
            }

            analysis.Drawing.CommitChanges();
            messages?.Add(
                "BOTTOM FLANGE SETOUT: grouped " + fittings.Count +
                " hanging fitting(s) on one chain below BASE / WEB, including beam start/finish closing dimensions.");
            return 1;
        }

        private static ViewAnalysis FindBaseWebView(DrawingAnalysisResult analysis)
        {
            return analysis.Views
                .Where(view => view.View != null && view.Kind == ViewKind.Web)
                .Where(view => !IsGenerated(view))
                .OrderByDescending(view => view.MainPartBounds == null
                    ? 0.0
                    : view.MainPartBounds.Width * view.MainPartBounds.Height)
                .FirstOrDefault();
        }

        private static bool IsGenerated(ViewAnalysis view)
        {
            var name = view == null || view.View == null
                ? string.Empty
                : (view.View.Name ?? string.Empty).Trim().ToUpperInvariant();

            return name.StartsWith("TDA_") || name == "A-A" || name == "B-B";
        }
    }
}
