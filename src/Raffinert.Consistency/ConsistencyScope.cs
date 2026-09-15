namespace Raffinert.Consistency;

/// <summary>
/// Records host assertions that runtime object sets are complete for an authoritative consistency boundary.
/// Raffinert does not verify these assertions against a database or another external store.
/// </summary>
public sealed class ConsistencyScope
{
    private readonly HashSet<IObjectSetDefinition> _completeSets =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Asserts that the runtime contains every object belonging to <paramref name="set"/> within the
    /// authoritative consistency boundary used by the current operation.
    /// </summary>
    public ConsistencyScope Complete<T>(ObjectSet<T> set) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        _completeSets.Add(set.Definition);
        return this;
    }

    internal IReadOnlyCollection<IObjectSetDefinition> CompleteSets => _completeSets;
    internal bool Contains(IObjectSetDefinition set) => _completeSets.Contains(set);
}

/// <summary>Describes why complete coverage of an object set is required.</summary>
public enum ConsistencyScopeRequirementKind
{
    RelationSourceCoverage,
    RelationTargetCoverage,
    ProjectedConsumerCoverage
}

/// <summary>
/// Describes missing complete-set coverage. <paramref name="ObjectSetId"/> is model-scoped diagnostic
/// identity and is not a durable identity across model versions.
/// </summary>
public sealed record ConsistencyScopeGap(
    int ObjectSetId,
    string? ObjectSetDefinitionKey,
    Type ObjectType,
    ConsistencyScopeRequirementKind RequirementKind);
