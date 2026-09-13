using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;
/// <summary>
/// Stores and queries the runtime state of a compiled relation model. This type is not thread-safe;
/// mutations and queries must be externally synchronized.
/// </summary>
public sealed partial class RelationRuntime
{
    private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
    private readonly IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> _relations;
    private readonly NavigationIndexRegistry _navigation;
    private readonly ImpactResolver _impactResolver;
    private readonly IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> _derivedStates;
    private readonly IReadOnlyDictionary<IInvariantDefinition, IInvariantRuntimeState> _invariants;
    private readonly IReadOnlyList<ISourceLifecycleParticipant> _sourceLifecycleParticipants;
    private readonly DependencyGraphRuntime _dependencyGraph;
    private readonly IReadOnlyDictionary<IRelationDefinition, int> _relationIds;
    private readonly IReadOnlyDictionary<IDerivedDefinition, int> _derivedIds;
    private readonly IReadOnlyDictionary<IInvariantDefinition, int> _invariantIds;
    private readonly RuntimeDiagnosticOptions _diagnosticOptions;
    private long _version;
    private long _reindexedRoots;
    private long _affectedSources;
    private long _relationPairsAdded;
    private long _relationPairsRemoved;
    private long _policyRequestsEmitted;
    private bool _rollbackSnapshotsEnabled = true;

    /// <summary>The monotonically increasing version of runtime-owned relation state.</summary>
    public long Version => _version;

    internal IReadOnlyDictionary<IRelationDefinition, RelationImpact> LastRelationImpacts { get; private set; } =
        new Dictionary<IRelationDefinition, RelationImpact>();

    /// <summary>Returns diagnostic counters accumulated since creation or the last reset.</summary>
    public RuntimeDiagnostics Diagnostics => new(
        _relations.Values.Sum(relation => relation.PredicateEvaluationCount),
        _reindexedRoots,
        _affectedSources,
        _relationPairsAdded,
        _relationPairsRemoved,
        _derivedStates.Values.Sum(state => state.FullRecomputationCount),
        _derivedStates.Values.Sum(state => state.IncrementalUpdateCount),
        _policyRequestsEmitted,
        _relations.OrderBy(pair => _relationIds[pair.Key])
            .Select(pair => CreateRelationDiagnostics(pair.Key, pair.Value))
            .ToArray());

    /// <summary>Resets diagnostic counters without changing relation or dependency state.</summary>
    public void ResetDiagnostics()
    {
        foreach (var relation in _relations.Values)
            relation.ResetDiagnostics();
        foreach (var derived in _derivedStates.Values)
            derived.ResetDiagnostics();
        _reindexedRoots = 0;
        _affectedSources = 0;
        _relationPairsAdded = 0;
        _relationPairsRemoved = 0;
        _policyRequestsEmitted = 0;
    }

