namespace Raffinert.Relations;

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    IReadOnlyDictionary<IRelationDefinition, RelationImpact> RelationImpacts,
    DependencyPropagationResult DependencyPropagation,
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
    public IReadOnlyList<DependencyImpactCause> Causes { get; init; } = [];
}

public enum RuntimeImpactDetailLevel { Summary, Causal }
public enum ImpactCausePrecision { Exact, Conservative }
public enum MutationOriginKind { SourceMemberChanged, CollectionChanged, ObjectAdded, ObjectRemoved }
public enum RelationImpactCauseKind
{
    MembershipAdded,
    MembershipRemoved,
    RelatedItemChanged,
    ConservativeCandidate
}

public sealed record MutationOrigin(
    int OriginId,
    MutationOriginKind Kind,
    object Source,
    string? MemberName)
{
    public SourceIdentity? SourceIdentity { get; init; }
    public CollectionChangeKind? CollectionKind { get; init; }
    public object? CollectionItem { get; init; }
}

public abstract record DependencyImpactCause(ImpactCausePrecision Precision);

public sealed record DirectSourceMemberCause(
    int OriginId,
    string MemberName,
    string Policy,
    DependencySeverity ClassifiedSeverity)
    : DependencyImpactCause(ImpactCausePrecision.Exact);

public sealed record RelationDependencyCause(
    int RelationId,
    RelationImpactCauseKind Kind,
    ImpactCausePrecision CausePrecision)
    : DependencyImpactCause(CausePrecision)
{
    public string? DefinitionKey { get; init; }
    public IReadOnlyList<int> OriginIds { get; init; } = [];
}

public sealed record UpstreamDerivedCause(
    int DerivedId,
    ImpactCausePrecision CausePrecision)
    : DependencyImpactCause(CausePrecision)
{
    public string? DefinitionKey { get; init; }
}

public sealed record InvariantReactionCause(
    InvariantReaction Reaction,
    DependencySeverity InputSeverity,
    DependencySeverity OutputSeverity)
    : DependencyImpactCause(ImpactCausePrecision.Exact);

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
public sealed record SourceKeyPart(string? Name, string Type, string Value);

public sealed record DurableSourceIdentity(
    string ObjectSetKey,
    string SourceType,
    IReadOnlyList<SourceKeyPart> KeyParts);

public sealed record DurablePolicyRequestIdentity(
    string DefinitionKey,
    DurableSourceIdentity Source);

public sealed record SourceIdentity(string? ObjectSetKey, Type SourceType, object SourceKey)
{
    public DurableSourceIdentity? DurableIdentity { get; init; }
    public bool IsDurable => DurableIdentity is not null;
}

public sealed record RepairRequestInfo(
    int InvariantId,
    object Source,
    DependencySeverity Reason)
{
    public string? DefinitionKey { get; init; }
    public SourceIdentity? SourceIdentity { get; init; }
    public bool IsDurable => DefinitionKey is not null && SourceIdentity?.IsDurable == true;

    public DurablePolicyRequestIdentity GetDurableIdentity() => IsDurable
        ? new DurablePolicyRequestIdentity(DefinitionKey!, SourceIdentity!.DurableIdentity!)
        : throw new InvalidOperationException(
            "The policy request has no durable identity. Name both the invariant and source object set, and use a canonically supported source key.");
}

/// <summary>A request for an immediate invariant evaluation during policy dispatch.</summary>
public sealed record ImmediateEvaluationRequestInfo(
    int InvariantId,
    object Source)
{
    public string? DefinitionKey { get; init; }
    public SourceIdentity? SourceIdentity { get; init; }
    public bool IsDurable => DefinitionKey is not null && SourceIdentity?.IsDurable == true;

    public DurablePolicyRequestIdentity GetDurableIdentity() => IsDurable
        ? new DurablePolicyRequestIdentity(DefinitionKey!, SourceIdentity!.DurableIdentity!)
        : throw new InvalidOperationException(
            "The policy request has no durable identity. Name both the invariant and source object set, and use a canonically supported source key.");
}

internal static class DurableSourceIdentityFactory
{
    public static DurableSourceIdentity? Create(string? objectSetKey, Type sourceType, object sourceKey)
    {
        if (objectSetKey is null || sourceType.FullName is null || !TryParts(sourceKey, out var parts))
            return null;
        return new DurableSourceIdentity(objectSetKey, sourceType.FullName, parts);
    }

