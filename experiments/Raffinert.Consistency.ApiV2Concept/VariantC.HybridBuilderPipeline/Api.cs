using System.Linq.Expressions;
using Raffinert.Consistency.ApiV2Concept.Common;

namespace Raffinert.Consistency.ApiV2Concept.VariantC.HybridBuilderPipeline;

internal sealed class HybridModel
{
    internal ConsistencyModelBuilder Builder { get; } = new();

    public HybridSet<T> Objects<T>() where T : class => new(this, Builder.Objects<T>());

    public HybridRelationDraft<TLeft, TRight> Relation<TLeft, TRight>(
        HybridSet<TLeft> left,
        HybridSet<TRight> right)
        where TLeft : class
        where TRight : class => new(this, left, right);

    public HybridDerivedStart<T> Derived<T>(HybridSet<T> source) where T : class => new(this, source);

    public HybridInvariant<T> Invariant<T>(HybridSet<T> source, HybridValue<T, bool> value)
        where T : class => new(Builder.Invariant(source.Raw).Using(value.Raw));

    public CompiledConsistencyModel Build() => Builder.Build();
}

internal sealed class HybridSet<T> where T : class
{
    private readonly ObjectSetBuilder<T> _builder;
    private string? _name;

    internal HybridSet(HybridModel model, ObjectSetBuilder<T> builder) => (Model, _builder) = (model, builder);
    internal HybridModel Model { get; }
    internal ObjectSet<T> Raw { get; private set; } = null!;

    public HybridSet<T> Named(string name)
    {
        _name = name;
        return this;
    }

    public HybridSet<T> Key<TKey>(Expression<Func<T, TKey>> key)
    {
        if (_name is not null)
            _builder.Named(_name);
        Raw = _builder.Key(key);
        return this;
    }
}

internal sealed class HybridDerivedStart<TSource> where TSource : class
{
    private readonly HybridModel _model;
    private readonly HybridSet<TSource> _source;

    internal HybridDerivedStart(HybridModel model, HybridSet<TSource> source) =>
        (_model, _source) = (model, source);

    public HybridValue<TSource, TValue> Select<TValue>(Expression<Func<TSource, TValue>> expression) =>
        new(_source, _model.Builder.Derived(_source.Raw).Compute(expression));

    public HybridValue<TSource, TValue> Select<TValue>(
        Expression<Func<TSource, TValue>> expression,
        Action<DerivedImpactPolicyBuilder<TSource>> impact) =>
        new(_source, _model.Builder.Derived(_source.Raw).Impact(impact).Compute(expression));

    public HybridSource<TSource> DependsOn<TValue>(Expression<Func<TSource, TValue>> dependency) =>
        new(_source, _model.Builder.Derived(_source.Raw).DependsOn(dependency));

    public HybridRelationAggregate<TSource, TItem> From<TItem>(HybridRelation<TSource, TItem> relation)
        where TItem : class => new(_source, relation);

    public HybridPair<TSource, TFirst, TSecond> From<TFirst, TSecond>(
        HybridValue<TSource, TFirst> first,
        HybridValue<TSource, TSecond> second) => new(_source, first, second);

    public HybridProjectedPair<TSource, TTarget, TFirst, TSecond> From<TTarget, TFirst, TSecond>(
        Expression<Func<TSource, TTarget>> projection,
        HybridValue<TTarget, TFirst> first,
        HybridValue<TTarget, TSecond> second)
        where TTarget : class => new(_source, projection, first, second);
}

internal sealed class HybridSource<TSource> where TSource : class
{
    private readonly HybridSet<TSource> _source;
    private readonly DerivedBuilder<TSource> _builder;

    internal HybridSource(HybridSet<TSource> source, DerivedBuilder<TSource> builder) =>
        (_source, _builder) = (source, builder);

    public HybridSource<TSource> DependsOn<TValue>(Expression<Func<TSource, TValue>> dependency)
    {
        _builder.DependsOn(dependency);
        return this;
    }

    public HybridValue<TSource, TValue> Select<TValue>(Expression<Func<TSource, TValue>> expression) =>
        new(_source, _builder.Compute(expression));
}

internal sealed class HybridValue<TSource, TValue> where TSource : class
{
    internal HybridValue(HybridSet<TSource> source, Derived<TSource, TValue> raw) =>
        (Source, Raw) = (source, raw);

    internal HybridSet<TSource> Source { get; }
    internal Derived<TSource, TValue> Raw { get; }

    public HybridValue<TSource, TValue> Named(string name)
    {
        Raw.Named(name);
        return this;
    }
}

internal sealed class HybridPair<TSource, TFirst, TSecond> where TSource : class
{
    private readonly HybridSet<TSource> _source;
    private readonly HybridValue<TSource, TFirst> _first;
    private readonly HybridValue<TSource, TSecond> _second;

