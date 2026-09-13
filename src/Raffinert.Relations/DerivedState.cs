using System.Linq.Expressions;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

/// <summary>Describes the freshness and usability of a cached derived value.</summary>
public enum DerivedValueState
{
    /// <summary>The value was computed from all currently accepted dependencies.</summary>
    Fresh,
    /// <summary>No fresh value is available; it is either not computed yet or its cache may be stale.</summary>
    Dirty,
    /// <summary>The cached value must not be relied upon before successful recomputation.</summary>
    Invalid
}

/// <summary>Describes the evaluation and dependency state of an invariant.</summary>
public enum InvariantEvaluationState
{
    /// <summary>The invariant has not been evaluated for the source.</summary>
    Unknown,
    /// <summary>The invariant was evaluated successfully and its predicate returned true.</summary>
    Valid,
    /// <summary>The invariant was evaluated successfully and its predicate returned false.</summary>
    Violated,
    /// <summary>The evaluated result may be stale and should be evaluated again when freshness matters.</summary>
    Dirty,
    /// <summary>The evaluated result must not be relied upon before successful revalidation.</summary>
    Invalid
}

public enum InvariantReaction
{
    EvaluateImmediately,
    MarkDirty,
    MarkInvalid,
    ScheduleRepair
}

public sealed class DerivedBuilder<TSource> where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;
    private DerivedImpactPolicy _impactPolicy = new(
        DependencySeverity.Dirty,
        DependencySeverity.Dirty,
        DependencySeverity.Dirty,
        DependencySeverity.Dirty,
        false);

    internal DerivedBuilder(RelationModelBuilder model, ObjectSet<TSource> source)
    {
        _model = model;
        _source = source;
    }

    /// <summary>Configures semantic severity for direct source-member changes.</summary>
    public DerivedBuilder<TSource> Impact(Action<DerivedImpactPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DerivedImpactPolicyBuilder();
        configure(builder);
        _impactPolicy = builder.Build();
        return this;
    }

    public DerivedUsingBuilder<TSource, TItem> Using<TItem>(Relation<TSource, TItem> relation)
        where TItem : class
    {
        ArgumentNullException.ThrowIfNull(relation);
        _model.EnsureRelation(relation.Definition);
        if (!ReferenceEquals(relation.Definition.Left, _source.Definition))
            throw new ArgumentException("The relation's left object set must be the derived state's source set.", nameof(relation));
        return new DerivedUsingBuilder<TSource, TItem>(_model, _source, relation);
    }

    public DerivedUpstreamBuilder<TSource, TValue> Using<TValue>(Derived<TSource, TValue> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        _model.EnsureDerived(upstream.Definition);
        if (!ReferenceEquals(upstream.Definition.SourceSet, _source.Definition))
            throw new ArgumentException("The upstream source set must match the derived source set.", nameof(upstream));
        return new DerivedUpstreamBuilder<TSource, TValue>(_model, _source, upstream);
    }

    public DerivedUpstreamBuilder<TSource, TValue1, TValue2> Using<TValue1, TValue2>(
        Derived<TSource, TValue1> first,
        Derived<TSource, TValue2> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        _model.EnsureDerived(first.Definition);
        _model.EnsureDerived(second.Definition);
        if (!ReferenceEquals(first.Definition.SourceSet, _source.Definition) ||
            !ReferenceEquals(second.Definition.SourceSet, _source.Definition))
            throw new ArgumentException("All upstream source sets must match the derived source set.");
        return new DerivedUpstreamBuilder<TSource, TValue1, TValue2>(_model, _source, first, second);
    }

    /// <summary>Defines a value computed directly from the source object.</summary>
    public Derived<TSource, TValue> Compute<TValue>(Expression<Func<TSource, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new SourceDerivedDefinition<TSource, TValue>(
            _source.Definition,
            computation,
            computation.Compile(),
            _impactPolicy);
        _model.AddDerived(definition);
        return new Derived<TSource, TValue>(definition, _model.EnsureMutable);
    }
}

