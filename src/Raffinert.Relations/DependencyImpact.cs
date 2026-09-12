namespace Raffinert.Relations;

/// <summary>Controls whether an affected cached result is stale or immediately unusable.</summary>
public enum DependencySeverity
{
    /// <summary>The cached result may be stale and should be recomputed when freshness is required.</summary>
    Dirty,

    /// <summary>The cached result must not be used before successful recomputation.</summary>
    Invalid
}

/// <summary>Configures semantic dependency severity for a derived definition.</summary>
public sealed class DerivedImpactPolicyBuilder
{
    internal DependencySeverity AddedSeverity { get; private set; } = DependencySeverity.Dirty;
    internal DependencySeverity RemovedSeverity { get; private set; } = DependencySeverity.Dirty;
    internal DependencySeverity ItemChangedSeverity { get; private set; } = DependencySeverity.Dirty;
    internal bool IsConfigured { get; private set; }

    public DerivedImpactPolicyBuilder MembershipAdded(DependencySeverity severity)
    {
        AddedSeverity = Validate(severity);
        IsConfigured = true;
        return this;
    }

    public DerivedImpactPolicyBuilder MembershipRemoved(DependencySeverity severity)
    {
        RemovedSeverity = Validate(severity);
        IsConfigured = true;
        return this;
    }

    public DerivedImpactPolicyBuilder ItemChanged(DependencySeverity severity)
    {
        ItemChangedSeverity = Validate(severity);
        IsConfigured = true;
        return this;
    }

    internal DerivedImpactPolicy Build() =>
        new(AddedSeverity, RemovedSeverity, ItemChangedSeverity, IsConfigured);

    private static DependencySeverity Validate(DependencySeverity severity) =>
        Enum.IsDefined(severity)
            ? severity
            : throw new ArgumentOutOfRangeException(nameof(severity));
}

internal sealed record DerivedImpactPolicy(
    DependencySeverity MembershipAdded,
    DependencySeverity MembershipRemoved,
    DependencySeverity ItemChanged,
    bool IsConfigured)
{
    public DependencyImpactKind ClassifyMembership(RelationImpact impact)
    {
        var severity = DependencySeverity.Dirty;
        if (impact.AddedPairs.Count > 0)
            severity = Max(severity, MembershipAdded);
        if (impact.RemovedPairs.Count > 0)
            severity = Max(severity, MembershipRemoved);
        return severity.ToKind();
    }

    private static DependencySeverity Max(DependencySeverity left, DependencySeverity right) =>
        left == DependencySeverity.Invalid || right == DependencySeverity.Invalid
            ? DependencySeverity.Invalid
            : DependencySeverity.Dirty;
}

internal static class DependencySeverityExtensions
{
    public static DependencyImpactKind ToKind(this DependencySeverity severity) => severity switch
    {
        DependencySeverity.Dirty => DependencyImpactKind.Dirty,
        DependencySeverity.Invalid => DependencyImpactKind.Invalid,
        _ => throw new ArgumentOutOfRangeException(nameof(severity))
    };
}

internal enum DependencyImpactKind
{
    Dirty,
    Invalid
}

internal sealed record RelationMembershipDependencyImpact(
    RelationImpact RelationImpact,
    IDerivedDefinition Dependent,
    IReadOnlyList<PropertyChange> Changes);

internal interface IDependencyImpactPolicy
{
    DependencyImpactKind Classify(RelationMembershipDependencyImpact impact);
}

internal sealed class DefaultDependencyImpactPolicy : IDependencyImpactPolicy
{
    public static DefaultDependencyImpactPolicy Instance { get; } = new();

    private DefaultDependencyImpactPolicy()
    {
    }

    public DependencyImpactKind Classify(RelationMembershipDependencyImpact impact) =>
        DependencyImpactKind.Dirty;
}
