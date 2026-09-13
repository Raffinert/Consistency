using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Raffinert.Relations;

public sealed partial class RelationRuntime
{
    /// <summary>
    /// Commits a prepared mutation to runtime-owned state without invoking application callbacks.
    /// </summary>
    public ChangeImpact Commit(PreparedMutation prepared)
    {
        ValidatePreparedMutation(prepared);
        prepared.ValidateDomainState(_sets);
        var plannedImpact = new ResolvedChangeImpact();
        foreach (var change in prepared.Changes)
            plannedImpact.MergeFrom(_impactResolver.Resolve(change));
        var navigationRoots = _dependencyGraph.ResolveNavigationRoots(plannedImpact, prepared.Changes);
        var snapshot = _rollbackSnapshotsEnabled
            ? CaptureState(prepared.LifecycleMutations, prepared.Changes, plannedImpact, navigationRoots)
            : null;
        try
        {
            var result = CommitMutations(
                prepared.LifecycleMutations,
                prepared.Changes,
                plannedImpact,
                navigationRoots);
            _version++;
            prepared.MarkCommitted(result.PolicyActions);
            return result.Impact;
        }
        catch
        {
            if (snapshot is not null)
                RestoreState(snapshot);
            throw;
        }
    }

    /// <summary>Disables commit snapshots for benchmark comparisons only.</summary>
    internal RelationRuntime DisableRollbackSnapshotsForBenchmarking()
    {
        _rollbackSnapshotsEnabled = false;
        return this;
    }

    private RuntimeStateSnapshot CaptureState(
        IReadOnlyList<RuntimeMutation> lifecycleMutations,
        IReadOnlyList<PropertyChange> changes,
        ResolvedChangeImpact impact,
        IReadOnlyCollection<(IObjectSetDefinition Set, object Root)> navigationRoots)
    {
        var lifecycleSets = lifecycleMutations.Select(mutation => mutation is ObjectAdded added
            ? added.Set
            : ((ObjectRemoved)mutation).Set).ToHashSet();
        var affectedRelations = impact.AffectedRelations.ToHashSet();
        affectedRelations.UnionWith(_relations.Keys.Where(relation =>
            lifecycleSets.Contains(relation.LeftSet) || lifecycleSets.Contains(relation.RightSet)));
        var navigationChanged = lifecycleMutations.Count > 0 || navigationRoots.Count > 0;
        return new RuntimeStateSnapshot(
        lifecycleSets.ToDictionary(set => set, set => _sets[set].CaptureState()),
        affectedRelations.ToDictionary(relation => relation, relation => _relations[relation].CaptureState()),
        navigationChanged ? _navigation.CaptureState() : null,
        _dependencyGraph.CaptureState(affectedRelations, changes),
        LastRelationImpacts,
        _reindexedRoots,
        _affectedSources,
        _relationPairsAdded,
        _relationPairsRemoved,
        _policyRequestsEmitted);
    }

    private void RestoreState(RuntimeStateSnapshot snapshot)
    {
        foreach (var pair in snapshot.Sets)
            _sets[pair.Key].RestoreState(pair.Value);
        foreach (var pair in snapshot.Relations)
            _relations[pair.Key].RestoreState(pair.Value);
        if (snapshot.Navigation is not null)
            _navigation.RestoreState(snapshot.Navigation);
        _dependencyGraph.RestoreState(snapshot.Dependencies);
        LastRelationImpacts = snapshot.LastRelationImpacts;
        _reindexedRoots = snapshot.ReindexedRoots;
        _affectedSources = snapshot.AffectedSources;
        _relationPairsAdded = snapshot.RelationPairsAdded;
        _relationPairsRemoved = snapshot.RelationPairsRemoved;
        _policyRequestsEmitted = snapshot.PolicyRequestsEmitted;
    }

    private sealed record RuntimeStateSnapshot(
        IReadOnlyDictionary<IObjectSetDefinition, object> Sets,
        IReadOnlyDictionary<IRelationDefinition, object> Relations,
        object? Navigation,
        object Dependencies,
        IReadOnlyDictionary<IRelationDefinition, RelationImpact> LastRelationImpacts,
        long ReindexedRoots,
        long AffectedSources,
        long RelationPairsAdded,
        long RelationPairsRemoved,
        long PolicyRequestsEmitted);

