namespace Raffinert.Relations;

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
