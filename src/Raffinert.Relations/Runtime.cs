using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

public sealed class CompiledRelationModel
{
    private readonly IReadOnlyList<IObjectSetDefinition> _sets;
    private readonly IReadOnlyList<IRelationDefinition> _relations;
    private readonly IReadOnlyList<IDerivedDefinition> _derivedStates;
    private readonly IReadOnlyList<IInvariantDefinition> _invariants;

    internal CompiledRelationModel(
        IReadOnlyList<IObjectSetDefinition> sets,
        IReadOnlyList<IRelationDefinition> relations,
        IReadOnlyList<IDerivedDefinition> derivedStates,
        IReadOnlyList<IInvariantDefinition> invariants)
    {
        _sets = sets;
        _relations = relations;
        _derivedStates = derivedStates;
        _invariants = invariants;
        Diagnostics = CreateDiagnostics();
        DebugView = CreateDebugView();
    }

    public string DebugView { get; }
    public CompiledModelDiagnostics Diagnostics { get; }
    public RelationRuntime CreateRuntime() => new(_sets, _relations, _derivedStates, _invariants);
    public RelationRuntime CreateRuntime(RuntimeDiagnosticOptions diagnosticOptions)
    {
        ArgumentNullException.ThrowIfNull(diagnosticOptions);
        return new RelationRuntime(_sets, _relations, _derivedStates, _invariants, null, diagnosticOptions);
    }
    internal RelationRuntime CreateRuntime(IDependencyImpactPolicy dependencyImpactPolicy) =>
        new(_sets, _relations, _derivedStates, _invariants, dependencyImpactPolicy);

    private string CreateDebugView()
    {
        var lines = new List<string>();
        foreach (var set in _sets)
        {
            lines.Add($"ObjectSet {set.ObjectType.Name}");
            lines.Add($"  Key: {GetMemberName(set.KeyExpression?.Body)}");
        }
        foreach (var relation in _relations)
        {
            lines.Add($"Relation {relation.LeftSet.ObjectType.Name} -> {relation.RightSet.ObjectType.Name}");
            lines.Add($"  Predicate: {relation.PredicateExpression.Body}");
            lines.Add("  Dependencies:");
            foreach (var dependency in relation.Analysis.DependencyPaths)
                lines.Add($"    {dependency.DisplayName}");
            lines.Add("  Join key:");
            foreach (var key in relation.Analysis.JoinKeyParts)
                lines.Add($"    {key.Left.DisplayName} <-> {key.Right.DisplayName} ({key.EqualitySemantics})");
            lines.Add($"  Access plan: {relation.AccessPlan.DisplayName}");
            lines.Add($"  Reverse access plan: {relation.ReverseAccessPlan?.DisplayName ?? "Disabled"}");
            lines.Add($"  Relation materialization: {(relation.ReverseAccessPlan is null ? "None" : "ExactPropagation")}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(relation.Analysis.DependencyAnalysis)}");
            var materialized = _derivedStates.Any(derived => ReferenceEquals(derived.Relation, relation));
            lines.Add($"  Dependency tracking: {FormatDependencyTracking(
                relation.Analysis.DependencyAnalysis,
                relation.AllowIncompleteDependencies,
                materialized)}");
            lines.Add($"  Residual predicate: {(relation.Analysis.HasResidualPredicate ? "Yes" : "No")}");
            foreach (var residual in relation.Analysis.RecognizedResiduals)
                lines.Add($"    {residual}");
            var navigationEdges = relation.Analysis.DependencyPaths
                .SelectMany(path => path.Segments.Take(Math.Max(0, path.Segments.Count - 1)))
                .Where(segment => !segment.ValueType.IsValueType && segment.ValueType != typeof(string))
                .Select(segment => $"{segment.DeclaringType.Name}.{segment.Member.Name}")
                .Distinct()
                .ToArray();
            if (navigationEdges.Length > 0)
            {
                lines.Add("  Navigation indexes:");
                foreach (var edge in navigationEdges)
                    lines.Add($"    {edge} (shared reverse tracked)");
            }
        }
        foreach (var derived in _derivedStates)
        {
            lines.Add($"Derived {derived.SourceSet.ObjectType.Name} using {derived.Relation.RightSet.ObjectType.Name}: {derived.ComputationExpression.Body}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(derived.Analysis.Flags)}");
            lines.Add($"  Dependency tracking: {FormatDependencyTracking(
                derived.Analysis.Flags,
                derived.AllowIncompleteDependencies,
                cached: true)}");
            lines.Add($"  Relation membership: {(derived.Analysis.HasRelationMembershipDependency ? "Yes" : "No")}");
            lines.Add($"  Impact policy: membership added={derived.ImpactPolicy.MembershipAdded}, " +
                $"removed={derived.ImpactPolicy.MembershipRemoved}, item changed={derived.ImpactPolicy.ItemChanged}");
            lines.Add($"  Computation plan: {derived.ComputationPlanName}");
            lines.Add($"  LINQ semantics: {derived.Analysis.LinqSemantics}");
            foreach (var dependency in derived.Analysis.Dependencies)
                lines.Add($"  {dependency.Role}: {dependency.Path.DisplayName}");
        }
        foreach (var invariant in _invariants)
        {
            lines.Add($"Invariant {invariant.Derived.SourceSet.ObjectType.Name}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(invariant.Analysis.Flags)}");
            lines.Add($"  Dependency tracking: {FormatDependencyTracking(
                invariant.Analysis.Flags,
                invariant.AllowIncompleteDependencies,
                cached: true)}");
            foreach (var dependency in invariant.Analysis.Dependencies)
                lines.Add($"  {dependency.Role}: {dependency.Path.DisplayName}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private CompiledModelDiagnostics CreateDiagnostics()
    {
        var relationIds = _relations.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id);
        var derivedIds = _derivedStates.Select((definition, id) => (definition, id))
            .ToDictionary(pair => pair.definition, pair => pair.id);
        return new CompiledModelDiagnostics(
            _sets.Select(set => new ObjectSetModelDiagnostics(
                set.Id,
                set.ObjectType,
                set.KeyExpression?.Body.ToString() ?? "<missing>")
            { DefinitionKey = set.DefinitionKey })
                .ToArray(),
            _relations.Select((relation, id) => new RelationModelDiagnostics(
                id,
                relation.LeftSet.Id,
                relation.RightSet.Id,
                relation.LeftSet.ObjectType,
                relation.RightSet.ObjectType,
                relation.PredicateExpression.Body.ToString(),
                relation.Analysis.DependencyPaths.Select(path => path.DisplayName).ToArray(),
                relation.Analysis.JoinKeyParts.Select(key =>
                    $"{key.Left.DisplayName} <-> {key.Right.DisplayName} ({key.EqualitySemantics})").ToArray(),
                ToPublicAccessPlan(relation.AccessPlan),
                relation.ReverseAccessPlan is null ? null : ToPublicAccessPlan(relation.ReverseAccessPlan),
                relation.ReverseAccessPlan is null
                    ? RelationMaterializationMode.None
                    : RelationMaterializationMode.ExactPropagation,
                ToPublicCompleteness(relation.Analysis.DependencyAnalysis),
                relation.AllowIncompleteDependencies)
            { DefinitionKey = relation.DefinitionKey })
                .ToArray(),
            _derivedStates.Select((derived, id) => new DerivedModelDiagnostics(
                id,
                derived.SourceSet.Id,
                relationIds[derived.Relation],
                derived.ComputationExpression.Body.ToString(),
                derived.Analysis.Dependencies.Select(dependency =>
                    $"{dependency.Role}: {dependency.Path.DisplayName}").ToArray(),
                ToPublicCompleteness(derived.Analysis.Flags),
                derived.AllowIncompleteDependencies,
                derived.Analysis.HasRelationMembershipDependency,
                (DerivedLinqSemantics)(int)derived.Analysis.LinqSemantics,
                derived.ComputationPlanName,
                derived.ImpactPolicy.MembershipAdded,
                derived.ImpactPolicy.MembershipRemoved,
                derived.ImpactPolicy.ItemChanged)
            { DefinitionKey = derived.DefinitionKey })
                .ToArray(),
            _invariants.Select((invariant, id) => new InvariantModelDiagnostics(
                id,
                derivedIds[invariant.Derived],
                invariant.PredicateExpression.Body.ToString(),
                invariant.Analysis.Dependencies.Select(dependency =>
                    $"{dependency.Role}: {dependency.Path.DisplayName}").ToArray(),
                ToPublicCompleteness(invariant.Analysis.Flags),
                invariant.AllowIncompleteDependencies,
                invariant.Reaction)
            { DefinitionKey = invariant.DefinitionKey })
                .ToArray());
    }

    private static RelationAccessPlanKind ToPublicAccessPlan(RelationAccessPlan plan) => plan switch
    {
        HashJoinAccessPlan => RelationAccessPlanKind.HashJoin,
        _ => RelationAccessPlanKind.Scan
    };

    private static DependencyCompletenessIssue ToPublicCompleteness(DependencyAnalysisFlags flags)
    {
        var result = DependencyCompletenessIssue.None;
        if (flags.HasFlag(DependencyAnalysisFlags.ContainsOpaqueCode))
            result |= DependencyCompletenessIssue.OpaqueCode;
        if (flags.HasFlag(DependencyAnalysisFlags.ContainsExternalState))
            result |= DependencyCompletenessIssue.ExternalState;
        return result;
    }

    private static string FormatDependencyAnalysis(DependencyAnalysisFlags flags) =>
        flags == DependencyAnalysisFlags.Complete
            ? nameof(DependencyAnalysisFlags.Complete)
            : string.Join(", ", Enum.GetValues<DependencyAnalysisFlags>()
                .Where(flag => flag != DependencyAnalysisFlags.Complete && flags.HasFlag(flag)));

    private static string FormatDependencyTracking(
        DependencyAnalysisFlags flags,
        bool explicitlyAllowed,
        bool cached)
    {
        if (flags == DependencyAnalysisFlags.Complete)
            return "Fully tracked";
        if (explicitlyAllowed)
            return $"Incomplete, explicitly allowed ({flags}); cached freshness is not guaranteed";
        return cached
            ? $"Incomplete, rejected at build ({flags})"
            : $"Incomplete direct-query evaluation ({flags}); no cached freshness claim";
    }

    private static string GetMemberName(Expression? expression)
    {
        while (expression is UnaryExpression unary) expression = unary.Operand;
        return expression is MemberExpression member ? member.Member.Name : expression?.ToString() ?? "<missing>";
    }
}

