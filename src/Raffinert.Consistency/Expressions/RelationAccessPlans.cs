namespace Raffinert.Consistency.Expressions;

/// <summary>Describes the candidate-access strategy selected for a relation.</summary>
internal abstract record RelationAccessPlan
{
    public abstract string DisplayName { get; }
}

/// <summary>Evaluates the predicate over every object in the right object set.</summary>
internal sealed record ScanAccessPlan : RelationAccessPlan
{
    public static ScanAccessPlan Instance { get; } = new();

    private ScanAccessPlan()
    {
    }

    public override string DisplayName => "Scan";
}

/// <summary>Uses recognized equality joins to narrow candidates before predicate evaluation.</summary>
internal sealed record HashJoinAccessPlan(IReadOnlyList<JoinKeyPart> JoinKeyParts) : RelationAccessPlan
{
    public override string DisplayName => "HashJoin";
}

/// <summary>Selects a safe access strategy from analyzed predicate information.</summary>
internal static class RelationPlanner
{
    public static RelationAccessPlan Plan(RelationAnalysis analysis, bool forceScan = false)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        return forceScan || analysis.JoinKeyParts.Count == 0
            ? ScanAccessPlan.Instance
            : new HashJoinAccessPlan(analysis.JoinKeyParts);
    }

    public static RelationAccessPlan PlanReverse(RelationAnalysis analysis, bool forceScan = false) =>
        Plan(analysis, forceScan);
}
