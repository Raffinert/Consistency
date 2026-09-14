using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

internal sealed class ObjectSetRuntime
{
    private readonly IObjectSetDefinition _definition;
    private readonly HashSet<object> _instances = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _byKey = new();
    private readonly Dictionary<object, object> _registeredKeys = new(ReferenceEqualityComparer.Instance);

    public ObjectSetRuntime(IObjectSetDefinition definition) => _definition = definition;
    public IEnumerable<object> Instances => _instances;
    public int Count => _instances.Count;
    public IEnumerable<(object Instance, object Key)> RegisteredEntries =>
        _registeredKeys.Select(pair => (pair.Key, pair.Value));
    public bool Contains(object instance) => _instances.Contains(instance);
    public object GetRegisteredKey(object instance) => _registeredKeys.TryGetValue(instance, out var key)
        ? key
        : throw new InvalidOperationException("The source instance is not registered in its object set.");

    public object CaptureState() => new State(
        _instances.ToArray(),
        _byKey.ToArray(),
        _registeredKeys.ToArray());

    public object CaptureEntriesState(IEnumerable<object> instances) => instances
        .Distinct(ReferenceEqualityComparer.Instance)
        .Select(instance => _registeredKeys.TryGetValue(instance, out var key)
            ? new EntryState(instance, true, key)
            : new EntryState(instance, false, null))
        .ToArray();

    public void RestoreEntriesState(object snapshot)
    {
        foreach (var state in (EntryState[])snapshot)
        {
            if (_registeredKeys.TryGetValue(state.Instance, out var currentKey))
            {
                _instances.Remove(state.Instance);
                _registeredKeys.Remove(state.Instance);
                if (_byKey.TryGetValue(currentKey, out var current) && ReferenceEquals(current, state.Instance))
                    _byKey.Remove(currentKey);
            }
            if (!state.IsRegistered)
                continue;
            if (_byKey.TryGetValue(state.Key!, out var existing) && !ReferenceEquals(existing, state.Instance))
                throw new InvalidOperationException($"An object with key '{state.Key}' is already registered.");
            _instances.Add(state.Instance);
            _byKey[state.Key!] = state.Instance;
            _registeredKeys[state.Instance] = state.Key!;
        }
    }

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        _instances.Clear();
        _instances.UnionWith(state.Instances);
        _byKey.Clear();
        foreach (var pair in state.ByKey)
            _byKey.Add(pair.Key, pair.Value);
        _registeredKeys.Clear();
        foreach (var pair in state.RegisteredKeys)
            _registeredKeys.Add(pair.Key, pair.Value);
    }

    public void Add(object instance)
    {
        if (!_definition.ObjectType.IsInstanceOfType(instance))
            throw new ArgumentException($"Expected an instance of '{_definition.ObjectType.Name}'.");
        if (_instances.Contains(instance))
            throw new InvalidOperationException("The object instance is already registered in this object set.");
        var key = _definition.ReadKey(instance) ?? throw new InvalidOperationException("Object keys cannot be null.");
        if (_byKey.ContainsKey(key))
            throw new InvalidOperationException($"An object with key '{key}' is already registered in '{_definition.ObjectType.Name}'.");
        _instances.Add(instance);
        _byKey.Add(key, instance);
        _registeredKeys.Add(instance, key);
    }

    public bool Remove(object instance)
    {
        if (!_instances.Remove(instance)) return false;
        var key = _registeredKeys[instance];
        _registeredKeys.Remove(instance);
        if (_byKey.TryGetValue(key, out var keyed) && ReferenceEquals(keyed, instance))
            _byKey.Remove(key);
        return true;
    }

    private sealed record State(
        IReadOnlyList<object> Instances,
        IReadOnlyList<KeyValuePair<object, object>> ByKey,
        IReadOnlyList<KeyValuePair<object, object>> RegisteredKeys);

    private sealed record EntryState(object Instance, bool IsRegistered, object? Key);
}

