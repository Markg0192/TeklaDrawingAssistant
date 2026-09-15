using System;
using Tekla.Structures.Drawing;
using Tekla.Structures.Model;

namespace TeklaDrawingAssistant.Tekla
{
    public sealed class TeklaSession
    {
        public Model Model { get; }
        public DrawingHandler DrawingHandler { get; }

        public TeklaSession()
        {
            Model = new Model();
            DrawingHandler = new DrawingHandler();
        }

        public bool IsConnected => Model.GetConnectionStatus() && DrawingHandler.GetConnectionStatus();

        public Drawing GetActiveDrawing()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Could not connect to Tekla Structures 2026. Make sure a model is open.");

            var drawing = DrawingHandler.GetActiveDrawing();
            if (drawing == null)
                throw new InvalidOperationException("Open a drawing in Tekla before running the tool.");

            return drawing;
        }
    }
}
