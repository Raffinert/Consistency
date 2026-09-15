using Raffinert.Relations.Expressions;
using System.Reflection;

namespace Raffinert.Relations;

internal sealed record DerivedImpactSnapshot(
    IDerivedDefinition Definition,
    DependencyImpactKind Severity,
    IReadOnlyCollection<object> Sources,
    IReadOnlyList<DirectClassificationEvidence> DirectEvidence,
    IReadOnlyCollection<object> ConservativeSources);

internal sealed record DirectClassificationEvidence(
    object Source,
    MemberInfo Member,
    DependencySeverity Severity,
    string Policy);

internal sealed record InvariantImpactSnapshot(
    IInvariantDefinition Definition,
    DependencyImpactKind Severity,
    IReadOnlyCollection<object> Sources);

internal sealed record UpstreamPropagationEvidence(
    IDerivedDefinition Downstream,
    object DownstreamSource,
    IDerivedDefinition Upstream,
    object UpstreamSource,
    ImpactCausePrecision Precision);

internal sealed record InvariantUpstreamEvidence(
    IInvariantDefinition Invariant,
    object InvariantSource,
    IDerivedDefinition Upstream,
    object UpstreamSource,
    ImpactCausePrecision Precision);

internal sealed record DependencyPropagationResult(
    IReadOnlyList<DerivedImpactSnapshot> DerivedImpacts,
    IReadOnlyList<InvariantImpactSnapshot> InvariantImpacts,
    IReadOnlyList<UpstreamPropagationEvidence> UpstreamEvidence,
    IReadOnlyList<InvariantUpstreamEvidence> InvariantUpstreamEvidence);

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
    private readonly ProjectionIndexRegistry _projections;
    private readonly IDependencyImpactPolicy _impactPolicy;
    private readonly IReadOnlyList<DerivedNode> _derivedNodes;
    private readonly IReadOnlyList<InvariantNode> _invariantNodes;
    private readonly IReadOnlyDictionary<IRelationDefinition, IReadOnlyList<DerivedNode>> _derivedByRelation;
    private readonly IReadOnlyDictionary<MemberInfo, IReadOnlyList<DerivedNode>> _derivedByMember;
    private readonly IReadOnlyDictionary<DerivedNode, IReadOnlyList<InvariantNode>> _invariantsByDerived;
    private readonly IReadOnlyDictionary<DerivedNode, IReadOnlyList<DerivedNode>> _derivedByUpstream;
    private readonly IReadOnlyDictionary<DerivedNode, IReadOnlyList<(UpstreamDerivedInput Input, DerivedNode Node)>>
        _upstreamsByDerived;
    private readonly IReadOnlyDictionary<MemberInfo, IReadOnlyList<InvariantNode>> _invariantsByMember;
    private HashSet<DerivedNode> _previousDerived = [];
    private HashSet<InvariantNode> _previousInvariants = [];

    public DependencyGraphRuntime(
        IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> sets,
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> relations,
        NavigationIndexRegistry navigation,
        ProjectionIndexRegistry projections,
        IReadOnlyDictionary<IDerivedDefinition, IDerivedRuntimeState> derivedStates,
        IReadOnlyDictionary<IInvariantDefinition, IInvariantRuntimeState> invariants,
        CompiledDependencyGraph compiledGraph,
        IDependencyImpactPolicy impactPolicy)
    {
        _sets = sets;
        _relations = relations;
        _navigation = navigation;
        _projections = projections;
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
                definition.UpstreamDerived.Select(upstream => derivedByDefinition[upstream]).ToArray()))
            .ToArray();
        _derivedByRelation = Group(_derivedNodes.SelectMany(node => node.Definition.Inputs
            .OfType<RelationDerivedInput>()
            .Select(input => input.Relation)
            .Select(relation => (relation, node))));
        _derivedByMember = Group(_derivedNodes.SelectMany(node =>
            node.SourceDependencies.Concat(node.ItemDependencies)
                .SelectMany(dependency => dependency.Path.Segments)
                .Select(segment => (segment.Member, node))));
        _invariantsByDerived = Group(_invariantNodes.SelectMany(node =>
            node.Derived.Select(derived => (derived, node))));
        _derivedByUpstream = Group(_derivedNodes.SelectMany(node => node.Definition.Inputs
            .OfType<UpstreamDerivedInput>().Select(input => input.Upstream)
            .Select(upstream => (derivedByDefinition[upstream], node))));
        _upstreamsByDerived = _derivedNodes.SelectMany(node => node.Definition.Inputs
                .OfType<UpstreamDerivedInput>()
                .Select(input => (Downstream: node, Value: (Input: input, Node: derivedByDefinition[input.Upstream]))))
            .GroupBy(value => value.Downstream)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<(UpstreamDerivedInput Input, DerivedNode Node)>)group
                    .Select(value => value.Value).ToArray());
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
            AddRoots(node.Definition.SourceSet, node.SourceDependencies);

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
            Snapshot(node.Definition, node.InvalidSources, node.DirtySources, node.DirectEvidence,
                node.ConservativeSources))
        .ToArray();

    public IReadOnlyList<InvariantImpactSnapshot> GetInvariantImpacts() => _invariantNodes
        .SelectMany(node =>
            Snapshot(node.Definition, node.InvalidSources, node.DirtySources))
        .ToArray();

    public object CaptureState(
        ResolvedChangeImpact impact,
        IReadOnlyList<RuntimeMutation> lifecycleMutations,
        IReadOnlyList<PropertyChange> changes,
        object? previousState = null)
    {
        var previous = previousState as State;
        var derivedSources = _derivedNodes.ToDictionary(
            node => node,
            _ => new HashSet<object>(ReferenceEqualityComparer.Instance));
        foreach (var node in _derivedNodes)
        {
            AddLifecycleSources(node.Definition.SourceSet, derivedSources[node]);
            derivedSources[node].UnionWith(node.InvalidSources);
            derivedSources[node].UnionWith(node.DirtySources);
            derivedSources[node].UnionWith(node.ConservativeSources);
            if (Candidates(changes, _derivedByMember).Contains(node))
                derivedSources[node].UnionWith(ResolveRoots(
                    node.Definition.SourceSet, node.SourceDependencies, changes));
            if (node.Relation is null)
                continue;
            derivedSources[node].UnionWith(impact.GetAffectedRoots(node.Relation, node.Relation.LeftSet));
            var rights = impact.GetAffectedRoots(node.Relation, node.Relation.RightSet)
                .Concat(ResolveRoots(node.Relation.RightSet, node.ItemDependencies, changes))
                .Concat(lifecycleMutations.Select(mutation => mutation switch
                {
                    ObjectAdded added when ReferenceEquals(added.Set, node.Relation.RightSet) => added.Instance,
                    ObjectRemoved removed when ReferenceEquals(removed.Set, node.Relation.RightSet) =>
                        removed.Instance,
                    _ => null
                }).OfType<object>())
                .Distinct(ReferenceEqualityComparer.Instance)
                .ToArray();
            derivedSources[node].UnionWith(_relations[node.Relation].GetPotentialLeftsForRights(rights));
        }
        if (previous is not null)
            foreach (var pair in previous.Derived)
                derivedSources[pair.Key].UnionWith(pair.Value.Sources);

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in _derivedNodes)
            {
                if (!_upstreamsByDerived.TryGetValue(node, out var upstreams))
                    continue;
                foreach (var upstream in upstreams)
                    foreach (var source in DerivedNode.Map(
                                 upstream.Input, derivedSources[upstream.Node], _projections))
                        changed |= derivedSources[node].Add(source);
            }
        }

        var invariantSources = _invariantNodes.ToDictionary(
            node => node,
            _ => new HashSet<object>(ReferenceEqualityComparer.Instance));
        foreach (var node in _invariantNodes)
        {
            AddLifecycleSources(node.Definition.SourceSet, invariantSources[node]);
            invariantSources[node].UnionWith(node.InvalidSources);
            invariantSources[node].UnionWith(node.DirtySources);
            invariantSources[node].UnionWith(ResolveRoots(
                node.Definition.SourceSet, node.SourceDependencies, changes));
            foreach (var upstream in node.Derived)
                invariantSources[node].UnionWith(derivedSources[upstream]);
        }
        if (previous is not null)
            foreach (var pair in previous.Invariants)
                invariantSources[pair.Key].UnionWith(pair.Value.Sources);

        return new State(
            derivedSources.ToDictionary(
                pair => pair.Key,
                pair => new NodePatch(pair.Value.ToArray(), pair.Key.CaptureState(pair.Value))),
            invariantSources.ToDictionary(
                pair => pair.Key,
                pair => new NodePatch(pair.Value.ToArray(), pair.Key.CaptureState(pair.Value))),
            _previousDerived.ToArray(),
            _previousInvariants.ToArray());

        void AddLifecycleSources(IObjectSetDefinition set, HashSet<object> sources)
        {
            foreach (var mutation in lifecycleMutations)
                switch (mutation)
                {
                    case ObjectAdded added when ReferenceEquals(added.Set, set):
                        sources.Add(added.Instance);
                        break;
                    case ObjectRemoved removed when ReferenceEquals(removed.Set, set):
                        sources.Add(removed.Instance);
                        break;
                }
        }
    }

    private void ExpandDownstream(HashSet<DerivedNode> nodes)
    {
        var pending = new Queue<DerivedNode>(_derivedNodes.Where(nodes.Contains));
        while (pending.TryDequeue(out var node))
        {
            if (!_derivedByUpstream.TryGetValue(node, out var downstream))
                continue;
            foreach (var candidate in downstream)
                if (nodes.Add(candidate))
                    pending.Enqueue(candidate);
        }
    }

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        foreach (var pair in state.Derived)
            pair.Key.RestoreState(pair.Value.State);
        foreach (var pair in state.Invariants)
            pair.Key.RestoreState(pair.Value.State);
        _previousDerived = state.PreviousDerived.ToHashSet();
        _previousInvariants = state.PreviousInvariants.ToHashSet();
    }

    private sealed record State(
        IReadOnlyDictionary<DerivedNode, NodePatch> Derived,
        IReadOnlyDictionary<InvariantNode, NodePatch> Invariants,
        IReadOnlyList<DerivedNode> PreviousDerived,
        IReadOnlyList<InvariantNode> PreviousInvariants);

    private sealed record NodePatch(IReadOnlyList<object> Sources, object State);

    public int GetCapturedStateEntryCount(object snapshot)
    {
        var state = (State)snapshot;
        return state.Derived.Sum(pair => pair.Key.State.GetSourcesStateEntryCount(pair.Value.State)) +
            state.Invariants.Sum(pair => pair.Key.State.GetSourcesStateEntryCount(pair.Value.State));
    }

    public DependencyPropagationResult ApplyChangeImpacts(
        IReadOnlyDictionary<IRelationDefinition, RelationImpact> relationImpacts,
        IReadOnlyList<PropertyChange> changes,
        RuntimePolicyActions policyActions,
        bool captureCausalEvidence)
    {
        List<UpstreamPropagationEvidence>? upstreamEvidence = captureCausalEvidence ? [] : null;
        List<InvariantUpstreamEvidence>? invariantUpstreamEvidence = captureCausalEvidence ? [] : null;
        var currentDerived = new HashSet<DerivedNode>(Candidates(changes, _derivedByMember));
        foreach (var relation in relationImpacts.Keys)
            if (_derivedByRelation.TryGetValue(relation, out var nodes))
                currentDerived.UnionWith(nodes);
        ExpandDownstream(currentDerived);
        foreach (var node in _previousDerived.Except(currentDerived))
            node.ClearImpact();
        foreach (var node in _derivedNodes)
        {
            if (!currentDerived.Contains(node))
                continue;
            RelationImpact? relationImpact = null;
            if (node.Relation is not null)
                relationImpacts.TryGetValue(node.Relation, out relationImpact);
            var membershipRoots = node.Definition.Analysis.HasRelationMembershipDependency
                ? relationImpact?.AffectedLefts
                    .Where(_sets[node.Definition.SourceSet].Contains)
                    .ToArray() ?? []
                : [];
            var sourceRoots = ResolveRoots(node.Definition.SourceSet, node.SourceDependencies, changes);
            var (dirtySourceRoots, invalidSourceRoots) = node.ClassifySourceRoots(
                sourceRoots, changes, captureCausalEvidence);
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
                dirtySourceRoots,
                invalidSourceRoots,
                itemSources,
                dirtyMembershipRoots,
                invalidMembershipRoots,
                node.Definition.ImpactPolicy.ItemChanged.ToKind(),
                relationImpact,
                changes);
            if (_upstreamsByDerived.TryGetValue(node, out var upstreams))
                node.ApplyInherited(upstreams, _projections, upstreamEvidence);
        }

        var currentInvariants = new HashSet<InvariantNode>(Candidates(changes, _invariantsByMember));
        foreach (var derived in currentDerived)
            if (_invariantsByDerived.TryGetValue(derived, out var nodes))
                currentInvariants.UnionWith(nodes);
        foreach (var node in _previousInvariants.Except(currentInvariants))
            node.ClearImpact();
        foreach (var node in _invariantNodes)
        {
            if (!currentInvariants.Contains(node))
                continue;
            node.ApplyInherited(policyActions, invariantUpstreamEvidence);
            var invariantRoots = ResolveRoots(
                node.Definition.SourceSet,
                node.SourceDependencies,
                changes);
            if (invariantRoots.Count > 0)
                node.ApplyDirect(invariantRoots, policyActions);
        }
        _previousDerived = currentDerived;
        _previousInvariants = currentInvariants;
        return new DependencyPropagationResult(
            GetDerivedImpacts(),
            GetInvariantImpacts(),
            upstreamEvidence ?? [],
            invariantUpstreamEvidence ?? []);
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
        IReadOnlyCollection<object> dirty,
        IReadOnlyList<DirectClassificationEvidence> evidence,
        IReadOnlyCollection<object> conservativeSources)
    {
        if (invalid.Count > 0)
            yield return new DerivedImpactSnapshot(definition, DependencyImpactKind.Invalid, invalid, evidence,
                conservativeSources);
        if (dirty.Count > 0)
            yield return new DerivedImpactSnapshot(definition, DependencyImpactKind.Dirty, dirty, evidence,
                conservativeSources);
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
            .OfType<RelationDerivedInput>().Select(input => input.Relation).SingleOrDefault();
        public IDerivedRuntimeState State { get; }
        public IReadOnlyList<TrackedExpressionDependency> SourceDependencies { get; }
        public IReadOnlyList<TrackedExpressionDependency> ItemDependencies { get; }
        public HashSet<object> InvalidSources { get; private set; } = NewSet();
        public HashSet<object> DirtySources { get; private set; } = NewSet();
        public IReadOnlyList<DirectClassificationEvidence> DirectEvidence { get; private set; } = [];
        public HashSet<object> ConservativeSources { get; private set; } = NewSet();

        public object CaptureState(IEnumerable<object> sources) => new NodeState(
            State.CaptureSourcesState(sources),
            NewSet(InvalidSources),
            NewSet(DirtySources),
            NewSet(ConservativeSources));

        public void RestoreState(object snapshot)
        {
            var state = (NodeState)snapshot;
            State.RestoreSourcesState(state.RuntimeState);
            InvalidSources = state.InvalidSources;
            DirtySources = state.DirtySources;
            ConservativeSources = state.ConservativeSources;
        }

        private sealed record NodeState(
            object RuntimeState,
            HashSet<object> InvalidSources,
            HashSet<object> DirtySources,
            HashSet<object> ConservativeSources);

        public void Apply(
            IEnumerable<object> dirtySourceRoots,
            IEnumerable<object> invalidSourceRoots,
            IEnumerable<object> itemSources,
            IEnumerable<object> dirtyMembershipRoots,
            IEnumerable<object> invalidMembershipRoots,
            DependencyImpactKind itemSeverity,
            RelationImpact? relationImpact,
            IReadOnlyList<PropertyChange> changes)
        {
            InvalidSources = NewSet(invalidMembershipRoots);
            if (itemSeverity == DependencyImpactKind.Invalid)
                InvalidSources.UnionWith(itemSources);
            InvalidSources.UnionWith(invalidSourceRoots);
            DirtySources = NewSet(dirtySourceRoots);
            if (itemSeverity == DependencyImpactKind.Dirty)
                DirtySources.UnionWith(itemSources);
            DirtySources.UnionWith(dirtyMembershipRoots);
            DirtySources.ExceptWith(InvalidSources);
            ConservativeSources = Relation?.PropagationPlan == RelationPropagationPlan.ConservativeInvalidation
                ? NewSet(itemSources.Concat(dirtyMembershipRoots).Concat(invalidMembershipRoots))
                : NewSet();
            var incrementallyUpdated = State.ApplyIncremental(DirtySources, relationImpact, changes);
            if (InvalidSources.Count > 0)
                State.ApplyImpact(InvalidSources, DependencyImpactKind.Invalid);
            if (DirtySources.Count > incrementallyUpdated.Count)
                State.ApplyImpact(
                    DirtySources.Where(source => !incrementallyUpdated.Contains(source)),
                    DependencyImpactKind.Dirty);
        }

        public (IReadOnlyCollection<object> Dirty, IReadOnlyCollection<object> Invalid) ClassifySourceRoots(
            IEnumerable<object> sourceRoots,
            IReadOnlyList<PropertyChange> changes,
            bool captureCausalEvidence)
        {
            var dirty = NewSet();
            var invalid = NewSet();
            List<DirectClassificationEvidence>? evidence = captureCausalEvidence ? [] : null;
            var directDependencies = SourceDependencies
                .Where(dependency => dependency.Path.Segments.Count == 1)
                .Select(dependency => dependency.Path.Segments[0].Member)
                .ToHashSet();
            var rules = Definition.ImpactPolicy.SourceMemberRules
                .Where(rule => directDependencies.Contains(rule.Member))
                .ToDictionary(rule => rule.Member);
            foreach (var source in sourceRoots)
            {
                DependencySeverity? severity = null;
                foreach (var change in changes.Where(change => ReferenceEquals(change.Instance, source)))
                {
                    var classified = rules.TryGetValue(change.Member, out var rule)
                        ? rule.Classify(change.OldValue, change.NewValue)
                        : Definition.ImpactPolicy.SourceChanged;
                    evidence?.Add(new DirectClassificationEvidence(
                        source, change.Member, classified,
                        rule is null ? "fixed fallback" : "member-specific conditional policy"));
                    severity = severity is null ? classified : Max(severity.Value, classified);
                }
                severity ??= Definition.ImpactPolicy.SourceChanged;
                (severity == DependencySeverity.Invalid ? invalid : dirty).Add(source);
            }
            DirectEvidence = evidence ?? [];
            return (dirty, invalid);
        }

        private static DependencySeverity Max(DependencySeverity left, DependencySeverity right) =>
            left == DependencySeverity.Invalid || right == DependencySeverity.Invalid
                ? DependencySeverity.Invalid
                : DependencySeverity.Dirty;

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

        public void ApplyInherited(
            IEnumerable<(UpstreamDerivedInput Input, DerivedNode Node)> upstreams,
            ProjectionIndexRegistry projections,
            List<UpstreamPropagationEvidence>? evidence)
        {
            var inheritedInvalid = NewSet();
            var inheritedDirty = NewSet();
            var inheritedConservative = NewSet();
            foreach (var upstream in upstreams)
            {
                inheritedInvalid.UnionWith(Map(upstream.Input, upstream.Node.InvalidSources, projections));
                inheritedDirty.UnionWith(Map(upstream.Input, upstream.Node.DirtySources, projections));
                inheritedConservative.UnionWith(
                    Map(upstream.Input, upstream.Node.ConservativeSources, projections));
                if (evidence is null)
                    continue;
                foreach (var upstreamSource in upstream.Node.InvalidSources.Concat(upstream.Node.DirtySources)
                             .Distinct(ReferenceEqualityComparer.Instance))
                    foreach (var downstreamSource in Map(upstream.Input, NewSet([upstreamSource]), projections))
                        evidence.Add(new UpstreamPropagationEvidence(
                            Definition,
                            downstreamSource,
                            upstream.Node.Definition,
                            upstreamSource,
                            upstream.Node.ConservativeSources.Contains(upstreamSource)
                                ? ImpactCausePrecision.Conservative
                                : ImpactCausePrecision.Exact));
            }
            inheritedDirty.ExceptWith(inheritedInvalid);
            var newInvalid = inheritedInvalid.Except(InvalidSources, ReferenceEqualityComparer.Instance).ToArray();
            var newDirty = inheritedDirty.Except(DirtySources, ReferenceEqualityComparer.Instance)
                .Where(source => !InvalidSources.Contains(source)).ToArray();
            InvalidSources.UnionWith(inheritedInvalid);
            DirtySources.UnionWith(inheritedDirty);
            DirtySources.ExceptWith(InvalidSources);
            ConservativeSources.UnionWith(inheritedConservative);
            if (newInvalid.Length > 0)
                State.ApplyImpact(newInvalid, DependencyImpactKind.Invalid);
            if (newDirty.Length > 0)
                State.ApplyImpact(newDirty, DependencyImpactKind.Dirty);
        }

        internal static IEnumerable<object> Map(
            UpstreamDerivedInput input,
            IReadOnlySet<object> impacted,
            ProjectionIndexRegistry projections) => input is ProjectedUpstreamDerivedInput projected
            ? projections.Resolve(projected, impacted)
            : impacted;

        public void ClearImpact()
        {
            InvalidSources = NewSet();
            DirtySources = NewSet();
            DirectEvidence = [];
            ConservativeSources = NewSet();
        }

        private static HashSet<object> NewSet(IEnumerable<object>? values = null) =>
            values is null
                ? new HashSet<object>(ReferenceEqualityComparer.Instance)
                : new HashSet<object>(values, ReferenceEqualityComparer.Instance);
    }

    private sealed class InvariantNode
    {
        private readonly IReadOnlyList<DerivedNode> _derived;
        public IReadOnlyList<DerivedNode> Derived => _derived;

        public InvariantNode(
            IInvariantDefinition definition,
            IInvariantRuntimeState state,
            IReadOnlyList<DerivedNode> derived)
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

        public object CaptureState(IEnumerable<object> sources) => new NodeState(
            State.CaptureSourcesState(sources),
            NewSet(InvalidSources),
            NewSet(DirtySources));

        public void RestoreState(object snapshot)
        {
            var state = (NodeState)snapshot;
            State.RestoreSourcesState(state.RuntimeState);
            InvalidSources = state.InvalidSources;
            DirtySources = state.DirtySources;
        }

        private sealed record NodeState(
            object RuntimeState,
            HashSet<object> InvalidSources,
            HashSet<object> DirtySources);

        public void ApplyInherited(
            RuntimePolicyActions policyActions,
            List<InvariantUpstreamEvidence>? evidence)
        {
            if (evidence is not null)
                foreach (var upstream in _derived)
                    foreach (var source in upstream.InvalidSources.Concat(upstream.DirtySources)
                                 .Distinct(ReferenceEqualityComparer.Instance))
                        evidence.Add(new InvariantUpstreamEvidence(
                            Definition,
                            source,
                            upstream.Definition,
                            source,
                            upstream.ConservativeSources.Contains(source)
                                ? ImpactCausePrecision.Conservative
                                : ImpactCausePrecision.Exact));
            InvalidSources = NewSet(_derived.SelectMany(node => node.InvalidSources));
            DirtySources = NewSet(_derived.SelectMany(node => node.DirtySources));
            DirtySources.ExceptWith(InvalidSources);
            if (Definition.Reaction is InvariantReaction.MarkInvalid or InvariantReaction.ScheduleRepair)
            {
                InvalidSources.UnionWith(DirtySources);
                DirtySources.Clear();
            }
            if (InvalidSources.Count > 0)
                State.ApplyImpact(InvalidSources, DependencyImpactKind.Invalid, policyActions);
            if (DirtySources.Count > 0)
                State.ApplyImpact(DirtySources, DependencyImpactKind.Dirty, policyActions);
        }

        public void ApplyDirect(IEnumerable<object> sources, RuntimePolicyActions policyActions)
        {
            var affected = NewSet(sources);
            if (Definition.Reaction is InvariantReaction.MarkInvalid or InvariantReaction.ScheduleRepair)
            {
                InvalidSources.UnionWith(affected);
                DirtySources.ExceptWith(InvalidSources);
                State.ApplyImpact(affected, DependencyImpactKind.Invalid, policyActions);
            }
            else
            {
                DirtySources.UnionWith(affected);
                DirtySources.ExceptWith(InvalidSources);
                State.ApplyImpact(affected, DependencyImpactKind.Dirty, policyActions);
            }
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
