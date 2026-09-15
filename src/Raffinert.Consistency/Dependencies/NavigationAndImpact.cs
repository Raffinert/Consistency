using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency;

internal interface INavigationIndex
{
    MemberInfo Member { get; }
    void AddOwner(object owner);
    void RemoveOwner(object owner);
    IReadOnlyCollection<object> GetOwners(object target);
    IReadOnlyCollection<object> GetTargets(object owner);
    object CaptureState();
    void RestoreState(object snapshot);
    object CaptureOwnersState(IEnumerable<object> owners);
    void RestoreOwnersState(object state);
    int GetOwnersStateEntryCount(object state);
}

internal sealed class NavigationIndex(MemberInfo member) : INavigationIndex
{
    private readonly Dictionary<object, ForwardEntry> _forward = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, HashSet<object>> _reverse = new(ReferenceEqualityComparer.Instance);

    public MemberInfo Member { get; } = member;

    public void AddOwner(object owner)
    {
        var target = NavigationIndexRegistry.ReadMember(Member, owner);
        if (_forward.TryGetValue(owner, out var existing))
        {
            if (!ReferenceEquals(existing.Target, target))
                throw new InvalidOperationException($"Navigation '{Member.Name}' changed without its affected roots being refreshed.");
            existing.ReferenceCount++;
            return;
        }

        _forward.Add(owner, new ForwardEntry(target));
        if (target is null)
            return;
        if (!_reverse.TryGetValue(target, out var owners))
            _reverse.Add(target, owners = new HashSet<object>(ReferenceEqualityComparer.Instance));
        owners.Add(owner);
    }

    public void RemoveOwner(object owner)
    {
        if (!_forward.TryGetValue(owner, out var existing))
            return;
        if (--existing.ReferenceCount > 0)
            return;

        _forward.Remove(owner);
        if (existing.Target is null || !_reverse.TryGetValue(existing.Target, out var owners))
            return;
        owners.Remove(owner);
        if (owners.Count == 0)
            _reverse.Remove(existing.Target);
    }

    public IReadOnlyCollection<object> GetOwners(object target) =>
        _reverse.TryGetValue(target, out var owners) ? owners : [];

    public IReadOnlyCollection<object> GetTargets(object owner) =>
        _forward.TryGetValue(owner, out var entry) && entry.Target is not null ? [entry.Target] : [];

    public object CaptureState() => new State(
        _forward.ToDictionary(
            pair => pair.Key,
            pair => new ForwardEntry(pair.Value.Target) { ReferenceCount = pair.Value.ReferenceCount },
            ReferenceEqualityComparer.Instance),
        _reverse.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToHashSet(ReferenceEqualityComparer.Instance),
            ReferenceEqualityComparer.Instance));

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        Replace(_forward, state.Forward);
        Replace(_reverse, state.Reverse);
    }

    public object CaptureOwnersState(IEnumerable<object> owners) => owners
        .Distinct(ReferenceEqualityComparer.Instance)
        .ToDictionary(
            owner => owner,
            owner => _forward.TryGetValue(owner, out var entry)
                ? new OwnerState(true, entry.Target, entry.ReferenceCount)
                : new OwnerState(false, null, 0),
            ReferenceEqualityComparer.Instance);

    public void RestoreOwnersState(object snapshot)
    {
        var states = (IReadOnlyDictionary<object, OwnerState>)snapshot;
        foreach (var pair in states)
        {
            RemoveOwnerExactly(pair.Key);
            if (!pair.Value.Exists)
                continue;
            _forward.Add(pair.Key, new ForwardEntry(pair.Value.Target)
            {
                ReferenceCount = pair.Value.ReferenceCount
            });
            if (pair.Value.Target is not null)
            {
                if (!_reverse.TryGetValue(pair.Value.Target, out var owners))
                    _reverse.Add(pair.Value.Target,
                        owners = new HashSet<object>(ReferenceEqualityComparer.Instance));
                owners.Add(pair.Key);
            }
        }
    }

    public int GetOwnersStateEntryCount(object state) =>
        ((IReadOnlyDictionary<object, OwnerState>)state).Count;

    private void RemoveOwnerExactly(object owner)
    {
        if (!_forward.Remove(owner, out var entry) || entry.Target is null ||
            !_reverse.TryGetValue(entry.Target, out var owners))
            return;
        owners.Remove(owner);
        if (owners.Count == 0)
            _reverse.Remove(entry.Target);
    }

    private static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> source)
        where TKey : notnull
    {
        target.Clear();
        foreach (var pair in source)
            target.Add(pair.Key, pair.Value);
    }

    private sealed record State(
        Dictionary<object, ForwardEntry> Forward,
        Dictionary<object, HashSet<object>> Reverse);

    private sealed record OwnerState(bool Exists, object? Target, int ReferenceCount);

    private sealed class ForwardEntry(object? target)
    {
        public object? Target { get; } = target;
        public int ReferenceCount { get; set; } = 1;
    }
}