public sealed class DerivedUpstreamBuilder<TSource, TUpstream> where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;
    private readonly Derived<TSource, TUpstream> _upstream;

    internal DerivedUpstreamBuilder(RelationModelBuilder model, ObjectSet<TSource> source,
        Derived<TSource, TUpstream> upstream) => (_model, _source, _upstream) = (model, source, upstream);

    public Derived<TSource, TValue> Compute<TValue>(Expression<Func<TSource, TUpstream, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new ComposedDerivedDefinition<TSource, TUpstream, TValue>(
            _source.Definition, _upstream.Definition, computation, computation.Compile());
        _model.AddDerived(definition);
        return new Derived<TSource, TValue>(definition, _model.EnsureMutable);
    }
}

public sealed class DerivedUpstreamBuilder<TSource, TFirst, TSecond> where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;
    private readonly Derived<TSource, TFirst> _first;
    private readonly Derived<TSource, TSecond> _second;

    internal DerivedUpstreamBuilder(RelationModelBuilder model, ObjectSet<TSource> source,
        Derived<TSource, TFirst> first, Derived<TSource, TSecond> second) =>
        (_model, _source, _first, _second) = (model, source, first, second);

    public Derived<TSource, TValue> Compute<TValue>(
        Expression<Func<TSource, TFirst, TSecond, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new ComposedDerivedDefinition<TSource, TFirst, TSecond, TValue>(
            _source.Definition, _first.Definition, _second.Definition, computation, computation.Compile());
        _model.AddDerived(definition);
        return new Derived<TSource, TValue>(definition, _model.EnsureMutable);
    }
}

public sealed class DerivedUsingBuilder<TSource, TItem>
    where TSource : class
    where TItem : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;
    private readonly Relation<TSource, TItem> _relation;
    private DerivedImpactPolicy _impactPolicy = new(
        DependencySeverity.Dirty,
        DependencySeverity.Dirty,
        DependencySeverity.Dirty,
        DependencySeverity.Dirty,
        false);
    private bool _useIncrementalComputation;

    internal DerivedUsingBuilder(
        RelationModelBuilder model,
        ObjectSet<TSource> source,
        Relation<TSource, TItem> relation)
    {
        _model = model;
        _source = source;
        _relation = relation;
    }

    /// <summary>Configures semantic severity independently of the relation's access plan.</summary>
    public DerivedUsingBuilder<TSource, TItem> Impact(Action<DerivedImpactPolicyBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DerivedImpactPolicyBuilder();
        configure(builder);
        _impactPolicy = builder.Build();
        return this;
    }

    /// <summary>
    /// Enables conservative incremental planning for a recognized standalone aggregate. Unsupported
    /// expressions continue to use the original compiled computation as a full-recompute fallback.
    /// </summary>
    public DerivedUsingBuilder<TSource, TItem> Incrementally()
    {
        _useIncrementalComputation = true;
        return this;
    }

    public Derived<TSource, TValue> Compute<TValue>(
        Expression<Func<TSource, IReadOnlyList<TItem>, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new DerivedDefinition<TSource, TItem, TValue>(
            _source.Definition,
            _relation.Definition,
            computation,
            computation.Compile(),
            _impactPolicy,
            _model.ForceFullRecomputePlansForTesting || !_useIncrementalComputation);
        _model.AddDerived(definition);
        return new Derived<TSource, TValue>(definition, _model.EnsureMutable);
    }
}

public sealed class Derived<TSource, TValue>
    where TSource : class
{
    private readonly Action _ensureMutable;

    internal Derived(IDerivedDefinition definition, Action ensureMutable)
    {
        Definition = definition;
        _ensureMutable = ensureMutable;
    }
    internal IDerivedDefinition Definition { get; }

    /// <summary>The optional stable logical key assigned to this derived value.</summary>
    public string? DefinitionKey => Definition.DefinitionKey;

    /// <summary>Assigns a stable logical key for diagnostics and durable integration messages.</summary>
    public Derived<TSource, TValue> Named(string definitionKey)
    {
        _ensureMutable();
        Definition.DefinitionKey = ObjectSetBuilder<TSource>.ValidateDefinitionKey(definitionKey);
        return this;
    }

    /// <summary>
    /// Explicitly permits incomplete dependency tracking for this computation. Cached freshness is
    /// not guaranteed for dependencies hidden by opaque code or mutable external state.
    /// </summary>
    public Derived<TSource, TValue> AllowIncompleteDependencies()
    {
        _ensureMutable();
        Definition.AllowIncompleteDependencies = true;
        return this;
    }
}

