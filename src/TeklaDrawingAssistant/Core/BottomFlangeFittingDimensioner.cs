using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using TeklaDrawingAssistant.Tekla;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingView = Tekla.Structures.Drawing.View;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Dedicated set-out for fittings hanging from the bottom flange in the retained
    /// web/elevation view.
    ///
    /// Fabrication rule:
    /// - locate these fittings by their HOLES, not by plate edges;
    /// - one common horizontal chain below the member;
    /// - one common vertical chain from the bottom-flange datum to the hole rows;
    /// - include member start/finish in the horizontal chain for the closing dimension.
    ///
    /// Fittings without holes are deliberately ignored by this pass. They can be handled
    /// later by a specific no-hole fitting rule rather than silently mixing plate-edge and
    /// hole-centre conventions on the same drawing.
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
            var holePoints = new List<Point>();
            var fittingCount = 0;
            var boltGroups = new HashSet<int>();

            var assembly = analysis.MainPart.GetAssembly();
            if (assembly != null)
            {
                foreach (var item in assembly.GetSecondaries())
                {
                    var part = item as ModelPart;
                    if (part == null)
                        continue;

                    var bounds = _geometryReader.GetPartBounds(view.View, part.Identifier);
                    if (bounds == null || !IsBottomFlangeFitting(bounds, main))
                        continue;

                    var attached = GetAttachedBoltIds(part);
                    if (attached.Count == 0)
                        continue;

                    var projected = ProjectBoltGroups(view.View, attached);
                    if (projected.Count == 0)
                        continue;

                    fittingCount++;
                    foreach (var group in projected)
                    {
                        boltGroups.Add(group.ModelIdentifierId);
                        holePoints.AddRange(group.Points);
                    }
                }
            }

            if (holePoints.Count == 0)
            {
                messages?.Add("BOTTOM FLANGE SETOUT: no hole-centred bottom-flange fitting set-out required.");
                return 0;
            }

            var created = 0;

            // ONE horizontal chain: beam start -> all unique hole stations -> beam finish.
            // It projects below the member so extension lines do not run through the steel.
            var horizontalPoints = new List<Point>
            {
                new Point(main.MinX, main.MinY, 0.0)
            };

            horizontalPoints.AddRange(UniqueByX(holePoints));
            horizontalPoints.Add(new Point(main.MaxX, main.MinY, 0.0));

            created += CreateDimension(
                view.View,
                horizontalPoints,
                new Vector(0.0, -1.0, 0.0),
                DimensionLayout.GetBaseOffset(view),
                compareX: true);

            // ONE vertical chain: bottom flange -> all unique hole rows. Use the left-most
            // hole column as the extension-line location so the dimension remains local.
            var leftMostX = holePoints.Min(point => point.X);
            var verticalPoints = new List<Point>
            {
                new Point(leftMostX, main.MinY, 0.0)
            };

            verticalPoints.AddRange(
                UniqueByY(holePoints)
                    .Select(point => new Point(leftMostX, point.Y, 0.0)));

            created += CreateDimension(
                view.View,
                verticalPoints,
                new Vector(-1.0, 0.0, 0.0),
                DimensionLayout.GetLocalFeatureOffset(view),
                compareX: false);

            analysis.Drawing.CommitChanges();
            messages?.Add(
                "BOTTOM FLANGE SETOUT: " + fittingCount +
                " holed fitting(s), bolt groups [" + string.Join(",", boltGroups.OrderBy(id => id)) +
                "] -> exactly one horizontal + one vertical hole-centre chain in BASE / WEB.");

            return created;
        }

        private static bool IsBottomFlangeFitting(ViewBounds bounds, ViewBounds main)
        {
            var touchesBottom = bounds.MaxY >= main.MinY - EdgeTolerance &&
                                bounds.MaxY <= main.MinY + EdgeTolerance;
            var hangsBelow = bounds.MinY < main.MinY - CoordinateTolerance;
            return touchesBottom && hangsBelow;
        }

        private static HashSet<int> GetAttachedBoltIds(ModelPart part)
        {
            var result = new HashSet<int>();
            var bolts = part.GetBolts();
            while (bolts != null && bolts.MoveNext())
            {
                var group = bolts.Current as BoltGroup;
                if (group != null)
                    result.Add(group.Identifier.ID);
            }
            return result;
        }

        private List<HoleGroup> ProjectBoltGroups(DrawingView view, IEnumerable<int> ids)
        {
            var result = new List<HoleGroup>();
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(view.DisplayCoordinateSystem));

                foreach (var id in ids.Distinct())
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

        private static List<Point> UniqueByX(IEnumerable<Point> points)
        {
            var result = new List<Point>();
            foreach (var point in points.OrderBy(point => point.X))
            {
                if (result.All(existing => Math.Abs(existing.X - point.X) > CoordinateTolerance))
                    result.Add(point);
            }
            return result;
        }

        private static List<Point> UniqueByY(IEnumerable<Point> points)
        {
            var result = new List<Point>();
            foreach (var point in points.OrderBy(point => point.Y))
            {
                if (result.All(existing => Math.Abs(existing.Y - point.Y) > CoordinateTolerance))
                    result.Add(point);
            }
            return result;
        }

        private static int CreateDimension(
            DrawingView view,
            IEnumerable<Point> points,
            Vector direction,
            double offset,
            bool compareX)
        {
            var unique = new List<Point>();
            foreach (var point in points.OrderBy(point => compareX ? point.X : point.Y))
            {
                var coordinate = compareX ? point.X : point.Y;
                if (unique.All(existing =>
                    Math.Abs((compareX ? existing.X : existing.Y) - coordinate) > CoordinateTolerance))
                    unique.Add(point);
            }

            if (unique.Count < 2)
                return 0;

            var list = new PointList();
            foreach (var point in unique)
                list.Add(point);

            var attributes = new StraightDimensionSet.StraightDimensionSetAttributes(null, "standard");
            attributes.Placing.Placing = DimensionSetBaseAttributes.Placings.Free;

            var dimension = new StraightDimensionSetHandler().CreateDimensionSet(
                view,
                list,
                direction,
                offset,
                attributes);

            return dimension == null ? 0 : 1;
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