internal sealed class CollectionNavigationIndex(MemberInfo member) : INavigationIndex
{
    private readonly Dictionary<object, ForwardEntry> _forward = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, HashSet<object>> _reverse = new(ReferenceEqualityComparer.Instance);

    public MemberInfo Member { get; } = member;

    public void AddOwner(object owner)
    {
        if (_forward.TryGetValue(owner, out var existing))
        {
            existing.ReferenceCount++;
            return;
        }
        var items = ReadItems(owner).ToHashSet(ReferenceEqualityComparer.Instance);
        _forward.Add(owner, new ForwardEntry(items));
        foreach (var item in items)
        {
            if (!_reverse.TryGetValue(item, out var owners))
                _reverse.Add(item, owners = new HashSet<object>(ReferenceEqualityComparer.Instance));
            owners.Add(owner);
        }
    }

    public void RemoveOwner(object owner)
    {
        if (!_forward.TryGetValue(owner, out var entry))
            return;
        if (--entry.ReferenceCount > 0)
            return;
        _forward.Remove(owner);
        foreach (var item in entry.Items)
        {
            var owners = _reverse[item];
            owners.Remove(owner);
            if (owners.Count == 0)
                _reverse.Remove(item);
        }
    }

    public IReadOnlyCollection<object> GetOwners(object target) =>
        _reverse.TryGetValue(target, out var owners) ? owners : [];

    public IReadOnlyCollection<object> GetTargets(object owner) =>
        _forward.TryGetValue(owner, out var entry) ? entry.Items : [];

    public object CaptureState() => new State(
        _forward.ToDictionary(
            pair => pair.Key,
            pair => new ForwardEntry(pair.Value.Items.ToHashSet(ReferenceEqualityComparer.Instance))
            {
                ReferenceCount = pair.Value.ReferenceCount
            },
            ReferenceEqualityComparer.Instance),
        _reverse.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToHashSet(ReferenceEqualityComparer.Instance),
            ReferenceEqualityComparer.Instance));

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        Replace(_forward, state.Forward);
        Replace(_reverse, state.Reverse);
    }

    public object CaptureOwnersState(IEnumerable<object> owners) => owners
        .Distinct(ReferenceEqualityComparer.Instance)
        .ToDictionary(
            owner => owner,
            owner => _forward.TryGetValue(owner, out var entry)
                ? new OwnerState(
                    true,
                    entry.Items.ToHashSet(ReferenceEqualityComparer.Instance),
                    entry.ReferenceCount)
                : new OwnerState(false, [], 0),
            ReferenceEqualityComparer.Instance);

    public void RestoreOwnersState(object snapshot)
    {
        var states = (IReadOnlyDictionary<object, OwnerState>)snapshot;
        foreach (var pair in states)
        {
            RemoveOwnerExactly(pair.Key);
            if (!pair.Value.Exists)
                continue;
            var items = pair.Value.Items.ToHashSet(ReferenceEqualityComparer.Instance);
            _forward.Add(pair.Key, new ForwardEntry(items) { ReferenceCount = pair.Value.ReferenceCount });
            foreach (var item in items)
            {
                if (!_reverse.TryGetValue(item, out var owners))
                    _reverse.Add(item, owners = new HashSet<object>(ReferenceEqualityComparer.Instance));
                owners.Add(pair.Key);
            }
        }
    }

    public int GetOwnersStateEntryCount(object state)
    {
        var states = (IReadOnlyDictionary<object, OwnerState>)state;
        return states.Count + states.Values.Where(value => value.Exists).Sum(value => value.Items.Count);
    }

    private void RemoveOwnerExactly(object owner)
    {
        if (!_forward.Remove(owner, out var entry))
            return;
        foreach (var item in entry.Items)
        {
            var owners = _reverse[item];
            owners.Remove(owner);
            if (owners.Count == 0)
                _reverse.Remove(item);
        }
    }

    private static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target, Dictionary<TKey, TValue> source)
        where TKey : notnull
    {
        target.Clear();
        foreach (var pair in source)
            target.Add(pair.Key, pair.Value);
    }

    private sealed record State(
        Dictionary<object, ForwardEntry> Forward,
        Dictionary<object, HashSet<object>> Reverse);

    private sealed record OwnerState(bool Exists, HashSet<object> Items, int ReferenceCount);

    private IEnumerable<object> ReadItems(object owner)
    {
        if (NavigationIndexRegistry.ReadMember(Member, owner) is not System.Collections.IEnumerable collection)
            yield break;
        foreach (var item in collection)
            if (item is not null)
                yield return item;
    }

    private sealed class ForwardEntry(HashSet<object> items)
    {
        public HashSet<object> Items { get; } = items;
        public int ReferenceCount { get; set; } = 1;
    }
}

