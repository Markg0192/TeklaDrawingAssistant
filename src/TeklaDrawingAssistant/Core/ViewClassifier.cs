using System;
using TeklaDrawingAssistant.Models;
using Tekla.Structures.Drawing;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Model;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    public sealed class ViewClassifier
    {
        public ViewKind Classify(View view, ModelPart mainPart)
        {
            if (view == null || mainPart == null)
                return ViewKind.Unknown;

            var partCs = mainPart.GetCoordinateSystem();
            var lengthAxis = Normalize(new Vector(partCs.AxisX));
            var localY = Normalize(new Vector(partCs.AxisY));
            var localZ = Normalize(Cross(lengthAxis, localY));

            var widthAxis = localY;
            var depthAxis = localZ;

            // Tekla local Y/Z is not consistent enough to assume one is always
            // web-normal and the other flange-normal. Use the actual section spans:
            // larger transverse span = section depth, smaller = section width.
            var model = new Model();
            var handler = model.GetWorkPlaneHandler();
            var original = handler.GetCurrentTransformationPlane();

            try
            {
                handler.SetCurrentTransformationPlane(new TransformationPlane(partCs));
                var solid = mainPart.GetSolid();
                if (solid != null)
                {
                    var spanY = Math.Abs(solid.MaximumPoint.Y - solid.MinimumPoint.Y);
                    var spanZ = Math.Abs(solid.MaximumPoint.Z - solid.MinimumPoint.Z);

                    if (spanY >= spanZ)
                    {
                        depthAxis = localY;
                        widthAxis = localZ;
                    }
                    else
                    {
                        depthAxis = localZ;
                        widthAxis = localY;
                    }
                }
            }
            finally
            {
                handler.SetCurrentTransformationPlane(original);
            }

            var viewCs = view.DisplayCoordinateSystem;
            var viewNormal = Normalize(Cross(new Vector(viewCs.AxisX), new Vector(viewCs.AxisY)));

            var alongMember = Math.Abs(Dot(viewNormal, lengthAxis));
            var throughWidth = Math.Abs(Dot(viewNormal, widthAxis));
            var throughDepth = Math.Abs(Dot(viewNormal, depthAxis));

            const double parallelTolerance = 0.90;

            if (alongMember >= parallelTolerance)
                return ViewKind.End;

            // Looking through the section width shows the web elevation.
            if (throughWidth >= parallelTolerance && throughWidth >= throughDepth)
                return ViewKind.Web;

            // Looking through the section depth shows a top/bottom flange view.
            if (throughDepth >= parallelTolerance)
                return ViewKind.Flange;

            return ViewKind.Unknown;
        }

        private static Vector Cross(Vector a, Vector b)
        {
            return new Vector(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private static double Dot(Vector a, Vector b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private static Vector Normalize(Vector vector)
        {
            var length = Math.Sqrt(vector.X * vector.X + vector.Y * vector.Y + vector.Z * vector.Z);
            return length < 0.000001
                ? new Vector()
                : new Vector(vector.X / length, vector.Y / length, vector.Z / length);
        }
    }
}
