using System.Linq;
using TeklaDrawingAssistant.Models;

namespace TeklaDrawingAssistant.Core
{
    public sealed class DimensionRuleEngine
    {
        public DimensionPlan BuildPlan(FabricationContext context)
        {
            var plan = new DimensionPlan
            {
                DrawingName = context.DrawingName
            };

            AddOverallMemberRules(context, plan);
            AddCurvedMemberRules(context, plan);
            AddEndConditionRules(context, plan);
            AddFeatureRules(context, plan);
            AddAnalysisWarnings(context, plan);

            return plan;
        }

        private static void AddOverallMemberRules(FabricationContext context, DimensionPlan plan)
        {
            plan.Requirements.Add(new DimensionRequirement
            {
                Type = DimensionRequirementType.Overall,
                Description = "Main part overall length",
                Datum = FabricationDatumType.MainPartStart,
                PreferredView = "Main",
                RuleSource = "General fabrication dimensioning",
                Priority = 10
            });

            if (context.MemberType == FabricationMemberType.Chs)
            {
                plan.Requirements.Add(new DimensionRequirement
                {
                    Type = DimensionRequirementType.FeatureLocation,
                    Description = "Locate CHS attachments to the CHS circumference",
                    Datum = FabricationDatumType.ChsCircumference,
                    PreferredView = "End/Section",
                    RuleSource = "GDOM SEV-78 - CHS tab plates",
                    Priority = 20
                });
            }
        }

        private static void AddCurvedMemberRules(FabricationContext context, DimensionPlan plan)
        {
            if (!context.IsCurved)
                return;

            plan.Requirements.Add(new DimensionRequirement
            {
                Type = DimensionRequirementType.Radius,
                Description = "Curved member radius",
                Datum = FabricationDatumType.ConvexFace,
                PreferredView = "Main",
                RuleSource = "GDOM SEV-74 to SEV-77 - curved members",
                Priority = 20
            });

            plan.Requirements.Add(new DimensionRequirement
            {
                Type = DimensionRequirementType.Check,
                Description = "Independent curved member check dimension",
                Datum = FabricationDatumType.ReferencePoint,
                PreferredView = "Main",
                RuleSource = "GDOM SEV-74 to SEV-77 - curved members",
                Priority = 30
            });

            foreach (var feature in context.Features)
            {
                if (feature.Type != FeatureType.HoleGroup && feature.Type != FeatureType.BoltGroup && feature.Type != FeatureType.Fitting)
                    continue;

                plan.Requirements.Add(new DimensionRequirement
                {
                    Type = DimensionRequirementType.FeatureLocation,
                    Description = "Locate " + GetFeatureName(feature) + " on curved member using a separate dimension line",
                    ModelIdentifierId = feature.ModelIdentifierId,
                    Datum = FabricationDatumType.ConvexFace,
                    PreferredView = "Main",
                    RuleSource = "GDOM SEV-74 to SEV-77 - curved members",
                    Priority = 40
                });
            }
        }

        private static void AddEndConditionRules(FabricationContext context, DimensionPlan plan)
        {
            AddEndConditionRule("start", context.StartEndCondition, plan);
            AddEndConditionRule("finish", context.FinishEndCondition, plan);
        }

        private static void AddEndConditionRule(string endName, EndConditionType endCondition, DimensionPlan plan)
        {
            FabricationDatumType datum;

            switch (endCondition)
            {
                case EndConditionType.WebSkewed:
                    datum = FabricationDatumType.WebCentreLine;
                    break;
                case EndConditionType.WebSquareCut:
                    datum = FabricationDatumType.ContactPoint;
                    break;
                case EndConditionType.WebSkewCut:
                    datum = FabricationDatumType.ContactCentreLine;
                    break;
                case EndConditionType.Square:
                    return;
                default:
                    return;
            }

            plan.Requirements.Add(new DimensionRequirement
            {
                Type = DimensionRequirementType.Skew,
                Description = "Define " + endName + " end condition explicitly",
                Datum = datum,
                PreferredView = "Main",
                RuleSource = "GDOM SEV-80 - typical dimensioning details",
                Priority = 20
            });
        }

        private static void AddFeatureRules(FabricationContext context, DimensionPlan plan)
        {
            foreach (var feature in context.Features)
            {
                AddFeatureLocationRule(context, feature, plan);

                if (feature.Type == FeatureType.HoleGroup || feature.Type == FeatureType.BoltGroup)
                    AddHoleGroupRules(feature, plan);

                if (feature.IsSkewed)
                    AddSkewedFeatureRule(feature, plan);
            }
        }

