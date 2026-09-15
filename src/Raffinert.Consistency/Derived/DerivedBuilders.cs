using System.Linq.Expressions;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency;

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
        false,
        []);

    internal DerivedBuilder(RelationModelBuilder model, ObjectSet<TSource> source)
    {
        _model = model;
        _source = source;
    }

    /// <summary>Configures semantic severity for direct source-member changes.</summary>
    public DerivedBuilder<TSource> Impact(Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DerivedImpactPolicyBuilder<TSource>();
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

    /// <summary>Uses a derived value owned by an object reached through a tracked reference.</summary>
    public ProjectedDerivedUpstreamBuilder<TSource, TUpstreamSource, TValue> Using<TUpstreamSource, TValue>(
        Expression<Func<TSource, TUpstreamSource>> selector,
        Derived<TUpstreamSource, TValue> upstream)
        where TUpstreamSource : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(upstream);
        _model.EnsureDerived(upstream.Definition);
        return new ProjectedDerivedUpstreamBuilder<TSource, TUpstreamSource, TValue>(
            _model, _source, selector, upstream);
    }

    public ProjectedDerivedUpstreamBuilder<TSource, TUpstreamSource, TFirst, TSecond>
        Using<TUpstreamSource, TFirst, TSecond>(
            Expression<Func<TSource, TUpstreamSource>> selector,
            Derived<TUpstreamSource, TFirst> first,
            Derived<TUpstreamSource, TSecond> second)
        where TUpstreamSource : class
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        _model.EnsureDerived(first.Definition);
        _model.EnsureDerived(second.Definition);
        if (!ReferenceEquals(first.Definition.SourceSet, second.Definition.SourceSet))
            throw new ArgumentException("Both projected upstream values must belong to the same object set.");
        return new ProjectedDerivedUpstreamBuilder<TSource, TUpstreamSource, TFirst, TSecond>(
            _model, _source, selector, first, second);
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

public sealed class ProjectedDerivedUpstreamBuilder<TSource, TUpstreamSource, TUpstream>
    where TSource : class
    where TUpstreamSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;
    private readonly Expression<Func<TSource, TUpstreamSource>> _selector;
    private readonly Derived<TUpstreamSource, TUpstream> _upstream;
    private DerivedImpactPolicy _impactPolicy = new(
        DependencySeverity.Dirty, DependencySeverity.Dirty, DependencySeverity.Dirty,
        DependencySeverity.Dirty, false, []);

    internal ProjectedDerivedUpstreamBuilder(
        RelationModelBuilder model,
        ObjectSet<TSource> source,
        Expression<Func<TSource, TUpstreamSource>> selector,
        Derived<TUpstreamSource, TUpstream> upstream) =>
        (_model, _source, _selector, _upstream) = (model, source, selector, upstream);

    public ProjectedDerivedUpstreamBuilder<TSource, TUpstreamSource, TUpstream> Impact(
        Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DerivedImpactPolicyBuilder<TSource>();
        configure(builder);
        _impactPolicy = builder.Build();
        return this;
    }

    public Derived<TSource, TValue> Compute<TValue>(Expression<Func<TSource, TUpstream, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new ProjectedComposedDerivedDefinition<TSource, TUpstreamSource, TUpstream, TValue>(
            _source.Definition, _upstream.Definition, _selector, _selector.Compile(),
            computation, computation.Compile(), _impactPolicy);
        _model.AddDerived(definition);
        return new Derived<TSource, TValue>(definition, _model.EnsureMutable);
    }
}

public sealed class ProjectedDerivedUpstreamBuilder<TSource, TUpstreamSource, TFirst, TSecond>
    where TSource : class
    where TUpstreamSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;
    private readonly Expression<Func<TSource, TUpstreamSource>> _selector;
    private readonly Derived<TUpstreamSource, TFirst> _first;
    private readonly Derived<TUpstreamSource, TSecond> _second;
    private DerivedImpactPolicy _impactPolicy = new(
        DependencySeverity.Dirty, DependencySeverity.Dirty, DependencySeverity.Dirty,
        DependencySeverity.Dirty, false, []);

    internal ProjectedDerivedUpstreamBuilder(
        RelationModelBuilder model,
        ObjectSet<TSource> source,
        Expression<Func<TSource, TUpstreamSource>> selector,
        Derived<TUpstreamSource, TFirst> first,
        Derived<TUpstreamSource, TSecond> second) =>
        (_model, _source, _selector, _first, _second) = (model, source, selector, first, second);

    public ProjectedDerivedUpstreamBuilder<TSource, TUpstreamSource, TFirst, TSecond> Impact(
        Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DerivedImpactPolicyBuilder<TSource>();
        configure(builder);
        _impactPolicy = builder.Build();
        return this;
    }

    public Derived<TSource, TValue> Compute<TValue>(
        Expression<Func<TSource, TFirst, TSecond, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new ProjectedComposedDerivedDefinition<TSource, TUpstreamSource, TFirst, TSecond, TValue>(
            _source.Definition, _first.Definition, _second.Definition, _selector, _selector.Compile(),
            computation, computation.Compile(), _impactPolicy);
        _model.AddDerived(definition);
        return new Derived<TSource, TValue>(definition, _model.EnsureMutable);
    }
}

