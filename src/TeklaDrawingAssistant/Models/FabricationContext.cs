using System.Collections.Generic;

namespace TeklaDrawingAssistant.Models
{
    public enum FabricationMemberType
    {
        Unknown,
        ISection,
        Pfc,
        Chs,
        Rhs,
        Shs,
        Plate,
        BuiltUp
    }

    public enum FabricationDatumType
    {
        Unknown,
        MainPartStart,
        MainPartEnd,
        MainPartCentreLine,
        WebCentreLine,
        FlangeCentreLine,
        ContactPoint,
        ContactCentreLine,
        ReferencePoint,
        PlateEdge,
        HoleCentre,
        ChsCircumference,
        ConvexFace
    }

    public enum EndConditionType
    {
        Unknown,
        Square,
        WebSkewed,
        WebSquareCut,
        WebSkewCut
    }

    public enum FeatureType
    {
        Unknown,
        Plate,
        Fitting,
        BoltGroup,
        HoleGroup,
        Stub,
        Gusset
    }

    public sealed class FabricationContext
    {
        public string DrawingName { get; set; }
        public string MainPartProfile { get; set; }
        public FabricationMemberType MemberType { get; set; }
        public bool IsCurved { get; set; }
        public bool IsBuiltUp { get; set; }
        public EndConditionType StartEndCondition { get; set; }
        public EndConditionType FinishEndCondition { get; set; }
        public List<FabricationFeature> Features { get; } = new List<FabricationFeature>();
        public List<string> AnalysisWarnings { get; } = new List<string>();
    }

    public sealed class FabricationFeature
    {
        public int ModelIdentifierId { get; set; }
        public FeatureType Type { get; set; }
        public string Description { get; set; }
        public int HoleCount { get; set; }
        public bool IsSkewed { get; set; }
        public bool IsOnCurvedMember { get; set; }
        public FabricationDatumType PreferredDatum { get; set; }
    }
}
