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
        public int Dimension(ViewAnalysis analysis, DimensioningOptions options, ISet<int> ownedHoleGroupIds)
        {
            if (!analysis.ContainsMainPart || analysis.MainPartBounds == null || ownedHoleGroupIds == null || ownedHoleGroupIds.Count == 0)
                return 0;

            var groups = analysis.HoleGroups
                .Where(group => ownedHoleGroupIds.Contains(group.ModelIdentifierId) && group.Points.Count > 0)
                .ToList();

            if (groups.Count == 0)
                return 0;

            if (options.DeleteExistingStraightDimensions)
                DeleteStraightDimensions(analysis.View);

            var created = 0;
            var bounds = analysis.MainPartBounds;
            var baseOffset = GetAdaptiveOffset(bounds, options);

            for (var index = 0; index < groups.Count; index++)
            {
                var group = groups[index];
                var offset = baseOffset + index * 8.0;

                switch (analysis.Kind)
                {
                    case ViewKind.Web:
                        created += CreateHorizontalChain(
                            analysis.View,
                            bounds.MinX,
                            bounds.MaxY,
                            group.Points,
                            new Vector(0.0, 1.0, 0.0),
                            offset,
                            options);

                        created += CreateVerticalChain(
                            analysis.View,
                            bounds.MinX,
                            bounds.MaxY,
                            group.Points,
                            new Vector(-1.0, 0.0, 0.0),
                            offset,
                            options);
                        break;

                    case ViewKind.Flange:
                        created += CreateHorizontalChain(
                            analysis.View,
                            bounds.MinX,
                            bounds.MaxY,
                            group.Points,
                            new Vector(0.0, 1.0, 0.0),
                            offset,
                            options);

                        created += CreateFlangeCentrelineDimensions(analysis, group, offset, options);
                        break;

                    case ViewKind.End:
                        created += CreateHorizontalChain(
                            analysis.View,
                            bounds.CentreX,
                            bounds.MaxY,
                            group.Points,
                            new Vector(0.0, 1.0, 0.0),
                            offset,
                            options);

                        created += CreateVerticalChain(
                            analysis.View,
                            bounds.MinX,
                            bounds.MaxY,
                            group.Points,
                            new Vector(-1.0, 0.0, 0.0),
                            offset,
                            options);
                        break;
                }
            }

            return created;
        }

        private static int CreateFlangeCentrelineDimensions(
            ViewAnalysis analysis,
            HoleGroup group,
            double offset,
            DimensioningOptions options)
        {
            var bounds = analysis.MainPartBounds;
            var referenceX = Clamp(group.CentreX, bounds.MinX, bounds.MaxX);

            // Dimension transverse flange geometry from the flange centreline and keep
            // the dimension beside the connection it belongs to.
            var direction = referenceX <= bounds.CentreX
                ? new Vector(-1.0, 0.0, 0.0)
                : new Vector(1.0, 0.0, 0.0);

            return CreateVerticalChain(
                analysis.View,
                referenceX,
                bounds.CentreY,
                group.Points,
                direction,
                offset,
                options);
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
