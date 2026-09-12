using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

public sealed class CompiledRelationModel
{
    private readonly IReadOnlyList<IObjectSetDefinition> _sets;
    private readonly IReadOnlyList<IRelationDefinition> _relations;
    private readonly IReadOnlyList<IDerivedDefinition> _derivedStates;
    private readonly IReadOnlyList<IInvariantDefinition> _invariants;

    internal CompiledRelationModel(
        IReadOnlyList<IObjectSetDefinition> sets,
        IReadOnlyList<IRelationDefinition> relations,
        IReadOnlyList<IDerivedDefinition> derivedStates,
        IReadOnlyList<IInvariantDefinition> invariants)
    {
        _sets = sets;
        _relations = relations;
        _derivedStates = derivedStates;
        _invariants = invariants;
        DebugView = CreateDebugView();
    }

    public string DebugView { get; }
    public RelationRuntime CreateRuntime() => new(_sets, _relations, _derivedStates, _invariants);

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
            foreach (var dependency in relation.Analysis.DependencyPaths)
                lines.Add($"    {dependency.DisplayName}");
            lines.Add("  Join key:");
            foreach (var key in relation.Analysis.JoinKeyParts)
                lines.Add($"    {key.Left.DisplayName} <-> {key.Right.DisplayName} ({key.EqualitySemantics})");
            lines.Add($"  Access plan: {relation.AccessPlan.DisplayName}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(relation.Analysis.DependencyAnalysis)}");
            lines.Add($"  Residual predicate: {(relation.Analysis.HasResidualPredicate ? "Yes" : "No")}");
            foreach (var residual in relation.Analysis.RecognizedResiduals)
                lines.Add($"    {residual}");
            var navigationEdges = relation.Analysis.DependencyPaths
                .SelectMany(path => path.Segments.Take(Math.Max(0, path.Segments.Count - 1)))
                .Where(segment => !segment.ValueType.IsValueType && segment.ValueType != typeof(string))
                .Select(segment => $"{segment.DeclaringType.Name}.{segment.Member.Name}")
                .Distinct()
                .ToArray();
            if (navigationEdges.Length > 0)
            {
                lines.Add("  Navigation indexes:");
                foreach (var edge in navigationEdges)
                    lines.Add($"    {edge} (shared reverse tracked)");
            }
        }
        foreach (var derived in _derivedStates)
        {
            lines.Add($"Derived {derived.SourceSet.ObjectType.Name} using {derived.Relation.RightSet.ObjectType.Name}: {derived.ComputationExpression.Body}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(derived.Analysis.Flags)}");
            lines.Add($"  Relation membership: {(derived.Analysis.HasRelationMembershipDependency ? "Yes" : "No")}");
            foreach (var dependency in derived.Analysis.Dependencies)
                lines.Add($"  {dependency.Role}: {dependency.Path.DisplayName}");
        }
        foreach (var invariant in _invariants)
        {
            lines.Add($"Invariant {invariant.Derived.SourceSet.ObjectType.Name}");
            lines.Add($"  Dependency analysis: {FormatDependencyAnalysis(invariant.Analysis.Flags)}");
            foreach (var dependency in invariant.Analysis.Dependencies)
                lines.Add($"  {dependency.Role}: {dependency.Path.DisplayName}");
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDependencyAnalysis(DependencyAnalysisFlags flags) =>
        flags == DependencyAnalysisFlags.Complete
            ? nameof(DependencyAnalysisFlags.Complete)
            : string.Join(", ", Enum.GetValues<DependencyAnalysisFlags>()
                .Where(flag => flag != DependencyAnalysisFlags.Complete && flags.HasFlag(flag)));

    private static string GetMemberName(Expression? expression)
    {
        while (expression is UnaryExpression unary) expression = unary.Operand;
        return expression is MemberExpression member ? member.Member.Name : expression?.ToString() ?? "<missing>";
    }
}

/// <summary>
/// Stores and queries the runtime state of a compiled relation model. This type is not thread-safe;
/// mutations and queries must be externally synchronized.
/// </summary>
public sealed class RelationRuntime
{
    private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
    private readonly IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> _relations;
    private readonly NavigationIndexRegistry _navigation;
    private readonly ImpactResolver _impactResolver;
    private readonly IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> _derivedStates;
    private readonly IReadOnlyDictionary<IInvariantDefinition, IInvariantRuntimeState> _invariants;

