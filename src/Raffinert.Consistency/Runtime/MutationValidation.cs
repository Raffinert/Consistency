using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency;

public sealed partial class RelationRuntime
{
    private ValidatedMutationBatch ValidateMutations(
        IReadOnlyList<RuntimeMutation> mutations,
        ChangeValidationMode validationMode)
    {
        var lifecycle = mutations
            .Where(mutation => mutation is ObjectAdded or ObjectRemoved)
            .ToArray();
        var simulations = new Dictionary<IObjectSetDefinition, ObjectSetSimulation>();
        foreach (var mutation in lifecycle)
        {
            var set = mutation switch
            {
                ObjectAdded added => added.Set,
                ObjectRemoved removed => removed.Set,
                _ => throw new InvalidOperationException("Unsupported lifecycle mutation.")
            };
            if (!simulations.TryGetValue(set, out var simulation))
            {
                simulation = new ObjectSetSimulation(set, GetSet(set));
                simulations.Add(set, simulation);
            }
            if (mutation is ObjectAdded addition)
                simulation.Add(addition.Instance);
            else
                simulation.Remove(((ObjectRemoved)mutation).Instance);
        }

        var lifecycleTargets = lifecycle.Select(mutation => mutation switch
        {
            ObjectAdded added => new SetInstance(added.Set, added.Instance),
            ObjectRemoved removed => new SetInstance(removed.Set, removed.Instance),
            _ => throw new InvalidOperationException("Unsupported lifecycle mutation.")
        }).ToHashSet();
        var properties = NormalizeChanges(mutations.OfType<PropertyChange>()
            .Select(change => ValidateBatchChange(change, lifecycleTargets))
            .ToArray());
        var normalizedCollections = NormalizeCollectionChanges(mutations.OfType<CollectionChange>().ToArray());
        var collections = normalizedCollections
            .Select(change => IsLifecycleTarget(change.Set, change.Owner, lifecycleTargets)
                ? new PropertyChange(change.Set, change.Owner, change.Member, null, null)
                : ValidateCollectionChange(change))
            .ToArray();
        var changes = properties
            .Concat(collections)
            .Where(change => change.Set is null ||
                !lifecycleTargets.Contains(new SetInstance(change.Set, change.Instance)))
            .ToArray();
        if (validationMode == ChangeValidationMode.StrictNewValue)
            ValidateCurrentValues(properties);
        var provenance = lifecycle.Select(mutation => mutation switch
            {
                ObjectAdded added => new NormalizedMutationProvenance(
                    MutationOriginKind.ObjectAdded, added.Set, added.Instance, null, null, null, null, null),
                ObjectRemoved removed => new NormalizedMutationProvenance(
                    MutationOriginKind.ObjectRemoved, removed.Set, removed.Instance, null, null, null, null, null),
                _ => throw new InvalidOperationException("Unsupported lifecycle mutation.")
            })
            .Concat(properties.Select(change => new NormalizedMutationProvenance(
                MutationOriginKind.SourceMemberChanged, change.Set, change.Instance, change.Member,
                change.OldValue, change.NewValue, null, null)))
            .Concat(normalizedCollections.Select(change => new NormalizedMutationProvenance(
                MutationOriginKind.CollectionChanged, change.Set, change.Owner, change.Member,
                null, null, change.Kind, change.Item)))
            .ToArray();
        return new ValidatedMutationBatch(lifecycle, changes, provenance);
    }

    private PropertyChange ValidateBatchChange(
        PropertyChange change,
        IReadOnlySet<SetInstance> lifecycleTargets)
    {
        if (change.Set is not null && IsLifecycleTarget(change.Set, change.Instance, lifecycleTargets))
        {
            GetSet(change.Set);
            return change;
        }
        return ValidateChange(change);
    }

    private static bool IsLifecycleTarget(
        IObjectSetDefinition? set,
        object instance,
        IReadOnlySet<SetInstance> lifecycleTargets) =>
        set is not null && lifecycleTargets.Contains(new SetInstance(set, instance));

    private static IReadOnlyList<CollectionChange> NormalizeCollectionChanges(
        IReadOnlyList<CollectionChange> changes)
    {
        var normalized = new List<CollectionChange>();
        foreach (var group in changes.GroupBy(
                     change => new ChangedMember(change.Owner, change.Member)))
        {
            var groupChanges = group.ToArray();
            var set = groupChanges[0].Set;
            if (groupChanges.Any(change => !ReferenceEquals(change.Set, set)))
                throw new InvalidOperationException(
                    $"Conflicting object sets were reported for collection '{groupChanges[0].Member.Name}'.");
            normalized.Add(groupChanges.Length == 1
                ? groupChanges[0]
                : CollectionChange.Create(
                    set,
                    groupChanges[0].Owner,
                    groupChanges[0].Member,
                    CollectionChangeKind.Reset,
                    null));
        }
        return normalized;
    }

    private PropertyChange ValidateChange(PropertyChange change)
    {
        if (change.Set is null)
        {
            var matches = _sets
                .Where(pair => pair.Key.ObjectType.IsInstanceOfType(change.Instance) && pair.Value.Contains(change.Instance))
                .Select(pair => pair.Key)
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidOperationException($"Expected the changed instance in at most one object set, but found {matches.Length}. Use the object-set Change.Property overload.");
            if (matches.Length == 1)
                change = change.WithSet(matches[0]);
        }

        if (change.Set is not null)
        {
            var set = GetSet(change.Set);
            if (!set.Contains(change.Instance))
                throw new InvalidOperationException("The changed instance is not registered in the specified object set.");
            if (change.Set.KeyMembers.Contains(change.Member))
                throw new InvalidOperationException(
                    $"The registered key member '{change.Member.Name}' of '{change.Set.ObjectType.Name}' cannot be changed. " +
                    "Remove and re-add the object, or declare a genuinely stable key.");
        }
        return change;
    }

