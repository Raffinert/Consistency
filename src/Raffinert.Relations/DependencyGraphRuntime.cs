using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

internal sealed record DerivedImpactSnapshot(
    IDerivedDefinition Definition,
    DependencyImpactKind Severity,
    IReadOnlyCollection<object> Sources);

internal sealed record InvariantImpactSnapshot(
    IInvariantDefinition Definition,
    DependencyImpactKind Severity,
    IReadOnlyCollection<object> Sources);

/// <summary>
/// Propagates source-scoped dependency impacts after relation state has been updated. This is a
/// deliberately small graph of the node kinds the runtime currently supports, rather than a
/// general-purpose graph framework.
/// </summary>
internal sealed class DependencyGraphRuntime
{
    private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
    private readonly IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> _relations;
    private readonly NavigationIndexRegistry _navigation;
    private readonly IDependencyImpactPolicy _impactPolicy;
    private readonly IReadOnlyList<DerivedNode> _derivedNodes;
    private readonly IReadOnlyList<InvariantNode> _invariantNodes;

    public DependencyGraphRuntime(
        IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets,
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        NavigationIndexRegistry navigation,
        IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> derivedStates,
        IReadOnlyDictionary<IInvariantDefinition, IInvariantRuntimeState> invariants,
        IDependencyImpactPolicy impactPolicy)
    {
        _sets = sets;
        _relations = relations;
        _navigation = navigation;
        _impactPolicy = impactPolicy;
        var derivedByDefinition = derivedStates.ToDictionary(
            pair => pair.Key,
            pair => new DerivedNode(pair.Key, pair.Value));
        _derivedNodes = derivedByDefinition.Values.ToArray();
        _invariantNodes = invariants
            .Select(pair => new InvariantNode(pair.Key, pair.Value, derivedByDefinition[pair.Key.Derived]))
            .ToArray();
    }

    public IReadOnlyCollection<(IObjectSetDefinition Set, object Root)> ResolveNavigationRoots(
        ResolvedChangeImpact impact,
        IReadOnlyList<PropertyChange> changes)
    {
        var rootsBySet = _sets.Keys.ToDictionary(
            set => set,
            _ => new HashSet<object>(ReferenceEqualityComparer.Instance));
        foreach (var (set, root) in impact.AffectedRoots)
            rootsBySet[set].Add(root);

        foreach (var node in _derivedNodes)
        {
            AddRoots(node.Definition.SourceSet, node.SourceDependencies);
            AddRoots(node.Definition.Relation.RightSet, node.ItemDependencies);
        }
        foreach (var node in _invariantNodes)
            AddRoots(node.Definition.Derived.SourceSet, node.SourceDependencies);

        return rootsBySet.SelectMany(pair => pair.Value.Select(root => (pair.Key, root))).ToArray();

        void AddRoots(IObjectSetDefinition rootSet, IEnumerable<TrackedExpressionDependency> dependencies)
        {
            foreach (var dependency in dependencies)
                foreach (var change in changes)
                    rootsBySet[rootSet].UnionWith(
                        _navigation.ResolveRoots(rootSet, dependency.Path, change.Instance, change.Member));
        }
    }

    public IReadOnlyList<DerivedImpactSnapshot> GetDerivedImpacts() => _derivedNodes
        .SelectMany(node =>
            Snapshot(node.Definition, node.InvalidSources, node.DirtySources))
        .ToArray();

    public IReadOnlyList<InvariantImpactSnapshot> GetInvariantImpacts() => _invariantNodes
        .SelectMany(node =>
            Snapshot(node.Definition, node.InvalidSources, node.DirtySources))
        .ToArray();

