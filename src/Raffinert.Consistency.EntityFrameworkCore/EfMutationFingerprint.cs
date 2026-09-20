using System.Reflection;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed class EfMutationFingerprint
{
    private readonly IReadOnlyList<EfMutationEvidence> _mutations;

    private EfMutationFingerprint(IReadOnlyList<EfMutationEvidence> mutations) => _mutations = mutations;

    internal int Count => _mutations.Count;

    public static EfMutationFingerprint Create(
        ChangeTracker changeTracker,
        IReadOnlyList<RuntimeMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        ArgumentNullException.ThrowIfNull(mutations);
        var entries = new Dictionary<object, EntityEntry>(ReferenceEqualityComparer.Instance);
        foreach (var entry in changeTracker.Entries())
            entries.Add(entry.Entity, entry);
        return new(mutations.Select(mutation => EfMutationEvidence.Create(mutation, entries)).ToArray());
    }

    public bool Equals(EfMutationFingerprint? other) =>
        other is not null && _mutations.Count == other._mutations.Count &&
        _mutations.Zip(other._mutations).All(pair => pair.First.Equals(pair.Second));

    private sealed class EfMutationEvidence
    {
        private EfMutationEvidence(
            Type kind,
            IObjectSetDefinition? set,
            object? instance,
            MemberInfo? member,
            object? oldValue,
            object? newValue,
            ValueComparer? valueComparer,
            CollectionChangeKind? collectionKind,
            object? item)
        {
            Kind = kind;
            Set = set;
            Instance = instance;
            Member = member;
            OldValue = oldValue;
            NewValue = newValue;
            ValueComparer = valueComparer;
            CollectionKind = collectionKind;
            Item = item;
        }

        private Type Kind { get; }
        private IObjectSetDefinition? Set { get; }
        private object? Instance { get; }
        private MemberInfo? Member { get; }
        private object? OldValue { get; }
        private object? NewValue { get; }
        private ValueComparer? ValueComparer { get; }
        private CollectionChangeKind? CollectionKind { get; }
        private object? Item { get; }

        public static EfMutationEvidence Create(
            RuntimeMutation mutation,
            IReadOnlyDictionary<object, EntityEntry> entries) => mutation switch
            {
                PropertyChange change => CreateProperty(change, entries),
                CollectionChange change => new(
                    mutation.GetType(), change.Set, change.Owner, change.Member,
                    null, null, null, change.Kind, change.Item),
                ObjectAdded change => new(
                    mutation.GetType(), ((IAddedMutation)change).Set, change.Instance,
                    null, null, null, null, null, null),
                ObjectRemoved change => new(
                    mutation.GetType(), change.Set, change.Instance, null, null, null, null, null, null),
                CoverageAdmission change => new(
                    mutation.GetType(), change.Set, change.Instance, null, null, null, null, null, null),
                _ => throw new InvalidOperationException(
                    $"Unsupported EF mutation fingerprint type '{mutation.GetType().Name}'.")
            };

        private static EfMutationEvidence CreateProperty(
            PropertyChange change,
            IReadOnlyDictionary<object, EntityEntry> entries)
        {
            ValueComparer? comparer = null;
            if (entries.TryGetValue(change.Instance, out var entry))
            {
                var property = entry.Metadata.FindProperty(change.Member.Name);
                if (property is not null &&
                    (property.PropertyInfo == change.Member || property.FieldInfo == change.Member))
                    comparer = property.GetValueComparer() ?? property.GetTypeMapping().Comparer;
            }
            return new EfMutationEvidence(
                change.GetType(), change.Set, change.Instance, change.Member,
                comparer?.Snapshot(change.OldValue) ?? change.OldValue,
                comparer?.Snapshot(change.NewValue) ?? change.NewValue,
                comparer, null, null);
        }

        public bool Equals(EfMutationEvidence? other) => other is not null &&
            Kind == other.Kind && ReferenceEquals(Set, other.Set) &&
            ReferenceEquals(Instance, other.Instance) && Member == other.Member &&
            ValuesEqual(OldValue, other.OldValue) && ValuesEqual(NewValue, other.NewValue) &&
            CollectionKind == other.CollectionKind && ReferenceEquals(Item, other.Item);

        private bool ValuesEqual(object? left, object? right) =>
            ValueComparer?.Equals(left, right) ?? object.Equals(left, right);
    }
}
