using System.Linq.Expressions;
using Raffinert.Consistency.ApiV2Concept.Common;

namespace Raffinert.Consistency.ApiV2Concept.VariantA.ProviderPipeline;

internal sealed class ProviderModel
{
    internal ConsistencyModelBuilder Builder { get; } = new();

    public ObjectProvider<T> Objects<T>() where T : class => new(this, Builder.Objects<T>());

    public InvariantProvider<T> Invariant<T>(
        ObjectProvider<T> source,
        ValueProvider<T, bool> value)
        where T : class => new(Builder.Invariant(source.Raw).Using(value.Raw));

    public CompiledConsistencyModel Build() => Builder.Build();
}

internal sealed class ObjectProvider<T> where T : class
{
    private readonly ObjectSetBuilder<T> _builder;
    private string? _name;

    internal ObjectProvider(ProviderModel model, ObjectSetBuilder<T> builder) =>
        (Model, _builder) = (model, builder);

    internal ProviderModel Model { get; }
    internal ObjectSet<T> Raw { get; private set; } = null!;

    public ObjectProvider<T> Named(string name)
    {
        _name = name;
        return this;
    }

    public ObjectProvider<T> Key<TKey>(Expression<Func<T, TKey>> key)
    {
        if (_name is not null)
            _builder.Named(_name);
        Raw = _builder.Key(key);
        return this;
    }

    public ValueProvider<T, TValue> Value<TValue>(Expression<Func<T, TValue>> expression) =>
        new(this, Model.Builder.Derived(Raw).Compute(expression));

    public ValueProvider<T, TValue> Value<TValue>(
        Expression<Func<T, TValue>> expression,
        Action<DerivedImpactPolicyBuilder<T>> impact)
    {
        var value = Model.Builder.Derived(Raw).Impact(impact).Compute(expression);
        return new ValueProvider<T, TValue>(this, value);
    }

    public SourceProvider<T> DependsOn<TValue>(Expression<Func<T, TValue>> dependency) =>
        new(this, Model.Builder.Derived(Raw).DependsOn(dependency));

    public RelationDraft<T, TRight> RelateTo<TRight>(ObjectProvider<TRight> right)
        where TRight : class => new(this, right);
}

internal sealed class SourceProvider<T> where T : class
{
    private readonly ObjectProvider<T> _source;
    private readonly DerivedBuilder<T> _builder;

    internal SourceProvider(ObjectProvider<T> source, DerivedBuilder<T> builder) =>
        (_source, _builder) = (source, builder);

    public SourceProvider<T> DependsOn<TValue>(Expression<Func<T, TValue>> dependency)
    {
        _builder.DependsOn(dependency);
        return this;
    }

    public SourceProvider<T> WithImpact(Action<DerivedImpactPolicyBuilder<T>> impact)
    {
        _builder.Impact(impact);
        return this;
    }

    public ValueProvider<T, TValue> Select<TValue>(Expression<Func<T, TValue>> expression) =>
        new(_source, _builder.Compute(expression));
}

internal sealed class ValueProvider<TSource, TValue> where TSource : class
{
    internal ValueProvider(ObjectProvider<TSource> source, Derived<TSource, TValue> raw) =>
        (Source, Raw) = (source, raw);

    internal ObjectProvider<TSource> Source { get; }
    internal Derived<TSource, TValue> Raw { get; }

    public ValueProvider<TSource, TValue> Named(string name)
    {
        Raw.Named(name);
        return this;
    }

    public CombinedValueProvider<TSource, TValue, TOther> Combine<TOther>(
        ValueProvider<TSource, TOther> other) => new(Source, this, other);

    public ProjectedValueProvider<TNewSource, TSource, TValue> For<TNewSource>(
        ObjectProvider<TNewSource> source,
        Expression<Func<TNewSource, TSource>> projection)
        where TNewSource : class => new(source, projection, this);
}

internal sealed class CombinedValueProvider<TSource, TFirst, TSecond> where TSource : class
{
    private readonly ObjectProvider<TSource> _source;
    private readonly ValueProvider<TSource, TFirst> _first;
    private readonly ValueProvider<TSource, TSecond> _second;
    private Action<DerivedImpactPolicyBuilder<TSource>>? _impact;

    internal CombinedValueProvider(
        ObjectProvider<TSource> source,
        ValueProvider<TSource, TFirst> first,
        ValueProvider<TSource, TSecond> second) =>
        (_source, _first, _second) = (source, first, second);

    public CombinedValueProvider<TSource, TFirst, TSecond> WithImpact(
        Action<DerivedImpactPolicyBuilder<TSource>> impact)
    {
        _impact = impact;
        return this;
    }

    public ValueProvider<TSource, TResult> Select<TResult>(
        Expression<Func<TSource, TFirst, TSecond, TResult>> expression)
    {
        var builder = _source.Model.Builder.Derived(_source.Raw).Using(_first.Raw, _second.Raw);
        if (_impact is not null)
            builder.Impact(_impact);
        return new ValueProvider<TSource, TResult>(_source, builder.Compute(expression));
    }
}

