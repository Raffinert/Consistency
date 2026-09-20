using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed class TrackedGraphSnapshot
{
    private readonly Dictionary<PrincipalIndexKey, Dictionary<TrackedValueKey, List<EntityEntry>>>
        _principalIndexes = [];
    private readonly Dictionary<DependentIndexKey, Dictionary<TrackedValueKey, List<EntityEntry>>>
        _dependentIndexes = [];
    private readonly Dictionary<object, EntityEntry> _entriesByReference;
    private readonly EfFingerprintDiagnostics? _diagnostics;

    private TrackedGraphSnapshot(EntityEntry[] entries, EfFingerprintDiagnostics? diagnostics)
    {
        Entries = entries;
        _entriesByReference = new Dictionary<object, EntityEntry>(ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
            _entriesByReference.Add(entry.Entity, entry);
        _diagnostics = diagnostics;
        if (diagnostics is not null)
            diagnostics.TrackedEntries = entries.Length;
    }

    internal IReadOnlyList<EntityEntry> Entries { get; }

    internal EntityEntry? FindEntry(object entity) =>
        _entriesByReference.GetValueOrDefault(entity);

    internal static TrackedGraphSnapshot Create(
        ChangeTracker changeTracker,
        EfFingerprintDiagnostics? diagnostics = null) =>
        new(changeTracker.Entries().ToArray(), diagnostics);

    internal IReadOnlyList<EntityEntry> FindPrincipals(
        IEntityType target,
        IKey key,
        IReadOnlyList<object?> values,
        bool original)
    {
        if (_diagnostics is not null)
            _diagnostics.ReferenceIndexLookups++;
        var descriptor = new PrincipalIndexKey(target, key, original);
        if (!_principalIndexes.TryGetValue(descriptor, out var index))
        {
            index = BuildIndex(
                target,
                key.Properties,
                key.Properties,
                original);
            _principalIndexes.Add(descriptor, index);
        }
        return index.TryGetValue(new TrackedValueKey(values), out var matches) ? matches : [];
    }

    internal IReadOnlyList<EntityEntry> FindDependents(
        IEntityType target,
        IForeignKey foreignKey,
        IReadOnlyList<object?> values,
        bool original,
        bool collectionLookup = false)
    {
        if (_diagnostics is not null)
        {
            if (collectionLookup)
                _diagnostics.CollectionIndexLookups++;
            else
                _diagnostics.ReferenceIndexLookups++;
        }
        var descriptor = new DependentIndexKey(target, foreignKey, original);
        if (!_dependentIndexes.TryGetValue(descriptor, out var index))
        {
            index = BuildIndex(
                target,
                foreignKey.Properties,
                foreignKey.PrincipalKey.Properties,
                original,
                foreignKey);
            _dependentIndexes.Add(descriptor, index);
        }
        return index.TryGetValue(new TrackedValueKey(values), out var matches) ? matches : [];
    }

    private Dictionary<TrackedValueKey, List<EntityEntry>> BuildIndex(
        IEntityType target,
        IReadOnlyList<IProperty> valueProperties,
        IReadOnlyList<IProperty> comparisonProperties,
        bool original,
        IForeignKey? foreignKey = null)
    {
        var index = new Dictionary<TrackedValueKey, List<EntityEntry>>(
            new TrackedValueKeyComparer(comparisonProperties));
        foreach (var entry in Entries)
        {
            if (!target.ClrType.IsInstanceOfType(entry.Entity))
                continue;
            var values = valueProperties.Select(property =>
                original
                    ? entry.Property(property.Name).OriginalValue
                    : entry.Property(property.Name).CurrentValue).ToArray();
            if (foreignKey is not null && EfRelationshipKey.IsNull(foreignKey, values))
                continue;
            var key = new TrackedValueKey(values);
            if (!index.TryGetValue(key, out var bucket))
                index.Add(key, bucket = []);
            bucket.Add(entry);
        }
        return index;
    }

    private readonly record struct PrincipalIndexKey(IEntityType Target, IKey Key, bool Original);
    private readonly record struct DependentIndexKey(IEntityType Target, IForeignKey ForeignKey, bool Original);

    private readonly struct TrackedValueKey
    {
        internal readonly object?[] Values;

        internal TrackedValueKey(IEnumerable<object?> values) => Values = values.ToArray();
    }

    private sealed class TrackedValueKeyComparer : IEqualityComparer<TrackedValueKey>
    {
        private readonly ValueComparer[] _comparers;

        internal TrackedValueKeyComparer(IReadOnlyList<IProperty> properties)
        {
            // EF documents GetKeyValueComparer as the comparer used for key values. Using it here
            // preserves structural arrays and configured value-converted/custom key equality.
            _comparers = properties.Select(property => property.GetKeyValueComparer()).ToArray();
        }

        public bool Equals(TrackedValueKey left, TrackedValueKey right)
        {
            if (left.Values.Length != right.Values.Length || left.Values.Length != _comparers.Length)
                return false;
            for (var index = 0; index < _comparers.Length; index++)
            {
                if (!_comparers[index].Equals(left.Values[index], right.Values[index]))
                    return false;
            }
            return true;
        }

        public int GetHashCode(TrackedValueKey key)
        {
            var hash = new HashCode();
            for (var index = 0; index < _comparers.Length; index++)
                hash.Add(key.Values[index] is null ? 0 : _comparers[index].GetHashCode(key.Values[index]!));
            return hash.ToHashCode();
        }
    }
}

internal static class EfRelationshipKey
{
    internal static bool IsNull(IForeignKey foreignKey, IReadOnlyList<object?> values)
    {
        if (foreignKey.Properties.Count != values.Count)
            throw new ArgumentException("The foreign-key value count does not match its metadata.", nameof(values));
        return values.Any(value => value is null);
    }

    internal static bool ValuesEqual(
        IForeignKey foreignKey,
        IReadOnlyList<object?> left,
        IReadOnlyList<object?> right)
    {
        if (left.Count != foreignKey.Properties.Count || right.Count != foreignKey.Properties.Count)
            return false;
        for (var index = 0; index < foreignKey.Properties.Count; index++)
        {
            var comparer = foreignKey.PrincipalKey.Properties[index].GetKeyValueComparer();
            if (!comparer.Equals(left[index], right[index]))
                return false;
        }
        return true;
    }

    internal static bool RelationshipsEqual(
        IForeignKey foreignKey,
        IReadOnlyList<object?> left,
        IReadOnlyList<object?> right)
    {
        var leftNull = IsNull(foreignKey, left);
        var rightNull = IsNull(foreignKey, right);
        return leftNull || rightNull
            ? leftNull == rightNull
            : ValuesEqual(foreignKey, left, right);
    }
}