public sealed class InvariantBuilder<TSource> where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;

    internal InvariantBuilder(RelationModelBuilder model, ObjectSet<TSource> source)
    {
        _model = model;
        _source = source;
    }

    public InvariantUsingBuilder<TSource, TValue> Using<TValue>(Derived<TSource, TValue> derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        _model.EnsureDerived(derived.Definition);
        if (!ReferenceEquals(derived.Definition.SourceSet, _source.Definition))
            throw new ArgumentException("The derived state's source set must match the invariant source set.", nameof(derived));
        return new InvariantUsingBuilder<TSource, TValue>(_model, derived);
    }
}

public sealed class InvariantUsingBuilder<TSource, TValue>
    where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly Derived<TSource, TValue> _derived;

    internal InvariantUsingBuilder(RelationModelBuilder model, Derived<TSource, TValue> derived)
    {
        _model = model;
        _derived = derived;
    }

    public Invariant<TSource> Must(Expression<Func<TSource, TValue, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var definition = _derived.Definition.CreateInvariant(predicate, predicate.Compile());
        _model.AddInvariant(definition);
        return new Invariant<TSource>(definition, _model.EnsureMutable);
    }
}

public sealed class Invariant<TSource>
    where TSource : class
{
    private readonly Action _ensureMutable;

    internal Invariant(IInvariantDefinition definition, Action ensureMutable)
    {
        Definition = definition;
        _ensureMutable = ensureMutable;
    }
    internal IInvariantDefinition Definition { get; }

    /// <summary>The optional stable logical key assigned to this invariant.</summary>
    public string? DefinitionKey => Definition.DefinitionKey;

    /// <summary>Assigns a stable logical key for diagnostics and durable integration messages.</summary>
    public Invariant<TSource> Named(string definitionKey)
    {
        _ensureMutable();
        Definition.DefinitionKey = ObjectSetBuilder<TSource>.ValidateDefinitionKey(definitionKey);
        return this;
    }

    public Invariant<TSource> ReactWith(InvariantReaction reaction)
    {
        _ensureMutable();
        if (reaction == InvariantReaction.ScheduleRepair)
            throw new ArgumentException("Use ScheduleRepairWith(...) to configure a repair scheduler.", nameof(reaction));
        Definition.Reaction = reaction;
        return this;
    }

    public Invariant<TSource> ScheduleRepairWith(Action<TSource> scheduler)
    {
        _ensureMutable();
        ArgumentNullException.ThrowIfNull(scheduler);
        Definition.SetRepairScheduler(scheduler);
        Definition.Reaction = InvariantReaction.ScheduleRepair;
        return this;
    }

    /// <summary>
    /// Explicitly permits incomplete dependency tracking for this predicate. Cached evaluation
    /// freshness is not guaranteed for dependencies hidden by opaque code or mutable external state.
    /// </summary>
    public Invariant<TSource> AllowIncompleteDependencies()
    {
        _ensureMutable();
        Definition.AllowIncompleteDependencies = true;
        return this;
    }
}

internal interface IDerivedDefinition
{
    string? DefinitionKey { get; set; }
    IObjectSetDefinition SourceSet { get; }
    IReadOnlyList<DerivedInput> Inputs { get; }
    LambdaExpression ComputationExpression { get; }
    ExpressionDependencyAnalysis Analysis { get; }
    DerivedImpactPolicy ImpactPolicy { get; }
    string ComputationPlanName { get; }
    bool AllowIncompleteDependencies { get; set; }
    IDerivedRuntimeState CreateState(
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived);
    IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate);
}

internal sealed record DerivedInput(IRelationDefinition? Relation, IDerivedDefinition? Upstream = null);

