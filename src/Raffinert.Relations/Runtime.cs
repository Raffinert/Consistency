using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

public sealed class CompiledInvariantModel
{
    private readonly IReadOnlyList<IObjectSetDefinition> _sets;
    private readonly IReadOnlyList<IRelationDefinition> _relations;

    internal CompiledInvariantModel(IReadOnlyList<IObjectSetDefinition> sets, IReadOnlyList<IRelationDefinition> relations)
    {
        _sets = sets;
        _relations = relations;
        DebugView = CreateDebugView();
    }

    public string DebugView { get; }
    public InvariantRuntime CreateRuntime() => new(_sets, _relations);

    private string CreateDebugView()
    {
        var lines = new List<string>();
        foreach (var set in _sets)
        {
            lines.Add($"ObjectSet {set.ObjectType.Name}");
            lines.Add($"  Key: {GetMemberName(set.KeyExpression?.Body)}");
        }
        foreach (var relation in _relations)
        {
            lines.Add($"Relation {relation.LeftSet.ObjectType.Name} -> {relation.RightSet.ObjectType.Name}");
            lines.Add($"  Predicate: {relation.PredicateExpression.Body}");
            lines.Add("  Dependencies:");
            foreach (var dependency in relation.Analysis.Dependencies)
                lines.Add($"    {dependency.DisplayName}");
            lines.Add("  Join key:");
            foreach (var key in relation.Analysis.JoinKeyParts)
                lines.Add($"    {key.Left.DisplayName} <-> {key.Right.DisplayName}");
            lines.Add($"  Access: {(relation.Analysis.JoinKeyParts.Count > 0 ? "HashIndex" : "Scan")}");
            lines.Add($"  Dependency analysis: {(relation.Analysis.IsDependencyAnalysisComplete ? "Complete" : "Incomplete")}");
            lines.Add($"  Residual predicate: {(relation.Analysis.HasResidualPredicate ? "Yes" : "No")}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string GetMemberName(Expression? expression)
    {
        while (expression is UnaryExpression unary) expression = unary.Operand;
        return expression is MemberExpression member ? member.Member.Name : expression?.ToString() ?? "<missing>";
    }
}

public sealed class InvariantRuntime
{
    private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
    private readonly IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> _relations;
    private readonly IReadOnlyDictionary<(IObjectSetDefinition Set, MemberInfo Member), IReadOnlyCollection<IRelationRuntimeState>> _dependencies;

    internal InvariantRuntime(IReadOnlyList<IObjectSetDefinition> sets, IReadOnlyList<IRelationDefinition> relations)
    {
        _sets = sets.ToDictionary(set => set, set => new ObjectSetRuntime(set));
        _relations = relations.ToDictionary(relation => relation, relation => relation.CreateState(_sets));
        var dependencies = new Dictionary<(IObjectSetDefinition, MemberInfo), HashSet<IRelationRuntimeState>>();
        foreach (var pair in _relations)
        {
            foreach (var dependency in pair.Key.Analysis.Dependencies)
            {
                foreach (var set in sets.Where(set => set.ObjectType == dependency.RootType))
                {
                    var key = (set, dependency.Members[0]);
                    if (!dependencies.TryGetValue(key, out var affected))
                        dependencies.Add(key, affected = []);
                    affected.Add(pair.Value);
                }
            }
        }
        _dependencies = dependencies.ToDictionary(pair => pair.Key, pair => (IReadOnlyCollection<IRelationRuntimeState>)pair.Value);
    }

    public void Add<T>(ObjectSetBuilder<T> set, T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instance);
        Add(set.Definition, instance);
    }

    public void Add<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        Add(FindUniqueSet<T>(), instance);
    }

    private void Add<T>(ObjectSetDefinition<T> definition, T instance) where T : class
    {
        var state = GetSet(definition);
        state.Add(instance);
        foreach (var relation in _relations.Values.Where(relation => ReferenceEquals(relation.RightSet, definition)))
            relation.AddRight(instance);
    }

    public bool Remove<T>(ObjectSetBuilder<T> set, T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(instance);
        return Remove(set.Definition, instance);
    }

    public bool Remove<T>(T instance) where T : class
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Remove(FindUniqueSet<T>(), instance);
    }

