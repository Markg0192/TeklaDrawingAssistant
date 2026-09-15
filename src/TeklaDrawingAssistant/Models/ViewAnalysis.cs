using System.Collections.Generic;
using Tekla.Structures.Geometry3d;
using Tekla.Structures.Drawing;

namespace TeklaDrawingAssistant.Models
{
    public sealed class ViewAnalysis
    {
        public View View { get; set; }
        public string Name { get; set; }
        public ViewKind Kind { get; set; }
        public ViewBounds MainPartBounds { get; set; }
        public List<Point> HolePoints { get; } = new List<Point>();
        public int PartCount { get; set; }
        public int BoltGroupCount { get; set; }
        public int ExistingDimensionSetCount { get; set; }
        public int MarkCount { get; set; }
        public int WeldCount { get; set; }
        public bool ContainsMainPart { get; set; }
    }
}
