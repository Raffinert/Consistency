using System.Linq.Expressions;

namespace Raffinert.Consistency.ApiV2Concept.Common;

internal static class FacadeExpressions
{
    public static Expression<Func<TLeft, IReadOnlyList<TRight>, decimal>> Sum<TLeft, TRight>(
        Expression<Func<TRight, decimal>> selector)
        where TLeft : class
        where TRight : class
    {
        var left = Expression.Parameter(typeof(TLeft), "left");
        var rows = Expression.Parameter(typeof(IReadOnlyList<TRight>), "rows");
        var body = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Sum),
            [typeof(TRight)],
            rows,
            selector);
        return Expression.Lambda<Func<TLeft, IReadOnlyList<TRight>, decimal>>(body, left, rows);
    }
}