    internal RelationRuntime(
        IReadOnlyList<IObjectSetDefinition> sets,
        IReadOnlyList<IRelationDefinition> relations,
        IReadOnlyList<IDerivedDefinition> derivedStates,
        IReadOnlyList<IInvariantDefinition> invariants)
    {
        _sets = sets.ToDictionary(set => set, set => new ObjectSetRuntime(set));
        _relations = relations.ToDictionary(relation => relation, relation => relation.CreateState(_sets));
        _navigation = new NavigationIndexRegistry(sets, relations, derivedStates, invariants, _sets);
        _impactResolver = new ImpactResolver(relations, _relations, _navigation);
        _derivedStates = derivedStates.ToDictionary(
            definition => definition,
            definition => definition.CreateState(_relations[definition.Relation]));
        _invariants = invariants.ToDictionary(
            definition => definition,
            definition => definition.CreateState(
                _derivedStates[definition.Derived],
                _sets[definition.Derived.SourceSet]));
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
        _navigation.AddRoot(definition, instance);
        var affectedRelations = _relations
            .Where(pair => ReferenceEquals(pair.Value.RightSet, definition))
            .ToArray();
        foreach (var relation in affectedRelations.Select(pair => pair.Value))
            relation.AddRight(instance);
        InvalidateForRelationMutations(affectedRelations.Select(pair => pair.Key));
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
        var affectedRelations = _relations
            .Where(pair => ReferenceEquals(pair.Value.RightSet, definition))
            .ToArray();
        foreach (var relation in affectedRelations.Select(pair => pair.Value))
            relation.RemoveRight(instance);
        _navigation.RemoveRoot(definition, instance);
        var removed = state.Remove(instance);
        InvalidateForRelationMutations(affectedRelations.Select(pair => pair.Key));
        return removed;
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

    public IReadOnlyList<TRight> RelatedFromLeft<TLeft, TRight>(Relation<TLeft, TRight> relation, TLeft left)
        where TLeft : class where TRight : class => Related(relation, left);

    public IReadOnlyList<TLeft> RelatedFromRight<TLeft, TRight>(Relation<TLeft, TRight> relation, TRight right)
        where TLeft : class where TRight : class
    {
        ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(right);
        if (!_relations.TryGetValue(relation.Definition, out var state))
            throw new ArgumentException("The relation does not belong to this compiled model.", nameof(relation));
        if (!_sets[relation.Definition.Right].Contains(right))
            throw new InvalidOperationException("The right instance is not registered in the relation's object set.");
        return ((RelationRuntimeState<TLeft, TRight>)state).RelatedFromRight(right);
    }

    public TValue Get<TSource, TItem, TValue>(Derived<TSource, TItem, TValue> derived, TSource source)
        where TSource : class where TItem : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        if (!_derivedStates.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived state does not belong to this compiled model.", nameof(derived));
        if (!_sets[derived.Definition.SourceSet].Contains(source))
            throw new InvalidOperationException("The source instance is not registered in the derived state's object set.");
        return ((DerivedRuntimeState<TSource, TItem, TValue>)state).Get(source);
    }

    public DerivedValueState GetState<TSource, TItem, TValue>(Derived<TSource, TItem, TValue> derived, TSource source)
        where TSource : class where TItem : class
    {
        ArgumentNullException.ThrowIfNull(derived);
        ArgumentNullException.ThrowIfNull(source);
        if (!_derivedStates.TryGetValue(derived.Definition, out var state))
            throw new ArgumentException("The derived state does not belong to this compiled model.", nameof(derived));
        return ((DerivedRuntimeState<TSource, TItem, TValue>)state).GetState(source);
    }

    public bool Evaluate<TSource, TItem, TValue>(Invariant<TSource, TItem, TValue> invariant, TSource source)
        where TSource : class where TItem : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(source);
        if (!_invariants.TryGetValue(invariant.Definition, out var state))
            throw new ArgumentException("The invariant does not belong to this compiled model.", nameof(invariant));
        return ((InvariantRuntimeState<TSource, TItem, TValue>)state).Evaluate(source);
    }

