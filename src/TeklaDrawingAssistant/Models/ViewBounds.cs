namespace TeklaDrawingAssistant.Models
{
    public sealed class ViewBounds
    {
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }

        public double CentreX => (MinX + MaxX) / 2.0;
        public double CentreY => (MinY + MaxY) / 2.0;
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }
}