internal sealed class NavigationIndexRegistry
{
    private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
    private readonly Dictionary<IObjectSetDefinition, IReadOnlyList<DependencyPath>> _pathsBySet;
    private readonly Dictionary<MemberInfo, INavigationIndex> _indexes = [];
    private readonly Dictionary<IObjectSetDefinition, Dictionary<object, IReadOnlyList<NavigationMembership>>> _registrations;

    public NavigationIndexRegistry(
        IReadOnlyList<IObjectSetDefinition> sets,
        IReadOnlyList<IRelationDefinition> relations,
        IReadOnlyList<IDerivedDefinition> derivedStates,
        IReadOnlyList<IInvariantDefinition> invariants,
        IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> runtimeSets)
    {
        _sets = runtimeSets;
        _registrations = sets.ToDictionary(
            set => set,
            _ => new Dictionary<object, IReadOnlyList<NavigationMembership>>(ReferenceEqualityComparer.Instance));

        var paths = sets.ToDictionary(set => set, _ => new List<DependencyPath>());
        foreach (var relation in relations)
        {
            foreach (var path in relation.Analysis.DependencyPaths)
            {
                var rootSet = path.RootParameterIndex == 0 ? relation.LeftSet : relation.RightSet;
                paths[rootSet].Add(path);
                EnsurePathIndexes(path);
            }
        }
        foreach (var derived in derivedStates)
        {
            foreach (var dependency in derived.Analysis.Dependencies)
            {
                var rootSet = dependency.Role switch
                {
                    ExpressionParameterRole.DerivedSource => derived.SourceSet,
                    ExpressionParameterRole.RelationItem => derived.Inputs
                        .OfType<RelationDerivedInput>()
                        .Select(input => input.Relation)
                        .Single().RightSet,
                    _ => null
                };
                if (rootSet is null)
                    continue;
                paths[rootSet].Add(dependency.Path);
                EnsurePathIndexes(dependency.Path);
            }
        }
        foreach (var invariant in invariants)
        {
            foreach (var dependency in invariant.Analysis.Dependencies
                         .Where(dependency => dependency.Role == ExpressionParameterRole.InvariantSource))
            {
                paths[invariant.SourceSet].Add(dependency.Path);
                EnsurePathIndexes(dependency.Path);
            }
        }
        _pathsBySet = paths.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<DependencyPath>)pair.Value);
    }

    public void AddRoot(IObjectSetDefinition set, object root)
    {
        var memberships = new HashSet<NavigationMembership>(NavigationMembershipComparer.Instance);
        foreach (var path in _pathsBySet[set])
        {
            IReadOnlyCollection<object> owners = [root];
            for (var index = 0; index < path.Segments.Count - 1 && owners.Count > 0; index++)
            {
                var segment = path.Segments[index];
                if (!IsNavigation(segment))
                {
                    owners = owners
                        .Select(owner => ReadMember(segment.Member, owner))
                        .Where(value => value is not null)
                        .Cast<object>()
                        .Distinct(ReferenceEqualityComparer.Instance)
                        .ToArray();
                    continue;
                }

                var navigation = _indexes[segment.Member];
                foreach (var owner in owners)
                {
                    if (memberships.Add(new NavigationMembership(navigation, owner)))
                        navigation.AddOwner(owner);
                }
                owners = owners
                    .SelectMany(navigation.GetTargets)
                    .Distinct(ReferenceEqualityComparer.Instance)
                    .ToArray();
            }
        }

        _registrations[set].Add(root, memberships.ToArray());
    }

    public bool IsIndexedNavigation(MemberInfo member) => _indexes.ContainsKey(member);

    internal IReadOnlyCollection<object> GetOwners(MemberInfo member, object target) =>
        _indexes.TryGetValue(member, out var index) ? index.GetOwners(target) : [];

    public void RemoveRoot(IObjectSetDefinition set, object root)
    {
        if (!_registrations[set].Remove(root, out var memberships))
            return;
        foreach (var membership in memberships)
            membership.Index.RemoveOwner(membership.Owner);
    }

    public void RefreshRoot(IObjectSetDefinition set, object root)
    {
        if (!_registrations[set].ContainsKey(root))
            return;
        RemoveRoot(set, root);
        AddRoot(set, root);
    }

    public object CaptureTouchedState(
        IEnumerable<(IObjectSetDefinition Set, object Root)> touchedRoots,
        IEnumerable<(MemberInfo Member, object Owner)> touchedOwners,
        object? previousState = null)
    {
        var roots = new List<(IObjectSetDefinition Set, object Root)>();
        AddRoots(touchedRoots);
        var previous = previousState as TouchedState;
        if (previous is not null)
            AddRoots(previous.Roots.Select(root => (root.Set, root.Root)));

        var rootStates = roots.Select(value =>
        {
            var exists = _registrations[value.Set].TryGetValue(value.Root, out var memberships);
            return new RootState(value.Set, value.Root, exists, memberships?.ToArray() ?? []);
        }).ToArray();
        var owners = new Dictionary<INavigationIndex, HashSet<object>>();
        foreach (var membership in rootStates.SelectMany(root => root.Memberships))
            AddOwner(membership.Index, membership.Owner);
        foreach (var (member, owner) in touchedOwners)
            if (_indexes.TryGetValue(member, out var index))
                AddOwner(index, owner);
        if (previous is not null)
            foreach (var index in previous.Indexes)
                foreach (var owner in index.Owners)
                    AddOwner(index.Index, owner);
        return new TouchedState(
            rootStates,
            owners.Select(pair => new IndexState(
                pair.Key, pair.Value.ToArray(), pair.Key.CaptureOwnersState(pair.Value))).ToArray());

        void AddRoots(IEnumerable<(IObjectSetDefinition Set, object Root)> candidates)
        {
            foreach (var candidate in candidates)
                if (!roots.Any(existing => ReferenceEquals(existing.Set, candidate.Set) &&
                        ReferenceEquals(existing.Root, candidate.Root)))
                    roots.Add(candidate);
        }

        void AddOwner(INavigationIndex index, object owner)
        {
            if (!owners.TryGetValue(index, out var values))
                owners.Add(index, values = new HashSet<object>(ReferenceEqualityComparer.Instance));
            values.Add(owner);
        }
    }

    public void RestoreTouchedState(object snapshot)
    {
        var state = (TouchedState)snapshot;
        foreach (var index in state.Indexes)
            index.Index.RestoreOwnersState(index.State);
        foreach (var root in state.Roots)
        {
            if (root.Exists)
                _registrations[root.Set][root.Root] = root.Memberships;
            else
                _registrations[root.Set].Remove(root.Root);
        }
    }

    public int GetTouchedStateEntryCount(object snapshot)
    {
        var state = (TouchedState)snapshot;
        return state.Roots.Count + state.Roots.Sum(root => root.Memberships.Count) +
            state.Indexes.Sum(index => index.Index.GetOwnersStateEntryCount(index.State));
    }

    public object CaptureState() => new State(
        _indexes.ToDictionary(pair => pair.Key, pair => pair.Value.CaptureState()),
        _registrations.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToDictionary(
                registration => registration.Key,
                registration => registration.Value,
                ReferenceEqualityComparer.Instance)));

    public void RestoreState(object snapshot)
    {
        var state = (State)snapshot;
        foreach (var pair in state.Indexes)
            _indexes[pair.Key].RestoreState(pair.Value);
        foreach (var pair in _registrations)
        {
            pair.Value.Clear();
            foreach (var registration in state.Registrations[pair.Key])
                pair.Value.Add(registration.Key, registration.Value);
        }
    }

    public IReadOnlyCollection<object> ResolveRoots(
        IObjectSetDefinition rootSet,
        DependencyPath path,
        object changedInstance,
        MemberInfo changedMember)
    {
        var roots = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var changedIndex = 0; changedIndex < path.Segments.Count; changedIndex++)
        {
            var changedSegment = path.Segments[changedIndex];
            if (changedSegment.Member != changedMember ||
                !changedSegment.DeclaringType.IsInstanceOfType(changedInstance))
                continue;

            IEnumerable<object> candidates = [changedInstance];
            for (var index = changedIndex - 1; index >= 0; index--)
            {
                var segment = path.Segments[index];
                if (!IsNavigation(segment) || !_indexes.TryGetValue(segment.Member, out var navigation))
                {
                    candidates = [];
                    break;
                }
                candidates = candidates
                    .SelectMany(navigation.GetOwners)
                    .Distinct(ReferenceEqualityComparer.Instance)
                    .ToArray();
            }

            foreach (var candidate in candidates)
            {
                if (_registrations[rootSet].ContainsKey(candidate))
                    roots.Add(candidate);
            }
        }
        return roots;
    }

    private void EnsurePathIndexes(DependencyPath path)
    {
        for (var index = 0; index < path.Segments.Count - 1; index++)
        {
            var segment = path.Segments[index];
            if (IsNavigation(segment))
                _indexes.TryAdd(segment.Member, IsCollection(segment.Member)
                    ? new CollectionNavigationIndex(segment.Member)
                    : new NavigationIndex(segment.Member));
        }
    }

    private static bool IsNavigation(DependencyPathSegment segment) =>
        IsCollection(segment.Member) ||
        (!segment.ValueType.IsValueType && segment.ValueType != typeof(string));

    private static bool IsCollection(MemberInfo member)
    {
        var type = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => typeof(object)
        };
        return type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    internal static object? ReadMember(MemberInfo member, object instance) =>
        MemberReader.Read(member, instance);

    private sealed record NavigationMembership(INavigationIndex Index, object Owner);

    private sealed record RootState(
        IObjectSetDefinition Set,
        object Root,
        bool Exists,
        IReadOnlyList<NavigationMembership> Memberships);

    private sealed record IndexState(INavigationIndex Index, IReadOnlyList<object> Owners, object State);

    private sealed record TouchedState(IReadOnlyList<RootState> Roots, IReadOnlyList<IndexState> Indexes);

    private sealed record State(
        IReadOnlyDictionary<MemberInfo, object> Indexes,
        IReadOnlyDictionary<IObjectSetDefinition, Dictionary<object, IReadOnlyList<NavigationMembership>>> Registrations);

    private sealed class NavigationMembershipComparer : IEqualityComparer<NavigationMembership>
    {
        public static NavigationMembershipComparer Instance { get; } = new();

        public bool Equals(NavigationMembership? x, NavigationMembership? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null && ReferenceEquals(x.Index, y.Index) && ReferenceEquals(x.Owner, y.Owner));

        public int GetHashCode(NavigationMembership obj) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Index), RuntimeHelpers.GetHashCode(obj.Owner));
    }
}

