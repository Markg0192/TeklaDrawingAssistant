using System.Collections.Generic;

namespace TeklaDrawingAssistant.Models
{
    public enum DimensionRequirementType
    {
        Overall,
        FeatureLocation,
        HolePattern,
        Pitch,
        Gauge,
        Radius,
        Check,
        Skew,
        LocalGeometry
    }

    public sealed class DimensionPlan
    {
        public string DrawingName { get; set; }
        public List<DimensionRequirement> Requirements { get; } = new List<DimensionRequirement>();
        public List<string> Warnings { get; } = new List<string>();

        public bool RequiresManualReview => Warnings.Count > 0;
    }

    public sealed class DimensionRequirement
    {
        public DimensionRequirementType Type { get; set; }
        public string Description { get; set; }
        public int? ModelIdentifierId { get; set; }
        public FabricationDatumType Datum { get; set; }
        public string PreferredView { get; set; }
        public string RuleSource { get; set; }
        public int Priority { get; set; }

        public override string ToString()
        {
            var datumText = Datum == FabricationDatumType.Unknown ? string.Empty : " from " + Datum;
            return Description + datumText;
        }
    }
}