        private static void AddFeatureLocationRule(FabricationContext context, FabricationFeature feature, DimensionPlan plan)
        {
            var datum = feature.PreferredDatum;

            if (datum == FabricationDatumType.Unknown)
                datum = DefaultDatumFor(context, feature);

            plan.Requirements.Add(new DimensionRequirement
            {
                Type = DimensionRequirementType.FeatureLocation,
                Description = "Locate " + GetFeatureName(feature),
                ModelIdentifierId = feature.ModelIdentifierId,
                Datum = datum,
                PreferredView = "Main",
                RuleSource = "GDOM general rule - locate feature from a stable fabrication datum",
                Priority = 40
            });
        }

        private static void AddHoleGroupRules(FabricationFeature feature, DimensionPlan plan)
        {
            if (feature.HoleCount <= 0)
                return;

            var convention = feature.HoleCount <= 4
                ? "small hole group convention (up to 4 holes)"
                : "large hole group convention (over 4 holes)";

            plan.Requirements.Add(new DimensionRequirement
            {
                Type = DimensionRequirementType.HolePattern,
                Description = "Dimension " + GetFeatureName(feature) + " using " + convention,
                ModelIdentifierId = feature.ModelIdentifierId,
                Datum = FabricationDatumType.HoleCentre,
                PreferredView = "Best true-shape view",
                RuleSource = "GDOM SEV-84 - hole quantity convention",
                Priority = 50
            });

            if (feature.HoleCount > 1)
            {
                plan.Requirements.Add(new DimensionRequirement
                {
                    Type = DimensionRequirementType.Pitch,
                    Description = "Define hole pitch within " + GetFeatureName(feature),
                    ModelIdentifierId = feature.ModelIdentifierId,
                    Datum = FabricationDatumType.HoleCentre,
                    PreferredView = "Best true-shape view",
                    RuleSource = "GDOM typical bolt/hole group dimensioning",
                    Priority = 60
                });
            }

            if (feature.HoleCount > 1)
            {
                plan.Requirements.Add(new DimensionRequirement
                {
                    Type = DimensionRequirementType.Gauge,
                    Description = "Define hole gauge where more than one row exists",
                    ModelIdentifierId = feature.ModelIdentifierId,
                    Datum = FabricationDatumType.HoleCentre,
                    PreferredView = "Best true-shape view",
                    RuleSource = "GDOM typical bolt/hole group dimensioning",
                    Priority = 60
                });
            }
        }

        private static void AddSkewedFeatureRule(FabricationFeature feature, DimensionPlan plan)
        {
            plan.Requirements.Add(new DimensionRequirement
            {
                Type = DimensionRequirementType.LocalGeometry,
                Description = "Dimension " + GetFeatureName(feature) + " locally in its own skewed orientation",
                ModelIdentifierId = feature.ModelIdentifierId,
                Datum = FabricationDatumType.ReferencePoint,
                PreferredView = "True-shape/Section",
                RuleSource = "GDOM SEV-83 - skewed plates and gussets",
                Priority = 50
            });
        }

        private static FabricationDatumType DefaultDatumFor(FabricationContext context, FabricationFeature feature)
        {
            if (context.MemberType == FabricationMemberType.Chs)
                return FabricationDatumType.ChsCircumference;

            if (context.IsCurved)
                return FabricationDatumType.ConvexFace;

            if (feature.Type == FeatureType.Gusset)
                return FabricationDatumType.ReferencePoint;

            return FabricationDatumType.MainPartStart;
        }

        private static void AddAnalysisWarnings(FabricationContext context, DimensionPlan plan)
        {
            foreach (var warning in context.AnalysisWarnings.Where(warning => !string.IsNullOrWhiteSpace(warning)))
                plan.Warnings.Add(warning);

            if (context.StartEndCondition == EndConditionType.Unknown)
                plan.Warnings.Add("Start end condition has not yet been classified.");

            if (context.FinishEndCondition == EndConditionType.Unknown)
                plan.Warnings.Add("Finish end condition has not yet been classified.");
        }

        private static string GetFeatureName(FabricationFeature feature)
        {
            if (!string.IsNullOrWhiteSpace(feature.Description))
                return feature.Description;

            if (feature.ModelIdentifierId > 0)
                return feature.Type + " " + feature.ModelIdentifierId;

            return feature.Type.ToString();
        }
    }
}
