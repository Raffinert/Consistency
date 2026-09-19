using System.Linq.Expressions;
using Raffinert.Consistency.ApiV2Concept.Common;

namespace Raffinert.Consistency.ApiV2Concept.VariantB.TypedDomainPipeline;

internal sealed class DomainModel
{
    internal ConsistencyModelBuilder Builder { get; } = new();

    public ObjectSetNode<T> Objects<T>() where T : class => new(this, Builder.Objects<T>());

    public DomainInvariant<T> Invariant<T>(ObjectSetNode<T> source, ObjectValue<T, bool> value)
        where T : class => new(Builder.Invariant(source.Raw).Using(value.Raw));

    public CompiledConsistencyModel Build() => Builder.Build();
}

internal sealed class ObjectSetNode<T> where T : class
{
    private readonly ObjectSetBuilder<T> _builder;
    private string? _name;

    internal ObjectSetNode(DomainModel model, ObjectSetBuilder<T> builder) => (Model, _builder) = (model, builder);
    internal DomainModel Model { get; }
    internal ObjectSet<T> Raw { get; private set; } = null!;

    public ObjectSetNode<T> Named(string name)
    {
        _name = name;
        return this;
    }

    public ObjectSetNode<T> Key<TKey>(Expression<Func<T, TKey>> key)
    {
        if (_name is not null)
            _builder.Named(_name);
        Raw = _builder.Key(key);
        return this;
    }

    public ObjectValue<T, TValue> Select<TValue>(Expression<Func<T, TValue>> expression) =>
        new(this, Model.Builder.Derived(Raw).Compute(expression));

    public ObjectValue<T, TValue> Select<TValue>(
        Expression<Func<T, TValue>> expression,
        Action<DerivedImpactPolicyBuilder<T>> impact) =>
        new(this, Model.Builder.Derived(Raw).Impact(impact).Compute(expression));

    public DomainSource<T> DependsOn<TValue>(Expression<Func<T, TValue>> dependency) =>
        new(this, Model.Builder.Derived(Raw).DependsOn(dependency));

    public DomainRelationDraft<T, TRight> Relate<TRight>(ObjectSetNode<TRight> right)
        where TRight : class => new(this, right);

    public ProjectedObjectValues<T, TTarget, TFirst, TSecond> SelectWith<TTarget, TFirst, TSecond>(
        Expression<Func<T, TTarget>> projection,
        ObjectValue<TTarget, TFirst> first,
        ObjectValue<TTarget, TSecond> second)
        where TTarget : class => new(this, projection, first, second);
}

internal sealed class DomainSource<T> where T : class
{
    private readonly ObjectSetNode<T> _source;
    private readonly DerivedBuilder<T> _builder;

    internal DomainSource(ObjectSetNode<T> source, DerivedBuilder<T> builder) =>
        (_source, _builder) = (source, builder);

    public DomainSource<T> DependsOn<TValue>(Expression<Func<T, TValue>> dependency)
    {
        _builder.DependsOn(dependency);
        return this;
    }

    public ObjectValue<T, TValue> Select<TValue>(Expression<Func<T, TValue>> expression) =>
        new(_source, _builder.Compute(expression));
}

internal sealed class ObjectValue<TSource, TValue> where TSource : class
{
    internal ObjectValue(ObjectSetNode<TSource> source, Derived<TSource, TValue> raw) =>
        (Source, Raw) = (source, raw);

    internal ObjectSetNode<TSource> Source { get; }
    internal Derived<TSource, TValue> Raw { get; }

    public ObjectValue<TSource, TValue> Named(string name)
    {
        Raw.Named(name);
        return this;
    }

    public ObjectValuePair<TSource, TValue, TOther> Combine<TOther>(ObjectValue<TSource, TOther> other) =>
        new(Source, this, other);
}

internal sealed class ObjectValuePair<TSource, TFirst, TSecond> where TSource : class
{
    private readonly ObjectSetNode<TSource> _source;
    private readonly ObjectValue<TSource, TFirst> _first;
    private readonly ObjectValue<TSource, TSecond> _second;

    internal ObjectValuePair(
        ObjectSetNode<TSource> source,
        ObjectValue<TSource, TFirst> first,
        ObjectValue<TSource, TSecond> second) =>
        (_source, _first, _second) = (source, first, second);

