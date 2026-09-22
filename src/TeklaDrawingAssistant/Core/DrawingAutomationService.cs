using System;
using System.Linq;
using System.Text;
using TeklaDrawingAssistant.Models;
using TeklaDrawingAssistant.Tekla;

namespace TeklaDrawingAssistant.Core
{
    public sealed class DrawingAutomationService
    {
        private readonly TeklaSession _session;
        private readonly DrawingAnalyzer _analyzer;
        private readonly FabricationContextBuilder _contextBuilder;
        private readonly DimensionRuleEngine _ruleEngine;
        private readonly ViewCreationPlanner _viewCreationPlanner;
        private readonly BeamEndViewEnsurer _endViewEnsurer;
        private readonly EndSectionDiagnosticProbe _endSectionDiagnosticProbe;

        public DrawingAutomationService(TeklaSession session)
        {
            _session = session;
            _analyzer = new DrawingAnalyzer(session);
            _contextBuilder = new FabricationContextBuilder();
            _ruleEngine = new DimensionRuleEngine();
            _viewCreationPlanner = new ViewCreationPlanner(session.Model);
            _endViewEnsurer = new BeamEndViewEnsurer(session.Model);
            _endSectionDiagnosticProbe = new EndSectionDiagnosticProbe(session.Model);
        }

        public DrawingAnalysisResult Analyze()
        {
            return _analyzer.Analyze();
        }

        public DimensionPlan AnalyzeDimensionRules()
        {
            var analysis = _analyzer.Analyze();
            var context = _contextBuilder.Build(analysis);
            return _ruleEngine.BuildPlan(context);
        }

        public string AnalyzeDimensionRulesText()
        {
            var analysis = _analyzer.Analyze();
            var context = _contextBuilder.Build(analysis);
            var plan = _ruleEngine.BuildPlan(context);

            var sb = new StringBuilder();
            sb.AppendLine($"Drawing: {analysis.Drawing.Mark} - {analysis.Drawing.Name}");
            sb.AppendLine($"Main part: {analysis.MainPart.Profile.ProfileString}");
            sb.AppendLine($"Member type: {context.MemberType}");
            sb.AppendLine();
            sb.AppendLine("PROPOSED DIMENSION PLAN");
            sb.AppendLine(new string('-', 40));

            foreach (var group in plan.Requirements
                .OrderBy(requirement => requirement.Priority)
                .ThenBy(requirement => requirement.Type)
                .GroupBy(requirement => requirement.Type))
            {
                sb.AppendLine(group.Key.ToString().ToUpperInvariant());

                foreach (var requirement in group)
                {
                    sb.Append("  + ");
                    sb.AppendLine(requirement.ToString());

                    if (!string.IsNullOrWhiteSpace(requirement.PreferredView))
                        sb.AppendLine("      View: " + requirement.PreferredView);

                    if (!string.IsNullOrWhiteSpace(requirement.RuleSource))
                        sb.AppendLine("      Rule: " + requirement.RuleSource);
                }

                sb.AppendLine();
            }

            if (plan.Warnings.Count > 0)
            {
                sb.AppendLine("MANUAL REVIEW / NOT YET CLASSIFIED");
                sb.AppendLine(new string('-', 40));

                foreach (var warning in plan.Warnings)
                    sb.AppendLine("  ! " + warning);
            }
            else
            {
                sb.AppendLine("No manual-review warnings.");
            }

            return sb.ToString();
        }

        public string BuildViewsOnly()
        {
            var log = new StringBuilder();
            var messages = new System.Collections.Generic.List<string>();

            var analysis = _analyzer.Analyze();
            log.AppendLine($"Drawing: {analysis.Drawing.Mark} - {analysis.Drawing.Name}");
            log.AppendLine($"Starting views: {analysis.Views.Count}");
            log.AppendLine();
            log.AppendLine("VIEW CREATION ONLY");
            log.AppendLine(new string('-', 40));

            var created = _viewCreationPlanner.RebuildRequiredViews(analysis, messages);

            // The general face classifier is useful for top/bottom/web decisions, but end
            // plates need a stronger test. Detect them by physical position along the main
            // member and build the section through the actual plate centre.
            var endSections = _endViewEnsurer.EnsureEndViews(analysis, messages);

            // If the normal end-section route still fails, run a deliberately verbose probe.
            // It logs every plate candidate, source-view coordinates, cut line, depth,
            // generated restriction box and the actual model IDs Tekla put in each temporary
            // section. It also tries a few alternate cuts/depths and keeps one if it succeeds.
            var rescuedEndSections = _endSectionDiagnosticProbe.DiagnoseAndRescue(analysis, messages);
            endSections += rescuedEndSections;

            foreach (var message in messages)
                log.AppendLine("  " + message);

            analysis.Drawing.CommitChanges();
            _session.DrawingHandler.SaveActiveDrawing();

            var finalAnalysis = _analyzer.Analyze();
            log.AppendLine();
            log.AppendLine($"General views created: {created}.");
            log.AppendLine($"End sections detected/rebuilt: {endSections}.");
            log.AppendLine($"Final views: {finalAnalysis.Views.Count}");
            log.AppendLine("Dimension creation is currently disabled while view setup is being tuned.");

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
