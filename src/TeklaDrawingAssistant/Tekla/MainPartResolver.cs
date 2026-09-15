using System;
using Tekla.Structures;
using Tekla.Structures.Drawing;
using Tekla.Structures.Model;
using ModelPart = Tekla.Structures.Model.Part;

namespace TeklaDrawingAssistant.Tekla
{
    public sealed class MainPartResolver
    {
        private readonly Model _model;

        public MainPartResolver(Model model)
        {
            _model = model;
        }

        public ModelPart Resolve(Drawing drawing, out Identifier mainPartIdentifier)
        {
            mainPartIdentifier = null;

            var assemblyDrawing = drawing as AssemblyDrawing;
            if (assemblyDrawing != null)
            {
                var assembly = _model.SelectModelObject(assemblyDrawing.AssemblyIdentifier) as Assembly;
                var mainPart = assembly?.GetMainPart() as ModelPart;
                if (mainPart == null)
                    throw new InvalidOperationException("Could not resolve the assembly main part.");

                mainPartIdentifier = mainPart.Identifier;
                return mainPart;
            }

            var singlePartDrawing = drawing as SinglePartDrawing;
            if (singlePartDrawing != null)
            {
                var part = _model.SelectModelObject(singlePartDrawing.PartIdentifier) as ModelPart;
                if (part == null)
                    throw new InvalidOperationException("Could not resolve the single-part drawing model object.");

                mainPartIdentifier = part.Identifier;
                return part;
            }

            throw new NotSupportedException("The first version supports assembly and single-part drawings only.");
        }
    }
}
