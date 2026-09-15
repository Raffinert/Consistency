namespace Raffinert.Consistency;

internal enum ScopeRequirementReason
{
    RelationSourceCoverage,
    RelationTargetCoverage,
    ProjectedConsumerCoverage
}

internal sealed record ScopeRequirement(
    IObjectSetDefinition Set,
    ScopeRequirementReason Reason);

internal static class ConsistencyScopeRequirementCompiler
{
    public static IReadOnlyList<ScopeRequirement> Compile(IDerivedDefinition definition)
    {
        var requirements = new HashSet<ScopeRequirement>();
        AddDerived(definition, requirements, new HashSet<IDerivedDefinition>(ReferenceEqualityComparer.Instance));
        return Order(requirements);
    }

    public static IReadOnlyList<ScopeRequirement> Compile(IInvariantDefinition definition)
    {
        var requirements = new HashSet<ScopeRequirement>();
        var visited = new HashSet<IDerivedDefinition>(ReferenceEqualityComparer.Instance);
        foreach (var upstream in definition.UpstreamDerived)
            AddDerived(upstream, requirements, visited);
        return Order(requirements);
    }

    private static void AddDerived(
        IDerivedDefinition definition,
        HashSet<ScopeRequirement> requirements,
        HashSet<IDerivedDefinition> visited)
    {
        if (!visited.Add(definition)) return;
        foreach (var input in definition.Inputs)
        {
            switch (input)
            {
                case RelationDerivedInput relation:
                    requirements.Add(new ScopeRequirement(
                        relation.Relation.LeftSet,
                        ScopeRequirementReason.RelationSourceCoverage));
                    requirements.Add(new ScopeRequirement(
                        relation.Relation.RightSet,
                        ScopeRequirementReason.RelationTargetCoverage));
                    break;
                case UpstreamDerivedInput upstream:
                    AddDerived(upstream.Upstream, requirements, visited);
                    if (upstream.IsProjected)
                        requirements.Add(new ScopeRequirement(
                            definition.SourceSet,
                            ScopeRequirementReason.ProjectedConsumerCoverage));
                    break;
            }
        }
    }

    private static IReadOnlyList<ScopeRequirement> Order(IEnumerable<ScopeRequirement> requirements) =>
        requirements.OrderBy(requirement => requirement.Set.Id)
            .ThenBy(requirement => requirement.Reason)
            .ToArray();
}
