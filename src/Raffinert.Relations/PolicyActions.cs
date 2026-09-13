namespace Raffinert.Relations;

using System.Collections;
using System.Reflection;
using Raffinert.Relations.Expressions;

internal sealed record RepairRequest(
    IInvariantDefinition Invariant,
    object Source,
    DependencyImpactKind Reason);

internal sealed record ImmediateInvariantEvaluation(
    IInvariantRuntimeState Invariant,
    object Source);

internal sealed record RuntimeCommitResult(
    ChangeImpact Impact,
    RuntimePolicyActions PolicyActions);

/// <summary>A relation membership pair reported by a detailed runtime result.</summary>
public sealed record RelationPairImpact(object Left, object Right);

/// <summary>Describes the externally meaningful effects on one relation.</summary>
public sealed record RelationMutationImpact(
    int RelationId,
    Type LeftType,
    Type RightType,
    IReadOnlyList<RelationPairImpact> AddedPairs,
    IReadOnlyList<RelationPairImpact> RemovedPairs,
    IReadOnlyList<object> AffectedSources)
{
    public string? DefinitionKey { get; init; }
}

/// <summary>Describes one source's dependency impact.</summary>
public sealed record SourceDependencyImpact(object Source, DependencySeverity Severity)
{
    public SourceIdentity? SourceIdentity { get; init; }
}

/// <summary>Describes a derived definition's source-scoped impacts.</summary>
public sealed record DerivedMutationImpact(
    int DerivedId,
    IReadOnlyList<SourceDependencyImpact> Sources)
{
    public string? DefinitionKey { get; init; }
}

/// <summary>Describes an invariant definition's dependency impact.</summary>
public sealed record InvariantMutationImpact(
    int InvariantId,
    IReadOnlyList<SourceDependencyImpact> Sources)
{
    public string? DefinitionKey { get; init; }
}

/// <summary>A request to schedule repair work for an affected source.</summary>
/// <summary>A durable logical identity for a source in a named object set.</summary>
public sealed record SourceIdentity(string? ObjectSetKey, Type SourceType, object SourceKey)
{
    /// <summary>Whether this identity has an explicit object-set key suitable for external persistence.</summary>
    public bool IsDurable => ObjectSetKey is not null;
}

public sealed record RepairRequestInfo(
    int InvariantId,
    object Source,
    DependencySeverity Reason)
{
    public string? DefinitionKey { get; init; }
    public SourceIdentity? SourceIdentity { get; init; }
}

/// <summary>A request for an immediate invariant evaluation during policy dispatch.</summary>
public sealed record ImmediateEvaluationRequestInfo(
    int InvariantId,
    object Source)
{
    public string? DefinitionKey { get; init; }
    public SourceIdentity? SourceIdentity { get; init; }
}

/// <summary>
/// Stable data produced by a committed mutation. Application callbacks are not invoked until
/// <see cref="DispatchPolicies"/> is called.
/// </summary>
public sealed class RuntimeApplyResult
{
    private readonly Action _dispatch;

    internal RuntimeApplyResult(
        ChangeImpact changeImpact,
        IReadOnlyList<RelationMutationImpact> relationImpacts,
        IReadOnlyList<DerivedMutationImpact> derivedImpacts,
        IReadOnlyList<InvariantMutationImpact> invariantImpacts,
        IReadOnlyList<RepairRequestInfo> repairRequests,
        IReadOnlyList<ImmediateEvaluationRequestInfo> immediateEvaluationRequests,
        Action dispatch)
    {
        ChangeImpact = changeImpact;
        RelationImpacts = relationImpacts;
        DerivedImpacts = derivedImpacts;
        InvariantImpacts = invariantImpacts;
        RepairRequests = repairRequests;
        ImmediateEvaluationRequests = immediateEvaluationRequests;
        _dispatch = dispatch;
    }

    public ChangeImpact ChangeImpact { get; }
    public IReadOnlyList<RelationMutationImpact> RelationImpacts { get; }
    public IReadOnlyList<DerivedMutationImpact> DerivedImpacts { get; }
    public IReadOnlyList<InvariantMutationImpact> InvariantImpacts { get; }
    public IReadOnlyList<RepairRequestInfo> RepairRequests { get; }
    public IReadOnlyList<ImmediateEvaluationRequestInfo> ImmediateEvaluationRequests { get; }
    public bool PoliciesDispatched { get; private set; }

    /// <summary>Invokes configured post-commit callbacks once.</summary>
    public void DispatchPolicies()
    {
        if (PoliciesDispatched)
            throw new InvalidOperationException("Policy actions have already been dispatched.");
        PoliciesDispatched = true;
        _dispatch();
    }
}

/// <summary>
/// A validated runtime mutation awaiting commit. Prepared mutations are bound to the runtime version
/// at which they were created and can be committed and dispatched only once.
/// </summary>
public sealed class PreparedMutation
{
    internal PreparedMutation(
        RelationRuntime runtime,
        long baseVersion,
        IReadOnlyList<RuntimeMutation> lifecycleMutations,
        IReadOnlyList<PropertyChange> changes,
        IReadOnlyList<PreparedDomainAssumption> domainAssumptions)
    {
        Runtime = runtime;
        BaseVersion = baseVersion;
        LifecycleMutations = lifecycleMutations;
        Changes = changes;
        DomainAssumptions = domainAssumptions;
    }

    internal RelationRuntime Runtime { get; }
    internal IReadOnlyList<RuntimeMutation> LifecycleMutations { get; }
    internal IReadOnlyList<PropertyChange> Changes { get; }
    internal IReadOnlyList<PreparedDomainAssumption> DomainAssumptions { get; }
    internal RuntimePolicyActions? PolicyActions { get; private set; }

