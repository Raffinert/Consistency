using System.Linq.Expressions;

namespace Raffinert.Relations;

public enum DerivedValueState
{
    Fresh,
    Dirty,
    Invalid
}

public enum InvariantEvaluationState
{
    Unknown,
    Valid,
    Violated,
    Dirty,
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

    public IDerivedRuntimeState CreateState(IRelationRuntimeState relationState) =>
        new DerivedRuntimeState<TSource, TItem, TValue>(this, (RelationRuntimeState<TSource, TItem>)relationState);
}

internal interface IDerivedRuntimeState
{
    IDerivedDefinition Definition { get; }
    void Invalidate(bool invalid);
}

internal sealed class DerivedRuntimeState<TSource, TItem, TValue>(
    DerivedDefinition<TSource, TItem, TValue> definition,
    RelationRuntimeState<TSource, TItem> relationState) : IDerivedRuntimeState
    where TSource : class
    where TItem : class
{
    private readonly Dictionary<TSource, CacheEntry> _cache = new(ReferenceEqualityComparer<TSource>.Instance);

    public IDerivedDefinition Definition => definition;

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

    public void Invalidate(bool invalid)
    {
        foreach (var entry in _cache.Values)
        {
            if (invalid || entry.State != DerivedValueState.Invalid)
                entry.State = invalid ? DerivedValueState.Invalid : DerivedValueState.Dirty;
        }
    }

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
    IInvariantRuntimeState CreateState(IDerivedRuntimeState derivedState, ObjectSetRuntime sourceObjects);
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
    public IDerivedDefinition Derived => DerivedDefinition;
    public InvariantReaction Reaction { get; set; } = InvariantReaction.MarkDirty;
    public Action<TSource>? RepairScheduler { get; set; }

    public IInvariantRuntimeState CreateState(IDerivedRuntimeState derivedState, ObjectSetRuntime sourceObjects) =>
        new InvariantRuntimeState<TSource, TItem, TValue>(
            this,
            (DerivedRuntimeState<TSource, TItem, TValue>)derivedState,
            sourceObjects);
}

internal interface IInvariantRuntimeState
{
    IInvariantDefinition Definition { get; }
    void OnDependencyChanged(bool invalid);
}

internal sealed class InvariantRuntimeState<TSource, TItem, TValue>(
    InvariantDefinition<TSource, TItem, TValue> definition,
    DerivedRuntimeState<TSource, TItem, TValue> derivedState,
    ObjectSetRuntime sourceObjects) : IInvariantRuntimeState
    where TSource : class
    where TItem : class
{
    private readonly Dictionary<TSource, InvariantEvaluationState> _states = new(ReferenceEqualityComparer<TSource>.Instance);

    public IInvariantDefinition Definition => definition;

    public bool Evaluate(TSource source)
    {
        var valid = definition.Predicate(source, derivedState.Get(source));
        _states[source] = valid ? InvariantEvaluationState.Valid : InvariantEvaluationState.Violated;
        return valid;
    }

    public InvariantEvaluationState GetState(TSource source) =>
        _states.TryGetValue(source, out var state) ? state : InvariantEvaluationState.Unknown;

    public void OnDependencyChanged(bool invalid)
    {
        switch (definition.Reaction)
        {
            case InvariantReaction.EvaluateImmediately:
                foreach (var source in sourceObjects.Instances.Cast<TSource>())
                    Evaluate(source);
                break;
            case InvariantReaction.MarkInvalid:
                MarkAll(InvariantEvaluationState.Invalid);
                break;
            case InvariantReaction.ScheduleRepair:
                MarkAll(InvariantEvaluationState.Invalid);
                foreach (var source in sourceObjects.Instances.Cast<TSource>())
                    definition.RepairScheduler!(source);
                break;
            default:
                MarkAll(invalid ? InvariantEvaluationState.Invalid : InvariantEvaluationState.Dirty);
                break;
        }
    }

    private void MarkAll(InvariantEvaluationState state)
    {
        foreach (var source in _states.Keys.ToArray())
            _states[source] = state;
    }
}
