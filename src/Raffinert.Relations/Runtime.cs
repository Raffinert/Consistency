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
    internal RelationRuntime CreateRuntime(IDependencyImpactPolicy dependencyImpactPolicy) =>
        new(_sets, _relations, _derivedStates, _invariants, dependencyImpactPolicy);

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
            lines.Add($"  Reverse access plan: {relation.ReverseAccessPlan?.DisplayName ?? "Disabled"}");
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
    private readonly IDependencyImpactPolicy _dependencyImpactPolicy;

    internal RelationRuntime(
        IReadOnlyList<IObjectSetDefinition> sets,
        IReadOnlyList<IRelationDefinition> relations,
        IReadOnlyList<IDerivedDefinition> derivedStates,
        IReadOnlyList<IInvariantDefinition> invariants,
        IDependencyImpactPolicy? dependencyImpactPolicy = null)
    {
        _dependencyImpactPolicy = dependencyImpactPolicy ?? DefaultDependencyImpactPolicy.Instance;
        _sets = sets.ToDictionary(set => set, set => new ObjectSetRuntime(set));
        _relations = relations.ToDictionary(relation => relation, relation => relation.CreateState(_sets));
        foreach (var relation in derivedStates.Select(derived => derived.Relation).Distinct())
            _relations[relation].EnableExactPropagation();
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
        var rightRelations = _relations
            .Where(pair => ReferenceEquals(pair.Value.RightSet, definition))
            .ToArray();
        var leftRelations = _relations
            .Where(pair => ReferenceEquals(pair.Value.LeftSet, definition))
            .ToArray();
        var deltas = new Dictionary<IRelationDefinition, RelationDelta>();
        foreach (var relation in rightRelations)
            deltas[relation.Key] = relation.Value.AddRight(instance);
        foreach (var relation in leftRelations)
            MergeDelta(deltas, relation.Key, relation.Value.AddLeft(instance));
        InvalidateForRelationMutations(deltas, []);
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
        var rightRelations = _relations
            .Where(pair => ReferenceEquals(pair.Value.RightSet, definition))
            .ToArray();
        var leftRelations = _relations
            .Where(pair => ReferenceEquals(pair.Value.LeftSet, definition))
            .ToArray();
        var deltas = new Dictionary<IRelationDefinition, RelationDelta>();
        foreach (var relation in rightRelations)
            deltas[relation.Key] = relation.Value.RemoveRight(instance);
        foreach (var relation in leftRelations)
            MergeDelta(deltas, relation.Key, relation.Value.RemoveLeft(instance));
        _navigation.RemoveRoot(definition, instance);
        var removed = state.Remove(instance);
        InvalidateForRelationMutations(deltas, []);
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
        foreach (var pair in impact.ReindexLeftRoots)
            foreach (var root in pair.Value)
                pair.Key.ReindexLeft(root);
        var relationDeltas = ResolveRelationDeltas(impact);
        ApplyDerivedAndInvariantImpact(impact, relationDeltas, changes);
        return impact.ToPublic();
    }

    private IReadOnlyDictionary<IRelationDefinition, RelationDelta> ResolveRelationDeltas(
        ResolvedChangeImpact impact)
    {
        var deltas = new Dictionary<IRelationDefinition, RelationDelta>();
        foreach (var relation in impact.AffectedRelations)
        {
            var state = _relations[relation];
            if (!state.HasExactPropagation)
                continue;
            var delta = state.RefreshMembership(
                impact.GetAffectedRoots(relation, relation.LeftSet),
                impact.GetAffectedRoots(relation, relation.RightSet));
            deltas.Add(relation, delta);
        }
        return deltas;
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
        IReadOnlyDictionary<IRelationDefinition, RelationDelta> relationDeltas,
        IReadOnlyList<PropertyChange> changes)
    {
        var affectedDerived = new Dictionary<IDerivedDefinition, (HashSet<object> Invalid, HashSet<object> Dirty)>();
        foreach (var pair in _derivedStates)
        {
            relationDeltas.TryGetValue(pair.Key.Relation, out var relationDelta);
            var membershipRoots = pair.Key.Analysis.HasRelationMembershipDependency
                ? relationDelta?.AffectedLefts ?? []
                : [];
            var sourceRoots = ResolveDependencyRoots(
                pair.Key.SourceSet,
                pair.Key.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.DerivedSource),
                changes);
            var itemRoots = ResolveDependencyRoots(
                pair.Key.Relation.RightSet,
                pair.Key.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.RelationItem),
                changes);
            var relationState = _relations[pair.Key.Relation];
            var itemSources = relationState.GetLeftsForRights(itemRoots);
            var membershipIsInvalid = membershipRoots.Count > 0 &&
                _dependencyImpactPolicy.Classify(new RelationMembershipDependencyImpact(
                    pair.Key.Relation,
                    pair.Key,
                    relationDelta!,
                    changes)) ==
                DependencyImpactKind.Invalid;
            var invalidSources = membershipIsInvalid
                ? new HashSet<object>(membershipRoots, ReferenceEqualityComparer.Instance)
                : new HashSet<object>(ReferenceEqualityComparer.Instance);
            var dirtySources = new HashSet<object>(sourceRoots, ReferenceEqualityComparer.Instance);
            dirtySources.UnionWith(itemSources);
            if (!membershipIsInvalid)
                dirtySources.UnionWith(membershipRoots);
            dirtySources.ExceptWith(invalidSources);
            if (invalidSources.Count > 0)
                pair.Value.Invalidate(invalidSources, invalid: true);
            if (dirtySources.Count > 0)
                pair.Value.Invalidate(dirtySources, invalid: false);
            if (invalidSources.Count > 0 || dirtySources.Count > 0)
            {
                affectedDerived[pair.Key] = (invalidSources, dirtySources);
            }
        }

        foreach (var pair in _invariants)
        {
            if (affectedDerived.TryGetValue(pair.Key.Derived, out var inherited))
            {
                if (inherited.Invalid.Count > 0)
                    pair.Value.OnDependencyChanged(inherited.Invalid, invalid: true);
                if (inherited.Dirty.Count > 0)
                    pair.Value.OnDependencyChanged(inherited.Dirty, invalid: false);
            }

            var invariantRoots = ResolveDependencyRoots(
                pair.Key.Derived.SourceSet,
                pair.Key.Analysis.Dependencies.Where(dependency => dependency.Role == ExpressionParameterRole.InvariantSource),
                changes);
            if (invariantRoots.Count > 0)
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

    private void InvalidateForRelationMutations(
        IReadOnlyDictionary<IRelationDefinition, RelationDelta> deltas,
        IReadOnlyList<PropertyChange> changes)
    {
        var affectedDerived = _derivedStates
            .Where(pair => pair.Key.Analysis.HasRelationMembershipDependency &&
                deltas.TryGetValue(pair.Key.Relation, out var delta) && delta.AffectedLefts.Count > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => _dependencyImpactPolicy.Classify(new RelationMembershipDependencyImpact(
                    pair.Key.Relation,
                    pair.Key,
                    deltas[pair.Key.Relation],
                    changes)));
        foreach (var pair in affectedDerived)
            _derivedStates[pair.Key].Invalidate(
                deltas[pair.Key.Relation].AffectedLefts,
                pair.Value == DependencyImpactKind.Invalid);
        foreach (var pair in _invariants)
        {
            if (affectedDerived.TryGetValue(pair.Key.Derived, out var severity))
            {
                var delta = deltas[pair.Key.Derived.Relation];
                pair.Value.OnDependencyChanged(
                    delta.AffectedLefts,
                    severity == DependencyImpactKind.Invalid);
            }
        }
    }

    private static void MergeDelta(
        IDictionary<IRelationDefinition, RelationDelta> deltas,
        IRelationDefinition relation,
        RelationDelta delta)
    {
        if (deltas.TryGetValue(relation, out var existing))
            existing.MergeFrom(delta);
        else
            deltas.Add(relation, delta);
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
    IObjectSetDefinition LeftSet { get; }
    IObjectSetDefinition RightSet { get; }
    bool HasExactPropagation { get; }
    void EnableExactPropagation();
    RelationDelta AddLeft(object instance);
    RelationDelta RemoveLeft(object instance);
    RelationDelta AddRight(object instance);
    RelationDelta RemoveRight(object instance);
    void ReindexRight(object instance);
    void ReindexLeft(object instance);
    RelationDelta RefreshMembership(IEnumerable<object> lefts, IEnumerable<object> rights);
    IReadOnlyCollection<object> GetLeftsForRights(IEnumerable<object> rights);
}

internal sealed record RelationPair(object Left, object Right);

internal sealed class RelationDelta
{
    private readonly List<RelationPair> _addedPairs = [];
    private readonly List<RelationPair> _removedPairs = [];
    private readonly HashSet<object> _affectedLefts = new(ReferenceEqualityComparer.Instance);

    public IReadOnlyList<RelationPair> AddedPairs => _addedPairs;
    public IReadOnlyList<RelationPair> RemovedPairs => _removedPairs;
    public IReadOnlyCollection<object> AffectedLefts => _affectedLefts;

    public void Add(object left, object right)
    {
        _addedPairs.Add(new RelationPair(left, right));
        _affectedLefts.Add(left);
    }

    public void Remove(object left, object right)
    {
        _removedPairs.Add(new RelationPair(left, right));
        _affectedLefts.Add(left);
    }

    public void MergeFrom(RelationDelta other)
    {
        _addedPairs.AddRange(other._addedPairs);
        _removedPairs.AddRange(other._removedPairs);
        _affectedLefts.UnionWith(other._affectedLefts);
    }
}

internal sealed class RelationRuntimeState<TLeft, TRight> : IRelationRuntimeState
    where TLeft : class where TRight : class
{
    private readonly RelationDefinition<TLeft, TRight> _definition;
    private readonly ObjectSetRuntime _leftObjects;
    private readonly ObjectSetRuntime _rightObjects;
    private readonly Dictionary<CompositeKey, HashSet<TRight>> _index = [];
    private readonly Dictionary<TRight, CompositeKey> _keys = new(ReferenceEqualityComparer<TRight>.Instance);
    private readonly Dictionary<CompositeKey, HashSet<TLeft>> _leftIndex = [];
    private readonly Dictionary<TLeft, CompositeKey> _leftKeys = new(ReferenceEqualityComparer<TLeft>.Instance);
    private readonly Dictionary<TLeft, HashSet<TRight>> _rightsByLeft = new(ReferenceEqualityComparer<TLeft>.Instance);
    private readonly Dictionary<TRight, HashSet<TLeft>> _leftsByRight = new(ReferenceEqualityComparer<TRight>.Instance);
    private bool _hasExactPropagation;

    public RelationRuntimeState(
        RelationDefinition<TLeft, TRight> definition,
        ObjectSetRuntime leftObjects,
        ObjectSetRuntime rightObjects)
    {
        _definition = definition;
        _leftObjects = leftObjects;
        _rightObjects = rightObjects;
    }

    public IObjectSetDefinition LeftSet => _definition.Left;
    public IObjectSetDefinition RightSet => _definition.Right;
    public bool HasExactPropagation => _hasExactPropagation;

    public void EnableExactPropagation() => _hasExactPropagation = true;

    public RelationDelta AddLeft(object instance)
    {
        var delta = new RelationDelta();
        if (!_hasExactPropagation)
            return delta;
        var left = (TLeft)instance;
        AddLeftToIndex(left);
        foreach (var right in Related(left))
            AddPair(left, right, delta);
        return delta;
    }

    public RelationDelta RemoveLeft(object instance)
    {
        var delta = new RelationDelta();
        if (!_hasExactPropagation)
            return delta;
        var left = (TLeft)instance;
        RemoveLeftFromIndex(left);
        if (!_rightsByLeft.Remove(left, out var rights))
            return delta;
        foreach (var right in rights)
        {
            RemoveReversePair(left, right);
            delta.Remove(left, right);
        }
        return delta;
    }

    public RelationDelta AddRight(object instance)
    {
        var delta = new RelationDelta();
        var right = (TRight)instance;
        AddToIndex(right);
        if (_hasExactPropagation)
            foreach (var left in RelatedFromRightCore(right))
                AddPair(left, right, delta);
        return delta;
    }

    public RelationDelta RemoveRight(object instance)
    {
        var delta = new RelationDelta();
        var right = (TRight)instance;
        if (_hasExactPropagation && _leftsByRight.Remove(right, out var lefts))
            foreach (var left in lefts)
            {
                _rightsByLeft[left].Remove(right);
                if (_rightsByLeft[left].Count == 0)
                    _rightsByLeft.Remove(left);
                delta.Remove(left, right);
            }
        RemoveFromIndex(right);
        return delta;
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

    public IReadOnlyList<TLeft> RelatedFromRight(TRight right) => RelatedFromRightCore(right);

    public void ReindexRight(object instance) => Reindex((TRight)instance);

    public void ReindexLeft(object instance)
    {
        if (!_hasExactPropagation)
            return;
        var left = (TLeft)instance;
        RemoveLeftFromIndex(left);
        AddLeftToIndex(left);
    }

    public RelationDelta RefreshMembership(IEnumerable<object> lefts, IEnumerable<object> rights)
    {
        var delta = new RelationDelta();
        if (!_hasExactPropagation)
            return delta;

        var typedLefts = lefts.Cast<TLeft>().ToHashSet(ReferenceEqualityComparer<TLeft>.Instance);
        var typedRights = rights.Cast<TRight>().ToHashSet(ReferenceEqualityComparer<TRight>.Instance);
        foreach (var left in typedLefts)
        {
            var oldRights = _rightsByLeft.TryGetValue(left, out var existing)
                ? existing.ToHashSet(ReferenceEqualityComparer<TRight>.Instance)
                : new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance);
            var newRights = Related(left).ToHashSet(ReferenceEqualityComparer<TRight>.Instance);
            foreach (var right in oldRights.Except(newRights, ReferenceEqualityComparer<TRight>.Instance).ToArray())
                RemovePair(left, right, delta);
            foreach (var right in newRights.Except(oldRights, ReferenceEqualityComparer<TRight>.Instance))
                AddPair(left, right, delta);
        }

        foreach (var right in typedRights)
        {
            var oldLefts = _leftsByRight.TryGetValue(right, out var existing)
                ? existing.ToHashSet(ReferenceEqualityComparer<TLeft>.Instance)
                : new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance);
            var newLefts = RelatedFromRightCore(right)
                .ToHashSet(ReferenceEqualityComparer<TLeft>.Instance);
            foreach (var left in oldLefts.Except(newLefts, ReferenceEqualityComparer<TLeft>.Instance).ToArray())
                RemovePair(left, right, delta);
            foreach (var left in newLefts.Except(oldLefts, ReferenceEqualityComparer<TLeft>.Instance))
                AddPair(left, right, delta);
        }
        return delta;
    }

    public IReadOnlyCollection<object> GetLeftsForRights(IEnumerable<object> rights)
    {
        var lefts = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!_hasExactPropagation)
            return lefts;
        foreach (var right in rights.Cast<TRight>())
            if (_leftsByRight.TryGetValue(right, out var related))
                lefts.UnionWith(related);
        return lefts;
    }

    private void Reindex(TRight right)
    {
        RemoveFromIndex(right);
        AddToIndex(right);
    }

    private void AddToIndex(TRight right)
    {
        if (_definition.AccessPlan is not HashJoinAccessPlan hashPlan)
            return;
        var key = ReadRightKey(hashPlan, right);
        if (!_index.TryGetValue(key, out var bucket))
            _index.Add(key, bucket = new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance));
        bucket.Add(right);
        _keys[right] = key;
    }

    private void AddLeftToIndex(TLeft left)
    {
        if (_definition.ReverseAccessPlan is not HashJoinAccessPlan hashPlan)
            return;
        var key = ReadLeftKey(hashPlan, left);
        if (!_leftIndex.TryGetValue(key, out var bucket))
            _leftIndex.Add(key, bucket = new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance));
        bucket.Add(left);
        _leftKeys[left] = key;
    }

    private void RemoveLeftFromIndex(TLeft left)
    {
        if (!_leftKeys.Remove(left, out var key))
            return;
        if (!_leftIndex.TryGetValue(key, out var bucket))
            return;
        bucket.Remove(left);
        if (bucket.Count == 0)
            _leftIndex.Remove(key);
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

    private void AddPair(TLeft left, TRight right, RelationDelta delta)
    {
        if (!_rightsByLeft.TryGetValue(left, out var rights))
            _rightsByLeft.Add(left, rights = new HashSet<TRight>(ReferenceEqualityComparer<TRight>.Instance));
        if (!rights.Add(right))
            return;
        if (!_leftsByRight.TryGetValue(right, out var lefts))
            _leftsByRight.Add(right, lefts = new HashSet<TLeft>(ReferenceEqualityComparer<TLeft>.Instance));
        lefts.Add(left);
        delta.Add(left, right);
    }

    private void RemovePair(TLeft left, TRight right, RelationDelta delta)
    {
        if (!_rightsByLeft.TryGetValue(left, out var rights) || !rights.Remove(right))
            return;
        if (rights.Count == 0)
            _rightsByLeft.Remove(left);
        RemoveReversePair(left, right);
        delta.Remove(left, right);
    }

    private void RemoveReversePair(TLeft left, TRight right)
    {
        if (!_leftsByRight.TryGetValue(right, out var lefts))
            return;
        lefts.Remove(left);
        if (lefts.Count == 0)
            _leftsByRight.Remove(right);
    }

    private IEnumerable<TRight> ReadHashCandidates(HashJoinAccessPlan plan, TLeft left)
    {
        var key = CreateKey(plan.JoinKeyParts.Select(part => part.Left.Read(left)), plan);
        return _index.TryGetValue(key, out var bucket) ? bucket : [];
    }

    private IReadOnlyList<TLeft> RelatedFromRightCore(TRight right)
    {
        IEnumerable<TLeft> candidates = _definition.ReverseAccessPlan switch
        {
            null or ScanAccessPlan => _leftObjects.Instances.Cast<TLeft>(),
            HashJoinAccessPlan hashPlan => ReadReverseHashCandidates(hashPlan, right),
            _ => throw new NotSupportedException(
                $"Unsupported reverse access plan '{_definition.ReverseAccessPlan.GetType().Name}'.")
        };
        return candidates.Where(left => _definition.Predicate(left, right)).ToArray();
    }

    private IEnumerable<TLeft> ReadReverseHashCandidates(HashJoinAccessPlan plan, TRight right)
    {
        var key = ReadRightKey(plan, right);
        return _leftIndex.TryGetValue(key, out var bucket) ? bucket : [];
    }

    private static CompositeKey ReadLeftKey(HashJoinAccessPlan plan, TLeft left) =>
        CreateKey(plan.JoinKeyParts.Select(part => part.Left.Read(left)), plan);

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
