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
        private readonly PartFaceDimensioner _partDimensioner;
        private readonly FabricationContextBuilder _contextBuilder;
        private readonly DimensionRuleEngine _ruleEngine;
        private readonly FaceViewPlanner _faceViewPlanner;

        public DrawingAutomationService(TeklaSession session)
        {
            _session = session;
            _analyzer = new DrawingAnalyzer(session);
            _dimensioner = new HoleDimensioner();
            _partDimensioner = new PartFaceDimensioner(session.Model);
            _contextBuilder = new FabricationContextBuilder();
            _ruleEngine = new DimensionRuleEngine();
            _faceViewPlanner = new FaceViewPlanner(session.Model);
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
            var log = new StringBuilder();
            var viewMessages = new System.Collections.Generic.List<string>();

            var analysis = _analyzer.Analyze();
            log.AppendLine($"Drawing: {analysis.Drawing.Mark} - {analysis.Drawing.Name}");
            log.AppendLine($"Starting views: {analysis.Views.Count}");

            var createdViews = _faceViewPlanner.EnsureRequiredViews(analysis, viewMessages);
            if (createdViews > 0)
            {
                // Re-read the drawing so the new Tekla views and their drawing objects are included.
                analysis = _analyzer.Analyze();
            }

            var ownership = _faceViewPlanner.BuildOwnership(analysis);

            log.AppendLine($"Face views created: {createdViews}");
            foreach (var message in viewMessages)
                log.AppendLine("  " + message);

            log.AppendLine();
            log.AppendLine("FEATURE VIEW OWNERSHIP");
            log.AppendLine(new string('-', 40));
            foreach (var message in ownership.Messages)
                log.AppendLine("  " + message);

            log.AppendLine();
            log.AppendLine("DIMENSIONING");
            log.AppendLine(new string('-', 40));

            var totalHoleDimensions = 0;
            var totalPartDimensions = 0;

            foreach (var view in analysis.Views)
            {
                if (!view.ContainsMainPart)
                {
                    log.AppendLine($"{view.Name}: skipped - main part not visible.");
                    continue;
                }

                var ownedHoleGroups = ownership.GetHoleGroups(view.View);
                var ownedParts = ownership.GetParts(view.View);

                if (ownedHoleGroups.Count == 0 && ownedParts.Count == 0)
                {
                    log.AppendLine($"{view.Name}: no fabrication features owned by this face view.");
                    continue;
                }

                if (view.Kind == ViewKind.Unknown)
                {
                    log.AppendLine($"{view.Name}: owns {ownedParts.Count} part(s) and {ownedHoleGroups.Count} hole group(s), but this is a custom/skew face. View ownership is correct; its custom dimension rule is still to be added.");
                    continue;
                }

                var holeDimensions = _dimensioner.Dimension(view, options, ownedHoleGroups);
                var partDimensions = _partDimensioner.Dimension(view, ownedParts, options);

                totalHoleDimensions += holeDimensions;
                totalPartDimensions += partDimensions;

                log.AppendLine(
                    $"{view.Name}: {view.Kind}, owns {ownedParts.Count} part(s) / {ownedHoleGroups.Count} hole group(s), " +
                    $"created {partDimensions} plate dimensions and {holeDimensions} hole dimensions.");
            }

            analysis.Drawing.CommitChanges();

            if (options.SaveDrawingAfterRun)
                _session.DrawingHandler.SaveActiveDrawing();

            log.AppendLine();
            log.AppendLine($"Created {totalPartDimensions} plate dimension sets and {totalHoleDimensions} hole dimension sets.");
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
