using Raffinert.Consistency.Expressions;

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

    internal ExternalConsumerAnalysis GetExternalConsumerAnalysis(IDerivedDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_derivedStates.ContainsKey(definition))
            throw new ArgumentException("The derived definition belongs to another compiled model.", nameof(definition));
        return CompileExternalConsumerAnalysis(
            definition.SourceSet,
            definition.Analysis.Dependencies
                .Where(dependency => dependency.Role == ExpressionParameterRole.DerivedSource),
            definition);
    }

    internal ExternalConsumerAnalysis GetExternalConsumerAnalysis(IInvariantDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_invariants.ContainsKey(definition))
            throw new ArgumentException("The invariant belongs to another compiled model.", nameof(definition));
        return CompileExternalConsumerAnalysis(
            definition.SourceSet,
            definition.Analysis.Dependencies
                .Where(dependency => dependency.Role == ExpressionParameterRole.InvariantSource),
            definition);
    }

    private static ExternalConsumerAnalysis CompileExternalConsumerAnalysis(
        IObjectSetDefinition rootSet,
        IEnumerable<TrackedExpressionDependency> dependencies,
        object definition)
    {
        var descriptors = new List<ExternalConsumerDescriptor>();
        var unsupported = false;
        foreach (var dependency in dependencies)
        {
            var path = dependency.Path;
            if (!DependencyPathNavigation.RequiresReverseOwnerCoverage(path))
                continue;
            if (path.Segments.Count != 2 ||
                !DependencyPathNavigation.IsNavigation(path.Segments[0]) ||
                DependencyPathNavigation.IsCollection(path.Segments[0].Member))
            {
                unsupported = true;
                continue;
            }
            descriptors.Add(new ExternalConsumerDescriptor(
                rootSet,
                path.Segments[0].Member,
                path.Segments[0].ValueType,
                path.Segments[1].Member,
                definition));
        }
        return new ExternalConsumerAnalysis(
            descriptors
                .DistinctBy(descriptor => (descriptor.RootSet, descriptor.Navigation, descriptor.TargetMember))
                .ToArray(),
            unsupported);
    }

    private IReadOnlyList<ConsistencyScopeGap> GetScopeGaps(
        IReadOnlyList<ScopeRequirement> requirements,
        ConsistencyScope? scope)
    {
        ValidateScopeOwnership(scope);

        return requirements
            .Where(requirement => scope is null || !scope.Contains(requirement.Set))
            .Select(requirement => new ConsistencyScopeGap(
                requirement.Set.Id,
                requirement.Set.DefinitionKey,
                requirement.Set.ObjectType,
                (ConsistencyScopeRequirementKind)requirement.Reason))
            .ToArray();
    }

    internal void ValidateScopeOwnership(ConsistencyScope? scope)
    {
        if (scope is not null && scope.CompleteSets.Any(set => !_sets.ContainsKey(set)))
            throw new ArgumentException(
                "The consistency scope contains an object set from another compiled model.",
                nameof(scope));
    }
}
