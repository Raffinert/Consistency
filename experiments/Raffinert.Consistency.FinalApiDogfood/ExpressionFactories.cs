using System.Linq.Expressions;

namespace Raffinert.Consistency.FinalApiDogfood;

internal static class ExpressionFactories
{
    public static Expression<Func<TLeft, IReadOnlyList<TRight>, decimal>> Sum<TLeft, TRight>(
        Expression<Func<TRight, decimal>> selector)
        where TLeft : class where TRight : class => Build<TLeft, TRight, decimal>(nameof(Enumerable.Sum), selector);

    public static Expression<Func<TLeft, IReadOnlyList<TRight>, int>> Count<TLeft, TRight>()
        where TLeft : class where TRight : class => Build<TLeft, TRight, int>(nameof(Enumerable.Count));

    public static Expression<Func<TLeft, IReadOnlyList<TRight>, long>> LongCount<TLeft, TRight>()
        where TLeft : class where TRight : class => Build<TLeft, TRight, long>(nameof(Enumerable.LongCount));

    public static Expression<Func<TLeft, IReadOnlyList<TRight>, bool>> Any<TLeft, TRight>()
        where TLeft : class where TRight : class => Build<TLeft, TRight, bool>(nameof(Enumerable.Any));

    private static Expression<Func<TLeft, IReadOnlyList<TRight>, TResult>> Build<TLeft, TRight, TResult>(
        string method,
        LambdaExpression? selector = null)
        where TLeft : class where TRight : class
    {
        var left = Expression.Parameter(typeof(TLeft), "left");
        var rows = Expression.Parameter(typeof(IReadOnlyList<TRight>), "rows");
        var genericArguments = selector is null ? new[] { typeof(TRight) } : new[] { typeof(TRight) };
        var arguments = selector is null
            ? new Expression[] { rows }
            : [rows, selector];
        var body = Expression.Call(typeof(Enumerable), method, genericArguments, arguments);
        return Expression.Lambda<Func<TLeft, IReadOnlyList<TRight>, TResult>>(body, left, rows);
    }
}
