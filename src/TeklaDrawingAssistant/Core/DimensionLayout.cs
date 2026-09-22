using TeklaDrawingAssistant.Models;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// StraightDimensionSetHandler.CreateDimensionSet distance is supplied in paper millimetres.
    /// Keep automatic dimensions deliberately tight to the steel. Final collision/layout work
    /// happens after all annotations have been created.
    /// </summary>
    public static class DimensionLayout
    {
        private const double BasePaperOffset = 0.75;
        private const double PartPaperOffset = 0.625;
        private const double LanePaperSpacing = 0.75;
        private const double EndPlatePaperOffset = 0.75;
        private const double LocalFeaturePaperOffset = 0.625;

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
            return paperMillimetres < 0.25 ? 0.25 : paperMillimetres;
        }
    }
}
