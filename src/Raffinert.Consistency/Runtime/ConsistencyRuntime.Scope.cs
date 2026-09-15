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
}
