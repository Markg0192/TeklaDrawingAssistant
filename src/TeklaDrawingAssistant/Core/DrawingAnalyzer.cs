using TeklaDrawingAssistant.Models;
using TeklaDrawingAssistant.Tekla;
using Tekla.Structures.Drawing;
using DrawingBolt = Tekla.Structures.Drawing.Bolt;
using DrawingMark = Tekla.Structures.Drawing.Mark;
using DrawingWeld = Tekla.Structures.Drawing.Weld;

namespace TeklaDrawingAssistant.Core
{
    public sealed class DrawingAnalyzer
    {
        private readonly TeklaSession _session;
        private readonly MainPartResolver _mainPartResolver;
        private readonly ViewGeometryReader _geometryReader;
        private readonly ViewClassifier _viewClassifier;

        public DrawingAnalyzer(TeklaSession session)
        {
            _session = session;
            _mainPartResolver = new MainPartResolver(session.Model);
            _geometryReader = new ViewGeometryReader(session.Model);
            _viewClassifier = new ViewClassifier();
        }

        public DrawingAnalysisResult Analyze()
        {
            var drawing = _session.GetActiveDrawing();
            var mainPart = _mainPartResolver.Resolve(drawing, out var mainPartIdentifier);

            var result = new DrawingAnalysisResult
            {
                Drawing = drawing,
                MainPart = mainPart,
                MainPartIdentifier = mainPartIdentifier
            };

            var views = drawing.GetSheet().GetAllViews();
            while (views.MoveNext())
            {
                var view = views.Current as View;
                if (view == null)
                    continue;

                var containsMainPart = _geometryReader.ContainsModelObject(view, mainPartIdentifier);
                var analysis = new ViewAnalysis
                {
                    View = view,
                    Name = string.IsNullOrWhiteSpace(view.Name) ? "Unnamed view" : view.Name,
                    ContainsMainPart = containsMainPart,
                    Kind = containsMainPart ? _viewClassifier.Classify(view, mainPart) : ViewKind.Unknown,
                    PartCount = _geometryReader.CountObjects<Part>(view),
                    BoltGroupCount = _geometryReader.CountObjects<DrawingBolt>(view),
                    ExistingDimensionSetCount = _geometryReader.CountObjects<StraightDimensionSet>(view),
                    MarkCount = _geometryReader.CountObjects<DrawingMark>(view),
                    WeldCount = _geometryReader.CountObjects<DrawingWeld>(view)
                };

                if (containsMainPart)
                {
                    analysis.MainPartBounds = _geometryReader.GetPartBounds(view, mainPartIdentifier);
                    analysis.HolePoints.AddRange(_geometryReader.GetVisibleBoltPositions(view));
                }

                result.Views.Add(analysis);
            }

            return result;
        }
    }
}
