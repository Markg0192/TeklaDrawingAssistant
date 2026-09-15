namespace TeklaDrawingAssistant.Models
{
    public sealed class DimensioningOptions
    {
        public double DimensionOffset { get; set; } = 12.0;
        public double CoordinateTolerance { get; set; } = 0.5;
        public bool DeleteExistingStraightDimensions { get; set; }
        public bool SaveDrawingAfterRun { get; set; } = true;
    }
}