    private bool Remove<T>(ObjectSetDefinition<T> definition, T instance) where T : class
    {
        var state = GetSet(definition);
        if (!state.Contains(instance)) return false;
        foreach (var relation in _relations.Values.Where(relation => ReferenceEquals(relation.RightSet, definition)))
            relation.RemoveRight(instance);
        return state.Remove(instance);
    }

    public IReadOnlyList<TRight> Related<TLeft, TRight>(Relation<TLeft, TRight> relation, TLeft left)
        where TLeft : class where TRight : class
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(left);
        if (!_relations.TryGetValue(relation.Definition, out var state))
            throw new ArgumentException("The relation does not belong to this compiled model.", nameof(relation));
        if (!_sets[relation.Definition.Left].Contains(left))
            throw new InvalidOperationException("The left instance is not registered in the relation's object set.");
        return ((RelationRuntimeState<TLeft, TRight>)state).Related(left);
    }

    public IReadOnlyList<TRight> Related<TLeft, TRight>(TLeft left, Relation<TLeft, TRight> relation)
        where TLeft : class where TRight : class => Related(relation, left);

    public void Apply(PropertyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Set is null)
        {
            var matches = _sets
                .Where(pair => pair.Key.ObjectType.IsInstanceOfType(change.Instance) && pair.Value.Contains(change.Instance))
                .Select(pair => pair.Key)
                .ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException($"Expected the changed instance in exactly one object set, but found {matches.Length}. Use the object-set Change.Property overload.");
            change = change.WithSet(matches[0]);
        }

        var set = GetSet(change.Set!);
        if (!set.Contains(change.Instance))
            throw new InvalidOperationException("The changed instance is not registered in the specified object set.");
        if (_dependencies.TryGetValue((change.Set!, change.Member), out var affectedRelations))
        {
            foreach (var relation in affectedRelations)
                relation.Apply(change);
        }
    }

    private ObjectSetDefinition<T> FindUniqueSet<T>() where T : class
    {
        var matches = _sets.Keys.OfType<ObjectSetDefinition<T>>().ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"Expected exactly one registered object set for '{typeof(T).Name}', but found {matches.Length}. Use the explicit object-set overload.");
        return matches[0];
    }

    private ObjectSetRuntime GetSet(IObjectSetDefinition definition)
    {
        if (!_sets.TryGetValue(definition, out var state))
            throw new ArgumentException("The object set does not belong to this compiled model.");
        return state;
    }
}

internal sealed class ObjectSetRuntime
{
    private readonly IObjectSetDefinition _definition;
    private readonly HashSet<object> _instances = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _byKey = new();

    public ObjectSetRuntime(IObjectSetDefinition definition) => _definition = definition;
    public IEnumerable<object> Instances => _instances;
    public bool Contains(object instance) => _instances.Contains(instance);

    public void Add(object instance)
    {
        if (!_definition.ObjectType.IsInstanceOfType(instance))
            throw new ArgumentException($"Expected an instance of '{_definition.ObjectType.Name}'.");
        var key = _definition.ReadKey(instance) ?? throw new InvalidOperationException("Object keys cannot be null.");
        if (_byKey.ContainsKey(key))
            throw new InvalidOperationException($"An object with key '{key}' is already registered in '{_definition.ObjectType.Name}'.");
        _instances.Add(instance);
        _byKey.Add(key, instance);
    }

    public bool Remove(object instance)
    {
        if (!_instances.Remove(instance)) return false;
        var key = _definition.ReadKey(instance);
        if (key is not null && _byKey.TryGetValue(key, out var keyed) && ReferenceEquals(keyed, instance))
            _byKey.Remove(key);
        return true;
    }
}

internal interface IRelationRuntimeState
{
    IObjectSetDefinition RightSet { get; }
    void AddRight(object instance);
    void RemoveRight(object instance);
    void Apply(PropertyChange change);
}