    public void ApplyChangeImpacts(
        IReadOnlyDictionary<IRelationDefinition, RelationImpact> relationImpacts,
        IReadOnlyList<PropertyChange> changes,
        RuntimePolicyActions policyActions)
    {
        foreach (var node in _derivedNodes)
        {
            relationImpacts.TryGetValue(node.Definition.Relation, out var relationImpact);
            var membershipRoots = node.Definition.Analysis.HasRelationMembershipDependency
                ? relationImpact?.AffectedLefts
                    .Where(_sets[node.Definition.SourceSet].Contains)
                    .ToArray() ?? []
                : [];
            var sourceRoots = ResolveRoots(node.Definition.SourceSet, node.SourceDependencies, changes);
            var itemRoots = ResolveRoots(node.Definition.Relation.RightSet, node.ItemDependencies, changes);
            var itemSources = _relations[node.Definition.Relation].GetLeftsForRights(itemRoots);
            var membershipSeverity = DependencyImpactKind.Dirty;
            if (membershipRoots.Length > 0)
                membershipSeverity = node.Definition.ImpactPolicy.IsConfigured
                    ? node.Definition.ImpactPolicy.ClassifyMembership(relationImpact!)
                    : _impactPolicy.Classify(new RelationMembershipDependencyImpact(
                        relationImpact!,
                        node.Definition,
                        changes));
            node.Apply(
                sourceRoots,
                itemSources,
                membershipRoots,
                membershipSeverity,
                node.Definition.ImpactPolicy.ItemChanged.ToKind(),
                relationImpact,
                changes);
        }

        foreach (var node in _invariantNodes)
        {
            node.ApplyInherited(policyActions);
            var invariantRoots = ResolveRoots(
                node.Definition.Derived.SourceSet,
                node.SourceDependencies,
                changes);
            if (invariantRoots.Count > 0)
                node.ApplyDirect(invariantRoots, policyActions);
        }
    }

    public void ApplyRelationImpacts(
        IReadOnlyDictionary<IRelationDefinition, RelationImpact> relationImpacts,
        IReadOnlyList<PropertyChange> changes,
        RuntimePolicyActions policyActions)
    {
        foreach (var node in _derivedNodes)
        {
            node.ClearImpact();
            if (!node.Definition.Analysis.HasRelationMembershipDependency ||
                !relationImpacts.TryGetValue(node.Definition.Relation, out var relationImpact) ||
                relationImpact.AffectedLefts.Count == 0)
                continue;
            var sources = relationImpact.AffectedLefts
                .Where(_sets[node.Definition.SourceSet].Contains)
                .ToHashSet(ReferenceEqualityComparer.Instance);
            if (sources.Count == 0)
                continue;
            var severity = node.Definition.ImpactPolicy.IsConfigured
                ? node.Definition.ImpactPolicy.ClassifyMembership(relationImpact)
                : _impactPolicy.Classify(new RelationMembershipDependencyImpact(
                    relationImpact,
                    node.Definition,
                    changes));
            node.Apply(sources, severity);
        }

        foreach (var node in _invariantNodes)
            node.ApplyInherited(policyActions);
    }

