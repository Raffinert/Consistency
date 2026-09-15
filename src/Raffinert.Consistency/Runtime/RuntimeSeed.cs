namespace Raffinert.Consistency;

/// <summary>Collects authoritative objects used to construct a new runtime baseline.</summary>
public sealed class RuntimeSeedBuilder
{
    private readonly List<RuntimeSeedEntry> _entries = [];

    internal IReadOnlyList<RuntimeSeedEntry> Entries => _entries;

    public RuntimeSeedBuilder Add<T>(ObjectSet<T> set, IEnumerable<T> instances) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instances);
        var values = instances.ToArray();
        if (values.Any(value => value is null))
            throw new ArgumentException("A runtime seed cannot contain null instances.", nameof(instances));
        _entries.Add(new RuntimeSeedEntry(set.Definition, values.Cast<object>().ToArray()));
        return this;
    }
}

internal sealed record RuntimeSeedEntry(
    IObjectSetDefinition Set,
    IReadOnlyList<object> Instances);

public sealed partial class RelationRuntime
{
    internal void Bootstrap(IReadOnlyList<RuntimeSeedEntry> entries)
    {
        var collected = _sets.Keys.ToDictionary(
            set => set,
            _ => new List<object>());
        foreach (var entry in entries)
        {
            if (!collected.TryGetValue(entry.Set, out var values))
                throw new ArgumentException("The seeded object set does not belong to this compiled model.");
            values.AddRange(entry.Instances);
        }

        foreach (var (set, values) in collected)
        {
            var instances = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var keys = new HashSet<object>();
            foreach (var instance in values)
            {
                if (!set.ObjectType.IsInstanceOfType(instance))
                    throw new ArgumentException($"Expected an instance of '{set.ObjectType.Name}'.");
                if (!instances.Add(instance))
                    throw new InvalidOperationException("The runtime seed contains the same object instance more than once.");
                var key = set.ReadKey(instance) ?? throw new InvalidOperationException("Object keys cannot be null.");
                if (!keys.Add(key))
                    throw new InvalidOperationException(
                        $"The runtime seed contains duplicate key '{key}' in '{set.ObjectType.Name}'.");
            }
        }

        var deltas = new Dictionary<IRelationDefinition, RelationDelta>();
        foreach (var set in _sets.Keys.OrderBy(set => set.Id))
            foreach (var instance in collected[set])
                CommitAdd(new ObjectAdded(set, instance), deltas);
        _projections.ValidateAll();
        ResetDiagnostics();
    }
}