    internal RelationRuntime(
        IReadOnlyList<IObjectSetDefinition> sets,
        IReadOnlyList<IRelationDefinition> relations,
        IReadOnlyList<IDerivedDefinition> derivedStates,
        IReadOnlyList<IInvariantDefinition> invariants,
        CompiledDependencyGraph compiledDependencyGraph,
        IDependencyImpactPolicy? dependencyImpactPolicy = null,
        RuntimeDiagnosticOptions? diagnosticOptions = null)
    {
        _diagnosticOptions = diagnosticOptions ?? new RuntimeDiagnosticOptions();
        _diagnosticOptions.Validate();
        var impactPolicy = dependencyImpactPolicy ?? DefaultDependencyImpactPolicy.Instance;
        _sets = sets.ToDictionary(set => set, set => new ObjectSetRuntime(set));
        _relations = relations.ToDictionary(relation => relation, relation => relation.CreateState(_sets));
        _relationIds = relations.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id);
        _derivedIds = derivedStates.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id);
        _invariantIds = invariants.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id);
        foreach (var relation in relations.Where(relation =>
                     relation.PropagationPlan == RelationPropagationPlan.ExactMaterialized))
            _relations[relation].EnableExactPropagation();
        _navigation = new NavigationIndexRegistry(sets, relations, derivedStates, invariants, _sets);
        _impactResolver = new ImpactResolver(relations, _relations, _navigation);
        var mutableDerivedStates = new Dictionary<IDerivedDefinition, IDerivedRuntimeState>();
        foreach (var node in compiledDependencyGraph.Nodes.Where(node => node.Kind == DependencyNodeKind.Derived))
        {
            var definition = (IDerivedDefinition)node.Definition;
            mutableDerivedStates.Add(definition, definition.CreateState(
                _relations,
                upstream => mutableDerivedStates[upstream]));
        }
        _derivedStates = mutableDerivedStates;
        _invariants = invariants.ToDictionary(
            definition => definition,
            definition => definition.CreateState(_derivedStates));
        _sourceLifecycleParticipants = _derivedStates.Values.Cast<ISourceLifecycleParticipant>()
            .Concat(_invariants.Values)
            .ToArray();
        _dependencyGraph = new DependencyGraphRuntime(
            _sets,
            _relations,
            _navigation,
            _derivedStates,
            _invariants,
            compiledDependencyGraph,
            impactPolicy);
    }

    private RelationRuntimeDiagnostics CreateRelationDiagnostics(
        IRelationDefinition definition,
        IRelationRuntimeState state)
    {
        var leftCount = _sets[definition.LeftSet].Count;
        var averageFanOut = leftCount == 0 ? 0 : (double)state.MaterializedPairCount / leftCount;
        return new RelationRuntimeDiagnostics(
            _relationIds[definition],
            definition.LeftSet.ObjectType,
            definition.RightSet.ObjectType,
            state.HasExactPropagation
                ? RelationMaterializationMode.ExactPropagation
                : RelationMaterializationMode.None,
            state.ForwardIndexEntryCount,
            state.ReverseIndexEntryCount,
            state.MaterializedPairCount,
            averageFanOut,
            state.HasExactPropagation &&
            (state.MaterializedPairCount >= _diagnosticOptions.MaterializedPairWarningThreshold ||
             averageFanOut >= _diagnosticOptions.AverageFanOutWarningThreshold));
    }

    public void Add<T>(ObjectSet<T> set, T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instance);
        Apply(MutationSet.Create(Change.Add(set, instance)));
    }

    public void Add<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var definition = FindUniqueSet<T>();
        Apply(MutationSet.Create(new ObjectAdded(definition, instance)));
    }

    public bool Remove<T>(ObjectSet<T> set, T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instance);
        if (!GetSet(set.Definition).Contains(instance))
            return false;
        Apply(MutationSet.Create(Change.Remove(set, instance)));
        return true;
    }

    public bool Remove<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        var definition = FindUniqueSet<T>();
        if (!GetSet(definition).Contains(instance))
            return false;
        Apply(MutationSet.Create(new ObjectRemoved(definition, instance)));
        return true;
    }

    public IReadOnlyList<TRight> Related<TLeft, TRight>(Relation<TLeft, TRight> relation, TLeft left)
        where TLeft : class where TRight : class
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(left);
        if (!_relations.TryGetValue(relation.Definition, out var state))
            throw new ArgumentException("The relation does not belong to this compiled model.", nameof(relation));
        EnsureRegistered(relation.Definition.Left, left, "left");
        return ((RelationRuntimeState<TLeft, TRight>)state).Related(left);
    }

    public IReadOnlyList<TRight> Related<TLeft, TRight>(TLeft left, Relation<TLeft, TRight> relation)
        where TLeft : class where TRight : class => Related(relation, left);

    public IReadOnlyList<TRight> RelatedFromLeft<TLeft, TRight>(Relation<TLeft, TRight> relation, TLeft left)
        where TLeft : class where TRight : class => Related(relation, left);

    public IReadOnlyList<TLeft> RelatedFromRight<TLeft, TRight>(Relation<TLeft, TRight> relation, TRight right)
        where TLeft : class where TRight : class
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(right);
        if (!_relations.TryGetValue(relation.Definition, out var state))
            throw new ArgumentException("The relation does not belong to this compiled model.", nameof(relation));
        EnsureRegistered(relation.Definition.Right, right, "right");
        return ((RelationRuntimeState<TLeft, TRight>)state).RelatedFromRight(right);
    }

    public TValue Get<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        if (!_derivedStates.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived state does not belong to this compiled model.", nameof(derived));
        EnsureRegistered(derived.Definition.SourceSet, source, "source");
        return (TValue)state.GetValue(source)!;
    }

    public DerivedValueState GetState<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        if (!_derivedStates.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived state does not belong to this compiled model.", nameof(derived));
        EnsureRegistered(derived.Definition.SourceSet, source, "source");
        return state.GetValueState(source);
    }

    public bool Evaluate<TSource>(Invariant<TSource> invariant, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(source);
        if (!_invariants.TryGetValue(invariant.Definition, out var state))
            throw new ArgumentException("The invariant does not belong to this compiled model.", nameof(invariant));
        EnsureRegistered(invariant.Definition.SourceSet, source, "source");
        return state.EvaluateValue(source);
    }

    public InvariantEvaluationState GetState<TSource>(Invariant<TSource> invariant, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(source);
        if (!_invariants.TryGetValue(invariant.Definition, out var state))
            throw new ArgumentException("The invariant does not belong to this compiled model.", nameof(invariant));
        EnsureRegistered(invariant.Definition.SourceSet, source, "source");
        return state.GetValueState(source);
    }

    public ChangeImpact Apply(PropertyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Apply(ChangeSet.Create(change));
    }

    public ChangeImpact Apply(PropertyChange change, ChangeValidationMode validationMode)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Apply(ChangeSet.Create(change), validationMode);
    }

    public ChangeImpact Apply(CollectionChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Apply(MutationSet.Create(change));
    }

    public ChangeImpact Apply(ChangeSet changeSet)
        => Apply(changeSet, ChangeValidationMode.Default);

    /// <summary>
    /// Validates the entire batch, atomically commits runtime-owned indexes and dependency state,
    /// and then dispatches policy callbacks. Domain mutations must already have occurred and are
    /// never rolled back by this operation.
    /// </summary>
    public ChangeImpact Apply(ChangeSet changeSet, ChangeValidationMode validationMode)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        return Apply(MutationSet.Create(changeSet.Changes.Cast<RuntimeMutation>().ToArray()), validationMode);
    }

    public ChangeImpact Apply(MutationSet mutationSet)
        => Apply(mutationSet, ChangeValidationMode.Default);

    /// <summary>
    /// Validates and applies lifecycle, property, and collection mutations as one logical runtime
    /// operation, then dispatches policy callbacks once after all runtime-owned state is committed.
    /// Domain mutations must already have occurred and are never rolled back by this operation.
    /// </summary>
    public ChangeImpact Apply(MutationSet mutationSet, ChangeValidationMode validationMode)
    {
        var prepared = Prepare(mutationSet, validationMode);
        var impact = Commit(prepared);
        Dispatch(prepared);
        return impact;
    }

    /// <summary>
    /// Validates and commits a mutation batch, returning stable impact/request data without invoking
    /// application callbacks. Use the returned dispatch handle to invoke them.
    /// </summary>
    public RuntimeApplication ApplyDetailed(MutationSet mutationSet) =>
        ApplyDetailed(mutationSet, ChangeValidationMode.Default);

    /// <summary>
    /// Validates and commits a mutation batch, returning stable impact/request data without invoking
    /// application callbacks. Use the returned dispatch handle to invoke them.
    /// </summary>
    public RuntimeApplication ApplyDetailed(MutationSet mutationSet, ChangeValidationMode validationMode)
    {
        var prepared = Prepare(mutationSet, validationMode);
        var result = CommitWithResult(prepared);
        return CreateDetailedApplication(prepared, result);
    }

    /// <summary>Validates a mutation batch without changing runtime-owned state.</summary>
    public PreparedMutation Prepare(MutationSet mutationSet) =>
        Prepare(mutationSet, ChangeValidationMode.Default);

    /// <summary>
    /// Validates and normalizes a mutation batch without changing runtime-owned state. The returned
    /// mutation can be committed only while this runtime remains at the same version.
    /// </summary>
    public PreparedMutation Prepare(MutationSet mutationSet, ChangeValidationMode validationMode)
    {
        ArgumentNullException.ThrowIfNull(mutationSet);
        if (!Enum.IsDefined(validationMode))
            throw new ArgumentOutOfRangeException(nameof(validationMode));
        var batch = ValidateMutations(mutationSet.Mutations, validationMode);
        return new PreparedMutation(
            this,
            Version,
            batch.LifecycleMutations,
            batch.Changes,
            batch.Changes.Select(PreparedDomainAssumption.Capture).ToArray());
    }

    internal int DerivedStateEntryCount =>
        _derivedStates.Values.Sum(state => state.SourceStateEntryCount);

    internal int InvariantStateEntryCount =>
        _invariants.Values.Sum(state => state.SourceStateEntryCount);

    internal int MaterializedRelationPairCount =>
        _relations.Values.Sum(state => state.MaterializedPairCount);

    private ObjectSetDefinition<T> FindUniqueSet<T>() where T : class
    {
        var matches = _sets.Keys.OfType<ObjectSetDefinition<T>>().ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected exactly one registered object set for '{typeof(T).Name}', but found {matches.Length}. Use the explicit object-set overload.");
        return matches[0];
    }

    private ObjectSetRuntime GetSet(IObjectSetDefinition definition)
    {
        if (!_sets.TryGetValue(definition, out var state))
            throw new ArgumentException("The object set does not belong to this compiled model.");
        return state;
    }

    private void EnsureRegistered(IObjectSetDefinition set, object source, string role)
    {
        if (!_sets[set].Contains(source))
            throw new InvalidOperationException(
                $"The {role} instance is not registered in its declared object set.");
    }
}
