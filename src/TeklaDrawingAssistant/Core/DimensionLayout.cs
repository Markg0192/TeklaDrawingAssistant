using System;
using TeklaDrawingAssistant.Models;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Dimension distances in the Tekla drawing API are paper millimetres.
    /// Keep dimensions close enough to read as belonging to the feature while still
    /// scaling gently with the view size.
    /// </summary>
    public static class DimensionLayout
    {
        public static double GetBaseOffset(ViewAnalysis analysis)
        {
            if (analysis == null || analysis.View == null)
                return 7.0;

            var width = Math.Abs(analysis.View.Width);
            var height = Math.Abs(analysis.View.Height);
            var shortSide = Math.Min(width, height);

            if (shortSide < 0.001)
                return 7.0;

            return Clamp(shortSide * 0.08, 6.0, 12.0);
        }

        public static double GetPartOffset(ViewAnalysis analysis)
        {
            return Math.Max(5.0, GetBaseOffset(analysis) * 0.75);
        }

        public static double GetLaneSpacing(ViewAnalysis analysis)
        {
            return Clamp(GetBaseOffset(analysis) * 0.55, 4.5, 7.0);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
