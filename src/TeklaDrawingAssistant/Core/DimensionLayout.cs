using System;
using TeklaDrawingAssistant.Models;
using Tekla.Structures.Drawing;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Tekla's CreateDimensionSet distance is supplied in view/model units.
    /// Choose the visual spacing in paper millimetres, then multiply by the
    /// drawing-view scale so 1:10, 1:15 and 1:20 drawings look consistent.
    ///
    /// These values are deliberately tight. Final collision/layout work happens
    /// after all annotations have been created.
    /// </summary>
    public static class DimensionLayout
    {
        private const double BasePaperOffset = 4.0;
        private const double PartPaperOffset = 3.5;
        private const double LanePaperSpacing = 3.5;
        private const double EndPlatePaperOffset = 4.0;

        public static double GetBaseOffset(ViewAnalysis analysis)
        {
            return ToViewDistance(analysis, BasePaperOffset);
        }

        public static double GetPartOffset(ViewAnalysis analysis)
        {
            return ToViewDistance(analysis, PartPaperOffset);
        }

        public static double GetLaneSpacing(ViewAnalysis analysis)
        {
            return ToViewDistance(analysis, LanePaperSpacing);
        }

        public static double GetEndPlateOffset(ViewAnalysis analysis)
        {
            return ToViewDistance(analysis, EndPlatePaperOffset);
        }

        public static double ToViewDistance(ViewAnalysis analysis, double paperMillimetres)
        {
            var scale = GetScale(analysis == null ? null : analysis.View);
            return Math.Max(1.0, paperMillimetres * scale);
        }

        private static double GetScale(View view)
        {
            return view != null && view.Attributes != null && view.Attributes.Scale > 0.0
                ? view.Attributes.Scale
                : 1.0;
        }
    }
}
