namespace TeklaDrawingAssistant.Models
{
    public sealed class DimensioningOptions
    {
        public double DimensionOffset { get; set; } = 25.0;
        public double DimensionOffsetScale { get; set; } = 0.15;
        public double MaximumDimensionOffset { get; set; } = 100.0;
        public double CoordinateTolerance { get; set; } = 0.5;
        public bool DeleteExistingStraightDimensions { get; set; }
        public bool SaveDrawingAfterRun { get; set; } = true;
    }
}
