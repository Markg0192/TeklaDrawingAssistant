using TeklaDrawingAssistant.Models;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// StraightDimensionSetHandler.CreateDimensionSet expects Distance in PAPER millimetres.
    /// Do not multiply by the drawing scale. That was the reason our dimension lines were
    /// ending up much farther from the steel than requested.
    /// </summary>
    public static class DimensionLayout
    {
        private const double BasePaperOffset = 3.0;
        private const double PartPaperOffset = 2.5;
        private const double LanePaperSpacing = 3.0;
        private const double EndPlatePaperOffset = 3.0;
        private const double LocalFeaturePaperOffset = 2.5;

        public static double GetBaseOffset(ViewAnalysis analysis)
        {
            return BasePaperOffset;
        }

        public static double GetPartOffset(ViewAnalysis analysis)
        {
            return PartPaperOffset;
        }

        public static double GetLaneSpacing(ViewAnalysis analysis)
        {
            return LanePaperSpacing;
        }

        public static double GetEndPlateOffset(ViewAnalysis analysis)
        {
            return EndPlatePaperOffset;
        }

        public static double GetLocalFeatureOffset(ViewAnalysis analysis)
        {
            return LocalFeaturePaperOffset;
        }

        public static double ToViewDistance(ViewAnalysis analysis, double paperMillimetres)
        {
            return paperMillimetres < 1.0 ? 1.0 : paperMillimetres;
        }
    }
}
