using System.Collections.Generic;
using System.Linq;
using Tekla.Structures.Geometry3d;

namespace TeklaDrawingAssistant.Models
{
    public sealed class HoleGroup
    {
        public int ModelIdentifierId { get; set; }
        public List<Point> Points { get; } = new List<Point>();

        public double CentreX => Points.Count == 0 ? 0.0 : Points.Average(point => point.X);
        public double CentreY => Points.Count == 0 ? 0.0 : Points.Average(point => point.Y);
    }
}
