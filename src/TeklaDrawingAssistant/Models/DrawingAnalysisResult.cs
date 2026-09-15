using System.Collections.Generic;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Models
{
    public sealed class DrawingAnalysisResult
    {
        public Drawing Drawing { get; set; }
        public Identifier MainPartIdentifier { get; set; }
        public ModelPart MainPart { get; set; }
        public List<ViewAnalysis> Views { get; } = new List<ViewAnalysis>();
    }
}