    internal HybridPair(
        HybridSet<TSource> source,
        HybridValue<TSource, TFirst> first,
        HybridValue<TSource, TSecond> second) =>
        (_source, _first, _second) = (source, first, second);

    public HybridValue<TSource, TResult> Select<TResult>(
        Expression<Func<TSource, TFirst, TSecond, TResult>> expression) =>
        new(_source, _source.Model.Builder.Derived(_source.Raw)
            .Using(_first.Raw, _second.Raw)
            .Compute(expression));
}

internal sealed class HybridProjectedPair<TSource, TTarget, TFirst, TSecond>
    where TSource : class
    where TTarget : class
{
    private readonly HybridSet<TSource> _source;
    private readonly Expression<Func<TSource, TTarget>> _projection;
    private readonly HybridValue<TTarget, TFirst> _first;
    private readonly HybridValue<TTarget, TSecond> _second;
    private Action<DerivedImpactPolicyBuilder<TSource>>? _impact;

    internal HybridProjectedPair(
        HybridSet<TSource> source,
        Expression<Func<TSource, TTarget>> projection,
        HybridValue<TTarget, TFirst> first,
        HybridValue<TTarget, TSecond> second) =>
        (_source, _projection, _first, _second) = (source, projection, first, second);

    public HybridProjectedPair<TSource, TTarget, TFirst, TSecond> Impact(
        Action<DerivedImpactPolicyBuilder<TSource>> impact)
    {
        _impact = impact;
        return this;
    }

    public HybridValue<TSource, TResult> Select<TResult>(
        Expression<Func<TSource, TFirst, TSecond, TResult>> expression)
    {
        var builder = _source.Model.Builder.Derived(_source.Raw)
            .Using(_projection, _first.Raw, _second.Raw);
        if (_impact is not null)
            builder.Impact(_impact);
        return new HybridValue<TSource, TResult>(_source, builder.Compute(expression));
    }
}

internal sealed class HybridRelationDraft<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private readonly HybridModel _model;
    private readonly HybridSet<TLeft> _left;
    private readonly HybridSet<TRight> _right;

    internal HybridRelationDraft(HybridModel model, HybridSet<TLeft> left, HybridSet<TRight> right) =>
        (_model, _left, _right) = (model, left, right);

    public HybridRelation<TLeft, TRight> Where(Expression<Func<TLeft, TRight, bool>> predicate) =>
        new(_left, _right, _model.Builder.Relation(_left.Raw, _right.Raw).Where(predicate));
}

internal sealed class HybridRelation<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    internal HybridRelation(
        HybridSet<TLeft> left,
        HybridSet<TRight> right,
        Relation<TLeft, TRight> raw) => (Left, Right, Raw) = (left, right, raw);

    internal HybridSet<TLeft> Left { get; }
    internal HybridSet<TRight> Right { get; }
    internal Relation<TLeft, TRight> Raw { get; }

    public HybridRelation<TLeft, TRight> Named(string name)
    {
        Raw.Named(name);
        return this;
    }
}

internal sealed class HybridRelationAggregate<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private readonly HybridSet<TLeft> _source;
    private readonly HybridRelation<TLeft, TRight> _relation;
    private Action<DerivedImpactPolicyBuilder<TLeft>>? _impact;

    internal HybridRelationAggregate(HybridSet<TLeft> source, HybridRelation<TLeft, TRight> relation) =>
        (_source, _relation) = (source, relation);

    public HybridRelationAggregate<TLeft, TRight> Impact(Action<DerivedImpactPolicyBuilder<TLeft>> impact)
    {
        _impact = impact;
        return this;
    }

    public HybridValue<TLeft, decimal> Sum(Expression<Func<TRight, decimal>> selector)
    {
        var builder = _source.Model.Builder.Derived(_source.Raw).Using(_relation.Raw);
        if (_impact is not null)
            builder.Impact(_impact);
        return new HybridValue<TLeft, decimal>(_source,
            builder.Incrementally().Compute(FacadeExpressions.Sum<TLeft, TRight>(selector)));
    }
}

internal sealed class HybridInvariant<TSource> where TSource : class
{
    private readonly InvariantUsingBuilder<TSource, bool> _builder;

    internal HybridInvariant(InvariantUsingBuilder<TSource, bool> builder) => _builder = builder;

    public ConfiguredHybridInvariant<TSource> Must(Expression<Func<TSource, bool, bool>> predicate) =>
        new(_builder.Must(predicate));
}

internal sealed class ConfiguredHybridInvariant<TSource> where TSource : class
{
    internal ConfiguredHybridInvariant(Invariant<TSource> raw) => Raw = raw;
    internal Invariant<TSource> Raw { get; }

    public ConfiguredHybridInvariant<TSource> Named(string name)
    {
        Raw.Named(name);
        return this;
    }

    public ConfiguredHybridInvariant<TSource> ScheduleRepairWith(Action<TSource> repair)
    {
        Raw.ScheduleRepairWith(repair);
        return this;
    }
}