    private static bool TryParts(object key, out IReadOnlyList<SourceKeyPart> parts)
    {
        if (TryScalar(key, null, out var scalar))
        {
            parts = [scalar];
            return true;
        }

        if (key is ITuple tuple)
        {
            var values = new List<SourceKeyPart>();
            for (var index = 0; index < tuple.Length; index++)
            {
                if (tuple[index] is null || !TryScalar(tuple[index]!, $"Item{index + 1}", out scalar))
                {
                    parts = [];
                    return false;
                }
                values.Add(scalar);
            }
            parts = values;
            return values.Count > 0;
        }

        var keyType = key.GetType();
        var isAnonymous = keyType.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) &&
                          keyType.Name.Contains("AnonymousType", StringComparison.Ordinal);
        var properties = isAnonymous
            ? keyType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            : [];
        if (properties.Length > 0)
        {
            var values = new List<SourceKeyPart>();
            foreach (var property in properties.OrderBy(property => property.MetadataToken))
            {
                var value = property.GetValue(key);
                if (value is null || !TryScalar(value, property.Name, out scalar))
                {
                    parts = [];
                    return false;
                }
                values.Add(scalar);
            }
            parts = values;
            return true;
        }

        parts = [];
        return false;
    }

    private static bool TryScalar(object value, string? name, out SourceKeyPart part)
    {
        var type = value.GetType();
        string? token = null;
        string? formatted = null;
        if (type.IsEnum)
        {
            token = $"enum:{type.FullName}";
            formatted = Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            (token, formatted) = value switch
            {
                string text => ("string", text),
                Guid guid => ("guid", guid.ToString("D")),
                bool boolean => ("bool", boolean ? "true" : "false"),
                byte number => ("uint8", number.ToString(CultureInfo.InvariantCulture)),
                sbyte number => ("int8", number.ToString(CultureInfo.InvariantCulture)),
                short number => ("int16", number.ToString(CultureInfo.InvariantCulture)),
                ushort number => ("uint16", number.ToString(CultureInfo.InvariantCulture)),
                int number => ("int32", number.ToString(CultureInfo.InvariantCulture)),
                uint number => ("uint32", number.ToString(CultureInfo.InvariantCulture)),
                long number => ("int64", number.ToString(CultureInfo.InvariantCulture)),
                ulong number => ("uint64", number.ToString(CultureInfo.InvariantCulture)),
                DateOnly date => ("date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                DateTime date => ("datetime", date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                DateTimeOffset date => ("datetimeoffset", date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                _ => (null, null)
            };
        }
        part = new SourceKeyPart(name, token ?? string.Empty, formatted ?? string.Empty);
        return token is not null;
    }
}

/// <summary>
/// Stable data produced by a committed mutation. This object contains no callback capability
/// and does not retain the relation runtime.
/// </summary>
public sealed class RuntimeApplyResult
{
    internal RuntimeApplyResult(
        ChangeImpact changeImpact,
        IReadOnlyList<RelationMutationImpact> relationImpacts,
        IReadOnlyList<DerivedMutationImpact> derivedImpacts,
        IReadOnlyList<InvariantMutationImpact> invariantImpacts,
        IReadOnlyList<RepairRequestInfo> repairRequests,
        IReadOnlyList<ImmediateEvaluationRequestInfo> immediateEvaluationRequests,
        RuntimeImpactDetailLevel detailLevel,
        IReadOnlyList<MutationOrigin> mutationOrigins)
    {
        ChangeImpact = changeImpact;
        RelationImpacts = relationImpacts;
        DerivedImpacts = derivedImpacts;
        InvariantImpacts = invariantImpacts;
        RepairRequests = repairRequests;
        ImmediateEvaluationRequests = immediateEvaluationRequests;
        DetailLevel = detailLevel;
        MutationOrigins = mutationOrigins;
    }

    public ChangeImpact ChangeImpact { get; }
    public IReadOnlyList<RelationMutationImpact> RelationImpacts { get; }
    public IReadOnlyList<DerivedMutationImpact> DerivedImpacts { get; }
    public IReadOnlyList<InvariantMutationImpact> InvariantImpacts { get; }
    public IReadOnlyList<RepairRequestInfo> RepairRequests { get; }
    public IReadOnlyList<ImmediateEvaluationRequestInfo> ImmediateEvaluationRequests { get; }
    public RuntimeImpactDetailLevel DetailLevel { get; }
    public IReadOnlyList<MutationOrigin> MutationOrigins { get; }
}

public static class RuntimeImpactTraceRenderer
{
    public static string Render(RuntimeApplyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = new List<string>();
        foreach (var impact in result.DerivedImpacts)
            foreach (var source in impact.Sources)
            {
                lines.Add($"{impact.DefinitionKey ?? $"derived-{impact.DerivedId}"} -> {source.Severity}");
                lines.AddRange(source.Causes.Select(cause => $"  because {Describe(cause)} [{cause.Precision}]"));
            }
        foreach (var impact in result.InvariantImpacts)
            foreach (var source in impact.Sources)
            {
                lines.Add($"{impact.DefinitionKey ?? $"invariant-{impact.InvariantId}"} -> {source.Severity}");
                lines.AddRange(source.Causes.Select(cause => $"  because {Describe(cause)} [{cause.Precision}]"));
            }
        return string.Join(Environment.NewLine, lines);
    }

