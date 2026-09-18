using System.Linq.Expressions;

namespace Raffinert.Consistency;

internal static class RecognizedAggregateExpressions
{
    public static Expression<Func<TSource, IReadOnlyList<TItem>, TValue>> Sum<TSource, TItem, TValue>(
        Expression<Func<TItem, TValue>> selector)
        where TSource : class
        where TItem : class => Build<TSource, TItem, TValue>(nameof(Enumerable.Sum), selector);

    public static Expression<Func<TSource, IReadOnlyList<TItem>, int>> Count<TSource, TItem>()
        where TSource : class
        where TItem : class => Build<TSource, TItem, int>(nameof(Enumerable.Count));

    public static Expression<Func<TSource, IReadOnlyList<TItem>, long>> LongCount<TSource, TItem>()
        where TSource : class
        where TItem : class => Build<TSource, TItem, long>(nameof(Enumerable.LongCount));

    public static Expression<Func<TSource, IReadOnlyList<TItem>, bool>> Any<TSource, TItem>()
        where TSource : class
        where TItem : class => Build<TSource, TItem, bool>(nameof(Enumerable.Any));

    private static Expression<Func<TSource, IReadOnlyList<TItem>, TValue>> Build<TSource, TItem, TValue>(
        string method,
        LambdaExpression? selector = null)
        where TSource : class
        where TItem : class
    {
        var source = Expression.Parameter(typeof(TSource), "source");
        var items = Expression.Parameter(typeof(IReadOnlyList<TItem>), "items");
        var arguments = selector is null
            ? new Expression[] { items }
            : [items, selector];
        var body = Expression.Call(typeof(Enumerable), method, [typeof(TItem)], arguments);
        return Expression.Lambda<Func<TSource, IReadOnlyList<TItem>, TValue>>(body, source, items);
    }
}