internal sealed class DerivedDefinition<TSource, TItem, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    RelationDefinition<TSource, TItem> relation,
    LambdaExpression computationExpression,
    Func<TSource, IReadOnlyList<TItem>, TValue> computation,
    DerivedImpactPolicy impactPolicy,
    bool forceFullRecompute) : IDerivedDefinition
    where TSource : class
    where TItem : class
{
    public ObjectSetDefinition<TSource> SourceSetDefinition { get; } = sourceSet;
    public string? DefinitionKey { get; set; }
    public RelationDefinition<TSource, TItem> RelationDefinition { get; } = relation;
    public Func<TSource, IReadOnlyList<TItem>, TValue> Computation { get; } = computation;
    public IObjectSetDefinition SourceSet => SourceSetDefinition;
    public IReadOnlyList<DerivedInput> Inputs { get; } = [new(relation)];
    public LambdaExpression ComputationExpression { get; } = computationExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeDerived(computationExpression);
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public IDerivedComputationPlan<TSource, TItem, TValue>? IncrementalPlan { get; } =
        DerivedComputationPlanner.Create(
            (Expression<Func<TSource, IReadOnlyList<TItem>, TValue>>)computationExpression,
            forceFullRecompute);
    public string ComputationPlanName => IncrementalPlan?.DisplayName ?? "FullRecompute";
    public bool AllowIncompleteDependencies { get; set; }

    public IDerivedRuntimeState CreateState(IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new DerivedRuntimeState<TSource, TItem, TValue>(this, (RelationRuntimeState<TSource, TItem>)relations[RelationDefinition]);

    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(
            this,
            predicate,
            (Func<TSource, TValue, bool>)compiledPredicate);
}

internal sealed class SourceDerivedDefinition<TSource, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    Expression<Func<TSource, TValue>> computationExpression,
    Func<TSource, TValue> computation,
    DerivedImpactPolicy impactPolicy) : IDerivedDefinition
    where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs { get; } = [];
    public LambdaExpression ComputationExpression => computationExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeDerived(computationExpression);
    public DerivedImpactPolicy ImpactPolicy { get; } = impactPolicy;
    public string ComputationPlanName => "SourceFullRecompute";
    public bool AllowIncompleteDependencies { get; set; }

    public IDerivedRuntimeState CreateState(
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this, computation);

    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(
            this,
            predicate,
            (Func<TSource, TValue, bool>)compiledPredicate);
}

internal sealed class ComposedDerivedDefinition<TSource, TUpstream, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    IDerivedDefinition upstream,
    Expression<Func<TSource, TUpstream, TValue>> expression,
    Func<TSource, TUpstream, TValue> computation) : IDerivedDefinition where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs { get; } = [new(null, upstream)];
    public LambdaExpression ComputationExpression => expression;
    public ExpressionDependencyAnalysis Analysis { get; } = ExpressionDependencyAnalyzer.AnalyzeSourceDerived(expression);
    public DerivedImpactPolicy ImpactPolicy { get; } = DefaultImpact;
    public string ComputationPlanName => "DependencyFullRecompute";
    public bool AllowIncompleteDependencies { get; set; }
    public IDerivedRuntimeState CreateState(IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this,
            source => computation(source, (TUpstream)resolveDerived(upstream).GetValue(source)!));
    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(this, predicate, (Func<TSource, TValue, bool>)compiledPredicate);
    private static DerivedImpactPolicy DefaultImpact { get; } = new(
        DependencySeverity.Dirty, DependencySeverity.Dirty, DependencySeverity.Dirty,
        DependencySeverity.Dirty, false);
}

internal sealed class ComposedDerivedDefinition<TSource, TFirst, TSecond, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    IDerivedDefinition first,
    IDerivedDefinition second,
    Expression<Func<TSource, TFirst, TSecond, TValue>> expression,
    Func<TSource, TFirst, TSecond, TValue> computation) : IDerivedDefinition where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IObjectSetDefinition SourceSet => sourceSet;
    public IReadOnlyList<DerivedInput> Inputs { get; } = [new(null, first), new(null, second)];
    public LambdaExpression ComputationExpression => expression;
    public ExpressionDependencyAnalysis Analysis { get; } = ExpressionDependencyAnalyzer.AnalyzeSourceDerived(expression);
    public DerivedImpactPolicy ImpactPolicy { get; } = DefaultImpact;
    public string ComputationPlanName => "DependencyFullRecompute";
    public bool AllowIncompleteDependencies { get; set; }
    public IDerivedRuntimeState CreateState(IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        Func<IDerivedDefinition, IDerivedRuntimeState> resolveDerived) =>
        new SourceDerivedRuntimeState<TSource, TValue>(this, source => computation(
            source,
            (TFirst)resolveDerived(first).GetValue(source)!,
            (TSecond)resolveDerived(second).GetValue(source)!));
    public IInvariantDefinition CreateInvariant(LambdaExpression predicate, Delegate compiledPredicate) =>
        new InvariantDefinition<TSource, TValue>(this, predicate, (Func<TSource, TValue, bool>)compiledPredicate);
    private static DerivedImpactPolicy DefaultImpact { get; } = new(
        DependencySeverity.Dirty, DependencySeverity.Dirty, DependencySeverity.Dirty,
        DependencySeverity.Dirty, false);
}