/// <summary>
/// Stores and queries the runtime state of a compiled relation model. This type is not thread-safe;
/// mutations and queries must be externally synchronized.
/// </summary>
public sealed class RelationRuntime
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
        foreach (var relation in derivedStates.Select(derived => derived.Relation).Distinct())
            _relations[relation].EnableExactPropagation();
        _navigation = new NavigationIndexRegistry(sets, relations, derivedStates, invariants, _sets);
        _impactResolver = new ImpactResolver(relations, _relations, _navigation);
        _derivedStates = derivedStates.ToDictionary(
            definition => definition,
            definition => definition.CreateState(_relations[definition.Relation]));
        _invariants = invariants.ToDictionary(
            definition => definition,
            definition => definition.CreateState(_derivedStates[definition.Derived]));
        _sourceLifecycleParticipants = _derivedStates.Values.Cast<ISourceLifecycleParticipant>()
            .Concat(_invariants.Values)
            .ToArray();
        _dependencyGraph = new DependencyGraphRuntime(
            _sets,
            _relations,
            _navigation,
            _derivedStates,
            _invariants,
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
        if (!_sets[relation.Definition.Left].Contains(left))
            throw new InvalidOperationException("The left instance is not registered in the relation's object set.");
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
        if (!_sets[relation.Definition.Right].Contains(right))
            throw new InvalidOperationException("The right instance is not registered in the relation's object set.");
        return ((RelationRuntimeState<TLeft, TRight>)state).RelatedFromRight(right);
    }

    public TValue Get<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        if (!_derivedStates.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived state does not belong to this compiled model.", nameof(derived));
        if (!_sets[derived.Definition.SourceSet].Contains(source))
            throw new InvalidOperationException("The source instance is not registered in the derived state's object set.");
        return (TValue)state.GetValue(source)!;
    }

    public DerivedValueState GetState<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        if (!_derivedStates.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived state does not belong to this compiled model.", nameof(derived));
        return state.GetValueState(source);
    }

    public bool Evaluate<TSource>(Invariant<TSource> invariant, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(source);
        if (!_invariants.TryGetValue(invariant.Definition, out var state))
            throw new ArgumentException("The invariant does not belong to this compiled model.", nameof(invariant));
        return state.EvaluateValue(source);
    }

    public InvariantEvaluationState GetState<TSource>(Invariant<TSource> invariant, TSource source)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(source);
        if (!_invariants.TryGetValue(invariant.Definition, out var state))
            throw new ArgumentException("The invariant does not belong to this compiled model.", nameof(invariant));
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
        var result = ApplyDetailed(mutationSet, validationMode);
        result.DispatchPolicies();
        return result.ChangeImpact;
    }

    /// <summary>
    /// Validates and commits a mutation batch, returning stable impact/request data without invoking
    /// application callbacks. Call <see cref="RuntimeApplyResult.DispatchPolicies"/> to dispatch them.
    /// </summary>
    public RuntimeApplyResult ApplyDetailed(MutationSet mutationSet) =>
        ApplyDetailed(mutationSet, ChangeValidationMode.Default);

    /// <summary>
    /// Validates and commits a mutation batch, returning stable impact/request data without invoking
    /// application callbacks. Call <see cref="RuntimeApplyResult.DispatchPolicies"/> to dispatch them.
    /// </summary>
    public RuntimeApplyResult ApplyDetailed(MutationSet mutationSet, ChangeValidationMode validationMode)
    {
        var prepared = Prepare(mutationSet, validationMode);
        var impact = Commit(prepared);
        return CreateDetailedResult(prepared, impact);
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

    /// <summary>
    /// Commits a prepared mutation to runtime-owned state without invoking application callbacks.
    /// </summary>
    public ChangeImpact Commit(PreparedMutation prepared)
    {
        ValidatePreparedMutation(prepared);
        prepared.ValidateDomainState(_sets);
        var snapshot = CaptureState();
        try
        {
            var result = CommitMutations(prepared.LifecycleMutations, prepared.Changes);
            _version++;
            prepared.MarkCommitted(result.PolicyActions);
            return result.Impact;
        }
        catch
        {
            RestoreState(snapshot);
            throw;
        }
    }

    private RuntimeStateSnapshot CaptureState() => new(
        _sets.ToDictionary(pair => pair.Key, pair => pair.Value.CaptureState()),
        _relations.ToDictionary(pair => pair.Key, pair => pair.Value.CaptureState()),
        _navigation.CaptureState(),
        _dependencyGraph.CaptureState(),
        LastRelationImpacts,
        _reindexedRoots,
        _affectedSources,
        _relationPairsAdded,
        _relationPairsRemoved,
        _policyRequestsEmitted);

    private void RestoreState(RuntimeStateSnapshot snapshot)
    {
        foreach (var pair in snapshot.Sets)
            _sets[pair.Key].RestoreState(pair.Value);
        foreach (var pair in snapshot.Relations)
            _relations[pair.Key].RestoreState(pair.Value);
        _navigation.RestoreState(snapshot.Navigation);
        _dependencyGraph.RestoreState(snapshot.Dependencies);
        LastRelationImpacts = snapshot.LastRelationImpacts;
        _reindexedRoots = snapshot.ReindexedRoots;
        _affectedSources = snapshot.AffectedSources;
        _relationPairsAdded = snapshot.RelationPairsAdded;
        _relationPairsRemoved = snapshot.RelationPairsRemoved;
        _policyRequestsEmitted = snapshot.PolicyRequestsEmitted;
    }

    private sealed record RuntimeStateSnapshot(
        IReadOnlyDictionary<IObjectSetDefinition, object> Sets,
        IReadOnlyDictionary<IRelationDefinition, object> Relations,
        object Navigation,
        object Dependencies,
        IReadOnlyDictionary<IRelationDefinition, RelationImpact> LastRelationImpacts,
        long ReindexedRoots,
        long AffectedSources,
        long RelationPairsAdded,
        long RelationPairsRemoved,
        long PolicyRequestsEmitted);

    /// <summary>Dispatches a committed mutation's post-commit policy callbacks.</summary>
    public void Dispatch(PreparedMutation prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Runtime, this))
            throw new ArgumentException("The prepared mutation belongs to a different runtime.", nameof(prepared));
        if (!prepared.IsCommitted)
            throw new InvalidOperationException("The prepared mutation has not been committed.");
        if (prepared.IsDispatched)
            throw new InvalidOperationException("The prepared mutation's policy actions have already been dispatched.");
        prepared.MarkDispatched();
        prepared.PolicyActions!.Dispatch();
    }

    private void ValidatePreparedMutation(PreparedMutation prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Runtime, this))
            throw new ArgumentException("The prepared mutation belongs to a different runtime.", nameof(prepared));
        if (prepared.IsCommitted)
            throw new InvalidOperationException("The prepared mutation has already been committed.");
        if (prepared.BaseVersion != Version)
            throw new InvalidOperationException(
                $"The prepared mutation is stale: it was prepared at runtime version {prepared.BaseVersion}, " +
                $"but the current version is {Version}. Prepare the mutation again.");
    }

    private RuntimeCommitResult CommitMutations(
        IReadOnlyList<RuntimeMutation> lifecycleMutations,
        IReadOnlyList<PropertyChange> changes)
    {
        var relationDeltas = new Dictionary<IRelationDefinition, RelationDelta>();
        foreach (var mutation in lifecycleMutations)
        {
            if (mutation is ObjectAdded added)
                CommitAdd(added, relationDeltas);
            else
                CommitRemove((ObjectRemoved)mutation, relationDeltas);
        }
        var impact = new ResolvedChangeImpact();
        foreach (var change in changes)
            impact.MergeFrom(_impactResolver.Resolve(change));

        foreach (var (rootSet, root) in _dependencyGraph.ResolveNavigationRoots(impact, changes))
            _navigation.RefreshRoot(rootSet, root);
        foreach (var pair in impact.ReindexRoots)
            foreach (var root in pair.Value)
                pair.Key.ReindexRight(root);
        foreach (var pair in impact.ReindexLeftRoots)
            foreach (var root in pair.Value)
                pair.Key.ReindexLeft(root);
        foreach (var pair in ResolveRelationDeltas(impact))
            MergeDelta(relationDeltas, pair.Key, pair.Value);
        var relationImpacts = new Dictionary<IRelationDefinition, RelationImpact>(
            impact.CreateRelationImpacts(_relations, relationDeltas));
        foreach (var pair in relationDeltas)
            if (!relationImpacts.ContainsKey(pair.Key))
                relationImpacts.Add(pair.Key, RelationImpact.FromDelta(pair.Key, pair.Value));
        LastRelationImpacts = relationImpacts;
        var policyActions = new RuntimePolicyActions();
        _dependencyGraph.ApplyChangeImpacts(relationImpacts, changes, policyActions);
        var publicImpact = impact.ToPublic();
        _reindexedRoots += publicImpact.Access.ReindexedRoots;
        _affectedSources += _dependencyGraph.GetDerivedImpacts()
            .SelectMany(value => value.Sources)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();
        _relationPairsAdded += relationImpacts.Values.Sum(value => value.AddedPairs.Count);
        _relationPairsRemoved += relationImpacts.Values.Sum(value => value.RemovedPairs.Count);
        _policyRequestsEmitted += policyActions.ImmediateEvaluations.Count + policyActions.RepairRequests.Count;
        return new RuntimeCommitResult(publicImpact, policyActions);
    }

    private RuntimeApplyResult CreateDetailedResult(PreparedMutation prepared, ChangeImpact impact)
    {
        var relationImpacts = LastRelationImpacts
            .OrderBy(pair => _relationIds[pair.Key])
            .Select(pair => new RelationMutationImpact(
                _relationIds[pair.Key],
                pair.Key.LeftSet.ObjectType,
                pair.Key.RightSet.ObjectType,
                Array.AsReadOnly(pair.Value.AddedPairs
                    .Select(value => new RelationPairImpact(value.Left, value.Right)).ToArray()),
                Array.AsReadOnly(pair.Value.RemovedPairs
                    .Select(value => new RelationPairImpact(value.Left, value.Right)).ToArray()),
                Array.AsReadOnly(pair.Value.AffectedLefts.ToArray()))
            { DefinitionKey = pair.Key.DefinitionKey })
            .ToArray();
        var derivedImpacts = _dependencyGraph.GetDerivedImpacts()
            .GroupBy(value => value.Definition)
            .Select(group => new DerivedMutationImpact(
                _derivedIds[group.Key],
                group.Any(value => value.Severity == DependencyImpactKind.Invalid)
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty,
                Array.AsReadOnly(group.SelectMany(value => value.Sources)
                    .Distinct(ReferenceEqualityComparer.Instance).ToArray()))
            { DefinitionKey = group.Key.DefinitionKey })
            .OrderBy(value => value.DerivedId)
            .ToArray();
        var invariantImpacts = _dependencyGraph.GetInvariantImpacts()
            .GroupBy(value => value.Definition)
            .Select(group => new InvariantMutationImpact(
                _invariantIds[group.Key],
                group.Any(value => value.Severity == DependencyImpactKind.Invalid)
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty,
                Array.AsReadOnly(group.SelectMany(value => value.Sources)
                    .Distinct(ReferenceEqualityComparer.Instance).ToArray()))
            { DefinitionKey = group.Key.DefinitionKey })
            .OrderBy(value => value.InvariantId)
            .ToArray();
        var actions = prepared.PolicyActions!;
        var repairRequests = actions.RepairRequests
            .Select(request => new RepairRequestInfo(
                _invariantIds[request.Invariant],
                request.Source,
                request.Reason.ToSeverity())
            {
                DefinitionKey = request.Invariant.DefinitionKey,
                SourceIdentity = CreateSourceIdentity(request.Invariant.Derived.SourceSet, request.Source)
            })
            .ToArray();
        var immediateRequests = actions.ImmediateEvaluations
            .Select(request => new ImmediateEvaluationRequestInfo(
                _invariantIds[request.Invariant.Definition],
                request.Source)
            {
                DefinitionKey = request.Invariant.Definition.DefinitionKey,
                SourceIdentity = CreateSourceIdentity(request.Invariant.Definition.Derived.SourceSet, request.Source)
            })
            .ToArray();
        return new RuntimeApplyResult(
            impact,
            relationImpacts,
            derivedImpacts,
            invariantImpacts,
            repairRequests,
            immediateRequests,
            () => Dispatch(prepared));
    }

    private SourceIdentity CreateSourceIdentity(IObjectSetDefinition set, object source) =>
        new(set.DefinitionKey, set.ObjectType, _sets[set].GetRegisteredKey(source));

    private void CommitAdd(
        ObjectAdded mutation,
        IDictionary<IRelationDefinition, RelationDelta> deltas)
    {
        var state = GetSet(mutation.Set);
        state.Add(mutation.Instance);
        NotifySourceAdded(mutation.Set, mutation.Instance);
        _navigation.AddRoot(mutation.Set, mutation.Instance);
        foreach (var pair in _relations.Where(pair => ReferenceEquals(pair.Value.RightSet, mutation.Set)))
            MergeDelta(deltas, pair.Key, pair.Value.AddRight(mutation.Instance));
        foreach (var pair in _relations.Where(pair => ReferenceEquals(pair.Value.LeftSet, mutation.Set)))
            MergeDelta(deltas, pair.Key, pair.Value.AddLeft(mutation.Instance));
    }

    private void CommitRemove(
        ObjectRemoved mutation,
        IDictionary<IRelationDefinition, RelationDelta> deltas)
    {
        foreach (var pair in _relations.Where(pair => ReferenceEquals(pair.Value.RightSet, mutation.Set)))
            MergeDelta(deltas, pair.Key, pair.Value.RemoveRight(mutation.Instance));
        foreach (var pair in _relations.Where(pair => ReferenceEquals(pair.Value.LeftSet, mutation.Set)))
            MergeDelta(deltas, pair.Key, pair.Value.RemoveLeft(mutation.Instance));
        _navigation.RemoveRoot(mutation.Set, mutation.Instance);
        NotifySourceRemoved(mutation.Set, mutation.Instance);
        GetSet(mutation.Set).Remove(mutation.Instance);
    }

    private IReadOnlyDictionary<IRelationDefinition, RelationDelta> ResolveRelationDeltas(
        ResolvedChangeImpact impact)
    {
        var deltas = new Dictionary<IRelationDefinition, RelationDelta>();
        foreach (var relation in impact.AffectedRelations)
        {
            var state = _relations[relation];
            if (!state.HasExactPropagation)
                continue;
            var delta = state.RefreshMembership(
                impact.GetAffectedRoots(relation, relation.LeftSet),
                impact.GetAffectedRoots(relation, relation.RightSet));
            deltas.Add(relation, delta);
        }
        return deltas;
    }

    private static void MergeDelta(
        IDictionary<IRelationDefinition, RelationDelta> deltas,
        IRelationDefinition relation,
        RelationDelta delta)
    {
        if (deltas.TryGetValue(relation, out var existing))
            existing.MergeFrom(delta);
        else
            deltas.Add(relation, delta);
    }

    private void NotifySourceAdded(IObjectSetDefinition set, object source)
    {
        foreach (var participant in _sourceLifecycleParticipants)
            if (ReferenceEquals(participant.SourceSet, set))
                participant.OnSourceAdded(source);
    }

    private void NotifySourceRemoved(IObjectSetDefinition set, object source)
    {
        foreach (var participant in _sourceLifecycleParticipants)
            if (ReferenceEquals(participant.SourceSet, set))
                participant.OnSourceRemoved(source);
    }

    internal int DerivedStateEntryCount =>
        _derivedStates.Values.Sum(state => state.SourceStateEntryCount);

    internal int InvariantStateEntryCount =>
        _invariants.Values.Sum(state => state.SourceStateEntryCount);

    internal int MaterializedRelationPairCount =>
        _relations.Values.Sum(state => state.MaterializedPairCount);

    private ValidatedMutationBatch ValidateMutations(
        IReadOnlyList<RuntimeMutation> mutations,
        ChangeValidationMode validationMode)
    {
        var lifecycle = mutations
            .Where(mutation => mutation is ObjectAdded or ObjectRemoved)
            .ToArray();
        var simulations = _sets.ToDictionary(
            pair => pair.Key,
            pair => new ObjectSetSimulation(pair.Key, pair.Value));
        foreach (var mutation in lifecycle)
        {
            var set = mutation switch
            {
                ObjectAdded added => added.Set,
                ObjectRemoved removed => removed.Set,
                _ => throw new InvalidOperationException("Unsupported lifecycle mutation.")
            };
            if (!simulations.TryGetValue(set, out var simulation))
                throw new ArgumentException("The object set does not belong to this compiled model.");
            if (mutation is ObjectAdded addition)
                simulation.Add(addition.Instance);
            else
                simulation.Remove(((ObjectRemoved)mutation).Instance);
        }

        var lifecycleTargets = lifecycle.Select(mutation => mutation switch
        {
            ObjectAdded added => new SetInstance(added.Set, added.Instance),
            ObjectRemoved removed => new SetInstance(removed.Set, removed.Instance),
            _ => throw new InvalidOperationException("Unsupported lifecycle mutation.")
        }).ToHashSet();
        var properties = NormalizeChanges(mutations.OfType<PropertyChange>()
            .Select(change => ValidateBatchChange(change, lifecycleTargets))
            .ToArray());
        var collections = NormalizeCollectionChanges(mutations.OfType<CollectionChange>().ToArray())
            .Select(change => IsLifecycleTarget(change.Set, change.Owner, lifecycleTargets)
                ? new PropertyChange(change.Set, change.Owner, change.Member, null, null)
                : ValidateCollectionChange(change))
            .ToArray();
        var changes = properties
            .Concat(collections)
            .Where(change => change.Set is null ||
                !lifecycleTargets.Contains(new SetInstance(change.Set, change.Instance)))
            .ToArray();
        if (validationMode == ChangeValidationMode.StrictNewValue)
            ValidateCurrentValues(properties);
        return new ValidatedMutationBatch(lifecycle, changes);
    }

    private PropertyChange ValidateBatchChange(
        PropertyChange change,
        IReadOnlySet<SetInstance> lifecycleTargets)
    {
        if (change.Set is not null && IsLifecycleTarget(change.Set, change.Instance, lifecycleTargets))
        {
            GetSet(change.Set);
            return change;
        }
        return ValidateChange(change);
    }

    private static bool IsLifecycleTarget(
        IObjectSetDefinition? set,
        object instance,
        IReadOnlySet<SetInstance> lifecycleTargets) =>
        set is not null && lifecycleTargets.Contains(new SetInstance(set, instance));

    private static IReadOnlyList<CollectionChange> NormalizeCollectionChanges(
        IReadOnlyList<CollectionChange> changes)
    {
        var normalized = new List<CollectionChange>();
        foreach (var group in changes.GroupBy(
                     change => new ChangedMember(change.Owner, change.Member)))
        {
            var groupChanges = group.ToArray();
            var set = groupChanges[0].Set;
            if (groupChanges.Any(change => !ReferenceEquals(change.Set, set)))
                throw new InvalidOperationException(
                    $"Conflicting object sets were reported for collection '{groupChanges[0].Member.Name}'.");
            normalized.Add(groupChanges.Length == 1
                ? groupChanges[0]
                : CollectionChange.Create(
                    set,
                    groupChanges[0].Owner,
                    groupChanges[0].Member,
                    CollectionChangeKind.Reset,
                    null));
        }
        return normalized;
    }

    private PropertyChange ValidateChange(PropertyChange change)
    {
        if (change.Set is null)
        {
            var matches = _sets
                .Where(pair => pair.Key.ObjectType.IsInstanceOfType(change.Instance) && pair.Value.Contains(change.Instance))
                .Select(pair => pair.Key)
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException($"Expected the changed instance in at most one object set, but found {matches.Length}. Use the object-set Change.Property overload.");
            if (matches.Length == 1)
                change = change.WithSet(matches[0]);
        }

        if (change.Set is not null)
        {
            var set = GetSet(change.Set);
            if (!set.Contains(change.Instance))
                throw new InvalidOperationException("The changed instance is not registered in the specified object set.");
            if (change.Set.KeyMembers.Contains(change.Member))
                throw new InvalidOperationException(
                    $"The registered key member '{change.Member.Name}' of '{change.Set.ObjectType.Name}' cannot be changed. " +
                    "Remove and re-add the object, or declare a genuinely stable key.");
        }
        return change;
    }

    private PropertyChange ValidateCollectionChange(CollectionChange change)
    {
        var normalized = ValidateChange(new PropertyChange(
            change.Set,
            change.Owner,
            change.Member,
            null,
            null));
        var value = MemberReader.Read(change.Member, change.Owner);
        if (value is not IEnumerable collection)
            throw new InvalidOperationException($"Member '{change.Member.Name}' is not a collection.");
        if (change.Kind != CollectionChangeKind.Reset)
        {
            var contains = collection.Cast<object?>().Any(item => ReferenceEquals(item, change.Item));
            if (change.Kind == CollectionChangeKind.Add && !contains)
                throw new InvalidOperationException("The added item is not present in the current collection.");
            if (change.Kind == CollectionChangeKind.Remove && contains)
                throw new InvalidOperationException("The removed item is still present in the current collection.");
        }
        return normalized;
    }

    private static IReadOnlyList<PropertyChange> NormalizeChanges(IReadOnlyList<PropertyChange> changes)
    {
        var normalized = new List<PropertyChange>();
        var positions = new Dictionary<ChangedMember, int>();
        foreach (var change in changes)
        {
            var key = new ChangedMember(change.Instance, change.Member);
            if (!positions.TryGetValue(key, out var position))
            {
                positions.Add(key, normalized.Count);
                normalized.Add(change);
                continue;
            }

            var previous = normalized[position];
            if (!Equals(previous.NewValue, change.OldValue))
                throw new InvalidOperationException(
                    $"Conflicting changes for member '{change.Member.Name}'. " +
                    $"Expected the next old value to equal the previous new value.");
            normalized[position] = new PropertyChange(
                previous.Set,
                previous.Instance,
                previous.Member,
                previous.OldValue,
                change.NewValue);
        }
        return normalized;
    }

    private static void ValidateCurrentValues(IReadOnlyList<PropertyChange> changes)
    {
        foreach (var change in changes)
        {
            var actual = MemberReader.Read(change.Member, change.Instance);
            if (!Equals(actual, change.NewValue))
                throw new InvalidOperationException(
                    $"The current value of '{change.Member.Name}' does not equal the reported new value.");
        }
    }

    private readonly struct ChangedMember : IEquatable<ChangedMember>
    {
        private readonly object _instance;
        private readonly MemberInfo _member;

        public ChangedMember(object instance, MemberInfo member)
        {
            _instance = instance;
            _member = member;
        }

        public bool Equals(ChangedMember other) =>
            ReferenceEquals(_instance, other._instance) && _member.Equals(other._member);

        public override bool Equals(object? obj) => obj is ChangedMember other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(_instance),
            _member);
    }

    private readonly struct SetInstance : IEquatable<SetInstance>
    {
        private readonly IObjectSetDefinition _set;
        private readonly object _instance;

        public SetInstance(IObjectSetDefinition set, object instance)
        {
            _set = set;
            _instance = instance;
        }

        public bool Equals(SetInstance other) =>
            ReferenceEquals(_set, other._set) && ReferenceEquals(_instance, other._instance);

        public override bool Equals(object? obj) => obj is SetInstance other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(_set),
            RuntimeHelpers.GetHashCode(_instance));
    }

    private sealed class ObjectSetSimulation
    {
        private readonly IObjectSetDefinition _definition;
        private readonly HashSet<object> _instances;
        private readonly Dictionary<object, object> _keyOwners;
        private readonly Dictionary<object, object> _registeredKeys;

        public ObjectSetSimulation(IObjectSetDefinition definition, ObjectSetRuntime state)
        {
            _definition = definition;
            _instances = state.Instances.ToHashSet(ReferenceEqualityComparer.Instance);
            _registeredKeys = state.RegisteredEntries.ToDictionary(
                pair => pair.Instance,
                pair => pair.Key,
                ReferenceEqualityComparer.Instance);
            _keyOwners = state.RegisteredEntries.ToDictionary(pair => pair.Key, pair => pair.Instance);
        }

        public void Add(object instance)
        {
            if (!_definition.ObjectType.IsInstanceOfType(instance))
                throw new ArgumentException($"Expected an instance of '{_definition.ObjectType.Name}'.");
            if (!_instances.Add(instance))
                throw new InvalidOperationException("The object instance is already registered in this object set.");
            var key = _definition.ReadKey(instance) ?? throw new InvalidOperationException("Object keys cannot be null.");
            if (_keyOwners.ContainsKey(key))
                throw new InvalidOperationException(
                    $"An object with key '{key}' is already registered in '{_definition.ObjectType.Name}'.");
            _keyOwners.Add(key, instance);
            _registeredKeys.Add(instance, key);
        }

        public void Remove(object instance)
        {
            if (!_instances.Remove(instance))
                throw new InvalidOperationException("The removed instance is not registered in the specified object set.");
            var key = _registeredKeys[instance];
            _registeredKeys.Remove(instance);
            _keyOwners.Remove(key);
        }
    }

    private sealed record ValidatedMutationBatch(
        IReadOnlyList<RuntimeMutation> LifecycleMutations,
        IReadOnlyList<PropertyChange> Changes);

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
}

