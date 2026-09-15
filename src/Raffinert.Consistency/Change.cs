using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Consistency;

public static class Change
{
    public static ObjectAdded Add<T>(ObjectSet<T> set, T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instance);
        return new ObjectAdded(set.Definition, instance);
    }

    public static ObjectRemoved Remove<T>(ObjectSet<T> set, T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instance);
        return new ObjectRemoved(set.Definition, instance);
    }

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

    /// <summary>Describes a reflected change for a specific object set.</summary>
    public static PropertyChange Property<T>(
        ObjectSet<T> set,
        T instance,
        MemberInfo member,
        object? oldValue,
        object? newValue) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(member);
        if (member is not PropertyInfo and not FieldInfo)
            throw new ArgumentException("A property or field member is required.", nameof(member));
        return new PropertyChange(set.Definition, instance, member, oldValue, newValue);
    }

    /// <summary>Describes a property change that has already been made on the domain object.</summary>
    public static PropertyChange Property<T, TValue>(
        ObjectSet<T> set,
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

    public static CollectionChange CollectionAdd<TOwner, TItem>(
        TOwner owner,
        Expression<Func<TOwner, IEnumerable<TItem>>> collection,
        TItem item) where TOwner : class where TItem : class =>
        CollectionChange.Create(null, owner, GetDirectMember(collection), CollectionChangeKind.Add, item);

    public static CollectionChange CollectionAdd<TOwner, TItem>(
        ObjectSet<TOwner> set,
        TOwner owner,
        Expression<Func<TOwner, IEnumerable<TItem>>> collection,
        TItem item) where TOwner : class where TItem : class =>
        CollectionChange.Create(set?.Definition ?? throw new ArgumentNullException(nameof(set)), owner,
            GetDirectMember(collection), CollectionChangeKind.Add, item);

    public static CollectionChange CollectionRemove<TOwner, TItem>(
        TOwner owner,
        Expression<Func<TOwner, IEnumerable<TItem>>> collection,
        TItem item) where TOwner : class where TItem : class =>
        CollectionChange.Create(null, owner, GetDirectMember(collection), CollectionChangeKind.Remove, item);

    public static CollectionChange CollectionRemove<TOwner, TItem>(
        ObjectSet<TOwner> set,
        TOwner owner,
        Expression<Func<TOwner, IEnumerable<TItem>>> collection,
        TItem item) where TOwner : class where TItem : class =>
        CollectionChange.Create(set?.Definition ?? throw new ArgumentNullException(nameof(set)), owner,
            GetDirectMember(collection), CollectionChangeKind.Remove, item);

    public static CollectionChange CollectionReset<TOwner, TItem>(
        TOwner owner,
        Expression<Func<TOwner, IEnumerable<TItem>>> collection) where TOwner : class where TItem : class =>
        CollectionChange.Create(null, owner, GetDirectMember(collection), CollectionChangeKind.Reset, null);

    public static CollectionChange CollectionReset<TOwner, TItem>(
        ObjectSet<TOwner> set,
        TOwner owner,
        Expression<Func<TOwner, IEnumerable<TItem>>> collection) where TOwner : class where TItem : class =>
        CollectionChange.Create(set?.Definition ?? throw new ArgumentNullException(nameof(set)), owner,
            GetDirectMember(collection), CollectionChangeKind.Reset, null);

    /// <summary>Describes a reflected collection reset, primarily for change-tracking adapters.</summary>
    public static CollectionChange CollectionReset(object owner, MemberInfo member)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(member);
        return CollectionChange.Create(null, owner, member, CollectionChangeKind.Reset, null);
    }

    internal static MemberInfo GetDirectMember(LambdaExpression property)
    {
        Expression body = property.Body;
        while (body is UnaryExpression unary) body = unary.Operand;
        if (body is not MemberExpression { Expression: ParameterExpression } member ||
            member.Member is not PropertyInfo and not FieldInfo)
            throw new ArgumentException("A direct property or field expression is required.", nameof(property));
        return member.Member;
    }
}

public enum CollectionChangeKind
{
    Add,
    Remove,
    Reset
}

/// <summary>Describes an explicit collection mutation that has already occurred.</summary>
public sealed class CollectionChange : RuntimeMutation
{
    private CollectionChange(
        IObjectSetDefinition? set,
        object owner,
        MemberInfo member,
        CollectionChangeKind kind,
        object? item)
    {
        Set = set;
        Owner = owner;
        Member = member;
        Kind = kind;
        Item = item;
    }

    internal IObjectSetDefinition? Set { get; }
    internal object Owner { get; }
    internal MemberInfo Member { get; }
    public CollectionChangeKind Kind { get; }
    public object? Item { get; }

    internal static CollectionChange Create(
        IObjectSetDefinition? set,
        object owner,
        MemberInfo member,
        CollectionChangeKind kind,
        object? item)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (kind != CollectionChangeKind.Reset)
            ArgumentNullException.ThrowIfNull(item);
        return new CollectionChange(set, owner, member, kind, item);
    }
}

public sealed class PropertyChange : RuntimeMutation
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

/// <summary>A mutation that can participate in an atomic runtime mutation batch.</summary>
public abstract class RuntimeMutation
{
    private protected RuntimeMutation()
    {
    }
}

/// <summary>Describes adding an object to a runtime object set.</summary>
public sealed class ObjectAdded : RuntimeMutation
{
    internal ObjectAdded(IObjectSetDefinition set, object instance)
    {
        Set = set;
        Instance = instance;
    }

    internal IObjectSetDefinition Set { get; }
    public object Instance { get; }
}

/// <summary>Describes removing an object from a runtime object set.</summary>
public sealed class ObjectRemoved : RuntimeMutation
{
    internal ObjectRemoved(IObjectSetDefinition set, object instance)
    {
        Set = set;
        Instance = instance;
    }

    internal IObjectSetDefinition Set { get; }
    public object Instance { get; }
}

/// <summary>An immutable batch of lifecycle, property, and collection mutations.</summary>
public sealed class MutationSet
{
    private MutationSet(IReadOnlyList<RuntimeMutation> mutations) => Mutations = mutations;

    internal IReadOnlyList<RuntimeMutation> Mutations { get; }

    public static MutationSet Create(params RuntimeMutation[] mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        if (mutations.Length == 0)
            throw new ArgumentException("A mutation set must contain at least one mutation.", nameof(mutations));
        if (mutations.Any(mutation => mutation is null))
            throw new ArgumentException("A mutation set cannot contain null mutations.", nameof(mutations));
        return new MutationSet(mutations.ToArray());
    }
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