internal sealed class ProjectedValueProvider<TSource, TUpstreamSource, TFirst>
    where TSource : class
    where TUpstreamSource : class
{
    private readonly ObjectProvider<TSource> _source;
    private readonly Expression<Func<TSource, TUpstreamSource>> _projection;
    private readonly ValueProvider<TUpstreamSource, TFirst> _first;

    internal ProjectedValueProvider(
        ObjectProvider<TSource> source,
        Expression<Func<TSource, TUpstreamSource>> projection,
        ValueProvider<TUpstreamSource, TFirst> first) =>
        (_source, _projection, _first) = (source, projection, first);

    public ProjectedCombinedValueProvider<TSource, TUpstreamSource, TFirst, TSecond> Combine<TSecond>(
        ValueProvider<TUpstreamSource, TSecond> second) =>
        new(_source, _projection, _first, second);
}

internal sealed class ProjectedCombinedValueProvider<TSource, TUpstreamSource, TFirst, TSecond>
    where TSource : class
    where TUpstreamSource : class
{
    private readonly ObjectProvider<TSource> _source;
    private readonly Expression<Func<TSource, TUpstreamSource>> _projection;
    private readonly ValueProvider<TUpstreamSource, TFirst> _first;
    private readonly ValueProvider<TUpstreamSource, TSecond> _second;
    private Action<DerivedImpactPolicyBuilder<TSource>>? _impact;

    internal ProjectedCombinedValueProvider(
        ObjectProvider<TSource> source,
        Expression<Func<TSource, TUpstreamSource>> projection,
        ValueProvider<TUpstreamSource, TFirst> first,
        ValueProvider<TUpstreamSource, TSecond> second) =>
        (_source, _projection, _first, _second) = (source, projection, first, second);

    public ProjectedCombinedValueProvider<TSource, TUpstreamSource, TFirst, TSecond> WithImpact(
        Action<DerivedImpactPolicyBuilder<TSource>> impact)
    {
        _impact = impact;
        return this;
    }

    public ValueProvider<TSource, TResult> Select<TResult>(
        Expression<Func<TSource, TFirst, TSecond, TResult>> expression)
    {
        var builder = _source.Model.Builder.Derived(_source.Raw)
            .Using(_projection, _first.Raw, _second.Raw);
        if (_impact is not null)
            builder.Impact(_impact);
        return new ValueProvider<TSource, TResult>(_source, builder.Compute(expression));
    }
}

internal sealed class RelationDraft<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private readonly ObjectProvider<TLeft> _left;
    private readonly ObjectProvider<TRight> _right;

    internal RelationDraft(ObjectProvider<TLeft> left, ObjectProvider<TRight> right) =>
        (_left, _right) = (left, right);

    public RelationProvider<TLeft, TRight> Where(Expression<Func<TLeft, TRight, bool>> predicate) =>
        new(_left, _right, _left.Model.Builder.Relation(_left.Raw, _right.Raw).Where(predicate));
}

internal sealed class RelationProvider<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    internal RelationProvider(
        ObjectProvider<TLeft> left,
        ObjectProvider<TRight> right,
        Relation<TLeft, TRight> raw) => (Left, Right, Raw) = (left, right, raw);

    internal ObjectProvider<TLeft> Left { get; }
    internal ObjectProvider<TRight> Right { get; }
    internal Relation<TLeft, TRight> Raw { get; }

    public RelationProvider<TLeft, TRight> Named(string name)
    {
        Raw.Named(name);
        return this;
    }

    public LeftValuesProvider<TLeft, TRight> PerLeft() => new(this);
}

internal sealed class LeftValuesProvider<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private readonly RelationProvider<TLeft, TRight> _relation;
    private Action<DerivedImpactPolicyBuilder<TLeft>>? _impact;

    internal LeftValuesProvider(RelationProvider<TLeft, TRight> relation) => _relation = relation;

    public LeftValuesProvider<TLeft, TRight> WithImpact(
        Action<DerivedImpactPolicyBuilder<TLeft>> impact)
    {
        _impact = impact;
        return this;
    }

    public ValueProvider<TLeft, decimal> Sum(Expression<Func<TRight, decimal>> selector)
    {
        var builder = _relation.Left.Model.Builder.Derived(_relation.Left.Raw).Using(_relation.Raw);
        if (_impact is not null)
            builder.Impact(_impact);
        var raw = builder.Incrementally().Compute(FacadeExpressions.Sum<TLeft, TRight>(selector));
        return new ValueProvider<TLeft, decimal>(_relation.Left, raw);
    }
}

internal sealed class InvariantProvider<TSource> where TSource : class
{
    private readonly InvariantUsingBuilder<TSource, bool> _builder;

    internal InvariantProvider(InvariantUsingBuilder<TSource, bool> builder) => _builder = builder;

    public ConfiguredInvariantProvider<TSource> Must(Expression<Func<TSource, bool, bool>> predicate) =>
        new(_builder.Must(predicate));
}

internal sealed class ConfiguredInvariantProvider<TSource> where TSource : class
{
    internal ConfiguredInvariantProvider(Invariant<TSource> raw) => Raw = raw;
    internal Invariant<TSource> Raw { get; }

    public ConfiguredInvariantProvider<TSource> Named(string name)
    {
        Raw.Named(name);
        return this;
    }

    public ConfiguredInvariantProvider<TSource> RepairWhenViolated()
    {
        Raw.RepairWhenViolated();
        return this;
    }
}
