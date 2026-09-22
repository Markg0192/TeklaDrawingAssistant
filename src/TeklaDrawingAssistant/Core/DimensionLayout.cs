using System;
using TeklaDrawingAssistant.Models;

namespace TeklaDrawingAssistant.Core
{
    /// <summary>
    /// Dimension distances in the Tekla drawing API are paper millimetres.
    /// Keep them proportional to the actual view frame rather than asking the user
    /// for a fixed model-space looking value such as 25 mm.
    /// </summary>
    public static class DimensionLayout
    {
        public static double GetBaseOffset(ViewAnalysis analysis)
        {
            if (analysis == null || analysis.View == null)
                return 10.0;

            var width = Math.Abs(analysis.View.Width);
            var height = Math.Abs(analysis.View.Height);
            var shortSide = Math.Min(width, height);

            if (shortSide < 0.001)
                return 10.0;

            return Clamp(shortSide * 0.12, 8.0, 18.0);
        }

        public static double GetPartOffset(ViewAnalysis analysis)
        {
            return Math.Max(7.0, GetBaseOffset(analysis) * 0.80);
        }

        public static double GetLaneSpacing(ViewAnalysis analysis)
        {
            return Clamp(GetBaseOffset(analysis) * 0.65, 6.0, 10.0);
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
