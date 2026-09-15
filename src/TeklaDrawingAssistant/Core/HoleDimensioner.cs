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

            switch (analysis.Kind)
            {
                case ViewKind.Web:
                    created += CreateHorizontalChain(analysis.View, bounds.MinX, bounds.MaxY, analysis.HolePoints, options);
                    created += CreateVerticalChain(analysis.View, bounds.MinX, bounds.MaxY, analysis.HolePoints, options);
                    break;

                case ViewKind.Flange:
                    created += CreateHorizontalChain(analysis.View, bounds.MinX, bounds.MaxY, analysis.HolePoints, options);
                    created += CreateVerticalChain(analysis.View, bounds.MinX, bounds.CentreY, analysis.HolePoints, options);
                    break;

                case ViewKind.End:
                    created += CreateHorizontalChain(analysis.View, bounds.CentreX, bounds.MaxY, analysis.HolePoints, options);
                    created += CreateVerticalChain(analysis.View, bounds.MinX, bounds.MaxY, analysis.HolePoints, options);
                    break;
            }

            return created;
        }

        private static int CreateHorizontalChain(View view, double referenceX, double referenceY, IEnumerable<Point> holes, DimensioningOptions options)
        {
            var points = UniqueByCoordinate(holes, p => p.X, options.CoordinateTolerance)
                .OrderBy(p => p.X)
                .ToList();

            if (points.Count == 0)
                return 0;

            var reference = new Point(referenceX, referenceY, 0.0);
            if (!HasDifferentCoordinate(points, reference.X, p => p.X, options.CoordinateTolerance))
                return 0;

            var list = new PointList();
            list.Add(reference);
            foreach (var point in points)
                list.Add(point);

            var direction = new Vector(0.0, 1.0, 0.0);
            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view, list, direction, options.DimensionOffset);
            return dimension == null ? 0 : 1;
        }

        private static int CreateVerticalChain(View view, double referenceX, double referenceY, IEnumerable<Point> holes, DimensioningOptions options)
        {
            var points = UniqueByCoordinate(holes, p => p.Y, options.CoordinateTolerance)
                .OrderByDescending(p => p.Y)
                .ToList();

            if (points.Count == 0)
                return 0;

            var reference = new Point(referenceX, referenceY, 0.0);
            if (!HasDifferentCoordinate(points, reference.Y, p => p.Y, options.CoordinateTolerance))
                return 0;

            var list = new PointList();
            list.Add(reference);
            foreach (var point in points)
                list.Add(point);

            var direction = new Vector(-1.0, 0.0, 0.0);
            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view, list, direction, options.DimensionOffset);
            return dimension == null ? 0 : 1;
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
            var dimensions = view.GetObjects(typeof(StraightDimensionSet));
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
