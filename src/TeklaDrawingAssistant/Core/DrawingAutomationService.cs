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
        private readonly FlangeOnlyViewCreationPlanner _flangeViewPlanner;
        private readonly OutsideInEndSectionBuilder _endSectionBuilder;

        public DrawingAutomationService(TeklaSession session)
        {
            _session = session;
            _analyzer = new DrawingAnalyzer(session);
            _contextBuilder = new FabricationContextBuilder();
            _ruleEngine = new DimensionRuleEngine();
            _flangeViewPlanner = new FlangeOnlyViewCreationPlanner(session.Model);
            _endSectionBuilder = new OutsideInEndSectionBuilder(session.Model);
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

            // Keep the original view as the web/base view and only create flange views here.
            // End sections are handled separately so no older inside-out section attempt can
            // run before the fabrication-correct outside-in attempt.
            var flangeViews = _flangeViewPlanner.RebuildFlangeViews(analysis, messages);

            // Re-analyse after adding the flange views. The retained original base view is
            // still selected by the end builder, while the new view list is now current.
            analysis = _analyzer.Analyze();

            // End policy is strict: OUTSIDE plate face first, looking towards the member.
            // The inside plate face is tried only if the outside attempt genuinely fails.
            var endSections = _endSectionBuilder.Build(analysis, messages);

            foreach (var message in messages)
                log.AppendLine("  " + message);

            analysis.Drawing.CommitChanges();
            _session.DrawingHandler.SaveActiveDrawing();

            var finalAnalysis = _analyzer.Analyze();
            log.AppendLine();
            log.AppendLine($"Flange views created: {flangeViews}.");
            log.AppendLine($"End sections created: {endSections}.");
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