internal sealed class RelationRuntimeState<TLeft, TRight> : IRelationRuntimeState
    where TLeft : class where TRight : class
{
    private readonly RelationDefinition<TLeft, TRight> _definition;
    private readonly ObjectSetRuntime _rightObjects;
    private readonly Dictionary<CompositeKey, HashSet<TRight>> _index = [];
    private readonly Dictionary<TRight, CompositeKey> _keys = new(ReferenceEqualityComparer<TRight>.Instance);
    private readonly Dictionary<object, HashSet<TRight>> _reverse = new(ReferenceEqualityComparer.Instance);

    public RelationRuntimeState(RelationDefinition<TLeft, TRight> definition, ObjectSetRuntime rightObjects)
    {
        _definition = definition;
        _rightObjects = rightObjects;
    }

    public IObjectSetDefinition RightSet => _definition.Right;

    public void AddRight(object instance)
    {
        var right = (TRight)instance;
        if (_definition.Analysis.JoinKeyParts.Count > 0)
        {
            var key = ReadRightKey(right);
            if (!_index.TryGetValue(key, out var bucket)) _index.Add(key, bucket = new(ReferenceEqualityComparer<TRight>.Instance));
            bucket.Add(right);
            _keys[right] = key;
        }
        AddReverseReferences(right);
    }

    public void RemoveRight(object instance)
    {
        var right = (TRight)instance;
        RemoveFromIndex(right);
        RemoveReverseReferences(right);
    }

    public IReadOnlyList<TRight> Related(TLeft left)
    {
        IEnumerable<TRight> candidates;
        if (_definition.Analysis.JoinKeyParts.Count == 0)
        {
            candidates = _rightObjects.Instances.Cast<TRight>();
        }
        else
        {
            var key = new CompositeKey(_definition.Analysis.JoinKeyParts.Select(part => part.Left.Read(left)).ToArray());
            candidates = _index.TryGetValue(key, out var bucket) ? bucket : [];
        }
        return candidates.Where(right => _definition.Predicate(left, right)).ToArray();
    }

    public void Apply(PropertyChange change)
    {
        if (ReferenceEquals(change.Set, _definition.Right) && change.Instance is TRight right)
        {
            if (AffectsRightPath(change.Member)) Reindex(right);
        }

        if (_reverse.TryGetValue(change.Instance, out var roots) && AffectsNestedMember(change.Member))
        {
            foreach (var root in roots.ToArray()) Reindex(root);
        }
    }

    private bool AffectsRightPath(MemberInfo member) =>
        _definition.Analysis.JoinKeyParts.Any(part => part.Right.Members.Contains(member));

    private bool AffectsNestedMember(MemberInfo member) =>
        _definition.Analysis.JoinKeyParts.Any(part => part.Right.Members.Skip(1).Contains(member));

    private void Reindex(TRight right)
    {
        RemoveFromIndex(right);
        RemoveReverseReferences(right);
        AddRight(right);
    }

    private void RemoveFromIndex(TRight right)
    {
        if (!_keys.Remove(right, out var key)) return;
        if (_index.TryGetValue(key, out var bucket))
        {
            bucket.Remove(right);
            if (bucket.Count == 0) _index.Remove(key);
        }
    }

    private CompositeKey ReadRightKey(TRight right) =>
        new(_definition.Analysis.JoinKeyParts.Select(part => part.Right.Read(right)).ToArray());

    private void AddReverseReferences(TRight right)
    {
        foreach (var path in _definition.Analysis.JoinKeyParts.Select(part => part.Right).Where(path => path.Members.Count > 1))
        {
            var navigation = ReadMember(path.Members[0], right);
            if (navigation is null) continue;
            if (!_reverse.TryGetValue(navigation, out var roots)) _reverse.Add(navigation, roots = new(ReferenceEqualityComparer<TRight>.Instance));
            roots.Add(right);
        }
    }

    private void RemoveReverseReferences(TRight right)
    {
        foreach (var pair in _reverse.ToArray())
        {
            pair.Value.Remove(right);
            if (pair.Value.Count == 0) _reverse.Remove(pair.Key);
        }
    }

    private static object? ReadMember(MemberInfo member, object instance) => member switch
    {
        PropertyInfo property => property.GetValue(instance),
        FieldInfo field => field.GetValue(instance),
        _ => null
    };
}

internal readonly struct CompositeKey : IEquatable<CompositeKey>
{
    private readonly object?[] _components;
    public CompositeKey(object?[] components) => _components = components;
    public bool Equals(CompositeKey other) => _components.AsSpan().SequenceEqual(other._components);
    public override bool Equals(object? obj) => obj is CompositeKey other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in _components) hash.Add(component);
        return hash.ToHashCode();
    }
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static ReferenceEqualityComparer<T> Instance { get; } = new();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