    public InvariantEvaluationState GetState<TSource, TItem, TValue>(Invariant<TSource, TItem, TValue> invariant, TSource source)
        where TSource : class where TItem : class
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(source);
        if (!_invariants.TryGetValue(invariant.Definition, out var state))
            throw new ArgumentException("The invariant does not belong to this compiled model.", nameof(invariant));
        return ((InvariantRuntimeState<TSource, TItem, TValue>)state).GetState(source);
    }

    public ChangeImpact Apply(PropertyChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Apply(ChangeSet.Create(change));
    }

    public ChangeImpact Apply(ChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        var changes = changeSet.Changes.Select(ValidateChange).ToArray();
        var impact = new ResolvedChangeImpact();
        foreach (var change in changes)
            impact.MergeFrom(_impactResolver.Resolve(change));

        foreach (var (rootSet, root) in ResolveNavigationRoots(impact, changes))
            _navigation.RefreshRoot(rootSet, root);
        foreach (var pair in impact.ReindexRoots)
            foreach (var root in pair.Value)
                pair.Key.ReindexRight(root);
        ApplyDerivedAndInvariantImpact(impact, changes);
        return impact.ToPublic();
    }

    private IEnumerable<(IObjectSetDefinition Set, object Root)> ResolveNavigationRoots(
        ResolvedChangeImpact impact,
        IReadOnlyList<PropertyChange> changes)
    {
        var rootsBySet = _sets.Keys.ToDictionary(
            set => set,
            _ => new HashSet<object>(ReferenceEqualityComparer.Instance));
        foreach (var (set, root) in impact.AffectedRoots)
            rootsBySet[set].Add(root);

        foreach (var derived in _derivedStates.Keys)
        {
            AddRoots(
                derived.SourceSet,
                derived.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.DerivedSource));
            AddRoots(
                derived.Relation.RightSet,
                derived.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.RelationItem));
        }
        foreach (var invariant in _invariants.Keys)
            AddRoots(
                invariant.Derived.SourceSet,
                invariant.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.InvariantSource));

        return rootsBySet.SelectMany(pair => pair.Value.Select(root => (pair.Key, root))).ToArray();

        void AddRoots(IObjectSetDefinition rootSet, IEnumerable<TrackedExpressionDependency> dependencies)
        {
            foreach (var dependency in dependencies)
                foreach (var change in changes)
                    rootsBySet[rootSet].UnionWith(
                        _navigation.ResolveRoots(rootSet, dependency.Path, change.Instance, change.Member));
        }
    }

    private void ApplyDerivedAndInvariantImpact(
        ResolvedChangeImpact impact,
        IReadOnlyList<PropertyChange> changes)
    {
        var affectedDerived = new Dictionary<IDerivedDefinition, (bool Invalid, IReadOnlyCollection<object>? Sources)>();
        foreach (var pair in _derivedStates)
        {
            var relationAffected = pair.Key.Analysis.HasRelationMembershipDependency &&
                impact.AffectedRelations.Contains(pair.Key.Relation);
            var sourceRoots = ResolveDependencyRoots(
                pair.Key.SourceSet,
                pair.Key.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.DerivedSource),
                changes);
            var itemRoots = ResolveDependencyRoots(
                pair.Key.Relation.RightSet,
                pair.Key.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.RelationItem),
                changes);
            var relationState = _relations[pair.Key.Relation];
            var invalid = relationAffected &&
                impact.ReindexRoots.TryGetValue(relationState, out var roots) && roots.Count > 0;
            if (relationAffected || itemRoots.Count > 0)
            {
                pair.Value.Invalidate(invalid);
                affectedDerived[pair.Key] = (invalid, null);
            }
            else if (sourceRoots.Count > 0)
            {
                pair.Value.Invalidate(sourceRoots, invalid: false);
                affectedDerived[pair.Key] = (false, sourceRoots);
            }
        }

        foreach (var pair in _invariants)
        {
            if (affectedDerived.TryGetValue(pair.Key.Derived, out var inherited))
            {
                if (inherited.Sources is null)
                    pair.Value.OnDependencyChanged(inherited.Invalid);
                else
                    pair.Value.OnDependencyChanged(inherited.Sources, inherited.Invalid);
            }

            var invariantRoots = ResolveDependencyRoots(
                pair.Key.Derived.SourceSet,
                pair.Key.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.InvariantSource),
                changes);
            if (invariantRoots.Count > 0 &&
                (!affectedDerived.TryGetValue(pair.Key.Derived, out inherited) || inherited.Sources is not null))
                pair.Value.OnDependencyChanged(invariantRoots, invalid: false);
        }
    }

    private HashSet<object> ResolveDependencyRoots(
        IObjectSetDefinition rootSet,
        IEnumerable<TrackedExpressionDependency> dependencies,
        IReadOnlyList<PropertyChange> changes)
    {
        var roots = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var dependency in dependencies)
            foreach (var change in changes)
                roots.UnionWith(_navigation.ResolveRoots(rootSet, dependency.Path, change.Instance, change.Member));
        return roots;
    }

    private void InvalidateForRelationMutations(IEnumerable<IRelationDefinition> relations)
    {
        var affectedRelations = relations.ToHashSet();
        if (affectedRelations.Count == 0)
            return;
        var affectedDerived = _derivedStates
            .Where(pair => affectedRelations.Contains(pair.Key.Relation))
            .ToArray();
        foreach (var pair in affectedDerived)
            pair.Value.Invalidate(invalid: true);
        foreach (var pair in _invariants)
            if (affectedDerived.Any(derived => ReferenceEquals(derived.Key, pair.Key.Derived)))
                pair.Value.OnDependencyChanged(invalid: true);
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
    private readonly Dictionary<object, object> _registeredKeys = new(ReferenceEqualityComparer.Instance);

    public ObjectSetRuntime(IObjectSetDefinition definition) => _definition = definition;
    public IEnumerable<object> Instances => _instances;
    public bool Contains(object instance) => _instances.Contains(instance);

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
}

