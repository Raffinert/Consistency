using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed class TrackedGraphSnapshot
{
    private readonly Dictionary<PrincipalIndexKey, Dictionary<TrackedValueKey, List<EntityEntry>>>
        _principalIndexes = [];
    private readonly Dictionary<DependentIndexKey, Dictionary<TrackedValueKey, List<EntityEntry>>>
        _dependentIndexes = [];
    private readonly EfFingerprintDiagnostics? _diagnostics;

    private TrackedGraphSnapshot(EntityEntry[] entries, EfFingerprintDiagnostics? diagnostics)
    {
        Entries = entries;
        _diagnostics = diagnostics;
        if (diagnostics is not null)
            diagnostics.TrackedEntries = entries.Length;
    }

    internal IReadOnlyList<EntityEntry> Entries { get; }

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
                original);
            _dependentIndexes.Add(descriptor, index);
        }
        return index.TryGetValue(new TrackedValueKey(values), out var matches) ? matches : [];
    }

    private Dictionary<TrackedValueKey, List<EntityEntry>> BuildIndex(
        IEntityType target,
        IReadOnlyList<IProperty> properties,
        bool original)
    {
        var index = new Dictionary<TrackedValueKey, List<EntityEntry>>();
        foreach (var entry in Entries)
        {
            if (!target.ClrType.IsInstanceOfType(entry.Entity))
                continue;
            var key = new TrackedValueKey(properties.Select(property =>
                original
                    ? entry.Property(property.Name).OriginalValue
                    : entry.Property(property.Name).CurrentValue));
            if (!index.TryGetValue(key, out var bucket))
                index.Add(key, bucket = []);
            bucket.Add(entry);
        }
        return index;
    }

    private readonly record struct PrincipalIndexKey(IEntityType Target, IKey Key, bool Original);
    private readonly record struct DependentIndexKey(IEntityType Target, IForeignKey ForeignKey, bool Original);

    private readonly struct TrackedValueKey : IEquatable<TrackedValueKey>
    {
        private readonly object?[] _values;

        internal TrackedValueKey(IEnumerable<object?> values) => _values = values.ToArray();

        public bool Equals(TrackedValueKey other) => _values.AsSpan().SequenceEqual(other._values);
        public override bool Equals(object? value) => value is TrackedValueKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in _values)
                hash.Add(value);
            return hash.ToHashCode();
        }
    }
}
