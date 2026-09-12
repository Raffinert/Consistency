using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Relations;

public static class Change
{
    /// <summary>Describes a reflected property or field change, primarily for change-tracking adapters.</summary>
    public static PropertyChange Property(
        object instance,
        MemberInfo member,
        object? oldValue,
        object? newValue)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(member);
        if (member is not PropertyInfo and not FieldInfo)
            throw new ArgumentException("A property or field member is required.", nameof(member));
        return new PropertyChange(null, instance, member, oldValue, newValue);
    }

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

/// <summary>
/// An immutable batch describing domain mutations that have already occurred. The runtime validates
/// the complete batch before atomically updating its own indexes and dependency state; it does not
/// mutate or roll back domain objects.
/// </summary>
public sealed class ChangeSet
{
    private ChangeSet(IReadOnlyList<PropertyChange> changes) => Changes = changes;

    internal IReadOnlyList<PropertyChange> Changes { get; }

    public static ChangeSet Create(params PropertyChange[] changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Length == 0)
            throw new ArgumentException("A change set must contain at least one change.", nameof(changes));
        if (changes.Any(change => change is null))
            throw new ArgumentException("A change set cannot contain null changes.", nameof(changes));
        return new ChangeSet(changes.ToArray());
    }
}

/// <summary>Controls validation performed before a change is applied to runtime-owned state.</summary>
public enum ChangeValidationMode
{
    /// <summary>Validate registration, stable keys, and repeated-change consistency.</summary>
    Default,

    /// <summary>Also require each changed member's current value to equal its reported final new value.</summary>
    StrictNewValue
}
