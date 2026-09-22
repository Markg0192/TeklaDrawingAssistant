using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingPart = Tekla.Structures.Drawing.Part;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Dedicated end-plate dimensioning pass.
    ///
    /// End views have a very specific fabrication convention, so they are handled
    /// separately from the generic fitting dimensioner:
    /// - horizontal hole set-out is projected ABOVE from the TOP row of holes and
    ///   is dimensioned to the main-member centreline;
    /// - vertical hole set-out is projected LEFT from the LEFT-most holes and is
    ///   dimensioned from the TOP of the main-member flange;
    /// - dimension lines are intentionally kept tight to the view.
    ///
    /// This pass deletes any generic straight dimensions already created in A-A/B-B
    /// and replaces them with the controlled end-plate dimensions below.
    /// </summary>
    public sealed class EndPlateDimensioner
    {
        private const double CoordinateTolerance = 0.5;
        private const double EndZoneMinimum = 100.0;
        private const double TightOffset = 4.0;

        private readonly Model _model;

        public EndPlateDimensioner(Model model)
        {
            _model = model;
        }

        public int Dimension(DrawingAnalysisResult analysis, IList<string> messages)
        {
            if (analysis == null || analysis.MainPart == null)
                return 0;

            var assemblyParts = GetAssemblyParts(analysis.MainPart)
                .Where(part => part.Identifier.ID != analysis.MainPart.Identifier.ID)
                .ToList();

            var ends = FindEndPlates(analysis.MainPart, assemblyParts);
            var created = 0;

            created += DimensionEnd(
                analysis,
                analysis.Views.FirstOrDefault(view => NameEquals(view, "A-A")),
                ends.Start,
                "A-A",
                messages);

            created += DimensionEnd(
                analysis,
                analysis.Views.FirstOrDefault(view => NameEquals(view, "B-B")),
                ends.Finish,
                "B-B",
                messages);

            analysis.Drawing.CommitChanges();
            return created;
        }

        private int DimensionEnd(
            DrawingAnalysisResult analysis,
            ViewAnalysis view,
            ModelPart endPlate,
            string viewName,
            IList<string> messages)
        {
            if (view == null || view.View == null || view.MainPartBounds == null)
            {
                messages?.Add("END DIM " + viewName + ": view not available.");
                return 0;
            }

            DeleteStraightDimensions(view.View);

            if (endPlate == null)
            {
                messages?.Add("END DIM " + viewName + ": no transverse end plate detected.");
                return 0;
            }

            if (!IsPartVisible(view, endPlate.Identifier.ID))
            {
                messages?.Add("END DIM " + viewName + ": detected end plate " + Describe(endPlate) + " is not visible in this section.");
                return 0;
            }

            var holes = GetVisibleAttachedHolePoints(endPlate, view);
            if (holes.Count == 0)
            {
                messages?.Add("END DIM " + viewName + ": end plate " + Describe(endPlate) + " has no visible attached holes; no end-plate dimensions created.");
                return 0;
            }

            var created = 0;
            created += CreateHorizontalHoleSetout(view, holes);
            created += CreateVerticalHoleSetout(view, holes);

            messages?.Add(
                "END DIM " + viewName + ": end plate " + Describe(endPlate) +
                "; top-row holes -> member centreline above; left-most holes -> top flange on left; created " + created + " set(s).");

            return created;
        }

        private static int CreateHorizontalHoleSetout(ViewAnalysis view, IList<Point> holes)
        {
            var topRow = SelectExtremeByMeasuredCoordinate(
                holes,
                point => point.X,
                point => point.Y,
                true);

            if (topRow.Count == 0)
                return 0;

            var topY = topRow.Max(point => point.Y);
            var centreline = new Point(view.MainPartBounds.CentreX, topY, 0.0);

            var points = new List<Point>(topRow) { centreline };
            points = UniqueByCoordinate(points, point => point.X)
                .OrderBy(point => point.X)
                .ToList();

            if (points.Count < 2)
                return 0;

            return CreateDimensionSet(view.View, points, new Vector(0.0, 1.0, 0.0), GetTightOffset(view));
        }

        private static int CreateVerticalHoleSetout(ViewAnalysis view, IList<Point> holes)
        {
            var leftColumn = SelectExtremeByMeasuredCoordinate(
                holes,
                point => point.Y,
                point => point.X,
                false);

            if (leftColumn.Count == 0)
                return 0;

            // The fabrication datum is the TOP surface/edge of the main-member flange,
            // not the member centreline and not an end-plate edge.
            var topFlange = new Point(
                view.MainPartBounds.MinX,
                view.MainPartBounds.MaxY,
                0.0);

            var points = new List<Point>(leftColumn) { topFlange };
            points = UniqueByCoordinate(points, point => point.Y)
                .OrderByDescending(point => point.Y)
                .ToList();

            if (points.Count < 2)
                return 0;

            return CreateDimensionSet(view.View, points, new Vector(-1.0, 0.0, 0.0), GetTightOffset(view));
        }

        private static List<Point> SelectExtremeByMeasuredCoordinate(
            IEnumerable<Point> points,
            Func<Point, double> measuredCoordinate,
            Func<Point, double> projectionCoordinate,
            bool takeMaximumProjection)
        {
            var groups = new List<List<Point>>();

            foreach (var point in points.OrderBy(measuredCoordinate))
            {
                var coordinate = measuredCoordinate(point);
                var group = groups.FirstOrDefault(existing =>
                    Math.Abs(measuredCoordinate(existing[0]) - coordinate) <= CoordinateTolerance);

                if (group == null)
                {
                    group = new List<Point>();
                    groups.Add(group);
                }

                group.Add(point);
            }

            return groups
                .Select(group => takeMaximumProjection
                    ? group.OrderByDescending(projectionCoordinate).First()
                    : group.OrderBy(projectionCoordinate).First())
                .ToList();
        }

        private static List<Point> UniqueByCoordinate(
            IEnumerable<Point> points,
            Func<Point, double> coordinate)
        {
            var result = new List<Point>();

            foreach (var point in points.OrderBy(coordinate))
            {
                if (result.All(existing =>
                    Math.Abs(coordinate(existing) - coordinate(point)) > CoordinateTolerance))
                    result.Add(point);
            }

            return result;
        }

        private static int CreateDimensionSet(
            View view,
            IEnumerable<Point> points,
            Vector direction,
            double offset)
        {
            var pointList = new PointList();
            foreach (var point in points)
                pointList.Add(point);

            var handler = new StraightDimensionSetHandler();
            var dimension = handler.CreateDimensionSet(view, pointList, direction, offset);
            return dimension == null ? 0 : 1;
        }

        private static double GetTightOffset(ViewAnalysis view)
        {
            if (view == null || view.View == null)
                return TightOffset;

            var shortSide = Math.Min(Math.Abs(view.View.Width), Math.Abs(view.View.Height));
            if (shortSide < 0.001)
                return TightOffset;

            return Math.Max(3.5, Math.Min(5.0, shortSide * 0.055));
        }

        private List<Point> GetVisibleAttachedHolePoints(ModelPart part, ViewAnalysis view)
        {
            var attached = new HashSet<int>();
            var bolts = part.GetBolts();

            while (bolts.MoveNext())
            {
                var bolt = bolts.Current as BoltGroup;
                if (bolt != null)
                    attached.Add(bolt.Identifier.ID);
            }

            return view.HoleGroups
                .Where(group => attached.Contains(group.ModelIdentifierId))
                .SelectMany(group => group.Points)
                .ToList();
        }

        private EndPlatePair FindEndPlates(ModelPart mainPart, IList<ModelPart> parts)
        {
            var handler = _model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(mainPart.GetCoordinateSystem()));

                var main = mainPart.GetSolid();
                var mainMin = main.MinimumPoint.X;
                var mainMax = main.MaximumPoint.X;
                var mainLength = Math.Abs(mainMax - mainMin);
                var searchZone = Math.Max(EndZoneMinimum, Math.Min(400.0, mainLength * 0.04));

                ModelPart start = null;
                ModelPart finish = null;
                var startDistance = double.MaxValue;
                var finishDistance = double.MaxValue;

                foreach (var part in parts)
                {
                    var solid = part.GetSolid();
                    if (solid == null)
                        continue;

                    var sx = Math.Abs(solid.MaximumPoint.X - solid.MinimumPoint.X);
                    var sy = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                    var sz = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);
                    var transverse = Math.Max(sy, sz);
                    var transverseEndPlate = sx <= Math.Max(60.0, transverse * 0.65);
                    if (!transverseEndPlate)
                        continue;

                    var centreX = (solid.MinimumPoint.X + solid.MaximumPoint.X) * 0.5;
                    var dStart = Math.Abs(centreX - mainMin);
                    var dFinish = Math.Abs(centreX - mainMax);

                    if (dStart <= searchZone + sx * 0.5 && dStart < startDistance)
                    {
                        start = part;
                        startDistance = dStart;
                    }

                    if (dFinish <= searchZone + sx * 0.5 && dFinish < finishDistance)
                    {
                        finish = part;
                        finishDistance = dFinish;
                    }
                }

                return new EndPlatePair(start, finish);
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }
        }

        private static List<ModelPart> GetAssemblyParts(ModelPart mainPart)
        {
            var result = new List<ModelPart> { mainPart };
            var assembly = mainPart.GetAssembly();
            if (assembly == null)
                return result;

            foreach (var item in assembly.GetSecondaries())
            {
                var part = item as ModelPart;
                if (part != null)
                    result.Add(part);
            }

            return result;
        }

        private static bool IsPartVisible(ViewAnalysis view, int partId)
        {
            var objects = view.View.GetObjects(new[] { typeof(DrawingPart) });
            while (objects.MoveNext())
            {
                var part = objects.Current as DrawingPart;
                if (part != null && part.ModelIdentifier != null && part.ModelIdentifier.ID == partId)
                    return true;
            }

            return false;
        }

        private static bool NameEquals(ViewAnalysis view, string name)
        {
            return view != null && view.View != null &&
                   string.Equals(view.View.Name ?? string.Empty, name, StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteStraightDimensions(View view)
        {
            var objects = view.GetObjects(new[] { typeof(StraightDimensionSet) });
            var delete = new List<StraightDimensionSet>();

            while (objects.MoveNext())
            {
                var dimension = objects.Current as StraightDimensionSet;
                if (dimension != null)
                    delete.Add(dimension);
            }

            foreach (var dimension in delete)
                dimension.Delete();
        }

        private static string Describe(ModelPart part)
        {
            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            return part.Identifier.ID + (string.IsNullOrWhiteSpace(profile) ? string.Empty : " (" + profile + ")");
        }

        private sealed class EndPlatePair
        {
            public EndPlatePair(ModelPart start, ModelPart finish)
            {
                Start = start;
                Finish = finish;
            }

            public ModelPart Start { get; }
            public ModelPart Finish { get; }
        }
    }
}
