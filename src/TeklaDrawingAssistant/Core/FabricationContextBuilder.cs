using System;
using System.Collections.Generic;
using System.Linq;
using TeklaDrawingAssistant.Models;

namespace TeklaDrawingAssistant.Core
{
    public sealed class FabricationContextBuilder
    {
        public FabricationContext Build(DrawingAnalysisResult analysis)
        {
            if (analysis == null)
                throw new ArgumentNullException(nameof(analysis));

            if (analysis.MainPart == null)
                throw new InvalidOperationException("The drawing analysis does not contain a main part.");

            var profile = analysis.MainPart.Profile == null
                ? string.Empty
                : analysis.MainPart.Profile.ProfileString ?? string.Empty;

            var context = new FabricationContext
            {
                DrawingName = analysis.Drawing == null ? string.Empty : analysis.Drawing.Name,
                MainPartProfile = profile,
                MemberType = GetMemberType(profile),
                StartEndCondition = EndConditionType.Unknown,
                FinishEndCondition = EndConditionType.Unknown
            };

            AddHoleGroups(analysis, context);
            AddKnownLimitations(context);

            return context;
        }

        private static void AddHoleGroups(DrawingAnalysisResult analysis, FabricationContext context)
        {
            var groups = new Dictionary<int, HoleGroup>();

            foreach (var view in analysis.Views)
            {
                foreach (var group in view.HoleGroups)
                {
                    if (group.ModelIdentifierId <= 0 || group.Points.Count == 0)
                        continue;

                    if (!groups.ContainsKey(group.ModelIdentifierId))
                        groups.Add(group.ModelIdentifierId, group);
                }
            }

            foreach (var group in groups.Values.OrderBy(group => group.ModelIdentifierId))
            {
                context.Features.Add(new FabricationFeature
                {
                    ModelIdentifierId = group.ModelIdentifierId,
                    Type = FeatureType.HoleGroup,
                    Description = "hole group " + group.ModelIdentifierId,
                    HoleCount = group.Points.Count,
                    PreferredDatum = FabricationDatumType.Unknown
                });
            }
        }

        private static FabricationMemberType GetMemberType(string profile)
        {
            if (string.IsNullOrWhiteSpace(profile))
                return FabricationMemberType.Unknown;

            var value = profile.Trim().ToUpperInvariant();

            if (value.StartsWith("CHS"))
                return FabricationMemberType.Chs;

            if (value.StartsWith("RHS"))
                return FabricationMemberType.Rhs;

            if (value.StartsWith("SHS"))
                return FabricationMemberType.Shs;

            if (value.StartsWith("PFC") || value.Contains("CHANNEL"))
                return FabricationMemberType.Pfc;

            if (value.StartsWith("PL") || value.StartsWith("PLATE"))
                return FabricationMemberType.Plate;

            if (value.Contains("UB") || value.Contains("UC") || value.StartsWith("IPE") || value.StartsWith("HE"))
                return FabricationMemberType.ISection;

            return FabricationMemberType.Unknown;
        }

        private static void AddKnownLimitations(FabricationContext context)
        {
            context.AnalysisWarnings.Add("Curvature detection is not wired into the Tekla model reader yet.");
            context.AnalysisWarnings.Add("Start and finish end-condition classification is not wired in yet.");
            context.AnalysisWarnings.Add("Fittings, plates, gussets and stubs are not yet extracted into the rule context.");
        }
    }
}
