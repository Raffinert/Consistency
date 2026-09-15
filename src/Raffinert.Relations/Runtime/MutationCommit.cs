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
        => CommitWithResult(prepared, captureCausalEvidence: false).Impact;

    private RuntimeCommitResult CommitWithResult(PreparedMutation prepared, bool captureCausalEvidence)
    {
        ValidatePreparedMutation(prepared);
        prepared.ValidateDomainState(_sets);
        ValidateProjectedFinalState(prepared);
        var execution = ExecutePreparedMutation(prepared, captureCausalEvidence);
        _version++;
        prepared.MarkCommitted(execution.Result.PolicyActions);
        return execution.Result;
    }

    private PreparedMutationExecution ExecutePreparedMutation(
        PreparedMutation prepared,
        bool captureCausalEvidence,
        bool requireSnapshot = false,
        bool capturePostState = false)
    {
        var plannedImpact = new ResolvedChangeImpact();
        foreach (var change in prepared.Changes)
            plannedImpact.MergeFrom(_impactResolver.Resolve(change));
        var navigationRoots = _dependencyGraph.ResolveNavigationRoots(plannedImpact, prepared.Changes);
        var snapshot = _rollbackSnapshotsEnabled || requireSnapshot
            ? CaptureState(prepared.LifecycleMutations, prepared.Changes, plannedImpact, navigationRoots)
            : null;
        try
        {
            var result = CommitMutations(
                prepared.LifecycleMutations,
                prepared.Changes,
                plannedImpact,
                navigationRoots,
                captureCausalEvidence);
            var postState = capturePostState
                ? CaptureState(prepared.LifecycleMutations, prepared.Changes, plannedImpact, navigationRoots)
                : null;
            return new PreparedMutationExecution(
                result,
                snapshot is null ? null : new RuntimeRollbackJournal(snapshot),
                postState is null ? null : new RuntimeForwardPatch(postState));
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
        var navigationChanged = lifecycleMutations.Count > 0 ||
            changes.Any(change => _navigation.IsIndexedNavigation(change.Member));
        var projectionChanged = lifecycleMutations.Any(mutation => mutation switch
            {
                ObjectAdded added => _projections.IsDownstreamSet(added.Set),
                ObjectRemoved removed => _projections.IsDownstreamSet(removed.Set),
                _ => false
            }) || changes.Any(change => change.Set is not null &&
                _projections.IsSelectorChange(change.Set, change.Member));
        return new RuntimeStateSnapshot(
        lifecycleSets.ToDictionary(
            set => set,
            set => _sets[set].CaptureEntriesState(lifecycleMutations.Select(mutation => mutation switch
            {
                ObjectAdded added when ReferenceEquals(added.Set, set) => added.Instance,
                ObjectRemoved removed when ReferenceEquals(removed.Set, set) => removed.Instance,
                _ => null
            }).OfType<object>())),
        affectedRelations.ToDictionary(relation => relation, relation => _relations[relation].CaptureState()),
        navigationChanged ? _navigation.CaptureState() : null,
        projectionChanged ? _projections.CaptureState(lifecycleMutations, changes) : null,
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
            _sets[pair.Key].RestoreEntriesState(pair.Value);
        foreach (var pair in snapshot.Relations)
            _relations[pair.Key].RestoreState(pair.Value);
        if (snapshot.Navigation is not null)
            _navigation.RestoreState(snapshot.Navigation);
        if (snapshot.Projections is not null)
            _projections.RestoreState(snapshot.Projections);
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
        object? Projections,
        object Dependencies,
        IReadOnlyDictionary<IRelationDefinition, RelationImpact> LastRelationImpacts,
        long ReindexedRoots,
        long AffectedSources,
        long RelationPairsAdded,
        long RelationPairsRemoved,
        long PolicyRequestsEmitted);

    private RuntimeRollbackJournal CaptureInstallRollbackJournal(PreparedMutation prepared)
    {
        var impact = new ResolvedChangeImpact();
        foreach (var change in prepared.Changes)
            impact.MergeFrom(_impactResolver.Resolve(change));
        var navigationRoots = _dependencyGraph.ResolveNavigationRoots(impact, prepared.Changes);
        return new RuntimeRollbackJournal(CaptureState(
            prepared.LifecycleMutations, prepared.Changes, impact, navigationRoots));
    }

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
        IReadOnlyCollection<(IObjectSetDefinition Set, object Root)> navigationRoots,
        bool captureCausalEvidence)
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
        {
            _navigation.RefreshRoot(rootSet, root);
            _projections.RefreshRoot(rootSet, root);
        }
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
        var dependencyPropagation = _dependencyGraph.ApplyChangeImpacts(
            relationImpacts, changes, policyActions, captureCausalEvidence);
        var publicImpact = impact.ToPublic();
        _reindexedRoots += publicImpact.Access.ReindexedRoots;
        _affectedSources += dependencyPropagation.DerivedImpacts
            .SelectMany(value => value.Sources)
            .Distinct(ReferenceEqualityComparer.Instance)
            .Count();
        _relationPairsAdded += relationImpacts.Values.Sum(value => value.AddedPairs.Count);
        _relationPairsRemoved += relationImpacts.Values.Sum(value => value.RemovedPairs.Count);
        _policyRequestsEmitted += policyActions.ImmediateEvaluations.Count + policyActions.RepairRequests.Count;
        return new RuntimeCommitResult(publicImpact, relationImpacts, dependencyPropagation, policyActions);
    }

    private sealed record PreparedMutationExecution(
        RuntimeCommitResult Result,
        RuntimeRollbackJournal? Snapshot,
        RuntimeForwardPatch? PostState);

    private sealed record RuntimeRollbackJournal(RuntimeStateSnapshot State);
    private sealed record RuntimeForwardPatch(RuntimeStateSnapshot State);

    private void ValidateProjectedFinalState(PreparedMutation prepared)
    {
        var needsValidation = prepared.LifecycleMutations.Count > 0 || prepared.Changes.Any(change =>
            change.Set is not null && _projections.IsSelectorChange(change.Set, change.Member));
        if (!needsValidation)
            return;
        _projections.ValidateFinalState(prepared.LifecycleMutations, prepared.Changes);
    }

    private RuntimeApplyResult CreateDetailedResult(
        PreparedMutation prepared,
        RuntimeCommitResult commit,
        RuntimeImpactDetailLevel detailLevel,
        IReadOnlyList<MutationOrigin> origins)
    {
        var relationImpacts = commit.RelationImpacts
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
        var impactIds = detailLevel == RuntimeImpactDetailLevel.Causal
            ? CreateImpactIds(commit)
            : [];
        var derivedImpacts = commit.DependencyPropagation.DerivedImpacts
            .GroupBy(value => value.Definition)
            .Select(group => new DerivedMutationImpact(
                _derivedIds[group.Key],
                CreateSourceImpacts(group, group.Key.SourceSet, group.Key, prepared, commit, detailLevel, origins, impactIds))
            { DefinitionKey = group.Key.DefinitionKey })
            .OrderBy(value => value.DerivedId)
            .ToArray();
        var invariantImpacts = commit.DependencyPropagation.InvariantImpacts
            .GroupBy(value => value.Definition)
            .Select(group => new InvariantMutationImpact(
                _invariantIds[group.Key],
                CreateSourceImpacts(group, group.Key.SourceSet, group.Key, prepared, commit, detailLevel, origins, impactIds))
            { DefinitionKey = group.Key.DefinitionKey })
            .OrderBy(value => value.InvariantId)
            .ToArray();
        var actions = commit.PolicyActions;
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
        return new RuntimeApplyResult(
            commit.Impact,
            relationImpacts,
            derivedImpacts,
            invariantImpacts,
            repairRequests,
            immediateRequests,
            detailLevel,
            origins);
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
        IObjectSetDefinition sourceSet,
        object definition,
        PreparedMutation prepared,
        RuntimeCommitResult commit,
        RuntimeImpactDetailLevel detailLevel,
        IReadOnlyList<MutationOrigin> origins,
        IReadOnlyList<ImpactNodeIdentity> impactIds) where TSnapshot : class
    {
        var values = snapshots.SelectMany(snapshot => snapshot switch
        {
            DerivedImpactSnapshot derived => derived.Sources.Select(source => (source, derived.Severity)),
            InvariantImpactSnapshot invariant => invariant.Sources.Select(source => (source, invariant.Severity)),
            _ => throw new InvalidOperationException("Unsupported dependency impact snapshot.")
        });
        return values.GroupBy(value => value.source, ReferenceEqualityComparer.Instance)
            .Select(group =>
            {
                var severity = group.Any(value => value.Severity == DependencyImpactKind.Invalid)
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty;
                return new SourceDependencyImpact(group.Key, severity)
                {
                    ImpactId = FindImpactId(impactIds, definition, group.Key),
                    SourceIdentity = CreateSourceIdentity(sourceSet, group.Key),
                    Causes = detailLevel == RuntimeImpactDetailLevel.Causal
                        ? CreateCauses(definition, group.Key, severity, prepared, commit, origins, impactIds)
                        : []
                };
            })
            .ToArray();
    }

    private IReadOnlyList<MutationOrigin> CaptureMutationOrigins(PreparedMutation prepared)
    {
        return prepared.Provenance.Select((provenance, id) => new MutationOrigin(
            id,
            provenance.Kind,
            provenance.Source,
            provenance.Member?.Name)
        {
            SourceIdentity = provenance.Set is null
                ? null
                : provenance.Kind == MutationOriginKind.ObjectAdded
                    ? CreateUnregisteredSourceIdentity(provenance.Set, provenance.Source)
                    : TryCreateSourceIdentity(provenance.Set, provenance.Source),
            CollectionKind = provenance.CollectionKind,
            CollectionItem = provenance.CollectionItem
        }).ToArray();
    }

    private SourceIdentity? TryCreateSourceIdentity(IObjectSetDefinition set, object source)
    {
        try { return CreateSourceIdentity(set, source); }
        catch (InvalidOperationException) { return null; }
    }

    private static SourceIdentity CreateUnregisteredSourceIdentity(
        IObjectSetDefinition set,
        object source)
    {
        var key = set.ReadKey(source) ?? throw new InvalidOperationException("Object keys cannot be null.");
        return new SourceIdentity(set.DefinitionKey, set.ObjectType, key)
        {
            DurableIdentity = DurableSourceIdentityFactory.Create(set.DefinitionKey, set.ObjectType, key)
        };
    }

    private IReadOnlyList<DependencyImpactCause> CreateCauses(
        object definition,
        object source,
        DependencySeverity finalSeverity,
        PreparedMutation prepared,
        RuntimeCommitResult commit,
        IReadOnlyList<MutationOrigin> origins,
        IReadOnlyList<ImpactNodeIdentity> impactIds)
    {
        var causes = new List<DependencyImpactCause>();
        var analysis = definition switch
        {
            IDerivedDefinition derived => derived.Analysis,
            IInvariantDefinition invariant => invariant.Analysis,
            _ => throw new InvalidOperationException("Unsupported dependency definition.")
        };
        var directMembers = analysis.Dependencies
            .Where(dependency => dependency.Path.Segments.Count == 1)
            .Select(dependency => dependency.Path.Segments[0].Member)
            .ToHashSet();
        foreach (var change in prepared.Changes.Where(change =>
                     ReferenceEquals(change.Instance, source) && directMembers.Contains(change.Member)))
        {
            var origin = origins.Single(value =>
                ReferenceEquals(value.Source, source) && value.MemberName == change.Member.Name);
            var evidence = commit.DependencyPropagation.DerivedImpacts
                .Where(impact => ReferenceEquals(impact.Definition, definition))
                .SelectMany(impact => impact.DirectEvidence)
                .FirstOrDefault(value => ReferenceEquals(value.Source, source) && value.Member == change.Member);
            causes.Add(new DirectSourceMemberCause(
                origin.OriginId,
                change.Member.Name,
                evidence?.Policy ?? "fixed fallback",
                evidence?.Severity ?? finalSeverity));
        }
        if (definition is IDerivedDefinition derivedDefinition)
        {
            foreach (var input in derivedDefinition.Inputs.OfType<RelationDerivedInput>())
            {
                if (!commit.RelationImpacts.TryGetValue(input.Relation, out var impact) ||
                    !impact.AffectedLefts.Contains(source, ReferenceEqualityComparer.Instance))
                    continue;
                foreach (var group in impact.RouteTriggers
                             .Where(trigger => ReferenceEquals(trigger.Left, source))
                             .GroupBy(trigger => (trigger.Kind, trigger.Precision)))
                    causes.Add(new RelationDependencyCause(
                        _relationIds[input.Relation], group.Key.Kind, group.Key.Precision)
                    {
                        DefinitionKey = input.Relation.DefinitionKey,
                        OriginIds = MapTriggerOrigins(input.Relation, group.ToArray(), origins)
                    });
            }
            foreach (var evidence in commit.DependencyPropagation.UpstreamEvidence.Where(value =>
                         ReferenceEquals(value.Downstream, derivedDefinition) &&
                         ReferenceEquals(value.DownstreamSource, source)))
                causes.Add(new UpstreamDerivedCause(
                    _derivedIds[evidence.Upstream], evidence.Precision)
                {
                    DefinitionKey = evidence.Upstream.DefinitionKey,
                    UpstreamImpactId = FindImpactId(impactIds, evidence.Upstream, evidence.UpstreamSource)
                });
        }
        else if (definition is IInvariantDefinition invariantDefinition)
        {
            foreach (var evidence in commit.DependencyPropagation.InvariantUpstreamEvidence.Where(value =>
                         ReferenceEquals(value.Invariant, invariantDefinition) &&
                         ReferenceEquals(value.InvariantSource, source)))
                causes.Add(new UpstreamDerivedCause(
                    _derivedIds[evidence.Upstream], evidence.Precision)
                {
                    DefinitionKey = evidence.Upstream.DefinitionKey,
                    UpstreamImpactId = FindImpactId(impactIds, evidence.Upstream, evidence.UpstreamSource)
                });
            var inheritedSeverity = commit.DependencyPropagation.DerivedImpacts
                .Where(impact => invariantDefinition.UpstreamDerived.Contains(impact.Definition) &&
                    impact.Sources.Contains(source, ReferenceEqualityComparer.Instance))
                .Select(impact => impact.Severity.ToSeverity())
                .DefaultIfEmpty(DependencySeverity.Dirty)
                .Aggregate((left, right) => left == DependencySeverity.Invalid || right == DependencySeverity.Invalid
                    ? DependencySeverity.Invalid : DependencySeverity.Dirty);
            if (invariantDefinition.Reaction is InvariantReaction.MarkInvalid or InvariantReaction.ScheduleRepair &&
                inheritedSeverity == DependencySeverity.Dirty && finalSeverity == DependencySeverity.Invalid)
                causes.Add(new InvariantReactionCause(
                    invariantDefinition.Reaction, inheritedSeverity, DependencySeverity.Invalid));
        }
        return causes.Distinct().ToArray();
    }

    private static IReadOnlyList<int> MapTriggerOrigins(
        IRelationDefinition relation,
        IReadOnlyCollection<RelationRouteTrigger> routeTriggers,
        IReadOnlyList<MutationOrigin> origins)
    {
        var triggers = routeTriggers.Select(trigger => trigger.Trigger)
            .Concat(routeTriggers.Where(trigger => ReferenceEquals(trigger.Trigger, trigger.Left))
                .Select(trigger => trigger.Left))
            .ToHashSet(ReferenceEqualityComparer.Instance);
        var members = relation.Analysis.DependencyPaths
            .SelectMany(path => path.Segments)
            .Select(segment => segment.Member.Name)
            .ToHashSet(StringComparer.Ordinal);
        return origins.Where(origin => triggers.Contains(origin.Source) &&
                (origin.Kind is MutationOriginKind.ObjectAdded or MutationOriginKind.ObjectRemoved ||
                    origin.MemberName is not null && members.Contains(origin.MemberName)))
            .Select(origin => origin.OriginId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }

    private IReadOnlyList<ImpactNodeIdentity> CreateImpactIds(RuntimeCommitResult commit)
    {
        var nodes = commit.DependencyPropagation.DerivedImpacts
            .SelectMany(impact => impact.Sources.Select(source =>
                new ImpactNodeIdentity(impact.Definition, source, _derivedIds[impact.Definition])))
            .Concat(commit.DependencyPropagation.InvariantImpacts.SelectMany(impact => impact.Sources.Select(source =>
                new ImpactNodeIdentity(impact.Definition, source, _derivedIds.Count + _invariantIds[impact.Definition]))))
            .Distinct(ImpactNodeIdentityComparer.Instance)
            .OrderBy(node => node.DefinitionOrder)
            .ThenBy(node => GetIdentitySortKey(node), StringComparer.Ordinal)
            .ToArray();
        return nodes.Select((node, id) => node with { ImpactId = id }).ToArray();
    }

    private string GetIdentitySortKey(ImpactNodeIdentity node)
    {
        var set = node.Definition switch
        {
            IDerivedDefinition derived => derived.SourceSet,
            IInvariantDefinition invariant => invariant.SourceSet,
            _ => throw new InvalidOperationException("Unsupported impact definition.")
        };
        var identity = CreateSourceIdentity(set, node.Source);
        return identity.DurableIdentity is { } durable
            ? string.Join("|", durable.KeyParts.Select(part => part.Value))
            : identity.SourceKey?.ToString() ?? "";
    }

    private static int? FindImpactId(
        IReadOnlyList<ImpactNodeIdentity> identities,
        object definition,
        object source) => identities.FirstOrDefault(value =>
            ReferenceEquals(value.Definition, definition) && ReferenceEquals(value.Source, source))?.ImpactId;

    private sealed record ImpactNodeIdentity(object Definition, object Source, int DefinitionOrder)
    {
        public int ImpactId { get; init; }
    }

    private sealed class ImpactNodeIdentityComparer : IEqualityComparer<ImpactNodeIdentity>
    {
        public static ImpactNodeIdentityComparer Instance { get; } = new();
        public bool Equals(ImpactNodeIdentity? left, ImpactNodeIdentity? right) =>
            left is not null && right is not null && ReferenceEquals(left.Definition, right.Definition) &&
            ReferenceEquals(left.Source, right.Source);
        public int GetHashCode(ImpactNodeIdentity value) => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Definition),
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Source));
    }

    private void CommitAdd(
        ObjectAdded mutation,
        IDictionary<IRelationDefinition, RelationDelta> deltas)
    {
        var state = GetSet(mutation.Set);
        state.Add(mutation.Instance);
        NotifySourceAdded(mutation.Set, mutation.Instance);
        _navigation.AddRoot(mutation.Set, mutation.Instance);
        _projections.AddRoot(mutation.Set, mutation.Instance);
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
        _projections.RemoveRoot(mutation.Set, mutation.Instance);
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