internal sealed class ResolvedChangeImpact
{
    private readonly Dictionary<IRelationRuntimeState, HashSet<object>> _reindexRoots = [];
    private readonly Dictionary<IRelationRuntimeState, HashSet<object>> _reindexLeftRoots = [];
    private readonly HashSet<IRelationDefinition> _affectedRelations = [];
    private readonly Dictionary<IObjectSetDefinition, HashSet<object>> _affectedRoots = [];
    private readonly Dictionary<IRelationDefinition, Dictionary<IObjectSetDefinition, HashSet<object>>> _relationRoots = [];

    public IReadOnlyDictionary<IRelationRuntimeState, HashSet<object>> ReindexRoots => _reindexRoots;
    public IReadOnlyDictionary<IRelationRuntimeState, HashSet<object>> ReindexLeftRoots => _reindexLeftRoots;
    public IReadOnlyCollection<IRelationDefinition> AffectedRelations => _affectedRelations;
    public IEnumerable<(IObjectSetDefinition Set, object Root)> AffectedRoots =>
        _affectedRoots.SelectMany(pair => pair.Value.Select(root => (pair.Key, root)));

    public IReadOnlyCollection<object> GetAffectedRoots(
        IRelationDefinition relation,
        IObjectSetDefinition set) =>
        _relationRoots.TryGetValue(relation, out var bySet) && bySet.TryGetValue(set, out var roots)
            ? roots
            : [];

