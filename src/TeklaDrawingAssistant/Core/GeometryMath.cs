using System;
using Tekla.Structures.Geometry3d;

namespace TeklaDrawingAssistant.Core
{
    public static class GeometryMath
    {
        public static Vector Cross(Vector a, Vector b)
        {
            return new Vector(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        public static double AbsoluteDot(Vector a, Vector b)
        {
            var la = Length(a);
            var lb = Length(b);
            if (la < 0.000001 || lb < 0.000001)
                return 0.0;

            return Math.Abs((a.X * b.X + a.Y * b.Y + a.Z * b.Z) / (la * lb));
        }

        private static double Length(Vector v)
        {
            return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        }
    }
}