public sealed class DerivedUpstreamBuilder<TSource, TUpstream> where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;
    private readonly Derived<TSource, TUpstream> _upstream;
    private DerivedImpactPolicy _impactPolicy = DefaultImpact;

    internal DerivedUpstreamBuilder(RelationModelBuilder model, ObjectSet<TSource> source,
        Derived<TSource, TUpstream> upstream) => (_model, _source, _upstream) = (model, source, upstream);

    public DerivedUpstreamBuilder<TSource, TUpstream> Impact(Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DerivedImpactPolicyBuilder<TSource>();
        configure(builder);
        _impactPolicy = builder.Build();
        return this;
    }

    public Derived<TSource, TValue> Compute<TValue>(Expression<Func<TSource, TUpstream, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new ComposedDerivedDefinition<TSource, TUpstream, TValue>(
            _source.Definition, _upstream.Definition, computation, computation.Compile(), _impactPolicy);
        _model.AddDerived(definition);
        return new Derived<TSource, TValue>(definition, _model.EnsureMutable);
    }

    private static DerivedImpactPolicy DefaultImpact { get; } = new(
        DependencySeverity.Dirty, DependencySeverity.Dirty, DependencySeverity.Dirty,
        DependencySeverity.Dirty, false, []);
}

public sealed class DerivedUpstreamBuilder<TSource, TFirst, TSecond> where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly ObjectSet<TSource> _source;
    private readonly Derived<TSource, TFirst> _first;
    private readonly Derived<TSource, TSecond> _second;
    private DerivedImpactPolicy _impactPolicy = DefaultImpact;

    internal DerivedUpstreamBuilder(RelationModelBuilder model, ObjectSet<TSource> source,
        Derived<TSource, TFirst> first, Derived<TSource, TSecond> second) =>
        (_model, _source, _first, _second) = (model, source, first, second);

    public DerivedUpstreamBuilder<TSource, TFirst, TSecond> Impact(Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DerivedImpactPolicyBuilder<TSource>();
        configure(builder);
        _impactPolicy = builder.Build();
        return this;
    }

    public Derived<TSource, TValue> Compute<TValue>(
        Expression<Func<TSource, TFirst, TSecond, TValue>> computation)
    {
        ArgumentNullException.ThrowIfNull(computation);
        var definition = new ComposedDerivedDefinition<TSource, TFirst, TSecond, TValue>(
            _source.Definition, _first.Definition, _second.Definition, computation, computation.Compile(),
            _impactPolicy);
        _model.AddDerived(definition);
        return new Derived<TSource, TValue>(definition, _model.EnsureMutable);
    }

    private static DerivedImpactPolicy DefaultImpact { get; } = new(
        DependencySeverity.Dirty, DependencySeverity.Dirty, DependencySeverity.Dirty,
        DependencySeverity.Dirty, false, []);
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
        false,
        []);
    private bool _useIncrementalComputation;
    private bool _useConservativePropagation;

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
    public DerivedUsingBuilder<TSource, TItem> Impact(Action<DerivedImpactPolicyBuilder<TSource>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new DerivedImpactPolicyBuilder<TSource>();
        configure(builder);
        _impactPolicy = builder.Build();
        return this;
    }

    /// <summary>
    /// Enables exact incremental maintenance for a recognized standalone aggregate. Unsupported
    /// expressions continue to use the original compiled computation as a full-recompute fallback.
    /// </summary>
    public DerivedUsingBuilder<TSource, TItem> Incrementally()
    {
        if (_useConservativePropagation)
            throw new InvalidOperationException("Incremental computation requires exact propagation.");
        _useIncrementalComputation = true;
        return this;
    }

    /// <summary>
    /// Uses source invalidation instead of retaining exact relation pairs. The computation remains
    /// lazy and may invalidate a safe superset of sources.
    /// </summary>
    public DerivedUsingBuilder<TSource, TItem> PreferConservativePropagation()
    {
        if (_useIncrementalComputation)
            throw new InvalidOperationException("Conservative propagation cannot supply exact incremental deltas.");
        _useConservativePropagation = true;
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
            _model.ForceFullRecomputePlansForTesting || !_useIncrementalComputation,
            _useConservativePropagation);
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

    public InvariantUsingBuilder<TSource, TFirst, TSecond> Using<TFirst, TSecond>(
        Derived<TSource, TFirst> first,
        Derived<TSource, TSecond> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        _model.EnsureDerived(first.Definition);
        _model.EnsureDerived(second.Definition);
        if (!ReferenceEquals(first.Definition.SourceSet, _source.Definition) ||
            !ReferenceEquals(second.Definition.SourceSet, _source.Definition))
            throw new ArgumentException("All derived source sets must match the invariant source set.");
        return new InvariantUsingBuilder<TSource, TFirst, TSecond>(_model, first, second);
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

public sealed class InvariantUsingBuilder<TSource, TFirst, TSecond> where TSource : class
{
    private readonly RelationModelBuilder _model;
    private readonly Derived<TSource, TFirst> _first;
    private readonly Derived<TSource, TSecond> _second;

    internal InvariantUsingBuilder(RelationModelBuilder model, Derived<TSource, TFirst> first,
        Derived<TSource, TSecond> second) => (_model, _first, _second) = (model, first, second);

    public Invariant<TSource> Must(Expression<Func<TSource, TFirst, TSecond, bool>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var definition = new MultiInvariantDefinition<TSource, TFirst, TSecond>(
            _first.Definition,
            _second.Definition,
            predicate,
            predicate.Compile());
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