    public IReadOnlyDictionary<IRelationDefinition, RelationImpact> CreateRelationImpacts(
        IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> runtimeRelations,
        IReadOnlyDictionary<IRelationDefinition, RelationDelta> deltas)
    {
        var impacts = new Dictionary<IRelationDefinition, RelationImpact>();
        foreach (var pair in runtimeRelations)
        {
            var relation = pair.Key;
            var state = pair.Value;
            deltas.TryGetValue(relation, out var delta);
            var semanticLefts = GetAffectedRoots(relation, relation.LeftSet);
            var semanticRights = GetAffectedRoots(relation, relation.RightSet);
            var reindexedLefts = _reindexLeftRoots.TryGetValue(state, out var lefts) ? lefts : [];
            var reindexedRights = _reindexRoots.TryGetValue(state, out var rights) ? rights : [];
            if (delta is null && semanticLefts.Count == 0 && semanticRights.Count == 0 &&
                reindexedLefts.Count == 0 && reindexedRights.Count == 0)
                continue;
            impacts.Add(relation, new RelationImpact(
                relation,
                delta ?? new RelationDelta(),
                semanticLefts,
                semanticRights,
                reindexedLefts,
                reindexedRights));
        }
        return impacts;
    }

    public void AddSemantic(IRelationDefinition relation, IObjectSetDefinition set, IEnumerable<object> roots)
    {
        var materialized = roots.ToArray();
        if (materialized.Length == 0)
            return;
        _affectedRelations.Add(relation);
        if (!_affectedRoots.TryGetValue(set, out var affected))
            _affectedRoots.Add(set, affected = new HashSet<object>(ReferenceEqualityComparer.Instance));
        affected.UnionWith(materialized);
        if (!_relationRoots.TryGetValue(relation, out var bySet))
            _relationRoots.Add(relation, bySet = []);
        if (!bySet.TryGetValue(set, out var relationAffected))
            bySet.Add(set, relationAffected = new HashSet<object>(ReferenceEqualityComparer.Instance));
        relationAffected.UnionWith(materialized);
    }

