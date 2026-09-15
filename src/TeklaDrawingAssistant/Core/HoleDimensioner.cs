using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;

namespace TeklaDrawingAssistant.Core
{
    public sealed class HoleDimensioner
    {
        public int Dimension(ViewAnalysis analysis, DimensioningOptions options)
        {
            if (!analysis.ContainsMainPart || analysis.MainPartBounds == null || analysis.HolePoints.Count == 0)
                return 0;

            if (options.DeleteExistingStraightDimensions)
                DeleteStraightDimensions(analysis.View);

            var created = 0;
            var bounds = analysis.MainPartBounds;
            var offset = GetAdaptiveOffset(bounds, options);

            switch (analysis.Kind)
            {
                case ViewKind.Web:
                    created += CreateHorizontalChain(
                        analysis.View,
                        bounds.MinX,
                        bounds.MaxY,
                        analysis.HolePoints,
                        new Vector(0.0, 1.0, 0.0),
                        offset,
                        options);

                    created += CreateVerticalChain(
                        analysis.View,
                        bounds.MinX,
                        bounds.MaxY,
                        analysis.HolePoints,
                        new Vector(-1.0, 0.0, 0.0),
                        offset,
                        options);
                    break;

                case ViewKind.Flange:
                    created += CreateHorizontalChain(
                        analysis.View,
                        bounds.MinX,
                        bounds.MaxY,
                        analysis.HolePoints,
                        new Vector(0.0, 1.0, 0.0),
                        offset,
                        options);

                    created += CreateFlangeCentrelineDimensions(analysis, offset, options);
                    break;

                case ViewKind.End:
                    created += CreateHorizontalChain(
                        analysis.View,
                        bounds.CentreX,
                        bounds.MaxY,
                        analysis.HolePoints,
                        new Vector(0.0, 1.0, 0.0),
                        offset,
                        options);

                    created += CreateVerticalChain(
                        analysis.View,
                        bounds.MinX,
                        bounds.MaxY,
                        analysis.HolePoints,
                        new Vector(-1.0, 0.0, 0.0),
                        offset,
                        options);
                    break;
            }

            return created;
        }

        private static int CreateFlangeCentrelineDimensions(ViewAnalysis analysis, double offset, DimensioningOptions options)
        {
            var bounds = analysis.MainPartBounds;
            var created = 0;

            if (analysis.HoleGroups.Count == 0)
            {
                return CreateVerticalChain(
                    analysis.View,
                    bounds.CentreX,
                    bounds.CentreY,
                    analysis.HolePoints,
                    new Vector(-1.0, 0.0, 0.0),
                    offset,
                    options);
            }

            foreach (var group in analysis.HoleGroups)
            {
                if (group.Points.Count == 0)
                    continue;

                // Put the flange-centre datum beside the connection it belongs to,
                // rather than using a centre point at the end of the whole member.
                var referenceX = Clamp(group.CentreX, bounds.MinX, bounds.MaxX);

                // Keep the dimension on the nearest longitudinal side of the connection.
                var direction = referenceX <= bounds.CentreX
                    ? new Vector(-1.0, 0.0, 0.0)
                    : new Vector(1.0, 0.0, 0.0);

                created += CreateVerticalChain(
                    analysis.View,
                    referenceX,
                    bounds.CentreY,
                    group.Points,
                    direction,
                    offset,
                    options);
            }

            return created;
        }

        private static int CreateHorizontalChain(
            View view,
            double referenceX,
            double referenceY,
            IEnumerable<Point> holes,
            Vector direction,
            double offset,
            DimensioningOptions options)
        {
            var points = UniqueByCoordinate(holes, point => point.X, options.CoordinateTolerance)
                .OrderBy(point => point.X)
                .ToList();

            if (points.Count == 0)
                return 0;

            var reference = new Point(referenceX, referenceY, 0.0);
            if (!HasDifferentCoordinate(points, reference.X, point => point.X, options.CoordinateTolerance))
                return 0;

            var list = new PointList();
            list.Add(reference);

            foreach (var point in points)
                list.Add(point);

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view, list, direction, offset);
            return dimension == null ? 0 : 1;
        }

        private static int CreateVerticalChain(
            View view,
            double referenceX,
            double referenceY,
            IEnumerable<Point> holes,
            Vector direction,
            double offset,
            DimensioningOptions options)
        {
            var points = UniqueByCoordinate(holes, point => point.Y, options.CoordinateTolerance)
                .OrderByDescending(point => point.Y)
                .ToList();

            if (points.Count == 0)
                return 0;

            var reference = new Point(referenceX, referenceY, 0.0);
            if (!HasDifferentCoordinate(points, reference.Y, point => point.Y, options.CoordinateTolerance))
                return 0;

            var list = new PointList();
            list.Add(reference);

            foreach (var point in points)
                list.Add(point);

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view, list, direction, offset);
            return dimension == null ? 0 : 1;
        }

        private static double GetAdaptiveOffset(ViewBounds bounds, DimensioningOptions options)
        {
            var width = Math.Abs(bounds.Width);
            var height = Math.Abs(bounds.Height);
            var smallerProjectedSize = Math.Min(width, height);

            var scaledOffset = smallerProjectedSize * options.DimensionOffsetScale;
            var offset = Math.Max(options.DimensionOffset, scaledOffset);

            return Math.Min(offset, options.MaximumDimensionOffset);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static List<Point> UniqueByCoordinate(IEnumerable<Point> points, Func<Point, double> selector, double tolerance)
        {
            var result = new List<Point>();

            foreach (var point in points.OrderBy(selector))
            {
                if (result.All(existing => Math.Abs(selector(existing) - selector(point)) > tolerance))
                    result.Add(point);
            }

            return result;
        }

        private static bool HasDifferentCoordinate(IEnumerable<Point> points, double reference, Func<Point, double> selector, double tolerance)
        {
            return points.Any(point => Math.Abs(selector(point) - reference) > tolerance);
        }

        private static void DeleteStraightDimensions(View view)
        {
            var dimensions = view.GetObjects(new[] { typeof(StraightDimensionSet) });
            var toDelete = new List<StraightDimensionSet>();

            while (dimensions.MoveNext())
            {
                var dimension = dimensions.Current as StraightDimensionSet;
                if (dimension != null)
                    toDelete.Add(dimension);
            }

            foreach (var dimension in toDelete)
                dimension.Delete();
        }
    }
}
