using Raffinert.Relations.Expressions;
using System.Reflection;

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
    private readonly IReadOnlyDictionary<IRelationDefinition, IReadOnlyList<DerivedNode>> _derivedByRelation;
    private readonly IReadOnlyDictionary<MemberInfo, IReadOnlyList<DerivedNode>> _derivedByMember;
    private readonly IReadOnlyDictionary<DerivedNode, IReadOnlyList<InvariantNode>> _invariantsByDerived;
    private readonly IReadOnlyDictionary<MemberInfo, IReadOnlyList<InvariantNode>> _invariantsByMember;
    private HashSet<DerivedNode> _previousDerived = [];
    private HashSet<InvariantNode> _previousInvariants = [];

    public DependencyGraphRuntime(
        IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets,
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        NavigationIndexRegistry navigation,
        IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> derivedStates,
        IReadOnlyDictionary<IInvariantDefinition, IInvariantRuntimeState> invariants,
        CompiledDependencyGraph compiledGraph,
        IDependencyImpactPolicy impactPolicy)
    {
        _sets = sets;
        _relations = relations;
        _navigation = navigation;
        _impactPolicy = impactPolicy;
        var derivedByDefinition = derivedStates.ToDictionary(
            pair => pair.Key,
            pair => new DerivedNode(pair.Key, pair.Value));
        _derivedNodes = compiledGraph.Nodes.Where(node => node.Kind == DependencyNodeKind.Derived)
            .Select(node => derivedByDefinition[(IDerivedDefinition)node.Definition]).ToArray();
        _invariantNodes = compiledGraph.Nodes.Where(node => node.Kind == DependencyNodeKind.Invariant)
            .Select(node => (IInvariantDefinition)node.Definition)
            .Select(definition => new InvariantNode(
                definition,
                invariants[definition],
                derivedByDefinition[definition.Derived]))
            .ToArray();
        _derivedByRelation = Group(_derivedNodes.SelectMany(node => node.Definition.Inputs
            .Select(input => input.Relation)
            .OfType<IRelationDefinition>()
            .Select(relation => (relation, node))));
        _derivedByMember = Group(_derivedNodes.SelectMany(node =>
            node.SourceDependencies.Concat(node.ItemDependencies)
                .SelectMany(dependency => dependency.Path.Segments)
                .Select(segment => (segment.Member, node))));
        _invariantsByDerived = Group(_invariantNodes.Select(node => (node.Derived, node)));
        _invariantsByMember = Group(_invariantNodes.SelectMany(node =>
            node.SourceDependencies.SelectMany(dependency => dependency.Path.Segments)
                .Select(segment => (segment.Member, node))));
    }

    private static IReadOnlyDictionary<TKey, IReadOnlyList<TValue>> Group<TKey, TValue>(
        IEnumerable<(TKey Key, TValue Value)> values) where TKey : notnull =>
        values.GroupBy(value => value.Key)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TValue>)group.Select(value => value.Value).Distinct().ToArray());

    public IReadOnlyCollection<(IObjectSetDefinition Set, object Root)> ResolveNavigationRoots(
        ResolvedChangeImpact impact,
        IReadOnlyList<PropertyChange> changes)
    {
        var rootsBySet = _sets.Keys.ToDictionary(
            set => set,
            _ => new HashSet<object>(ReferenceEqualityComparer.Instance));
        foreach (var (set, root) in impact.AffectedRoots)
            rootsBySet[set].Add(root);

        foreach (var node in Candidates(changes, _derivedByMember))
        {
            AddRoots(node.Definition.SourceSet, node.SourceDependencies);
            if (node.Relation is not null)
                AddRoots(node.Relation.RightSet, node.ItemDependencies);
        }
        foreach (var node in Candidates(changes, _invariantsByMember))
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

    public object CaptureState(
        IReadOnlySet<IRelationDefinition> affectedRelations,
        IReadOnlyList<PropertyChange> changes)
    {
        var derived = new HashSet<DerivedNode>(_previousDerived);
        derived.UnionWith(Candidates(changes, _derivedByMember));
        foreach (var relation in affectedRelations)
            if (_derivedByRelation.TryGetValue(relation, out var nodes))
                derived.UnionWith(nodes);
        var invariants = new HashSet<InvariantNode>(_previousInvariants);
        invariants.UnionWith(Candidates(changes, _invariantsByMember));
        foreach (var node in derived)
            if (_invariantsByDerived.TryGetValue(node, out var nodes))
                invariants.UnionWith(nodes);
        return new State(
        derived.ToDictionary(node => node, node => node.CaptureState()),
        invariants.ToDictionary(node => node, node => node.CaptureState()),
        _previousDerived.ToArray(),
        _previousInvariants.ToArray());
    }

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        foreach (var pair in state.Derived)
            pair.Key.RestoreState(pair.Value);
        foreach (var pair in state.Invariants)
            pair.Key.RestoreState(pair.Value);
        _previousDerived = state.PreviousDerived.ToHashSet();
        _previousInvariants = state.PreviousInvariants.ToHashSet();
    }

    private sealed record State(
        IReadOnlyDictionary<DerivedNode, object> Derived,
        IReadOnlyDictionary<InvariantNode, object> Invariants,
        IReadOnlyList<DerivedNode> PreviousDerived,
        IReadOnlyList<InvariantNode> PreviousInvariants);

    public void ApplyChangeImpacts(
        IReadOnlyDictionary<IRelationDefinition, RelationImpact> relationImpacts,
        IReadOnlyList<PropertyChange> changes,
        RuntimePolicyActions policyActions)
    {
        var currentDerived = new HashSet<DerivedNode>(Candidates(changes, _derivedByMember));
        foreach (var relation in relationImpacts.Keys)
            if (_derivedByRelation.TryGetValue(relation, out var nodes))
                currentDerived.UnionWith(nodes);
        foreach (var node in _previousDerived.Except(currentDerived))
            node.ClearImpact();
        foreach (var node in currentDerived)
        {
            RelationImpact? relationImpact = null;
            if (node.Relation is not null)
                relationImpacts.TryGetValue(node.Relation, out relationImpact);
            var membershipRoots = node.Definition.Analysis.HasRelationMembershipDependency
                ? relationImpact?.AffectedLefts
                    .Where(_sets[node.Definition.SourceSet].Contains)
                    .ToArray() ?? []
                : [];
            var sourceRoots = ResolveRoots(node.Definition.SourceSet, node.SourceDependencies, changes);
            var itemRoots = node.Relation is null
                ? []
                : ResolveRoots(node.Relation.RightSet, node.ItemDependencies, changes);
            var itemSources = node.Relation is null
                ? []
                : _relations[node.Relation].GetLeftsForRights(itemRoots);
            var fallbackSeverity = membershipRoots.Length > 0 && !node.Definition.ImpactPolicy.IsConfigured
                ? _impactPolicy.Classify(new RelationMembershipDependencyImpact(
                    relationImpact!, node.Definition, changes))
                : DependencyImpactKind.Dirty;
            var invalidMembershipRoots = membershipRoots.Where(source =>
                    node.Definition.ImpactPolicy.IsConfigured
                        ? node.Definition.ImpactPolicy.ClassifyMembership(relationImpact!, source) == DependencyImpactKind.Invalid
                        : fallbackSeverity == DependencyImpactKind.Invalid)
                .ToArray();
            var dirtyMembershipRoots = membershipRoots.Except(
                invalidMembershipRoots,
                ReferenceEqualityComparer.Instance).ToArray();
            node.Apply(
                sourceRoots,
                itemSources,
                dirtyMembershipRoots,
                invalidMembershipRoots,
                node.Definition.ImpactPolicy.SourceChanged.ToKind(),
                node.Definition.ImpactPolicy.ItemChanged.ToKind(),
                relationImpact,
                changes);
        }

        var currentInvariants = new HashSet<InvariantNode>(Candidates(changes, _invariantsByMember));
        foreach (var derived in currentDerived)
            if (_invariantsByDerived.TryGetValue(derived, out var nodes))
                currentInvariants.UnionWith(nodes);
        foreach (var node in _previousInvariants.Except(currentInvariants))
            node.ClearImpact();
        foreach (var node in currentInvariants)
        {
            node.ApplyInherited(policyActions);
            var invariantRoots = ResolveRoots(
                node.Definition.Derived.SourceSet,
                node.SourceDependencies,
                changes);
            if (invariantRoots.Count > 0)
                node.ApplyDirect(invariantRoots, policyActions);
        }
        _previousDerived = currentDerived;
        _previousInvariants = currentInvariants;
    }

    public void ApplyRelationImpacts(
        IReadOnlyDictionary<IRelationDefinition, RelationImpact> relationImpacts,
        IReadOnlyList<PropertyChange> changes,
        RuntimePolicyActions policyActions)
    {
        var currentDerived = new HashSet<DerivedNode>();
        foreach (var relation in relationImpacts.Keys)
            if (_derivedByRelation.TryGetValue(relation, out var nodes))
                currentDerived.UnionWith(nodes);
        foreach (var node in _previousDerived.Except(currentDerived))
            node.ClearImpact();
        foreach (var node in currentDerived)
        {
            node.ClearImpact();
            if (!node.Definition.Analysis.HasRelationMembershipDependency ||
                node.Relation is null ||
                !relationImpacts.TryGetValue(node.Relation, out var relationImpact) ||
                relationImpact.AffectedLefts.Count == 0)
                continue;
            var sources = relationImpact.AffectedLefts
                .Where(_sets[node.Definition.SourceSet].Contains)
                .ToHashSet(ReferenceEqualityComparer.Instance);
            if (sources.Count == 0)
                continue;
            var fallbackSeverity = !node.Definition.ImpactPolicy.IsConfigured
                ? _impactPolicy.Classify(new RelationMembershipDependencyImpact(
                    relationImpact, node.Definition, changes))
                : DependencyImpactKind.Dirty;
            var invalid = sources.Where(source =>
                    node.Definition.ImpactPolicy.IsConfigured
                        ? node.Definition.ImpactPolicy.ClassifyMembership(relationImpact, source) == DependencyImpactKind.Invalid
                        : fallbackSeverity == DependencyImpactKind.Invalid)
                .ToArray();
            node.Apply(sources.Except(invalid, ReferenceEqualityComparer.Instance), invalid);
        }

        var currentInvariants = new HashSet<InvariantNode>();
        foreach (var derived in currentDerived)
            if (_invariantsByDerived.TryGetValue(derived, out var nodes))
                currentInvariants.UnionWith(nodes);
        foreach (var node in _previousInvariants.Except(currentInvariants))
            node.ClearImpact();
        foreach (var node in currentInvariants)
            node.ApplyInherited(policyActions);
        _previousDerived = currentDerived;
        _previousInvariants = currentInvariants;
    }

    private static IEnumerable<TNode> Candidates<TNode>(
        IReadOnlyList<PropertyChange> changes,
        IReadOnlyDictionary<MemberInfo, IReadOnlyList<TNode>> adjacency) =>
        changes.SelectMany(change => adjacency.TryGetValue(change.Member, out var nodes) ? nodes : [])
            .Distinct();

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
        public IRelationDefinition? Relation => Definition.Inputs
            .Select(input => input.Relation).OfType<IRelationDefinition>().SingleOrDefault();
        public IDerivedRuntimeState State { get; }
        public IReadOnlyList<TrackedExpressionDependency> SourceDependencies { get; }
        public IReadOnlyList<TrackedExpressionDependency> ItemDependencies { get; }
        public HashSet<object> InvalidSources { get; private set; } = NewSet();
        public HashSet<object> DirtySources { get; private set; } = NewSet();

        public object CaptureState() => new NodeState(
            State.CaptureState(),
            NewSet(InvalidSources),
            NewSet(DirtySources));

        public void RestoreState(object snapshot)
        {
            var state = (NodeState)snapshot;
            State.RestoreState(state.RuntimeState);
            InvalidSources = state.InvalidSources;
            DirtySources = state.DirtySources;
        }

        private sealed record NodeState(
            object RuntimeState,
            HashSet<object> InvalidSources,
            HashSet<object> DirtySources);

        public void Apply(
            IEnumerable<object> sourceRoots,
            IEnumerable<object> itemSources,
            IEnumerable<object> dirtyMembershipRoots,
            IEnumerable<object> invalidMembershipRoots,
            DependencyImpactKind sourceSeverity,
            DependencyImpactKind itemSeverity,
            RelationImpact? relationImpact,
            IReadOnlyList<PropertyChange> changes)
        {
            InvalidSources = NewSet(invalidMembershipRoots);
            if (itemSeverity == DependencyImpactKind.Invalid)
                InvalidSources.UnionWith(itemSources);
            if (sourceSeverity == DependencyImpactKind.Invalid)
                InvalidSources.UnionWith(sourceRoots);
            DirtySources = sourceSeverity == DependencyImpactKind.Dirty ? NewSet(sourceRoots) : NewSet();
            if (itemSeverity == DependencyImpactKind.Dirty)
                DirtySources.UnionWith(itemSources);
            DirtySources.UnionWith(dirtyMembershipRoots);
            DirtySources.ExceptWith(InvalidSources);
            var incrementallyUpdated = State.ApplyIncremental(DirtySources, relationImpact, changes);
            if (InvalidSources.Count > 0)
                State.ApplyImpact(InvalidSources, DependencyImpactKind.Invalid);
            if (DirtySources.Count > incrementallyUpdated.Count)
                State.ApplyImpact(
                    DirtySources.Where(source => !incrementallyUpdated.Contains(source)),
                    DependencyImpactKind.Dirty);
        }

        public void Apply(IEnumerable<object> dirtySources, IEnumerable<object> invalidSources)
        {
            ClearImpact();
            InvalidSources = NewSet(invalidSources);
            DirtySources = NewSet(dirtySources);
            if (InvalidSources.Count > 0)
                State.ApplyImpact(InvalidSources, DependencyImpactKind.Invalid);
            if (DirtySources.Count > 0)
                State.ApplyImpact(DirtySources, DependencyImpactKind.Dirty);
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
        public DerivedNode Derived => _derived;

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

        public object CaptureState() => new NodeState(
            State.CaptureState(),
            NewSet(InvalidSources),
            NewSet(DirtySources));

        public void RestoreState(object snapshot)
        {
            var state = (NodeState)snapshot;
            State.RestoreState(state.RuntimeState);
            InvalidSources = state.InvalidSources;
            DirtySources = state.DirtySources;
        }

        private sealed record NodeState(
            object RuntimeState,
            HashSet<object> InvalidSources,
            HashSet<object> DirtySources);

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
}