internal sealed class SourceDerivedRuntimeState<TSource, TValue>(
    IDerivedDefinition definition,
    Func<TSource, TValue> computation) : IDerivedRuntimeState
    where TSource : class
{
    private readonly Dictionary<TSource, CacheEntry> _cache = new(ReferenceEqualityComparer<TSource>.Instance);
    public IDerivedDefinition Definition => definition;
    public IObjectSetDefinition SourceSet => definition.SourceSet;
    public int SourceStateEntryCount => _cache.Count;
    public long FullRecomputationCount { get; private set; }
    public long IncrementalUpdateCount => 0;

    public object? GetValue(object source)
    {
        var typed = (TSource)source;
        if (_cache.TryGetValue(typed, out var entry) && entry.State == DerivedValueState.Fresh)
            return entry.Value;
        var value = computation(typed);
        FullRecomputationCount++;
        _cache[typed] = new CacheEntry(value, DerivedValueState.Fresh);
        return value;
    }

    public DerivedValueState GetValueState(object source) =>
        _cache.TryGetValue((TSource)source, out var entry) ? entry.State : DerivedValueState.Dirty;

    public void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact)
    {
        foreach (var source in sources.Cast<TSource>())
            if (_cache.TryGetValue(source, out var entry))
                entry.State = DependencyStateTransitions.Apply(entry.State, impact);
    }

    public IReadOnlyCollection<object> ApplyIncremental(
        IEnumerable<object> sources,
        RelationImpact? relationImpact,
        IReadOnlyList<PropertyChange> changes) => [];

    public void ResetDiagnostics() => FullRecomputationCount = 0;
    public object CaptureState() => new State(
        _cache.ToDictionary(pair => pair.Key, pair => new CacheEntry(pair.Value.Value, pair.Value.State),
            ReferenceEqualityComparer<TSource>.Instance),
        FullRecomputationCount);

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        _cache.Clear();
        foreach (var pair in state.Cache)
            _cache.Add(pair.Key, pair.Value);
        FullRecomputationCount = state.FullRecomputationCount;
    }

    public void OnSourceAdded(object source) => _cache.Remove((TSource)source);
    public void OnSourceRemoved(object source) => _cache.Remove((TSource)source);

    private sealed class CacheEntry(TValue value, DerivedValueState state)
    {
        public TValue Value { get; } = value;
        public DerivedValueState State { get; set; } = state;
    }

    private sealed record State(Dictionary<TSource, CacheEntry> Cache, long FullRecomputationCount);
}

internal interface IDerivedRuntimeState : ISourceLifecycleParticipant
{
    IDerivedDefinition Definition { get; }
    int SourceStateEntryCount { get; }
    long FullRecomputationCount { get; }
    long IncrementalUpdateCount { get; }
    void ResetDiagnostics();
    object CaptureState();
    void RestoreState(object snapshot);
    object? GetValue(object source);
    DerivedValueState GetValueState(object source);
    void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact);
    IReadOnlyCollection<object> ApplyIncremental(
        IEnumerable<object> sources,
        RelationImpact? relationImpact,
        IReadOnlyList<PropertyChange> changes);
}