    public void AddAccess(IRelationRuntimeState relation, IEnumerable<object> roots)
    {
        if (!_reindexRoots.TryGetValue(relation, out var affected))
            _reindexRoots.Add(relation, affected = new HashSet<object>(ReferenceEqualityComparer.Instance));
        affected.UnionWith(roots);
    }

    public void AddLeftAccess(IRelationRuntimeState relation, IEnumerable<object> roots)
    {
        if (!_reindexLeftRoots.TryGetValue(relation, out var affected))
            _reindexLeftRoots.Add(relation, affected = new HashSet<object>(ReferenceEqualityComparer.Instance));
        affected.UnionWith(roots);
    }

    public void MergeFrom(ResolvedChangeImpact other)
    {
        foreach (var relation in other._affectedRelations)
            _affectedRelations.Add(relation);
        foreach (var pair in other._affectedRoots)
        {
            if (!_affectedRoots.TryGetValue(pair.Key, out var roots))
                _affectedRoots.Add(pair.Key, roots = new HashSet<object>(ReferenceEqualityComparer.Instance));
            roots.UnionWith(pair.Value);
        }
        foreach (var relation in other._relationRoots)
        {
            if (!_relationRoots.TryGetValue(relation.Key, out var bySet))
                _relationRoots.Add(relation.Key, bySet = []);
            foreach (var pair in relation.Value)
            {
                if (!bySet.TryGetValue(pair.Key, out var roots))
                    bySet.Add(pair.Key, roots = new HashSet<object>(ReferenceEqualityComparer.Instance));
                roots.UnionWith(pair.Value);
            }
        }
        foreach (var pair in other._reindexRoots)
        {
            if (!_reindexRoots.TryGetValue(pair.Key, out var roots))
                _reindexRoots.Add(pair.Key, roots = new HashSet<object>(ReferenceEqualityComparer.Instance));
            roots.UnionWith(pair.Value);
        }
        foreach (var pair in other._reindexLeftRoots)
        {
            if (!_reindexLeftRoots.TryGetValue(pair.Key, out var roots))
                _reindexLeftRoots.Add(pair.Key, roots = new HashSet<object>(ReferenceEqualityComparer.Instance));
            roots.UnionWith(pair.Value);
        }
    }