internal sealed class ObjectSetRuntime
{
    private readonly IObjectSetDefinition _definition;
    private readonly HashSet<object> _instances = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _byKey = new();
    private readonly Dictionary<object, object> _registeredKeys = new(ReferenceEqualityComparer.Instance);

    public ObjectSetRuntime(IObjectSetDefinition definition) => _definition = definition;
    public IEnumerable<object> Instances => _instances;
    public int Count => _instances.Count;
    public IEnumerable<(object Instance, object Key)> RegisteredEntries =>
        _registeredKeys.Select(pair => (pair.Key, pair.Value));
    public bool Contains(object instance) => _instances.Contains(instance);
    public object GetRegisteredKey(object instance) => _registeredKeys.TryGetValue(instance, out var key)
        ? key
        : throw new InvalidOperationException("The source instance is not registered in its object set.");

    public object CaptureState() => new State(
        _instances.ToArray(),
        _byKey.ToArray(),
        _registeredKeys.ToArray());

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        _instances.Clear();
        _instances.UnionWith(state.Instances);
        _byKey.Clear();
        foreach (var pair in state.ByKey)
            _byKey.Add(pair.Key, pair.Value);
        _registeredKeys.Clear();
        foreach (var pair in state.RegisteredKeys)
            _registeredKeys.Add(pair.Key, pair.Value);
    }

    public void Add(object instance)
    {
        if (!_definition.ObjectType.IsInstanceOfType(instance))
            throw new ArgumentException($"Expected an instance of '{_definition.ObjectType.Name}'.");
        if (_instances.Contains(instance))
            throw new InvalidOperationException("The object instance is already registered in this object set.");
        var key = _definition.ReadKey(instance) ?? throw new InvalidOperationException("Object keys cannot be null.");
        if (_byKey.ContainsKey(key))
            throw new InvalidOperationException($"An object with key '{key}' is already registered in '{_definition.ObjectType.Name}'.");
        _instances.Add(instance);
        _byKey.Add(key, instance);
        _registeredKeys.Add(instance, key);
    }

    public bool Remove(object instance)
    {
        if (!_instances.Remove(instance)) return false;
        var key = _registeredKeys[instance];
        _registeredKeys.Remove(instance);
        if (_byKey.TryGetValue(key, out var keyed) && ReferenceEquals(keyed, instance))
            _byKey.Remove(key);
        return true;
    }

    private sealed record State(
        IReadOnlyList<object> Instances,
        IReadOnlyList<KeyValuePair<object, object>> ByKey,
        IReadOnlyList<KeyValuePair<object, object>> RegisteredKeys);
}