internal sealed class DerivedRuntimeState<TSource, TItem, TValue>(
    DerivedDefinition<TSource, TItem, TValue> definition,
    RelationRuntimeState<TSource, TItem> relationState) : IDerivedRuntimeState
    where TSource : class
    where TItem : class
{
    private readonly Dictionary<TSource, CacheEntry> _cache = new(ReferenceEqualityComparer<TSource>.Instance);

    public IDerivedDefinition Definition => definition;
    public IObjectSetDefinition SourceSet => definition.SourceSet;
    public int SourceStateEntryCount => _cache.Count;
    public long FullRecomputationCount { get; private set; }
    public long IncrementalUpdateCount { get; private set; }

    public TValue Get(TSource source)
    {
        if (_cache.TryGetValue(source, out var entry) && entry.State == DerivedValueState.Fresh)
            return entry.Value;
        var value = definition.Computation(source, relationState.Related(source));
        FullRecomputationCount++;
        _cache[source] = new CacheEntry(value, DerivedValueState.Fresh);
        return value;
    }

    public DerivedValueState GetState(TSource source) =>
        _cache.TryGetValue(source, out var entry) ? entry.State : DerivedValueState.Dirty;

    public object? GetValue(object source) => Get((TSource)source);
    public DerivedValueState GetValueState(object source) => GetState((TSource)source);

    public void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact)
    {
        foreach (var source in sources.Cast<TSource>())
        {
            if (!_cache.TryGetValue(source, out var entry))
                continue;
            entry.State = DependencyStateTransitions.Apply(entry.State, impact);
        }
    }

    public IReadOnlyCollection<object> ApplyIncremental(
        IEnumerable<object> sources,
        RelationImpact? relationImpact,
        IReadOnlyList<PropertyChange> changes)
    {
        if (definition.IncrementalPlan is null)
            return [];
        var updatedSources = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var source in sources.Cast<TSource>())
        {
            if (!_cache.TryGetValue(source, out var entry) || entry.State != DerivedValueState.Fresh ||
                !definition.IncrementalPlan.TryUpdate(
                    entry.Value, source, relationImpact, changes, relationState, out var updated))
                continue;
            entry.Value = updated;
            IncrementalUpdateCount++;
            updatedSources.Add(source);
        }
        return updatedSources;
    }

    public void ResetDiagnostics()
    {
        FullRecomputationCount = 0;
        IncrementalUpdateCount = 0;
    }

    public object CaptureState() => new State(
        _cache.ToDictionary(
            pair => pair.Key,
            pair => new CacheEntry(pair.Value.Value, pair.Value.State),
            ReferenceEqualityComparer<TSource>.Instance),
        FullRecomputationCount,
        IncrementalUpdateCount);

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        _cache.Clear();
        foreach (var pair in state.Cache)
            _cache.Add(pair.Key, pair.Value);
        FullRecomputationCount = state.FullRecomputationCount;
        IncrementalUpdateCount = state.IncrementalUpdateCount;
    }

    public void OnSourceAdded(object source) => _cache.Remove((TSource)source);

    public void OnSourceRemoved(object source) => _cache.Remove((TSource)source);

    private sealed class CacheEntry(TValue value, DerivedValueState state)
    {
        public TValue Value { get; set; } = value;
        public DerivedValueState State { get; set; } = state;
    }

    private sealed record State(
        Dictionary<TSource, CacheEntry> Cache,
        long FullRecomputationCount,
        long IncrementalUpdateCount);
}

internal interface IInvariantDefinition
{
    string? DefinitionKey { get; set; }
    IDerivedDefinition Derived { get; }
    IReadOnlyList<IDerivedDefinition> UpstreamDerived { get; }
    InvariantReaction Reaction { get; set; }
    LambdaExpression PredicateExpression { get; }
    ExpressionDependencyAnalysis Analysis { get; }
    bool AllowIncompleteDependencies { get; set; }
    IInvariantRuntimeState CreateState(IDerivedRuntimeState derivedState);
    void DispatchRepair(object source);
    void SetRepairScheduler(Delegate scheduler);
}

