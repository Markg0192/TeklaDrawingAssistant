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
        private readonly HoleDimensioner _dimensioner;
        private readonly FabricationContextBuilder _contextBuilder;
        private readonly DimensionRuleEngine _ruleEngine;

        public DrawingAutomationService(TeklaSession session)
        {
            _session = session;
            _analyzer = new DrawingAnalyzer(session);
            _dimensioner = new HoleDimensioner();
            _contextBuilder = new FabricationContextBuilder();
            _ruleEngine = new DimensionRuleEngine();
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