    private PropertyChange ValidateCollectionChange(CollectionChange change)
    {
        var normalized = ValidateChange(new PropertyChange(
            change.Set,
            change.Owner,
            change.Member,
            null,
            null));
        var value = MemberReader.Read(change.Member, change.Owner);
        if (value is not IEnumerable collection)
            throw new InvalidOperationException($"Member '{change.Member.Name}' is not a collection.");
        if (change.Kind != CollectionChangeKind.Reset)
        {
            var contains = collection.Cast<object?>().Any(item => ReferenceEquals(item, change.Item));
            if (change.Kind == CollectionChangeKind.Add && !contains)
                throw new InvalidOperationException("The added item is not present in the current collection.");
            if (change.Kind == CollectionChangeKind.Remove && contains)
                throw new InvalidOperationException("The removed item is still present in the current collection.");
        }
        return normalized;
    }

    private static IReadOnlyList<PropertyChange> NormalizeChanges(IReadOnlyList<PropertyChange> changes)
    {
        var normalized = new List<PropertyChange>();
        var positions = new Dictionary<ChangedMember, int>();
        foreach (var change in changes)
        {
            var key = new ChangedMember(change.Instance, change.Member);
            if (!positions.TryGetValue(key, out var position))
            {
                positions.Add(key, normalized.Count);
                normalized.Add(change);
                continue;
            }

            var previous = normalized[position];
            if (!Equals(previous.NewValue, change.OldValue))
                throw new InvalidOperationException(
                    $"Conflicting changes for member '{change.Member.Name}'. " +
                    $"Expected the next old value to equal the previous new value.");
            normalized[position] = new PropertyChange(
                previous.Set,
                previous.Instance,
                previous.Member,
                previous.OldValue,
                change.NewValue);
        }
        return normalized;
    }

    private static void ValidateCurrentValues(IReadOnlyList<PropertyChange> changes)
    {
        foreach (var change in changes)
        {
            var actual = MemberReader.Read(change.Member, change.Instance);
            if (!Equals(actual, change.NewValue))
                throw new InvalidOperationException(
                    $"The current value of '{change.Member.Name}' does not equal the reported new value.");
        }
    }

    private readonly struct ChangedMember : IEquatable<ChangedMember>
    {
        private readonly object _instance;
        private readonly MemberInfo _member;

        public ChangedMember(object instance, MemberInfo member)
        {
            _instance = instance;
            _member = member;
        }

        public bool Equals(ChangedMember other) =>
            ReferenceEquals(_instance, other._instance) && _member.Equals(other._member);

        public override bool Equals(object? obj) => obj is ChangedMember other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(_instance),
            _member);
    }

    private readonly struct SetInstance : IEquatable<SetInstance>
    {
        private readonly IObjectSetDefinition _set;
        private readonly object _instance;

        public SetInstance(IObjectSetDefinition set, object instance)
        {
            _set = set;
            _instance = instance;
        }

        public bool Equals(SetInstance other) =>
            ReferenceEquals(_set, other._set) && ReferenceEquals(_instance, other._instance);

        public override bool Equals(object? obj) => obj is SetInstance other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(_set),
            RuntimeHelpers.GetHashCode(_instance));
    }

    private sealed class ObjectSetSimulation
    {
        private readonly IObjectSetDefinition _definition;
        private readonly HashSet<object> _instances;
        private readonly Dictionary<object, object> _keyOwners;
        private readonly Dictionary<object, object> _registeredKeys;

        public ObjectSetSimulation(IObjectSetDefinition definition, ObjectSetRuntime state)
        {
            _definition = definition;
            _instances = state.Instances.ToHashSet(ReferenceEqualityComparer.Instance);
            _registeredKeys = state.RegisteredEntries.ToDictionary(
                pair => pair.Instance,
                pair => pair.Key,
                ReferenceEqualityComparer.Instance);
            _keyOwners = state.RegisteredEntries.ToDictionary(pair => pair.Key, pair => pair.Instance);
        }

        public void Add(object instance)
        {
            if (!_definition.ObjectType.IsInstanceOfType(instance))
                throw new ArgumentException($"Expected an instance of '{_definition.ObjectType.Name}'.");
            if (!_instances.Add(instance))
                throw new InvalidOperationException("The object instance is already registered in this object set.");
            var key = _definition.ReadKey(instance) ?? throw new InvalidOperationException("Object keys cannot be null.");
            if (_keyOwners.ContainsKey(key))
                throw new InvalidOperationException(
                    $"An object with key '{key}' is already registered in '{_definition.ObjectType.Name}'.");
            _keyOwners.Add(key, instance);
            _registeredKeys.Add(instance, key);
        }

        public void Remove(object instance)
        {
            if (!_instances.Remove(instance))
                throw new InvalidOperationException("The removed instance is not registered in the specified object set.");
            var key = _registeredKeys[instance];
            _registeredKeys.Remove(instance);
            _keyOwners.Remove(key);
        }
    }

    private sealed record ValidatedMutationBatch(
        IReadOnlyList<RuntimeMutation> LifecycleMutations,
        IReadOnlyList<PropertyChange> Changes,
        IReadOnlyList<NormalizedMutationProvenance> Provenance);

}

internal sealed record NormalizedMutationProvenance(
    MutationOriginKind Kind,
    IObjectSetDefinition? Set,
    object Source,
    MemberInfo? Member,
    object? OldValue,
    object? NewValue,
    CollectionChangeKind? CollectionKind,
    object? CollectionItem);
