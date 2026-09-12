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
    private readonly ObjectSetBuilder<TSource> _source;

    internal DerivedBuilder(RelationModelBuilder model, ObjectSetBuilder<TSource> source)
    {
        _model = model;
        _source = source;
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
}

public sealed class DerivedUsingBuilder<TSource, TItem>
    where TSource : class
    where TItem : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSetBuilder<TSource> _source;
    private readonly Relation<TSource, TItem> _relation;

    internal DerivedUsingBuilder(
        RelationModelBuilder model,
        ObjectSetBuilder<TSource> source,
        Relation<TSource, TItem> relation)
    {
        _model = model;
        _source = source;
        _relation = relation;
    }

    public Derived<TSource, TItem, TValue> Compute<TValue>(
        Expression<Func<TSource, IReadOnlyList<TItem>, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new DerivedDefinition<TSource, TItem, TValue>(
            _source.Definition,
            _relation.Definition,
            computation,
            computation.Compile());
        _model.AddDerived(definition);
        return new Derived<TSource, TItem, TValue>(definition);
    }
}

public sealed class Derived<TSource, TItem, TValue>
    where TSource : class
    where TItem : class
{
    internal Derived(DerivedDefinition<TSource, TItem, TValue> definition) => Definition = definition;
    internal DerivedDefinition<TSource, TItem, TValue> Definition { get; }
}

public sealed class InvariantBuilder<TSource> where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSetBuilder<TSource> _source;

    internal InvariantBuilder(RelationModelBuilder model, ObjectSetBuilder<TSource> source)
    {
        _model = model;
        _source = source;
    }

    public InvariantUsingBuilder<TSource, TItem, TValue> Using<TItem, TValue>(
        Derived<TSource, TItem, TValue> derived)
        where TItem : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        _model.EnsureDerived(derived.Definition);
        if (!ReferenceEquals(derived.Definition.SourceSet, _source.Definition))
            throw new ArgumentException("The derived state's source set must match the invariant source set.", nameof(derived));
        return new InvariantUsingBuilder<TSource, TItem, TValue>(_model, derived);
    }
}

public sealed class InvariantUsingBuilder<TSource, TItem, TValue>
    where TSource : class
    where TItem : class
{
    private readonly RelationModelBuilder _model;
    private readonly Derived<TSource, TItem, TValue> _derived;

    internal InvariantUsingBuilder(RelationModelBuilder model, Derived<TSource, TItem, TValue> derived)
    {
        _model = model;
        _derived = derived;
    }

    public Invariant<TSource, TItem, TValue> Must(Expression<Func<TSource, TValue, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var definition = new InvariantDefinition<TSource, TItem, TValue>(
            _derived.Definition,
            predicate,
            predicate.Compile());
        _model.AddInvariant(definition);
        return new Invariant<TSource, TItem, TValue>(definition, _model.EnsureMutable);
    }
}

public sealed class Invariant<TSource, TItem, TValue>
    where TSource : class
    where TItem : class
{
    private readonly Action _ensureMutable;

    internal Invariant(InvariantDefinition<TSource, TItem, TValue> definition, Action ensureMutable)
    {
        Definition = definition;
        _ensureMutable = ensureMutable;
    }
    internal InvariantDefinition<TSource, TItem, TValue> Definition { get; }

    public Invariant<TSource, TItem, TValue> ReactWith(InvariantReaction reaction)
    {
        _ensureMutable();
        if (reaction == InvariantReaction.ScheduleRepair)
            throw new ArgumentException("Use ScheduleRepairWith(...) to configure a repair scheduler.", nameof(reaction));
        Definition.Reaction = reaction;
        return this;
    }

    public Invariant<TSource, TItem, TValue> ScheduleRepairWith(Action<TSource> scheduler)
    {
        _ensureMutable();
        ArgumentNullException.ThrowIfNull(scheduler);
        Definition.RepairScheduler = scheduler;
        Definition.Reaction = InvariantReaction.ScheduleRepair;
        return this;
    }
}

internal interface IDerivedDefinition
{
    IObjectSetDefinition SourceSet { get; }
    IRelationDefinition Relation { get; }
    LambdaExpression ComputationExpression { get; }
    ExpressionDependencyAnalysis Analysis { get; }
    IDerivedRuntimeState CreateState(IRelationRuntimeState relationState);
}

internal sealed class DerivedDefinition<TSource, TItem, TValue>(
    ObjectSetDefinition<TSource> sourceSet,
    RelationDefinition<TSource, TItem> relation,
    LambdaExpression computationExpression,
    Func<TSource, IReadOnlyList<TItem>, TValue> computation) : IDerivedDefinition
    where TSource : class
    where TItem : class
{
    public ObjectSetDefinition<TSource> SourceSetDefinition { get; } = sourceSet;
    public RelationDefinition<TSource, TItem> RelationDefinition { get; } = relation;
    public Func<TSource, IReadOnlyList<TItem>, TValue> Computation { get; } = computation;
    public IObjectSetDefinition SourceSet => SourceSetDefinition;
    public IRelationDefinition Relation => RelationDefinition;
    public LambdaExpression ComputationExpression { get; } = computationExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeDerived(computationExpression);

    public IDerivedRuntimeState CreateState(IRelationRuntimeState relationState) =>
        new DerivedRuntimeState<TSource, TItem, TValue>(this, (RelationRuntimeState<TSource, TItem>)relationState);
}

