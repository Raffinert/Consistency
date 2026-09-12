using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Relations;

internal interface IDerivedComputationPlan<TSource, TItem, TValue>
    where TSource : class
    where TItem : class
{
    string DisplayName { get; }

    bool TryUpdate(
        TValue current,
        TSource source,
        RelationImpact? relationImpact,
        IReadOnlyList<PropertyChange> changes,
        RelationRuntimeState<TSource, TItem> relationState,
        out TValue updated);
}

internal static class DerivedComputationPlanner
{
    public static IDerivedComputationPlan<TSource, TItem, TValue>? Create<TSource, TItem, TValue>(
        Expression<Func<TSource, IReadOnlyList<TItem>, TValue>> computation,
        bool forceFullRecompute)
        where TSource : class
        where TItem : class
    {
        if (forceFullRecompute)
            return null;
        var body = StripConvert(computation.Body);
        var items = computation.Parameters[1];
        if (body is MemberExpression { Member.Name: nameof(IReadOnlyCollection<object>.Count) } member &&
            ReferenceEquals(StripConvert(member.Expression!), items) && typeof(TValue) == typeof(int))
            return (IDerivedComputationPlan<TSource, TItem, TValue>)(object)new CountPlan<TSource, TItem>();
        if (body is not MethodCallExpression call || call.Method.DeclaringType != typeof(Enumerable) ||
            call.Arguments.Count == 0 || !ReferenceEquals(StripConvert(call.Arguments[0]), items))
            return null;
        if (call.Method.Name == nameof(Enumerable.Count) && call.Arguments.Count == 1 && typeof(TValue) == typeof(int))
            return (IDerivedComputationPlan<TSource, TItem, TValue>)(object)new CountPlan<TSource, TItem>();
        if (call.Method.Name == nameof(Enumerable.LongCount) && call.Arguments.Count == 1 && typeof(TValue) == typeof(long))
            return (IDerivedComputationPlan<TSource, TItem, TValue>)(object)new LongCountPlan<TSource, TItem>();
        if (call.Method.Name == nameof(Enumerable.Any) && call.Arguments.Count == 1 && typeof(TValue) == typeof(bool))
            return (IDerivedComputationPlan<TSource, TItem, TValue>)(object)new AnyPlan<TSource, TItem>();
        if (call.Method.Name != nameof(Enumerable.Sum) || call.Arguments.Count != 2 ||
            Nullable.GetUnderlyingType(typeof(TValue)) is not null)
            return null;
        var selector = UnwrapLambda(call.Arguments[1]);
        if (selector is null || selector.Parameters.Count != 1 ||
            StripConvert(selector.Body) is not MemberExpression { Expression: ParameterExpression } selected ||
            selected.Member is not PropertyInfo and not FieldInfo || selected.Type != typeof(TValue))
            return null;
        try
        {
            var typedSelector = Expression.Lambda<Func<TItem, TValue>>(selected, selector.Parameters[0]).Compile();
            var left = Expression.Parameter(typeof(TValue), "left");
            var right = Expression.Parameter(typeof(TValue), "right");
            var add = Expression.Lambda<Func<TValue, TValue, TValue>>(
                Expression.Add(left, right), left, right).Compile();
            var subtract = Expression.Lambda<Func<TValue, TValue, TValue>>(
                Expression.Subtract(left, right), left, right).Compile();
            return new SumPlan<TSource, TItem, TValue>(selected.Member, typedSelector, add, subtract);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static LambdaExpression? UnwrapLambda(Expression expression)
    {
        expression = StripConvert(expression);
        return expression is LambdaExpression lambda ? lambda : null;
    }

    private static Expression StripConvert(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.Quote } unary)
            expression = unary.Operand;
        return expression;
    }

    private sealed class CountPlan<TSource, TItem> : IDerivedComputationPlan<TSource, TItem, int>
        where TSource : class where TItem : class
    {
        public string DisplayName => "IncrementalCount";

        public bool TryUpdate(int current, TSource source, RelationImpact? relationImpact,
            IReadOnlyList<PropertyChange> changes, RelationRuntimeState<TSource, TItem> relationState,
            out int updated)
        {
            updated = relationState.RelatedCount(source);
            return true;
        }
    }

    private sealed class LongCountPlan<TSource, TItem> : IDerivedComputationPlan<TSource, TItem, long>
        where TSource : class where TItem : class
    {
        public string DisplayName => "IncrementalLongCount";

        public bool TryUpdate(long current, TSource source, RelationImpact? relationImpact,
            IReadOnlyList<PropertyChange> changes, RelationRuntimeState<TSource, TItem> relationState,
            out long updated)
        {
            updated = relationState.RelatedCount(source);
            return true;
        }
    }

    private sealed class AnyPlan<TSource, TItem> : IDerivedComputationPlan<TSource, TItem, bool>
        where TSource : class where TItem : class
    {
        public string DisplayName => "IncrementalAny";

        public bool TryUpdate(bool current, TSource source, RelationImpact? relationImpact,
            IReadOnlyList<PropertyChange> changes, RelationRuntimeState<TSource, TItem> relationState,
            out bool updated)
        {
            updated = relationState.RelatedCount(source) != 0;
            return true;
        }
    }

    private sealed class SumPlan<TSource, TItem, TValue>(
        MemberInfo member,
        Func<TItem, TValue> selector,
        Func<TValue, TValue, TValue> add,
        Func<TValue, TValue, TValue> subtract) : IDerivedComputationPlan<TSource, TItem, TValue>
        where TSource : class where TItem : class
    {
        public string DisplayName => $"IncrementalSum({typeof(TItem).Name}.{member.Name})";

        public bool TryUpdate(TValue current, TSource source, RelationImpact? relationImpact,
            IReadOnlyList<PropertyChange> changes, RelationRuntimeState<TSource, TItem> relationState,
            out TValue updated)
        {
            updated = current;
            var changedMembership = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var pair in relationImpact?.RemovedPairs.Where(pair => ReferenceEquals(pair.Left, source)) ?? [])
            {
                changedMembership.Add(pair.Right);
                var oldValue = changes.LastOrDefault(change =>
                    ReferenceEquals(change.Instance, pair.Right) && change.Member.Equals(member))?.OldValue;
                updated = subtract(updated, oldValue is TValue typed ? typed : selector((TItem)pair.Right));
            }
            foreach (var pair in relationImpact?.AddedPairs.Where(pair => ReferenceEquals(pair.Left, source)) ?? [])
            {
                changedMembership.Add(pair.Right);
                updated = add(updated, selector((TItem)pair.Right));
            }
            foreach (var change in changes.Where(change => change.Member.Equals(member) &&
                         change.Instance is TItem && !changedMembership.Contains(change.Instance)))
            {
                var item = (TItem)change.Instance;
                if (!relationState.IsRelated(source, item) || change.OldValue is not TValue oldValue ||
                    change.NewValue is not TValue newValue)
                    continue;
                updated = add(subtract(updated, oldValue), newValue);
            }
            return true;
        }
    }
}