    private static string Describe(DependencyImpactCause cause) => cause switch
    {
        DirectSourceMemberCause direct => $"{direct.MemberName} changed ({direct.Policy})",
        RelationDependencyCause relation => $"{relation.DefinitionKey ?? $"relation-{relation.RelationId}"} {relation.Kind}",
        UpstreamDerivedCause upstream => $"upstream {upstream.DefinitionKey ?? $"derived-{upstream.DerivedId}"}",
        InvariantReactionCause reaction => $"{reaction.Reaction} escalated {reaction.InputSeverity} to {reaction.OutputSeverity}",
        _ => cause.GetType().Name
    };
}

/// <summary>A committed mutation's stable result data and separate in-process dispatch capability.</summary>
public sealed record RuntimeApplication(RuntimeApplyResult Result, PolicyDispatchHandle Dispatch);

/// <summary>
/// An immutable, binding impact result and internal runtime-state patch produced by
/// <see cref="RelationRuntime.PlanDetailed(PreparedMutation, RuntimeImpactDetailLevel)"/>.
/// </summary>
public sealed class PreparedImpactPlan
{
    internal PreparedImpactPlan(
        RelationRuntime runtime,
        PreparedMutation prepared,
        long baseVersion,
        RuntimeImpactDetailLevel detailLevel,
        RuntimeApplyResult result,
        object preState,
        object postState,
        RuntimePolicyActions policyActions)
    {
        Runtime = runtime;
        Prepared = prepared;
        BaseVersion = baseVersion;
        DetailLevel = detailLevel;
        Result = result;
        PreState = preState;
        PostState = postState;
        PolicyActions = policyActions;
    }

    internal RelationRuntime Runtime { get; }
    internal PreparedMutation Prepared { get; }
    internal object PreState { get; }
    internal object PostState { get; }
    internal RuntimePolicyActions PolicyActions { get; }
    public long BaseVersion { get; }
    public RuntimeImpactDetailLevel DetailLevel { get; }
    public RuntimeApplyResult Result { get; }
    public bool IsCommitted { get; private set; }
    internal void MarkCommitted() => IsCommitted = true;
}

/// <summary>Dispatches a committed mutation's configured post-commit callbacks.</summary>
public sealed class PolicyDispatchHandle
{
    private readonly Action _dispatch;

    internal PolicyDispatchHandle(Action dispatch) => _dispatch = dispatch;

    public bool IsDispatched { get; private set; }

    /// <summary>Invokes configured post-commit callbacks once.</summary>
    public void Invoke()
    {
        if (IsDispatched)
            throw new InvalidOperationException("Policy actions have already been dispatched.");
        _dispatch();
        IsDispatched = true;
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
        IReadOnlyList<NormalizedMutationProvenance> provenance,
        IReadOnlyList<PreparedDomainAssumption> domainAssumptions)
    {
        Runtime = runtime;
        BaseVersion = baseVersion;
        LifecycleMutations = lifecycleMutations;
        Changes = changes;
        Provenance = provenance;
        DomainAssumptions = domainAssumptions;
    }

    internal RelationRuntime Runtime { get; }
    internal IReadOnlyList<RuntimeMutation> LifecycleMutations { get; }
    internal IReadOnlyList<PropertyChange> Changes { get; }
    internal IReadOnlyList<NormalizedMutationProvenance> Provenance { get; }
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

        var simulated = new Dictionary<IObjectSetDefinition, PreparedSetState>();
        foreach (var mutation in LifecycleMutations)
        {
            var set = mutation is ObjectAdded added ? added.Set : ((ObjectRemoved)mutation).Set;
            if (!sets.TryGetValue(set, out var runtime))
                throw new ArgumentException("The object set does not belong to this compiled model.");
            if (!simulated.TryGetValue(set, out var state))
            {
                state = new PreparedSetState(runtime);
                simulated.Add(set, state);
            }
            if (mutation is ObjectAdded addition)
                state.Add(set, addition.Instance);
            else
                state.Remove(((ObjectRemoved)mutation).Instance);
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
    private int _nextAction;

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
        while (_nextAction < _immediateEvaluations.Count + _repairRequests.Count)
        {
            if (_nextAction < _immediateEvaluations.Count)
            {
                var evaluation = _immediateEvaluations[_nextAction];
                evaluation.Invariant.EvaluatePolicy(evaluation.Source);
            }
            else
            {
                var request = _repairRequests[_nextAction - _immediateEvaluations.Count];
                request.Invariant.DispatchRepair(request.Source);
            }
            _nextAction++;
        }
    }
}
