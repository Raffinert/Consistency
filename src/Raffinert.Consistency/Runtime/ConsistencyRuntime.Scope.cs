namespace Raffinert.Consistency;

public sealed partial class ConsistencyRuntime
{
    internal IReadOnlyList<ScopeRequirement> GetScopeRequirements(IDerivedDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_derivedStates.ContainsKey(definition))
            throw new ArgumentException("The derived definition belongs to another compiled model.", nameof(definition));
        return ConsistencyScopeRequirementCompiler.Compile(definition);
    }

    internal IReadOnlyList<ScopeRequirement> GetScopeRequirements(IInvariantDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_invariants.ContainsKey(definition))
            throw new ArgumentException("The invariant belongs to another compiled model.", nameof(definition));
        return ConsistencyScopeRequirementCompiler.Compile(definition);
    }

    internal IReadOnlyList<ConsistencyScopeGap> GetScopeGaps(
        IDerivedDefinition definition,
        ConsistencyScope? scope) => GetScopeGaps(GetScopeRequirements(definition), scope);

    internal IReadOnlyList<ConsistencyScopeGap> GetScopeGaps(
        IInvariantDefinition definition,
        ConsistencyScope? scope) => GetScopeGaps(GetScopeRequirements(definition), scope);

    private IReadOnlyList<ConsistencyScopeGap> GetScopeGaps(
        IReadOnlyList<ScopeRequirement> requirements,
        ConsistencyScope? scope)
    {
        if (scope is not null && scope.CompleteSets.Any(set => !_sets.ContainsKey(set)))
            throw new ArgumentException(
                "The consistency scope contains an object set from another compiled model.",
                nameof(scope));

        return requirements
            .Where(requirement => scope is null || !scope.Contains(requirement.Set))
            .Select(requirement => new ConsistencyScopeGap(
                requirement.Set.Id,
                requirement.Set.DefinitionKey,
                requirement.Set.ObjectType,
                (ConsistencyScopeRequirementKind)requirement.Reason))
            .ToArray();
    }
}