    public ObjectValue<TSource, TResult> Select<TResult>(
        Expression<Func<TSource, TFirst, TSecond, TResult>> expression) =>
        new(_source, _source.Model.Builder.Derived(_source.Raw)
            .Using(_first.Raw, _second.Raw)
            .Compute(expression));
}

internal sealed class ProjectedObjectValues<TSource, TTarget, TFirst, TSecond>
    where TSource : class
    where TTarget : class
{
    private readonly ObjectSetNode<TSource> _source;
    private readonly Expression<Func<TSource, TTarget>> _projection;
    private readonly ObjectValue<TTarget, TFirst> _first;
    private readonly ObjectValue<TTarget, TSecond> _second;
    private Action<DerivedImpactPolicyBuilder<TSource>>? _impact;

    internal ProjectedObjectValues(
        ObjectSetNode<TSource> source,
        Expression<Func<TSource, TTarget>> projection,
        ObjectValue<TTarget, TFirst> first,
        ObjectValue<TTarget, TSecond> second) =>
        (_source, _projection, _first, _second) = (source, projection, first, second);

    public ProjectedObjectValues<TSource, TTarget, TFirst, TSecond> WithImpact(
        Action<DerivedImpactPolicyBuilder<TSource>> impact)
    {
        _impact = impact;
        return this;
    }

    public ObjectValue<TSource, TResult> Select<TResult>(
        Expression<Func<TSource, TFirst, TSecond, TResult>> expression)
    {
        var builder = _source.Model.Builder.Derived(_source.Raw)
            .Using(_projection, _first.Raw, _second.Raw);
        if (_impact is not null)
            builder.Impact(_impact);
        return new ObjectValue<TSource, TResult>(_source, builder.Compute(expression));
    }
}

internal sealed class DomainRelationDraft<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private readonly ObjectSetNode<TLeft> _left;
    private readonly ObjectSetNode<TRight> _right;

    internal DomainRelationDraft(ObjectSetNode<TLeft> left, ObjectSetNode<TRight> right) =>
        (_left, _right) = (left, right);

    public DomainRelation<TLeft, TRight> Where(Expression<Func<TLeft, TRight, bool>> predicate) =>
        new(_left, _right, _left.Model.Builder.Relation(_left.Raw, _right.Raw).Where(predicate));
}

internal sealed class DomainRelation<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    private Action<DerivedImpactPolicyBuilder<TLeft>>? _impact;

    internal DomainRelation(
        ObjectSetNode<TLeft> left,
        ObjectSetNode<TRight> right,
        Relation<TLeft, TRight> raw) => (Left, Right, Raw) = (left, right, raw);

    internal ObjectSetNode<TLeft> Left { get; }
    internal ObjectSetNode<TRight> Right { get; }
    internal Relation<TLeft, TRight> Raw { get; }

    public DomainRelation<TLeft, TRight> Named(string name)
    {
        Raw.Named(name);
        return this;
    }

    public DomainRelation<TLeft, TRight> WithImpact(Action<DerivedImpactPolicyBuilder<TLeft>> impact)
    {
        _impact = impact;
        return this;
    }

    public ObjectValue<TLeft, decimal> SumByLeft(Expression<Func<TRight, decimal>> selector)
    {
        var builder = Left.Model.Builder.Derived(Left.Raw).Using(Raw);
        if (_impact is not null)
            builder.Impact(_impact);
        return new ObjectValue<TLeft, decimal>(Left,
            builder.Incrementally().Compute(FacadeExpressions.Sum<TLeft, TRight>(selector)));
    }
}

internal sealed class DomainInvariant<TSource> where TSource : class
{
    private readonly InvariantUsingBuilder<TSource, bool> _builder;

    internal DomainInvariant(InvariantUsingBuilder<TSource, bool> builder) => _builder = builder;

    public ConfiguredDomainInvariant<TSource> Must(Expression<Func<TSource, bool, bool>> predicate) =>
        new(_builder.Must(predicate));
}

internal sealed class ConfiguredDomainInvariant<TSource> where TSource : class
{
    internal ConfiguredDomainInvariant(Invariant<TSource> raw) => Raw = raw;
    internal Invariant<TSource> Raw { get; }

    public ConfiguredDomainInvariant<TSource> Named(string name)
    {
        Raw.Named(name);
        return this;
    }

    public ConfiguredDomainInvariant<TSource> RepairWhenViolated()
    {
        Raw.RepairWhenViolated();
        return this;
    }
}
