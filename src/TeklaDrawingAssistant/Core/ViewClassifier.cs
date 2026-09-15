using TeklaDrawingAssistant.Models;
using Tekla.Structures.Drawing;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Core
{
    public sealed class ViewClassifier
    {
        public ViewKind Classify(View view, ModelPart mainPart)
        {
            var partCs = mainPart.GetCoordinateSystem();
            var viewCs = view.DisplayCoordinateSystem;
            var viewNormal = GeometryMath.Cross(viewCs.AxisX, viewCs.AxisY);

            var alongMember = GeometryMath.AbsoluteDot(viewNormal, partCs.AxisX);
            var acrossFlange = GeometryMath.AbsoluteDot(viewNormal, partCs.AxisY);
            var vertical = GeometryMath.AbsoluteDot(viewNormal, GeometryMath.Cross(partCs.AxisX, partCs.AxisY));

            const double parallelTolerance = 0.90;

            if (alongMember >= parallelTolerance)
                return ViewKind.End;

            if (acrossFlange >= parallelTolerance)
                return ViewKind.Web;

            if (vertical >= parallelTolerance)
                return ViewKind.Flange;

            return ViewKind.Unknown;
        }
    }
}