    public ChangeImpact ToPublic() => new(
        new AccessImpact(
            _reindexRoots.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key)
                .Concat(_reindexLeftRoots.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key))
                .Distinct()
                .Count(),
            _reindexRoots.Sum(pair => pair.Value.Count) + _reindexLeftRoots.Sum(pair => pair.Value.Count)),
        new SemanticImpact(
            _affectedRelations.Count,
            _affectedRoots.Sum(pair => pair.Value.Count)));
}

/// <summary>Describes the index maintenance caused by an applied change.</summary>
public sealed record AccessImpact(int ReindexedRelations, int ReindexedRoots);

/// <summary>Describes relation semantics that may have changed, independently of index maintenance.</summary>
public sealed record SemanticImpact(int AffectedRelations, int AffectedRoots);

/// <summary>Summarizes both access-index and semantic effects of an applied change.</summary>
public sealed record ChangeImpact(AccessImpact Access, SemanticImpact Semantic);

/// <summary>Counts propagation work to make precision regressions observable.</summary>
/// <summary>How relation membership is retained for dependency propagation.</summary>
public enum RelationMaterializationMode
{
    None,
    ExactPropagation
}

/// <summary>Configurable warning thresholds for runtime diagnostic snapshots.</summary>
public sealed class RuntimeDiagnosticOptions
{
    public int MaterializedPairWarningThreshold { get; init; } = 100_000;
    public double AverageFanOutWarningThreshold { get; init; } = 1_000;

    internal void Validate()
    {
        if (MaterializedPairWarningThreshold <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaterializedPairWarningThreshold));
        if (AverageFanOutWarningThreshold <= 0 || double.IsNaN(AverageFanOutWarningThreshold))
            throw new ArgumentOutOfRangeException(nameof(AverageFanOutWarningThreshold));
    }
}

/// <summary>A structural and density snapshot for one runtime relation.</summary>
public sealed record RelationRuntimeDiagnostics(
    int RelationId,
    Type LeftType,
    Type RightType,
    RelationMaterializationMode Materialization,
    int ForwardIndexEntries,
    int ReverseIndexEntries,
    int MaterializedPairCount,
    double AverageFanOut,
    bool HasDensityWarning);

/// <summary>Accumulated execution counters and current relation materialization statistics.</summary>
public sealed record RuntimeDiagnostics(
    long PredicateEvaluations,
    long ReindexedRoots,
    long AffectedSources,
    long RelationPairsAdded,
    long RelationPairsRemoved,
    long DerivedFullRecomputations,
    long IncrementalDerivedUpdates,
    long PolicyRequestsEmitted,
    IReadOnlyList<RelationRuntimeDiagnostics> Relations)
{
    public int ProjectedDependencyConsumerCount { get; init; }
    public int ProjectionIndexCount { get; init; }
    public int ReverseProjectionEntryCount { get; init; }
    public int ProjectedTargetCount { get; init; }
}

internal sealed class ImpactResolver(
    IReadOnlyList<IRelationDefinition> relations,
    IReadOnlyDictionary<IRelationDefinition, IRelationRuntimeState> runtimeRelations,
    NavigationIndexRegistry navigation)
{
    public ResolvedChangeImpact Resolve(PropertyChange change)
    {
        var impact = new ResolvedChangeImpact();
        foreach (var relation in relations)
        {
            foreach (var path in relation.Analysis.DependencyPaths)
            {
                var rootSet = path.RootParameterIndex == 0 ? relation.LeftSet : relation.RightSet;
                var roots = navigation.ResolveRoots(rootSet, path, change.Instance, change.Member);
                impact.AddSemantic(relation, rootSet, roots);
            }

            if (relation.AccessPlan is HashJoinAccessPlan hashPlan)
                foreach (var keyPart in hashPlan.JoinKeyParts)
                {
                    var path = new DependencyPath(1, relation.RightSet.ObjectType, keyPart.Right.Members);
                    impact.AddAccess(
                        runtimeRelations[relation],
                        navigation.ResolveRoots(relation.RightSet, path, change.Instance, change.Member));
                }

            if (relation.ReverseAccessPlan is not HashJoinAccessPlan reverseHashPlan)
                continue;
            foreach (var keyPart in reverseHashPlan.JoinKeyParts)
            {
                var path = new DependencyPath(0, relation.LeftSet.ObjectType, keyPart.Left.Members);
                impact.AddLeftAccess(
                    runtimeRelations[relation],
                    navigation.ResolveRoots(relation.LeftSet, path, change.Instance, change.Member));
            }
        }
        return impact;
    }
}