    /// <summary>The runtime version against which this mutation was validated.</summary>
    public long BaseVersion { get; }

    /// <summary>Whether the prepared mutation has been committed.</summary>
    public bool IsCommitted { get; private set; }

    /// <summary>Whether post-commit policy actions have been dispatched.</summary>
    public bool IsDispatched { get; private set; }

    internal void MarkCommitted(RuntimePolicyActions policyActions)
    {
        PolicyActions = policyActions;
        IsCommitted = true;
    }

    internal void MarkDispatched() => IsDispatched = true;

    internal void ValidateDomainState(IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets)
    {
        foreach (var assumption in DomainAssumptions)
            assumption.Validate();

        var simulated = sets.ToDictionary(
            pair => pair.Key,
            pair => new PreparedSetState(pair.Value));
        foreach (var mutation in LifecycleMutations)
        {
            var set = mutation is ObjectAdded added ? added.Set : ((ObjectRemoved)mutation).Set;
            if (mutation is ObjectAdded addition)
                simulated[set].Add(set, addition.Instance);
            else
                simulated[set].Remove(((ObjectRemoved)mutation).Instance);
        }
    }

    private sealed class PreparedSetState(ObjectSetRuntime runtime)
    {
        private readonly HashSet<object> _instances = runtime.Instances.ToHashSet(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, object> _keys = runtime.RegisteredEntries
            .ToDictionary(pair => pair.Key, pair => pair.Instance);
        private readonly Dictionary<object, object> _registeredKeys = runtime.RegisteredEntries
            .ToDictionary(pair => pair.Instance, pair => pair.Key, ReferenceEqualityComparer.Instance);

        public void Add(IObjectSetDefinition set, object instance)
        {
            if (!_instances.Add(instance))
                throw new InvalidOperationException("The prepared domain state drifted: an added instance is already registered.");
            var key = set.ReadKey(instance) ?? throw new InvalidOperationException(
                "The prepared domain state drifted: an added object's final key is null.");
            if (_keys.ContainsKey(key))
                throw new InvalidOperationException(
                    $"The prepared domain state drifted: key '{key}' is no longer unique.");
            _keys.Add(key, instance);
            _registeredKeys.Add(instance, key);
        }

        public void Remove(object instance)
        {
            if (!_instances.Remove(instance) || !_registeredKeys.Remove(instance, out var key))
                throw new InvalidOperationException(
                    "The prepared domain state drifted: a removed instance is no longer registered.");
            _keys.Remove(key);
        }
    }
}

internal sealed class PreparedDomainAssumption
{
    private readonly object _instance;
    private readonly MemberInfo _member;
    private readonly object? _value;
    private readonly object[]? _collectionItems;

    private PreparedDomainAssumption(object instance, MemberInfo member, object? value, object[]? collectionItems)
    {
        _instance = instance;
        _member = member;
        _value = value;
        _collectionItems = collectionItems;
    }

    public static PreparedDomainAssumption Capture(PropertyChange change)
    {
        var value = MemberReader.Read(change.Member, change.Instance);
        return value is IEnumerable collection and not string
            ? new PreparedDomainAssumption(
                change.Instance,
                change.Member,
                null,
                collection.Cast<object?>().Where(item => item is not null).Cast<object>().ToArray())
            : new PreparedDomainAssumption(change.Instance, change.Member, value, null);
    }

    public void Validate()
    {
        var current = MemberReader.Read(_member, _instance);
        var matches = _collectionItems is null
            ? Equals(current, _value)
            : current is IEnumerable collection && CollectionMatches(collection);
        if (!matches)
            throw new InvalidOperationException(
                $"The prepared domain state drifted: member '{_member.Name}' changed between Prepare and Commit.");
    }

    private bool CollectionMatches(IEnumerable collection)
    {
        var current = collection.Cast<object?>().Where(item => item is not null).Cast<object>().ToArray();
        return current.Length == _collectionItems!.Length &&
               current.Zip(_collectionItems).All(pair => ReferenceEquals(pair.First, pair.Second));
    }
}

internal sealed class RuntimePolicyActions
{
    private readonly List<ImmediateInvariantEvaluation> _immediateEvaluations = [];
    private readonly List<RepairRequest> _repairRequests = [];

    public IReadOnlyList<ImmediateInvariantEvaluation> ImmediateEvaluations => _immediateEvaluations;
    public IReadOnlyList<RepairRequest> RepairRequests => _repairRequests;

    public void AddImmediateEvaluation(IInvariantRuntimeState invariant, object source)
    {
        if (_immediateEvaluations.Any(action =>
                ReferenceEquals(action.Invariant, invariant) && ReferenceEquals(action.Source, source)))
            return;
        _immediateEvaluations.Add(new ImmediateInvariantEvaluation(invariant, source));
    }

    public void AddRepairRequest(
        IInvariantDefinition invariant,
        object source,
        DependencyImpactKind reason)
    {
        if (_repairRequests.Any(request =>
                ReferenceEquals(request.Invariant, invariant) && ReferenceEquals(request.Source, source)))
            return;
        _repairRequests.Add(new RepairRequest(invariant, source, reason));
    }

    public void Dispatch()
    {
        foreach (var evaluation in _immediateEvaluations)
            evaluation.Invariant.EvaluatePolicy(evaluation.Source);
        foreach (var request in _repairRequests)
            request.Invariant.DispatchRepair(request.Source);
    }
}
