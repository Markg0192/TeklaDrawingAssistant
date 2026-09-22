using System;
using System.Collections.Generic;
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
    /// First-pass fitting dimensioner. Only dimensions plate face extents and only in
    /// the single view selected by FaceViewPlanner for that plate.
    /// Location/datum rules are deliberately left for the next pass.
    /// </summary>
    public sealed class PartFaceDimensioner
    {
        private readonly Model _model;
        private readonly ViewGeometryReader _geometryReader;

        public PartFaceDimensioner(Model model)
        {
            _model = model;
            _geometryReader = new ViewGeometryReader(model);
        }

        public int Dimension(ViewAnalysis analysis, ISet<int> ownedPartIds, DimensioningOptions options)
        {
            if (analysis == null || analysis.View == null || analysis.MainPartBounds == null || ownedPartIds == null)
                return 0;

            var created = 0;
            var lane = 0;

            foreach (var id in ownedPartIds)
            {
                var identifier = new Identifier(id);
                var part = _model.SelectModelObject(identifier) as ModelPart;
                if (part == null || !IsPlate(part))
                    continue;

                var bounds = _geometryReader.GetPartBounds(analysis.View, identifier);
                if (bounds == null)
                    continue;

                var offset = Math.Max(15.0, options.DimensionOffset * 0.65) + lane * 8.0;
                created += DimensionHorizontalExtent(analysis, bounds, offset, options);
                created += DimensionVerticalExtent(analysis, bounds, offset, options);
                lane++;
            }

            return created;
        }

        private static int DimensionHorizontalExtent(
            ViewAnalysis analysis,
            ViewBounds partBounds,
            double offset,
            DimensioningOptions options)
        {
            if (Math.Abs(partBounds.Width) <= options.CoordinateTolerance)
                return 0;

            var above = partBounds.CentreY >= analysis.MainPartBounds.CentreY;
            var y = above ? partBounds.MaxY : partBounds.MinY;
            var direction = above
                ? new Vector(0.0, 1.0, 0.0)
                : new Vector(0.0, -1.0, 0.0);

            var points = new PointList();
            points.Add(new Point(partBounds.MinX, y, 0.0));
            points.Add(new Point(partBounds.MaxX, y, 0.0));

            var dimension = new StraightDimensionSetHandler()
                .CreateDimensionSet(analysis.View, points, direction, offset);

            return dimension == null ? 0 : 1;
        }

        private static int DimensionVerticalExtent(
            ViewAnalysis analysis,
            ViewBounds partBounds,
            double offset,
            DimensioningOptions options)
        {
            if (Math.Abs(partBounds.Height) <= options.CoordinateTolerance)
                return 0;

            var right = partBounds.CentreX >= analysis.MainPartBounds.CentreX;
            var x = right ? partBounds.MaxX : partBounds.MinX;
            var direction = right
                ? new Vector(1.0, 0.0, 0.0)
                : new Vector(-1.0, 0.0, 0.0);

            var points = new PointList();
            points.Add(new Point(x, partBounds.MinY, 0.0));
            points.Add(new Point(x, partBounds.MaxY, 0.0));

            var dimension = new StraightDimensionSetHandler()
                .CreateDimensionSet(analysis.View, points, direction, offset);

            return dimension == null ? 0 : 1;
        }

        private static bool IsPlate(ModelPart part)
        {
            if (part is ContourPlate)
                return true;

            var profile = part.Profile == null ? string.Empty : part.Profile.ProfileString;
            if (string.IsNullOrWhiteSpace(profile))
                return false;

            var value = profile.Trim().ToUpperInvariant();
            return value.StartsWith("PL") || value.StartsWith("PLATE");
        }
    }
}
