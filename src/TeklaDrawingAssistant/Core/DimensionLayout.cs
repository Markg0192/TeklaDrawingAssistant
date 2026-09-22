using TeklaDrawingAssistant.Models;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// StraightDimensionSetHandler.CreateDimensionSet expects Distance in PAPER millimetres.
    /// Keep all first-lane dimensions deliberately tight to their steel; the final layout
    /// pass handles collisions after all annotations exist.
    /// </summary>
    public static class DimensionLayout
    {
        private const double BasePaperOffset = 1.5;
        private const double PartPaperOffset = 1.25;
        private const double LanePaperSpacing = 1.5;
        private const double EndPlatePaperOffset = 1.5;
        private const double LocalFeaturePaperOffset = 1.25;

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
            return paperMillimetres < 0.5 ? 0.5 : paperMillimetres;
        }
    }
}
