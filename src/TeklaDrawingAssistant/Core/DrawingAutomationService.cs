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
        private readonly ControlledEndViewBuilder _endViewBuilder;
        private readonly GeneratedViewPostProcessor _viewPostProcessor;
        private readonly FittingSetoutPlanner _setoutPlanner;
        private readonly FittingDimensioner _fittingDimensioner;
        private readonly MainPartFeatureDimensioner _mainPartFeatureDimensioner;
        private readonly EndPlateDimensioner _endPlateDimensioner;

        public DrawingAutomationService(TeklaSession session)
        {
            _session = session;
            _analyzer = new DrawingAnalyzer(session);
            _contextBuilder = new FabricationContextBuilder();
            _ruleEngine = new DimensionRuleEngine();
            _flangeViewPlanner = new FlangeOnlyViewCreationPlanner(session.Model);
            _endViewBuilder = new ControlledEndViewBuilder(session.Model);
            _viewPostProcessor = new GeneratedViewPostProcessor();
            _setoutPlanner = new FittingSetoutPlanner(session.Model);
            _fittingDimensioner = new FittingDimensioner(session.Model);
            _mainPartFeatureDimensioner = new MainPartFeatureDimensioner(session.Model);
            _endPlateDimensioner = new EndPlateDimensioner(session.Model);
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
            var viewMessages = new System.Collections.Generic.List<string>();
            var dimensionMessages = new System.Collections.Generic.List<string>();

            var analysis = _analyzer.Analyze();
            log.AppendLine($"Drawing: {analysis.Drawing.Mark} - {analysis.Drawing.Name}");
            log.AppendLine($"Starting views: {analysis.Views.Count}");
            log.AppendLine();
            log.AppendLine("DRAWING BUILD");
            log.AppendLine(new string('-', 40));

            var flangeViews = _flangeViewPlanner.RebuildFlangeViews(analysis, viewMessages);

            analysis = _analyzer.Analyze();
            var endViews = _endViewBuilder.Build(analysis, viewMessages);

            analysis = _analyzer.Analyze();
            _viewPostProcessor.Apply(analysis, viewMessages);
            analysis.Drawing.CommitChanges();

            var dimensionAnalysis = _analyzer.Analyze();
            var setoutPlan = _setoutPlanner.Build(dimensionAnalysis);
            _fittingDimensioner.Dimension(dimensionAnalysis, setoutPlan, dimensionMessages);

            // Main-member fabrication features are separate from fitting set-out:
            // holes through the web/top/bottom flange plus local boolean cuts/notches.
            var mainFeatureAnalysis = _analyzer.Analyze();
            _mainPartFeatureDimensioner.Dimension(mainFeatureAnalysis, dimensionMessages);

            // End sections have their own fabrication convention. Replace any generic
            // dimensions in A-A/B-B with the tightly controlled end-plate set-out.
            var endDimensionAnalysis = _analyzer.Analyze();
            _endPlateDimensioner.Dimension(endDimensionAnalysis, dimensionMessages);

            // Dimensions/marks change the amount of clear paper needed around each view.
            // Do a final deterministic layout pass only after annotation generation.
            var finalLayoutAnalysis = _analyzer.Analyze();
            _viewPostProcessor.FinaliseLayout(finalLayoutAnalysis, viewMessages);
            finalLayoutAnalysis.Drawing.CommitChanges();
            _session.DrawingHandler.SaveActiveDrawing();

            foreach (var message in viewMessages)
                log.AppendLine("  " + message);

            var finalAnalysis = _analyzer.Analyze();
            var finalDimensionCount = finalAnalysis.Views.Sum(view => view.ExistingDimensionSetCount);

            log.AppendLine();
            log.AppendLine($"Flange views created: {flangeViews}.");
            log.AppendLine($"End views created: {endViews}.");
            log.AppendLine($"Final views: {finalAnalysis.Views.Count}");
            log.AppendLine();

            AppendFittingSetoutPlan(log, setoutPlan);

            log.AppendLine();
            foreach (var message in dimensionMessages)
                log.AppendLine("  " + message);

            log.AppendLine();
            log.AppendLine("Final straight dimension sets on drawing: " + finalDimensionCount + ".");
            log.AppendLine("End sections use a dedicated end-plate dimensioning pass after the generic fitting dimensions.");
            log.AppendLine("Main-part hole groups are dimensioned on their owning web/top/bottom face; local boolean cuts/notches are dimensioned locally.");
            log.AppendLine("Final view layout is applied after dimensions so annotation corridors are preserved.");
            log.AppendLine("Part marks, weld marks and other misc annotations remain for later stages.");

            return log.ToString();
        }

        private static void AppendFittingSetoutPlan(StringBuilder log, FittingSetoutPlan plan)
        {
            log.AppendLine("FITTING SET-OUT PLAN");
            log.AppendLine(new string('-', 40));
            log.AppendLine("Rule: every fitting must be located in at least two independent member axes; hole centres are preferred where visible.");
            log.AppendLine();

            foreach (var fitting in plan.Fittings.OrderBy(item => item.PartId))
            {
                var profile = string.IsNullOrWhiteSpace(fitting.Profile) ? string.Empty : " (" + fitting.Profile + ")";
                log.AppendLine("Part " + fitting.PartId + profile);
                log.AppendLine("  Core view: " + fitting.CoreView);

                foreach (var requirement in fitting.Requirements.OrderBy(item => item.Axis))
                {
                    var target = requirement.TargetType == FittingSetoutTargetType.HoleGroup
                        ? "hole group " + requirement.HoleGroupId
                        : "fitting geometry";

                    log.AppendLine(
                        "  + " + requirement.AxisDescription +
                        " -> " + requirement.ViewName +
                        " | target: " + target);
                    log.AppendLine("      " + requirement.Reason);
                }

                log.AppendLine(fitting.IsComplete
                    ? "  COMPLETE: two independent set-out directions assigned."
                    : "  INCOMPLETE: fewer than two independent set-out directions assigned.");
                log.AppendLine();
            }

            if (plan.Warnings.Count > 0)
            {
                log.AppendLine("SET-OUT WARNINGS");
                foreach (var warning in plan.Warnings)
                    log.AppendLine("  ! " + warning);
            }
            else
            {
                log.AppendLine("All fittings have two independent set-out directions assigned.");
            }
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