internal interface IRelationRuntimeState
{
    IObjectSetDefinition LeftSet { get; }
    IObjectSetDefinition RightSet { get; }
    bool HasExactPropagation { get; }
    int MaterializedPairCount { get; }
    int ForwardIndexEntryCount { get; }
    int ReverseIndexEntryCount { get; }
    long PredicateEvaluationCount { get; }
    object CaptureState();
    void RestoreState(object snapshot);
    void ResetDiagnostics();
    void EnableExactPropagation();
    RelationDelta AddLeft(object instance);
    RelationDelta RemoveLeft(object instance);
    RelationDelta AddRight(object instance);
    RelationDelta RemoveRight(object instance);
    void ReindexRight(object instance);
    void ReindexLeft(object instance);
    RelationDelta RefreshMembership(IEnumerable<object> lefts, IEnumerable<object> rights);
    IReadOnlyCollection<object> GetLeftsForRights(IEnumerable<object> rights);
}

internal sealed class RelationRuntimeState<TLeft, TRight> : IRelationRuntimeState
    where TLeft : class where TRight : class
{
    private readonly RelationDefinition<TLeft, TRight> _definition;
    private readonly ObjectSetRuntime _leftObjects;
    private readonly ObjectSetRuntime _rightObjects;
    private readonly Dictionary<CompositeKey, HashSet<TRight>> _index = [];
    private readonly Dictionary<TRight, CompositeKey> _keys = new(ReferenceEqualityComparer<TRight>.Instance);
    private readonly Dictionary<CompositeKey, HashSet<TLeft>> _leftIndex = [];
    private readonly Dictionary<TLeft, CompositeKey> _leftKeys = new(ReferenceEqualityComparer<TLeft>.Instance);
    private readonly Dictionary<TLeft, HashSet<TRight>> _rightsByLeft = new(ReferenceEqualityComparer<TLeft>.Instance);
    private readonly Dictionary<TRight, HashSet<TLeft>> _leftsByRight = new(ReferenceEqualityComparer<TRight>.Instance);
    private bool _hasExactPropagation;
    private long _predicateEvaluationCount;

    public RelationRuntimeState(
        RelationDefinition<TLeft, TRight> definition,
        ObjectSetRuntime leftObjects,
        ObjectSetRuntime rightObjects)
    {
        _definition = definition;
        _leftObjects = leftObjects;
        _rightObjects = rightObjects;
    }

    public IObjectSetDefinition LeftSet => _definition.Left;
    public IObjectSetDefinition RightSet => _definition.Right;
    public bool HasExactPropagation => _hasExactPropagation;
    public int MaterializedPairCount => _rightsByLeft.Sum(pair => pair.Value.Count);
    public int ForwardIndexEntryCount => _keys.Count;
    public int ReverseIndexEntryCount => _leftKeys.Count;
    public long PredicateEvaluationCount => _predicateEvaluationCount;

    public void ResetDiagnostics() => _predicateEvaluationCount = 0;

    public object CaptureState() => new State(
        Copy(_index, ReferenceEqualityComparer<TRight>.Instance),
        new Dictionary<TRight, CompositeKey>(_keys, ReferenceEqualityComparer<TRight>.Instance),
        Copy(_leftIndex, ReferenceEqualityComparer<TLeft>.Instance),
        new Dictionary<TLeft, CompositeKey>(_leftKeys, ReferenceEqualityComparer<TLeft>.Instance),
        Copy(_rightsByLeft, ReferenceEqualityComparer<TRight>.Instance, ReferenceEqualityComparer<TLeft>.Instance),
        Copy(_leftsByRight, ReferenceEqualityComparer<TLeft>.Instance, ReferenceEqualityComparer<TRight>.Instance),
        _predicateEvaluationCount);

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        Replace(_index, state.Index);
        Replace(_keys, state.Keys);
        Replace(_leftIndex, state.LeftIndex);
        Replace(_leftKeys, state.LeftKeys);
        Replace(_rightsByLeft, state.RightsByLeft);
        Replace(_leftsByRight, state.LeftsByRight);
        _predicateEvaluationCount = state.PredicateEvaluationCount;
    }

    public int RelatedCount(TLeft left) =>
        _rightsByLeft.TryGetValue(left, out var rights) ? rights.Count : 0;

    public bool IsRelated(TLeft left, TRight right) =>
        _rightsByLeft.TryGetValue(left, out var rights) && rights.Contains(right);

    public void EnableExactPropagation() => _hasExactPropagation = true;

    public RelationDelta AddLeft(object instance)
    {
        var delta = new RelationDelta();
        if (!_hasExactPropagation)
            return delta;
        var left = (TLeft)instance;
        AddLeftToIndex(left);
        foreach (var right in Related(left))
            AddPair(left, right, delta);
        return delta;
    }

    public RelationDelta RemoveLeft(object instance)
    {
        var delta = new RelationDelta();
        if (!_hasExactPropagation)
            return delta;
        var left = (TLeft)instance;
        RemoveLeftFromIndex(left);
        if (!_rightsByLeft.Remove(left, out var rights))
            return delta;
        foreach (var right in rights)
        {
            RemoveReversePair(left, right);
            delta.Remove(left, right);
        }
        return delta;
    }

    public RelationDelta AddRight(object instance)
    {
        var delta = new RelationDelta();
        var right = (TRight)instance;
        AddToIndex(right);
        if (_hasExactPropagation)
            foreach (var left in RelatedFromRightCore(right))
                AddPair(left, right, delta);
        return delta;
    }

    public RelationDelta RemoveRight(object instance)
    {
        var delta = new RelationDelta();
        var right = (TRight)instance;
        if (_hasExactPropagation && _leftsByRight.Remove(right, out var lefts))
            foreach (var left in lefts)
            {
                _rightsByLeft[left].Remove(right);
                if (_rightsByLeft[left].Count == 0)
                    _rightsByLeft.Remove(left);
                delta.Remove(left, right);
            }
        RemoveFromIndex(right);
        return delta;
    }

    public IReadOnlyList<TRight> Related(TLeft left)
    {
        IEnumerable<TRight> candidates = _definition.AccessPlan switch
        {
            ScanAccessPlan => _rightObjects.Instances.Cast<TRight>(),
            HashJoinAccessPlan hashPlan => ReadHashCandidates(hashPlan, left),
            _ => throw new NotSupportedException($"Unsupported access plan '{_definition.AccessPlan.GetType().Name}'.")
        };
        return candidates.Where(right => Evaluate(left, right)).ToArray();
    }

    public IReadOnlyList<TLeft> RelatedFromRight(TRight right) => RelatedFromRightCore(right);

    public void ReindexRight(object instance) => Reindex((TRight)instance);

    public void ReindexLeft(object instance)
    {
        if (!_hasExactPropagation)
            return;
        var left = (TLeft)instance;
        RemoveLeftFromIndex(left);
        AddLeftToIndex(left);
    }

    public RelationDelta RefreshMembership(IEnumerable<object> lefts, IEnumerable<object> rights)
    {
        var delta = new RelationDelta();
        if (!_hasExactPropagation)
            return delta;

        var typedLefts = lefts.Cast<TLeft>().ToHashSet(ReferenceEqualityComparer<TLeft>.Instance);
        var typedRights = rights.Cast<TRight>().ToHashSet(ReferenceEqualityComparer<TRight>.Instance);
        foreach (var left in typedLefts)
        {
            var oldRights = _rightsByLeft.TryGetValue(left, out var existing)
                ? existing.ToHashSet(ReferenceEqualityComparer<TRight>.Instance)
                : new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance);
            var newRights = Related(left).ToHashSet(ReferenceEqualityComparer<TRight>.Instance);
            foreach (var right in oldRights.Except(newRights, ReferenceEqualityComparer<TRight>.Instance).ToArray())
                RemovePair(left, right, delta);
            foreach (var right in newRights.Except(oldRights, ReferenceEqualityComparer<TRight>.Instance))
                AddPair(left, right, delta);
        }

        foreach (var right in typedRights)
        {
            var oldLefts = _leftsByRight.TryGetValue(right, out var existing)
                ? existing.ToHashSet(ReferenceEqualityComparer<TLeft>.Instance)
                : new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance);
            var newLefts = RelatedFromRightCore(right)
                .ToHashSet(ReferenceEqualityComparer<TLeft>.Instance);
            foreach (var left in oldLefts.Except(newLefts, ReferenceEqualityComparer<TLeft>.Instance).ToArray())
                RemovePair(left, right, delta);
            foreach (var left in newLefts.Except(oldLefts, ReferenceEqualityComparer<TLeft>.Instance))
                AddPair(left, right, delta);
        }
        return delta;
    }

    public IReadOnlyCollection<object> GetLeftsForRights(IEnumerable<object> rights)
    {
        var lefts = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!_hasExactPropagation)
            return lefts;
        foreach (var right in rights.Cast<TRight>())
            if (_leftsByRight.TryGetValue(right, out var related))
                lefts.UnionWith(related);
        return lefts;
    }

    private void Reindex(TRight right)
    {
        RemoveFromIndex(right);
        AddToIndex(right);
    }

    private void AddToIndex(TRight right)
    {
        if (_definition.AccessPlan is not HashJoinAccessPlan hashPlan)
            return;
        var key = ReadRightKey(hashPlan, right);
        if (!_index.TryGetValue(key, out var bucket))
            _index.Add(key, bucket = new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance));
        bucket.Add(right);
        _keys[right] = key;
    }

    private void AddLeftToIndex(TLeft left)
    {
        if (_definition.ReverseAccessPlan is not HashJoinAccessPlan hashPlan)
            return;
        var key = ReadLeftKey(hashPlan, left);
        if (!_leftIndex.TryGetValue(key, out var bucket))
            _leftIndex.Add(key, bucket = new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance));
        bucket.Add(left);
        _leftKeys[left] = key;
    }

    private void RemoveLeftFromIndex(TLeft left)
    {
        if (!_leftKeys.Remove(left, out var key))
            return;
        if (!_leftIndex.TryGetValue(key, out var bucket))
            return;
        bucket.Remove(left);
        if (bucket.Count == 0)
            _leftIndex.Remove(key);
    }

    private void RemoveFromIndex(TRight right)
    {
        if (!_keys.Remove(right, out var key)) return;
        if (_index.TryGetValue(key, out var bucket))
        {
            bucket.Remove(right);
            if (bucket.Count == 0) _index.Remove(key);
        }
    }

    private void AddPair(TLeft left, TRight right, RelationDelta delta)
    {
        if (!_rightsByLeft.TryGetValue(left, out var rights))
            _rightsByLeft.Add(left, rights = new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance));
        if (!rights.Add(right))
            return;
        if (!_leftsByRight.TryGetValue(right, out var lefts))
            _leftsByRight.Add(right, lefts = new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance));
        lefts.Add(left);
        delta.Add(left, right);
    }

    private void RemovePair(TLeft left, TRight right, RelationDelta delta)
    {
        if (!_rightsByLeft.TryGetValue(left, out var rights) || !rights.Remove(right))
            return;
        if (rights.Count == 0)
            _rightsByLeft.Remove(left);
        RemoveReversePair(left, right);
        delta.Remove(left, right);
    }

    private void RemoveReversePair(TLeft left, TRight right)
    {
        if (!_leftsByRight.TryGetValue(right, out var lefts))
            return;
        lefts.Remove(left);
        if (lefts.Count == 0)
            _leftsByRight.Remove(right);
    }

    private IEnumerable<TRight> ReadHashCandidates(HashJoinAccessPlan plan, TLeft left)
    {
        var key = CreateKey(plan.JoinKeyParts.Select(part => part.Left.Read(left)), plan);
        return _index.TryGetValue(key, out var bucket) ? bucket : [];
    }

    private IReadOnlyList<TLeft> RelatedFromRightCore(TRight right)
    {
        IEnumerable<TLeft> candidates = _definition.ReverseAccessPlan switch
        {
            null or ScanAccessPlan => _leftObjects.Instances.Cast<TLeft>(),
            HashJoinAccessPlan hashPlan => ReadReverseHashCandidates(hashPlan, right),
            _ => throw new NotSupportedException(
                $"Unsupported reverse access plan '{_definition.ReverseAccessPlan.GetType().Name}'.")
        };
        return candidates.Where(left => Evaluate(left, right)).ToArray();
    }

    private bool Evaluate(TLeft left, TRight right)
    {
        _predicateEvaluationCount++;
        return _definition.Predicate(left, right);
    }

    private IEnumerable<TLeft> ReadReverseHashCandidates(HashJoinAccessPlan plan, TRight right)
    {
        var key = ReadRightKey(plan, right);
        return _leftIndex.TryGetValue(key, out var bucket) ? bucket : [];
    }

    private static CompositeKey ReadLeftKey(HashJoinAccessPlan plan, TLeft left) =>
        CreateKey(plan.JoinKeyParts.Select(part => part.Left.Read(left)), plan);

    private static CompositeKey ReadRightKey(HashJoinAccessPlan plan, TRight right) =>
        CreateKey(plan.JoinKeyParts.Select(part => part.Right.Read(right)), plan);

    private static CompositeKey CreateKey(IEnumerable<object?> components, HashJoinAccessPlan plan) =>
        new(components.ToArray(), plan.JoinKeyParts.Select(part => part.Comparer).ToArray());

    private static Dictionary<TKey, HashSet<TValue>> Copy<TKey, TValue>(
        Dictionary<TKey, HashSet<TValue>> source,
        IEqualityComparer<TValue> valueComparer,
        IEqualityComparer<TKey>? keyComparer = null) where TKey : notnull where TValue : class =>
        source.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToHashSet(valueComparer),
            keyComparer ?? source.Comparer);

    private static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> source)
        where TKey : notnull
    {
        target.Clear();
        foreach (var pair in source)
            target.Add(pair.Key, pair.Value);
    }

    private sealed record State(
        Dictionary<CompositeKey, HashSet<TRight>> Index,
        Dictionary<TRight, CompositeKey> Keys,
        Dictionary<CompositeKey, HashSet<TLeft>> LeftIndex,
        Dictionary<TLeft, CompositeKey> LeftKeys,
        Dictionary<TLeft, HashSet<TRight>> RightsByLeft,
        Dictionary<TRight, HashSet<TLeft>> LeftsByRight,
        long PredicateEvaluationCount);

}

internal readonly struct CompositeKey : IEquatable<CompositeKey>
{
    private readonly object?[] _components;
    private readonly IReadOnlyList<IEqualityComparer<object?>> _comparers;

    public CompositeKey(object?[] components, IReadOnlyList<IEqualityComparer<object?>> comparers)
    {
        _components = components;
        _comparers = comparers;
    }

    public bool Equals(CompositeKey other)
    {
        if (_components.Length != other._components.Length)
            return false;
        for (var index = 0; index < _components.Length; index++)
            if (!_comparers[index].Equals(_components[index], other._components[index]))
                return false;
        return true;
    }
    public override bool Equals(object? obj) => obj is CompositeKey other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var index = 0; index < _components.Length; index++)
        {
            var component = _components[index];
            hash.Add(component is null ? 0 : _comparers[index].GetHashCode(component!));
        }
        return hash.ToHashCode();
    }
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static ReferenceEqualityComparer<T> Instance { get; } = new();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