    private HashSet<object> ResolveRoots(
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

    private static IEnumerable<DerivedImpactSnapshot> Snapshot(
        IDerivedDefinition definition,
        IReadOnlyCollection<object> invalid,
        IReadOnlyCollection<object> dirty)
    {
        if (invalid.Count > 0)
            yield return new DerivedImpactSnapshot(definition, DependencyImpactKind.Invalid, invalid);
        if (dirty.Count > 0)
            yield return new DerivedImpactSnapshot(definition, DependencyImpactKind.Dirty, dirty);
    }

    private static IEnumerable<InvariantImpactSnapshot> Snapshot(
        IInvariantDefinition definition,
        IReadOnlyCollection<object> invalid,
        IReadOnlyCollection<object> dirty)
    {
        if (invalid.Count > 0)
            yield return new InvariantImpactSnapshot(definition, DependencyImpactKind.Invalid, invalid);
        if (dirty.Count > 0)
            yield return new InvariantImpactSnapshot(definition, DependencyImpactKind.Dirty, dirty);
    }

    private sealed class DerivedNode
    {
        public DerivedNode(IDerivedDefinition definition, IDerivedRuntimeState state)
        {
            Definition = definition;
            State = state;
            SourceDependencies = definition.Analysis.Dependencies
                .Where(dependency => dependency.Role == ExpressionParameterRole.DerivedSource)
                .ToArray();
            ItemDependencies = definition.Analysis.Dependencies
                .Where(dependency => dependency.Role == ExpressionParameterRole.RelationItem)
                .ToArray();
        }

        public IDerivedDefinition Definition { get; }
        public IDerivedRuntimeState State { get; }
        public IReadOnlyList<TrackedExpressionDependency> SourceDependencies { get; }
        public IReadOnlyList<TrackedExpressionDependency> ItemDependencies { get; }
        public HashSet<object> InvalidSources { get; private set; } = NewSet();
        public HashSet<object> DirtySources { get; private set; } = NewSet();

        public void Apply(
            IEnumerable<object> sourceRoots,
            IEnumerable<object> itemSources,
            IEnumerable<object> membershipRoots,
            DependencyImpactKind membershipSeverity,
            DependencyImpactKind itemSeverity,
            RelationImpact? relationImpact,
            IReadOnlyList<PropertyChange> changes)
        {
            InvalidSources = membershipSeverity == DependencyImpactKind.Invalid
                ? NewSet(membershipRoots)
                : NewSet();
            if (itemSeverity == DependencyImpactKind.Invalid)
                InvalidSources.UnionWith(itemSources);
            DirtySources = NewSet(sourceRoots);
            if (itemSeverity == DependencyImpactKind.Dirty)
                DirtySources.UnionWith(itemSources);
            if (membershipSeverity == DependencyImpactKind.Dirty)
                DirtySources.UnionWith(membershipRoots);
            DirtySources.ExceptWith(InvalidSources);
            var incrementallyUpdated = State.ApplyIncremental(DirtySources, relationImpact, changes);
            if (InvalidSources.Count > 0)
                State.ApplyImpact(InvalidSources, DependencyImpactKind.Invalid);
            if (DirtySources.Count > incrementallyUpdated.Count)
                State.ApplyImpact(
                    DirtySources.Where(source => !incrementallyUpdated.Contains(source)),
                    DependencyImpactKind.Dirty);
        }

        public void Apply(IEnumerable<object> sources, DependencyImpactKind severity)
        {
            ClearImpact();
            var affected = NewSet(sources);
            if (severity == DependencyImpactKind.Invalid)
                InvalidSources = affected;
            else
                DirtySources = affected;
            State.ApplyImpact(affected, severity);
        }

        public void ClearImpact()
        {
            InvalidSources = NewSet();
            DirtySources = NewSet();
        }

        private static HashSet<object> NewSet(IEnumerable<object>? values = null) =>
            values is null
                ? new HashSet<object>(ReferenceEqualityComparer.Instance)
                : new HashSet<object>(values, ReferenceEqualityComparer.Instance);
    }

    private sealed class InvariantNode
    {
        private readonly DerivedNode _derived;

        public InvariantNode(
            IInvariantDefinition definition,
            IInvariantRuntimeState state,
            DerivedNode derived)
        {
            Definition = definition;
            State = state;
            _derived = derived;
            SourceDependencies = definition.Analysis.Dependencies
                .Where(dependency => dependency.Role == ExpressionParameterRole.InvariantSource)
                .ToArray();
        }

        public IInvariantDefinition Definition { get; }
        public IInvariantRuntimeState State { get; }
        public IReadOnlyList<TrackedExpressionDependency> SourceDependencies { get; }
        public HashSet<object> InvalidSources { get; private set; } = NewSet();
        public HashSet<object> DirtySources { get; private set; } = NewSet();

        public void ApplyInherited(RuntimePolicyActions policyActions)
        {
            InvalidSources = NewSet(_derived.InvalidSources);
            DirtySources = NewSet(_derived.DirtySources);
            if (_derived.InvalidSources.Count > 0)
                State.ApplyImpact(_derived.InvalidSources, DependencyImpactKind.Invalid, policyActions);
            if (_derived.DirtySources.Count > 0)
                State.ApplyImpact(_derived.DirtySources, DependencyImpactKind.Dirty, policyActions);
        }

        public void ApplyDirect(IEnumerable<object> sources, RuntimePolicyActions policyActions)
        {
            var affected = NewSet(sources);
            DirtySources.UnionWith(affected);
            DirtySources.ExceptWith(InvalidSources);
            State.ApplyImpact(affected, DependencyImpactKind.Dirty, policyActions);
        }

        private static HashSet<object> NewSet(IEnumerable<object>? values = null) =>
            values is null
                ? new HashSet<object>(ReferenceEqualityComparer.Instance)
                : new HashSet<object>(values, ReferenceEqualityComparer.Instance);
    }
}
