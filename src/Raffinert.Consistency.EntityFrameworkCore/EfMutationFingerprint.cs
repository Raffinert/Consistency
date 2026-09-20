using System.Reflection;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed class EfMutationFingerprint
{
    private readonly IReadOnlyList<EfMutationEvidence> _mutations;

    private EfMutationFingerprint(IReadOnlyList<EfMutationEvidence> mutations) => _mutations = mutations;

    internal int Count => _mutations.Count;

    public static EfMutationFingerprint Create(IReadOnlyList<RuntimeMutation> mutations) =>
        new(mutations.Select(EfMutationEvidence.Create).ToArray());

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
            CollectionChangeKind? collectionKind,
            object? item)
        {
            Kind = kind;
            Set = set;
            Instance = instance;
            Member = member;
            OldValue = oldValue;
            NewValue = newValue;
            CollectionKind = collectionKind;
            Item = item;
        }

        private Type Kind { get; }
        private IObjectSetDefinition? Set { get; }
        private object? Instance { get; }
        private MemberInfo? Member { get; }
        private object? OldValue { get; }
        private object? NewValue { get; }
        private CollectionChangeKind? CollectionKind { get; }
        private object? Item { get; }

        public static EfMutationEvidence Create(RuntimeMutation mutation) => mutation switch
        {
            PropertyChange change => new(
                mutation.GetType(), change.Set, change.Instance, change.Member,
                change.OldValue, change.NewValue, null, null),
            CollectionChange change => new(
                mutation.GetType(), change.Set, change.Owner, change.Member,
                null, null, change.Kind, change.Item),
            ObjectAdded change => new(
                mutation.GetType(), ((IAddedMutation)change).Set, change.Instance,
                null, null, null, null, null),
            ObjectRemoved change => new(
                mutation.GetType(), change.Set, change.Instance, null, null, null, null, null),
            CoverageAdmission change => new(
                mutation.GetType(), change.Set, change.Instance, null, null, null, null, null),
            _ => throw new InvalidOperationException(
                $"Unsupported EF mutation fingerprint type '{mutation.GetType().Name}'.")
        };

        public bool Equals(EfMutationEvidence? other) => other is not null &&
            Kind == other.Kind && ReferenceEquals(Set, other.Set) &&
            ReferenceEquals(Instance, other.Instance) && Member == other.Member &&
            Equals(OldValue, other.OldValue) && Equals(NewValue, other.NewValue) &&
            CollectionKind == other.CollectionKind && ReferenceEquals(Item, other.Item);
    }
}