internal interface IDerivedRuntimeState : ISourceLifecycleParticipant
{
    IDerivedDefinition Definition { get; }
    int SourceStateEntryCount { get; }
    void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact);
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

    public TValue Get(TSource source)
    {
        if (_cache.TryGetValue(source, out var entry) && entry.State == DerivedValueState.Fresh)
            return entry.Value;
        var value = definition.Computation(source, relationState.Related(source));
        _cache[source] = new CacheEntry(value, DerivedValueState.Fresh);
        return value;
    }

    public DerivedValueState GetState(TSource source) =>
        _cache.TryGetValue(source, out var entry) ? entry.State : DerivedValueState.Dirty;

    public void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact)
    {
        foreach (var source in sources.Cast<TSource>())
        {
            if (!_cache.TryGetValue(source, out var entry))
                continue;
            entry.State = DependencyStateTransitions.Apply(entry.State, impact);
        }
    }

    public void OnSourceAdded(object source) => _cache.Remove((TSource)source);

    public void OnSourceRemoved(object source) => _cache.Remove((TSource)source);

    private sealed class CacheEntry(TValue value, DerivedValueState state)
    {
        public TValue Value { get; } = value;
        public DerivedValueState State { get; set; } = state;
    }
}

internal interface IInvariantDefinition
{
    IDerivedDefinition Derived { get; }
    InvariantReaction Reaction { get; }
    ExpressionDependencyAnalysis Analysis { get; }
    IInvariantRuntimeState CreateState(IDerivedRuntimeState derivedState);
}

internal sealed class InvariantDefinition<TSource, TItem, TValue>(
    DerivedDefinition<TSource, TItem, TValue> derived,
    LambdaExpression predicateExpression,
    Func<TSource, TValue, bool> predicate) : IInvariantDefinition
    where TSource : class
    where TItem : class
{
    public DerivedDefinition<TSource, TItem, TValue> DerivedDefinition { get; } = derived;
    public Func<TSource, TValue, bool> Predicate { get; } = predicate;
    public LambdaExpression PredicateExpression { get; } = predicateExpression;
    public ExpressionDependencyAnalysis Analysis { get; } =
        ExpressionDependencyAnalyzer.AnalyzeInvariant(predicateExpression);
    public IDerivedDefinition Derived => DerivedDefinition;
    public InvariantReaction Reaction { get; set; } = InvariantReaction.MarkDirty;
    public Action<TSource>? RepairScheduler { get; set; }

    public IInvariantRuntimeState CreateState(IDerivedRuntimeState derivedState) =>
        new InvariantRuntimeState<TSource, TItem, TValue>(
            this,
            (DerivedRuntimeState<TSource, TItem, TValue>)derivedState);
}

internal interface IInvariantRuntimeState : ISourceLifecycleParticipant
{
    IInvariantDefinition Definition { get; }
    int SourceStateEntryCount { get; }
    void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact);
}

internal sealed class InvariantRuntimeState<TSource, TItem, TValue>(
    InvariantDefinition<TSource, TItem, TValue> definition,
    DerivedRuntimeState<TSource, TItem, TValue> derivedState) : IInvariantRuntimeState
    where TSource : class
    where TItem : class
{
    private readonly Dictionary<TSource, InvariantEvaluationState> _states = new(ReferenceEqualityComparer<TSource>.Instance);

    public IInvariantDefinition Definition => definition;
    public IObjectSetDefinition SourceSet => definition.Derived.SourceSet;
    public int SourceStateEntryCount => _states.Count;

    public bool Evaluate(TSource source)
    {
        var valid = definition.Predicate(source, derivedState.Get(source));
        _states[source] = valid ? InvariantEvaluationState.Valid : InvariantEvaluationState.Violated;
        return valid;
    }

    public InvariantEvaluationState GetState(TSource source) =>
        _states.TryGetValue(source, out var state) ? state : InvariantEvaluationState.Unknown;

    public void ApplyImpact(IEnumerable<object> sources, DependencyImpactKind impact)
    {
        var typedSources = sources.Cast<TSource>().Distinct(ReferenceEqualityComparer<TSource>.Instance).ToArray();
        switch (definition.Reaction)
        {
            case InvariantReaction.EvaluateImmediately:
                foreach (var source in typedSources)
                    Evaluate(source);
                break;
            case InvariantReaction.MarkInvalid:
                Mark(typedSources, DependencyImpactKind.Invalid);
                break;
            case InvariantReaction.ScheduleRepair:
                Mark(typedSources, DependencyImpactKind.Invalid);
                foreach (var source in typedSources)
                    definition.RepairScheduler!(source);
                break;
            default:
                Mark(typedSources, impact);
                break;
        }
    }

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
