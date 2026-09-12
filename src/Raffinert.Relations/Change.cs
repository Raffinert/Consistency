using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Relations;

public static class Change
{
    /// <summary>Describes a property change that has already been made on the domain object.</summary>
    public static PropertyChange Property<T, TValue>(
        ObjectSetBuilder<T> set,
        T instance,
        Expression<Func<T, TValue>> property,
        TValue oldValue,
        TValue newValue) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(property);
        return new PropertyChange(set.Definition, instance, GetDirectMember(property), oldValue, newValue);
    }

    /// <summary>Describes a property change and lets the runtime infer its unique registered object set.</summary>
    public static PropertyChange Property<T, TValue>(
        T instance,
        Expression<Func<T, TValue>> property,
        TValue oldValue,
        TValue newValue) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(property);
        return new PropertyChange(null, instance, GetDirectMember(property), oldValue, newValue);
    }

    private static MemberInfo GetDirectMember(LambdaExpression property)
    {
        Expression body = property.Body;
        while (body is UnaryExpression unary) body = unary.Operand;
        if (body is not MemberExpression { Expression: ParameterExpression } member ||
            member.Member is not PropertyInfo and not FieldInfo)
            throw new ArgumentException("A direct property or field expression is required.", nameof(property));
        return member.Member;
    }
}

public sealed class PropertyChange
{
    internal PropertyChange(IObjectSetDefinition? set, object instance, MemberInfo member, object? oldValue, object? newValue)
    {
        Set = set;
        Instance = instance;
        Member = member;
        OldValue = oldValue;
        NewValue = newValue;
    }

    internal IObjectSetDefinition? Set { get; }
    internal object Instance { get; }
    internal MemberInfo Member { get; }
    public object? OldValue { get; }
    public object? NewValue { get; }

    internal PropertyChange WithSet(IObjectSetDefinition set) => new(set, Instance, Member, OldValue, NewValue);
}
