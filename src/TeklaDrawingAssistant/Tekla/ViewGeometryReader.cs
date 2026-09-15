using System;
using System.Collections.Generic;
using TeklaDrawingAssistant.Models;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using DrawingBolt = Tekla.Structures.Drawing.Bolt;
using ModelPart = Tekla.Structures.Model.Part;
using ModelBoltGroup = Tekla.Structures.Model.BoltGroup;

namespace TeklaDrawingAssistant.Tekla
{
    public sealed class ViewGeometryReader
    {
        private readonly Model _model;

        public ViewGeometryReader(Model model)
        {
            _model = model;
        }

        public bool ContainsModelObject(View view, Identifier identifier)
        {
            var objects = view.GetModelObjects(identifier);
            return objects != null && objects.MoveNext();
        }

        public ViewBounds GetPartBounds(View view, Identifier partIdentifier)
        {
            return WithViewTransformationPlane(view, () =>
            {
                var part = _model.SelectModelObject(partIdentifier) as ModelPart;
                if (part == null)
                    return null;

                var solid = part.GetSolid();
                if (solid == null)
                    return null;

                return new ViewBounds
                {
                    MinX = solid.MinimumPoint.X,
                    MaxX = solid.MaximumPoint.X,
                    MinY = solid.MinimumPoint.Y,
                    MaxY = solid.MaximumPoint.Y
                };
            });
        }

        public List<HoleGroup> GetVisibleHoleGroups(View view)
        {
            return WithViewTransformationPlane(view, () =>
            {
                var groups = new List<HoleGroup>();
                var seenGroups = new HashSet<int>();
                var drawingBolts = view.GetObjects(new[] { typeof(DrawingBolt) });

                while (drawingBolts.MoveNext())
                {
                    var drawingBolt = drawingBolts.Current as DrawingBolt;
                    if (drawingBolt == null)
                        continue;

                    var id = drawingBolt.ModelIdentifier;
                    if (id == null || !seenGroups.Add(id.ID))
                        continue;

                    var boltGroup = _model.SelectModelObject(id) as ModelBoltGroup;
                    if (boltGroup == null)
                        continue;

                    var group = new HoleGroup
                    {
                        ModelIdentifierId = id.ID
                    };

                    foreach (var item in boltGroup.BoltPositions)
                    {
                        var point = item as Point;
                        if (point != null)
                            group.Points.Add(new Point(point.X, point.Y, 0.0));
                    }

                    if (group.Points.Count > 0)
                        groups.Add(group);
                }

                return groups;
            });
        }

        public int CountObjects<T>(View view) where T : DrawingObject
        {
            var count = 0;
            var objects = view.GetObjects(new[] { typeof(T) });
            while (objects.MoveNext())
                count++;
            return count;
        }

        private T WithViewTransformationPlane<T>(View view, Func<T> action)
        {
            var workPlaneHandler = _model.GetWorkPlaneHandler();
            var original = workPlaneHandler.GetCurrentTransformationPlane();

            try
            {
                workPlaneHandler.SetCurrentTransformationPlane(new TransformationPlane(view.DisplayCoordinateSystem));
                return action();
            }
            finally
            {
                workPlaneHandler.SetCurrentTransformationPlane(original);
            }
        }
    }
}