internal interface IRelationRuntimeState
{
    IObjectSetDefinition RightSet { get; }
    void AddRight(object instance);
    void RemoveRight(object instance);
    void ReindexRight(object instance);
}

internal sealed class RelationRuntimeState<TLeft, TRight> : IRelationRuntimeState
    where TLeft : class where TRight : class
{
    private readonly RelationDefinition<TLeft, TRight> _definition;
    private readonly ObjectSetRuntime _leftObjects;
    private readonly ObjectSetRuntime _rightObjects;
    private readonly Dictionary<CompositeKey, HashSet<TRight>> _index = [];
    private readonly Dictionary<TRight, CompositeKey> _keys = new(ReferenceEqualityComparer<TRight>.Instance);

    public RelationRuntimeState(
        RelationDefinition<TLeft, TRight> definition,
        ObjectSetRuntime leftObjects,
        ObjectSetRuntime rightObjects)
    {
        _definition = definition;
        _leftObjects = leftObjects;
        _rightObjects = rightObjects;
    }

    public IObjectSetDefinition RightSet => _definition.Right;

    public void AddRight(object instance)
    {
        var right = (TRight)instance;
        if (_definition.AccessPlan is HashJoinAccessPlan hashPlan)
        {
            var key = ReadRightKey(hashPlan, right);
            if (!_index.TryGetValue(key, out var bucket)) _index.Add(key, bucket = new(ReferenceEqualityComparer<TRight>.Instance));
            bucket.Add(right);
            _keys[right] = key;
        }
    }

    public void RemoveRight(object instance)
    {
        var right = (TRight)instance;
        RemoveFromIndex(right);
    }

    public IReadOnlyList<TRight> Related(TLeft left)
    {
        IEnumerable<TRight> candidates = _definition.AccessPlan switch
        {
            ScanAccessPlan => _rightObjects.Instances.Cast<TRight>(),
            HashJoinAccessPlan hashPlan => ReadHashCandidates(hashPlan, left),
            _ => throw new NotSupportedException($"Unsupported access plan '{_definition.AccessPlan.GetType().Name}'.")
        };
        return candidates.Where(right => _definition.Predicate(left, right)).ToArray();
    }

    public IReadOnlyList<TLeft> RelatedFromRight(TRight right) =>
        _leftObjects.Instances.Cast<TLeft>().Where(left => _definition.Predicate(left, right)).ToArray();

    public void ReindexRight(object instance) => Reindex((TRight)instance);

    private void Reindex(TRight right)
    {
        RemoveFromIndex(right);
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

    private IEnumerable<TRight> ReadHashCandidates(HashJoinAccessPlan plan, TLeft left)
    {
        var key = CreateKey(plan.JoinKeyParts.Select(part => part.Left.Read(left)), plan);
        return _index.TryGetValue(key, out var bucket) ? bucket : [];
    }

    private static CompositeKey ReadRightKey(HashJoinAccessPlan plan, TRight right) =>
        CreateKey(plan.JoinKeyParts.Select(part => part.Right.Read(right)), plan);

    private static CompositeKey CreateKey(IEnumerable<object?> components, HashJoinAccessPlan plan) =>
        new(components.ToArray(), plan.JoinKeyParts.Select(part => part.Comparer).ToArray());

}

internal readonly struct CompositeKey : IEquatable<CompositeKey>
{
    private readonly object?[] _components;
    private readonly IReadOnlyList<IEqualityComparer<object?>> _comparers;

    public CompositeKey(object?[] components, IReadOnlyList<IEqualityComparer<object?>> comparers)
    {
        _components = components;
        _comparers = comparers;
    }

    public bool Equals(CompositeKey other)
    {
        if (_components.Length != other._components.Length)
            return false;
        for (var index = 0; index < _components.Length; index++)
            if (!_comparers[index].Equals(_components[index], other._components[index]))
                return false;
        return true;
    }
    public override bool Equals(object? obj) => obj is CompositeKey other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var index = 0; index < _components.Length; index++)
        {
            var component = _components[index];
            hash.Add(component is null ? 0 : _comparers[index].GetHashCode(component!));
        }
        return hash.ToHashCode();
    }
}

internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
{
    public static ReferenceEqualityComparer<T> Instance { get; } = new();
    public bool Equals(T? x, T? y) => ReferenceEquals(x, y);
    public int GetHashCode(T obj) => RuntimeHelpers.GetHashCode(obj);
}
