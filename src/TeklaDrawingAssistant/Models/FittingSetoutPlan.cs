using System.Collections.Generic;
using System.Linq;

namespace TeklaDrawingAssistant.Models
{
    public enum FittingSetoutAxis
    {
        MemberX,
        MemberY,
        MemberZ
    }

    public enum FittingSetoutTargetType
    {
        HoleGroup,
        PartGeometry
    }

    public sealed class FittingSetoutPlan
    {
        public List<FittingSetoutItem> Fittings { get; } = new List<FittingSetoutItem>();
        public List<string> Warnings { get; } = new List<string>();
    }

    public sealed class FittingSetoutItem
    {
        public int PartId { get; set; }
        public string Profile { get; set; }
        public string CoreView { get; set; }
        public List<FittingSetoutRequirement> Requirements { get; } = new List<FittingSetoutRequirement>();

        public bool IsComplete
        {
            get { return Requirements.Select(requirement => requirement.Axis).Distinct().Count() >= 2; }
        }
    }

    public sealed class FittingSetoutRequirement
    {
        public FittingSetoutAxis Axis { get; set; }
        public string AxisDescription { get; set; }
        public FittingSetoutTargetType TargetType { get; set; }
        public int? HoleGroupId { get; set; }
        public string ViewName { get; set; }
        public ViewKind ViewKind { get; set; }
        public double AxisProjection { get; set; }
        public double CoreAlignment { get; set; }
        public double SpaceScore { get; set; }
        public string Reason { get; set; }
    }
}
