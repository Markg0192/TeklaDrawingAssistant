using System;
using System.Text;
using TeklaDrawingAssistant.Models;
using TeklaDrawingAssistant.Tekla;

namespace TeklaDrawingAssistant.Core
{
    public sealed class DrawingAutomationService
    {
        private readonly TeklaSession _session;
        private readonly DrawingAnalyzer _analyzer;
        private readonly HoleDimensioner _dimensioner;

        public DrawingAutomationService(TeklaSession session)
        {
            _session = session;
            _analyzer = new DrawingAnalyzer(session);
            _dimensioner = new HoleDimensioner();
        }

        public DrawingAnalysisResult Analyze()
        {
            return _analyzer.Analyze();
        }

        public string DimensionHoles(DimensioningOptions options)
        {
            var analysis = _analyzer.Analyze();
            var log = new StringBuilder();
            var totalCreated = 0;

            log.AppendLine($"Drawing: {analysis.Drawing.Mark} - {analysis.Drawing.Name}");
            log.AppendLine($"Views found: {analysis.Views.Count}");
            log.AppendLine();

            foreach (var view in analysis.Views)
            {
                if (!view.ContainsMainPart)
                {
                    log.AppendLine($"{view.Name}: skipped - main part not visible.");
                    continue;
                }

                if (view.Kind == ViewKind.Unknown)
                {
                    log.AppendLine($"{view.Name}: skipped - orientation not recognised.");
                    continue;
                }

                var created = _dimensioner.Dimension(view, options);
                totalCreated += created;
                log.AppendLine($"{view.Name}: {view.Kind}, {view.HolePoints.Count} hole points, {created} dimension sets created.");
            }

            analysis.Drawing.CommitChanges();

            if (options.SaveDrawingAfterRun)
                _session.DrawingHandler.SaveActiveDrawing();

            log.AppendLine();
            log.AppendLine($"Created {totalCreated} dimension sets.");
            return log.ToString();
        }

        public static string FormatAnalysis(DrawingAnalysisResult analysis)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Drawing: {analysis.Drawing.Mark} - {analysis.Drawing.Name}");
            sb.AppendLine($"Main part: {analysis.MainPart.Profile.ProfileString}");
            sb.AppendLine($"Views: {analysis.Views.Count}");
            sb.AppendLine();

            foreach (var view in analysis.Views)
            {
                sb.AppendLine($"[{view.Kind}] {view.Name}");
                sb.AppendLine($"  Main part visible: {view.ContainsMainPart}");
                sb.AppendLine($"  Parts: {view.PartCount}");
                sb.AppendLine($"  Bolt groups: {view.BoltGroupCount}");
                sb.AppendLine($"  Hole points: {view.HolePoints.Count}");
                sb.AppendLine($"  Existing dimension sets: {view.ExistingDimensionSetCount}");
                sb.AppendLine($"  Marks: {view.MarkCount}");
                sb.AppendLine($"  Welds: {view.WeldCount}");
                sb.AppendLine();
            }

            return sb.ToString();
        }
    }
}