    /// <summary>Dispatches a committed mutation's post-commit policy callbacks.</summary>
    public void Dispatch(PreparedMutation prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Runtime, this))
            throw new ArgumentException("The prepared mutation belongs to a different runtime.", nameof(prepared));
        if (!prepared.IsCommitted)
            throw new InvalidOperationException("The prepared mutation has not been committed.");
        if (prepared.IsDispatched)
            throw new InvalidOperationException("The prepared mutation's policy actions have already been dispatched.");
        prepared.PolicyActions!.Dispatch();
        prepared.MarkDispatched();
    }

    private void ValidatePreparedMutation(PreparedMutation prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Runtime, this))
            throw new ArgumentException("The prepared mutation belongs to a different runtime.", nameof(prepared));
        if (prepared.IsCommitted)
            throw new InvalidOperationException("The prepared mutation has already been committed.");
        if (prepared.BaseVersion != Version)
            throw new InvalidOperationException(
                $"The prepared mutation is stale: it was prepared at runtime version {prepared.BaseVersion}, " +
                $"but the current version is {Version}. Prepare the mutation again.");
    }

    private RuntimeCommitResult CommitMutations(
        IReadOnlyList<RuntimeMutation> lifecycleMutations,
        IReadOnlyList<PropertyChange> changes,
        ResolvedChangeImpact impact,
        IReadOnlyCollection<(IObjectSetDefinition Set, object Root)> navigationRoots)
    {
        var relationDeltas = new Dictionary<IRelationDefinition, RelationDelta>();
        foreach (var mutation in lifecycleMutations)
        {
            if (mutation is ObjectAdded added)
                CommitAdd(added, relationDeltas);
            else
                CommitRemove((ObjectRemoved)mutation, relationDeltas);
        }
        foreach (var (rootSet, root) in navigationRoots)
            _navigation.RefreshRoot(rootSet, root);
        foreach (var pair in impact.ReindexRoots)
            foreach (var root in pair.Value)
                MergeDelta(relationDeltas, _relations.Single(relation => ReferenceEquals(relation.Value, pair.Key)).Key,
                    pair.Key.ReindexRight(root));
        foreach (var pair in impact.ReindexLeftRoots)
            foreach (var root in pair.Value)
                pair.Key.ReindexLeft(root);
        foreach (var pair in ResolveRelationDeltas(impact))
            MergeDelta(relationDeltas, pair.Key, pair.Value);
        var relationImpacts = new Dictionary<IRelationDefinition, RelationImpact>(
            impact.CreateRelationImpacts(_relations, relationDeltas));
        foreach (var pair in relationDeltas)
            if (!relationImpacts.ContainsKey(pair.Key))
                relationImpacts.Add(pair.Key, RelationImpact.FromDelta(pair.Key, pair.Value));
        LastRelationImpacts = relationImpacts;
        var policyActions = new RuntimePolicyActions();
        _dependencyGraph.ApplyChangeImpacts(relationImpacts, changes, policyActions);
        var publicImpact = impact.ToPublic();
        _reindexedRoots += publicImpact.Access.ReindexedRoots;
        _affectedSources += _dependencyGraph.GetDerivedImpacts()
            .SelectMany(value => value.Sources)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();
        _relationPairsAdded += relationImpacts.Values.Sum(value => value.AddedPairs.Count);
        _relationPairsRemoved += relationImpacts.Values.Sum(value => value.RemovedPairs.Count);
        _policyRequestsEmitted += policyActions.ImmediateEvaluations.Count + policyActions.RepairRequests.Count;
        return new RuntimeCommitResult(publicImpact, policyActions);
    }

    private RuntimeApplication CreateDetailedApplication(PreparedMutation prepared, ChangeImpact impact)
    {
        var relationImpacts = LastRelationImpacts
            .OrderBy(pair => _relationIds[pair.Key])
            .Select(pair => new RelationMutationImpact(
                _relationIds[pair.Key],
                pair.Key.LeftSet.ObjectType,
                pair.Key.RightSet.ObjectType,
                Array.AsReadOnly(pair.Value.AddedPairs
                    .Select(value => new RelationPairImpact(value.Left, value.Right)).ToArray()),
                Array.AsReadOnly(pair.Value.RemovedPairs
                    .Select(value => new RelationPairImpact(value.Left, value.Right)).ToArray()),
                Array.AsReadOnly(pair.Value.AffectedLefts.ToArray()))
            { DefinitionKey = pair.Key.DefinitionKey })
            .ToArray();
        var derivedImpacts = _dependencyGraph.GetDerivedImpacts()
            .GroupBy(value => value.Definition)
            .Select(group => new DerivedMutationImpact(
                _derivedIds[group.Key],
                CreateSourceImpacts(group, group.Key.SourceSet))
            { DefinitionKey = group.Key.DefinitionKey })
            .OrderBy(value => value.DerivedId)
            .ToArray();
        var invariantImpacts = _dependencyGraph.GetInvariantImpacts()
            .GroupBy(value => value.Definition)
            .Select(group => new InvariantMutationImpact(
                _invariantIds[group.Key],
                CreateSourceImpacts(group, group.Key.SourceSet))
            { DefinitionKey = group.Key.DefinitionKey })
            .OrderBy(value => value.InvariantId)
            .ToArray();
        var actions = prepared.PolicyActions!;
        var repairRequests = actions.RepairRequests
            .Select(request => new RepairRequestInfo(
                _invariantIds[request.Invariant],
                request.Source,
                request.Reason.ToSeverity())
            {
                DefinitionKey = request.Invariant.DefinitionKey,
                SourceIdentity = CreateSourceIdentity(request.Invariant.SourceSet, request.Source)
            })
            .ToArray();
        var immediateRequests = actions.ImmediateEvaluations
            .Select(request => new ImmediateEvaluationRequestInfo(
                _invariantIds[request.Invariant.Definition],
                request.Source)
            {
                DefinitionKey = request.Invariant.Definition.DefinitionKey,
                SourceIdentity = CreateSourceIdentity(request.Invariant.Definition.SourceSet, request.Source)
            })
            .ToArray();
        var result = new RuntimeApplyResult(
            impact,
            relationImpacts,
            derivedImpacts,
            invariantImpacts,
            repairRequests,
            immediateRequests);
        return new RuntimeApplication(result, new PolicyDispatchHandle(() => Dispatch(prepared)));
    }

    private SourceIdentity CreateSourceIdentity(IObjectSetDefinition set, object source)
    {
        var key = _sets[set].GetRegisteredKey(source);
        return new SourceIdentity(set.DefinitionKey, set.ObjectType, key)
        {
            DurableIdentity = DurableSourceIdentityFactory.Create(set.DefinitionKey, set.ObjectType, key)
        };
    }

    private IReadOnlyList<SourceDependencyImpact> CreateSourceImpacts<TSnapshot>(
        IEnumerable<TSnapshot> snapshots,
        IObjectSetDefinition sourceSet) where TSnapshot : class
    {
        var values = snapshots.SelectMany(snapshot => snapshot switch
        {
            DerivedImpactSnapshot derived => derived.Sources.Select(source => (source, derived.Severity)),
            InvariantImpactSnapshot invariant => invariant.Sources.Select(source => (source, invariant.Severity)),
            _ => throw new InvalidOperationException("Unsupported dependency impact snapshot.")
        });
        return values.GroupBy(value => value.source, ReferenceEqualityComparer.Instance)
            .Select(group => new SourceDependencyImpact(
                group.Key,
                group.Any(value => value.Severity == DependencyImpactKind.Invalid)
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty)
            {
                SourceIdentity = CreateSourceIdentity(sourceSet, group.Key)
            })
            .ToArray();
    }

    private void CommitAdd(
        ObjectAdded mutation,
        IDictionary<IRelationDefinition, RelationDelta> deltas)
    {
        var state = GetSet(mutation.Set);
        state.Add(mutation.Instance);
        NotifySourceAdded(mutation.Set, mutation.Instance);
        _navigation.AddRoot(mutation.Set, mutation.Instance);
        foreach (var pair in _relations.Where(pair => ReferenceEquals(pair.Value.RightSet, mutation.Set)))
            MergeDelta(deltas, pair.Key, pair.Value.AddRight(mutation.Instance));
        foreach (var pair in _relations.Where(pair => ReferenceEquals(pair.Value.LeftSet, mutation.Set)))
            MergeDelta(deltas, pair.Key, pair.Value.AddLeft(mutation.Instance));
    }

    private void CommitRemove(
        ObjectRemoved mutation,
        IDictionary<IRelationDefinition, RelationDelta> deltas)
    {
        foreach (var pair in _relations.Where(pair => ReferenceEquals(pair.Value.RightSet, mutation.Set)))
            MergeDelta(deltas, pair.Key, pair.Value.RemoveRight(mutation.Instance));
        foreach (var pair in _relations.Where(pair => ReferenceEquals(pair.Value.LeftSet, mutation.Set)))
            MergeDelta(deltas, pair.Key, pair.Value.RemoveLeft(mutation.Instance));
        _navigation.RemoveRoot(mutation.Set, mutation.Instance);
        NotifySourceRemoved(mutation.Set, mutation.Instance);
        GetSet(mutation.Set).Remove(mutation.Instance);
    }

    private IReadOnlyDictionary<IRelationDefinition, RelationDelta> ResolveRelationDeltas(
        ResolvedChangeImpact impact)
    {
        var deltas = new Dictionary<IRelationDefinition, RelationDelta>();
        foreach (var relation in impact.AffectedRelations)
        {
            var state = _relations[relation];
            var delta = state.RefreshMembership(
                impact.GetAffectedRoots(relation, relation.LeftSet),
                impact.GetAffectedRoots(relation, relation.RightSet));
            deltas.Add(relation, delta);
        }
        return deltas;
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

    private void NotifySourceAdded(IObjectSetDefinition set, object source)
    {
        foreach (var participant in _sourceLifecycleParticipants)
            if (ReferenceEquals(participant.SourceSet, set))
                participant.OnSourceAdded(source);
    }

    private void NotifySourceRemoved(IObjectSetDefinition set, object source)
    {
        foreach (var participant in _sourceLifecycleParticipants)
            if (ReferenceEquals(participant.SourceSet, set))
                participant.OnSourceRemoved(source);
    }

}
