using System.Linq.Expressions;
using Raffinert.Consistency.ApiV2Concept.Common;

namespace Raffinert.Consistency.ApiV2Concept.VariantD.ContextDsl;

internal abstract class ConsistencyModelContext
{
    private readonly ConsistencyModelBuilder _builder = new();

    protected ContextSet<T> Objects<T, TKey>(
        string name,
        Expression<Func<T, TKey>> key)
        where T : class
    {
        var raw = _builder.Objects<T>().Named(name).Key(key);
        return new ContextSet<T>(this, raw);
    }

    protected ContextRelation<TLeft, TRight> Relate<TLeft, TRight>(
        ContextSet<TLeft> left,
        ContextSet<TRight> right,
        string name,
        Expression<Func<TLeft, TRight, bool>> predicate)
        where TLeft : class
        where TRight : class =>
        new(left, right, _builder.Relation(left.Raw, right.Raw).Where(predicate).Named(name));

    protected ContextValue<T, TValue> Select<T, TValue>(
        ContextSet<T> source,
        string name,
        Expression<Func<T, TValue>> expression,
        Action<DerivedImpactPolicyBuilder<T>>? impact = null)
        where T : class
    {
        var builder = _builder.Derived(source.Raw);
        if (impact is not null)
            builder.Impact(impact);
        return new ContextValue<T, TValue>(source, builder.Compute(expression).Named(name));
    }

    protected ContextSource<T> DependsOn<T, TValue>(
        ContextSet<T> source,
        Expression<Func<T, TValue>> dependency)
        where T : class => new(source, _builder.Derived(source.Raw).DependsOn(dependency));

    protected ContextValue<TLeft, decimal> SumByLeft<TLeft, TRight>(
        ContextRelation<TLeft, TRight> relation,
        string name,
        Expression<Func<TRight, decimal>> selector,
        Action<DerivedImpactPolicyBuilder<TLeft>> impact)
        where TLeft : class
        where TRight : class
    {
        var builder = _builder.Derived(relation.Left.Raw).Using(relation.Raw).Impact(impact);
        var raw = builder.Incrementally()
            .Compute(FacadeExpressions.Sum<TLeft, TRight>(selector))
            .Named(name);
        return new ContextValue<TLeft, decimal>(relation.Left, raw);
    }

    protected ContextValue<T, TResult> Combine<T, TFirst, TSecond, TResult>(
        ContextSet<T> source,
        string name,
        ContextValue<T, TFirst> first,
        ContextValue<T, TSecond> second,
        Expression<Func<T, TFirst, TSecond, TResult>> expression)
        where T : class =>
        new(source, _builder.Derived(source.Raw).Using(first.Raw, second.Raw).Compute(expression).Named(name));

    protected ContextValue<T, TResult> Project<T, TTarget, TFirst, TSecond, TResult>(
        ContextSet<T> source,
        string name,
        Expression<Func<T, TTarget>> projection,
        ContextValue<TTarget, TFirst> first,
        ContextValue<TTarget, TSecond> second,
        Expression<Func<T, TFirst, TSecond, TResult>> expression,
        Action<DerivedImpactPolicyBuilder<T>> impact)
        where T : class
        where TTarget : class =>
        new(source, _builder.Derived(source.Raw)
            .Using(projection, first.Raw, second.Raw)
            .Impact(impact)
            .Compute(expression)
            .Named(name));

    protected ContextInvariant<T> Invariant<T>(
        ContextSet<T> source,
        ContextValue<T, bool> value,
        string name,
        Action<T> repair)
        where T : class =>
        new(_builder.Invariant(source.Raw)
            .Using(value.Raw)
            .Must((_, valid) => valid)
            .Named(name)
            .ScheduleRepairWith(repair));

    internal CompiledConsistencyModel Build() => _builder.Build();
}

internal sealed class ContextSet<T> where T : class
{
    internal ContextSet(ConsistencyModelContext context, ObjectSet<T> raw) => (Context, Raw) = (context, raw);
    internal ConsistencyModelContext Context { get; }
    internal ObjectSet<T> Raw { get; }
}

internal sealed class ContextValue<TSource, TValue> where TSource : class
{
    internal ContextValue(ContextSet<TSource> source, Derived<TSource, TValue> raw) =>
        (Source, Raw) = (source, raw);
    internal ContextSet<TSource> Source { get; }
    internal Derived<TSource, TValue> Raw { get; }
}

internal sealed class ContextRelation<TLeft, TRight>
    where TLeft : class
    where TRight : class
{
    internal ContextRelation(
        ContextSet<TLeft> left,
        ContextSet<TRight> right,
        Relation<TLeft, TRight> raw) => (Left, Right, Raw) = (left, right, raw);
    internal ContextSet<TLeft> Left { get; }
    internal ContextSet<TRight> Right { get; }
    internal Relation<TLeft, TRight> Raw { get; }
}

internal sealed class ContextSource<T> where T : class
{
    private readonly ContextSet<T> _source;
    private readonly DerivedBuilder<T> _builder;

    internal ContextSource(ContextSet<T> source, DerivedBuilder<T> builder) =>
        (_source, _builder) = (source, builder);

    public ContextSource<T> DependsOn<TValue>(Expression<Func<T, TValue>> dependency)
    {
        _builder.DependsOn(dependency);
        return this;
    }

    public ContextValue<T, TValue> Select<TValue>(
        string name,
        Expression<Func<T, TValue>> expression) =>
        new(_source, _builder.Compute(expression).Named(name));
}

internal sealed class ContextInvariant<T> where T : class
{
    internal ContextInvariant(Invariant<T> raw) => Raw = raw;
    internal Invariant<T> Raw { get; }
}