internal sealed class InvariantDefinition<TSource, TValue>(
    IDerivedDefinition derived,
    LambdaExpression predicateExpression,
    Func<TSource, TValue, bool> predicate) : IInvariantDefinition
    where TSource : class
{
    public string? DefinitionKey { get; set; }
    public IDerivedDefinition DerivedDefinition { get; } = derived;
    public Func<TSource, TValue, bool> Predicate { get; } = predicate;
    public LambdaExpression PredicateExpression { get; } = predicateExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeInvariant(predicateExpression);
    public bool AllowIncompleteDependencies { get; set; }
    public IDerivedDefinition Derived => DerivedDefinition;
    public IReadOnlyList<IDerivedDefinition> UpstreamDerived { get; } = [derived];
    public InvariantReaction Reaction { get; set; } = InvariantReaction.MarkDirty;
    public Action<TSource>? RepairScheduler { get; set; }

    public IInvariantRuntimeState CreateState(IDerivedRuntimeState derivedState) =>
        new InvariantRuntimeState<TSource, TValue>(
            this,
            derivedState);

    public void DispatchRepair(object source) => RepairScheduler!((TSource)source);
    public void SetRepairScheduler(Delegate scheduler) => RepairScheduler = (Action<TSource>)scheduler;
}

internal interface IInvariantRuntimeState : ISourceLifecycleParticipant
{
    IInvariantDefinition Definition { get; }
    int SourceStateEntryCount { get; }
    object CaptureState();
    void RestoreState(object snapshot);
    void ApplyImpact(
        IEnumerable<object> sources,
        DependencyImpactKind impact,
        RuntimePolicyActions policyActions);
    void EvaluatePolicy(object source);
    bool EvaluateValue(object source);
    InvariantEvaluationState GetValueState(object source);
}

internal sealed class InvariantRuntimeState<TSource, TValue>(
    InvariantDefinition<TSource, TValue> definition,
    IDerivedRuntimeState derivedState) : IInvariantRuntimeState
    where TSource : class
{
    private readonly Dictionary<TSource, InvariantEvaluationState> _states = new(ReferenceEqualityComparer<TSource>.Instance);

    public IInvariantDefinition Definition => definition;
    public IObjectSetDefinition SourceSet => definition.Derived.SourceSet;
    public int SourceStateEntryCount => _states.Count;

    public bool Evaluate(TSource source)
    {
        var valid = definition.Predicate(source, (TValue)derivedState.GetValue(source)!);
        _states[source] = valid ? InvariantEvaluationState.Valid : InvariantEvaluationState.Violated;
        return valid;
    }

    public InvariantEvaluationState GetState(TSource source) =>
        _states.TryGetValue(source, out var state) ? state : InvariantEvaluationState.Unknown;

    public object CaptureState() => new Dictionary<TSource, InvariantEvaluationState>(
        _states,
        ReferenceEqualityComparer<TSource>.Instance);

    public void RestoreState(object snapshot)
    {
        _states.Clear();
        foreach (var pair in (Dictionary<TSource, InvariantEvaluationState>)snapshot)
            _states.Add(pair.Key, pair.Value);
    }

    public void ApplyImpact(
        IEnumerable<object> sources,
        DependencyImpactKind impact,
        RuntimePolicyActions policyActions)
    {
        var typedSources = sources.Cast<TSource>().Distinct(ReferenceEqualityComparer<TSource>.Instance).ToArray();
        switch (definition.Reaction)
        {
            case InvariantReaction.EvaluateImmediately:
                Mark(typedSources, impact);
                foreach (var source in typedSources)
                    policyActions.AddImmediateEvaluation(this, source);
                break;
            case InvariantReaction.MarkInvalid:
                Mark(typedSources, DependencyImpactKind.Invalid);
                break;
            case InvariantReaction.ScheduleRepair:
                Mark(typedSources, DependencyImpactKind.Invalid);
                foreach (var source in typedSources)
                    policyActions.AddRepairRequest(
                        definition,
                        source,
                        DependencyImpactKind.Invalid);
                break;
            default:
                Mark(typedSources, impact);
                break;
        }
    }

    public void EvaluatePolicy(object source) => Evaluate((TSource)source);
    public bool EvaluateValue(object source) => Evaluate((TSource)source);
    public InvariantEvaluationState GetValueState(object source) => GetState((TSource)source);

    public void OnSourceAdded(object source) => _states.Remove((TSource)source);

    public void OnSourceRemoved(object source) => _states.Remove((TSource)source);

    private void Mark(IEnumerable<TSource> sources, DependencyImpactKind impact)
    {
        foreach (var source in sources)
        {
            if (!_states.TryGetValue(source, out var current))
                continue;
            _states[source] = DependencyStateTransitions.Apply(current, impact);
        }
    }
}
