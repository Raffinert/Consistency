using System.Reflection;
using System.Runtime.CompilerServices;
using Raffinert.Relations.Expressions;

namespace Raffinert.Relations;

internal sealed class NavigationIndex(MemberInfo member)
{
    private readonly Dictionary<object, ForwardEntry> _forward = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, HashSet<object>> _reverse = new(ReferenceEqualityComparer.Instance);

    public MemberInfo Member { get; } = member;

    public void AddOwner(object owner, object? target)
    {
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

    private sealed class ForwardEntry(object? target)
    {
        public object? Target { get; } = target;
        public int ReferenceCount { get; set; } = 1;
    }
}

internal sealed class NavigationIndexRegistry
{
    private readonly IReadOnlyDictionary<IObjectSetDefinition, ObjectSetRuntime> _sets;
    private readonly Dictionary<IObjectSetDefinition, IReadOnlyList<DependencyPath>> _pathsBySet;
    private readonly Dictionary<MemberInfo, NavigationIndex> _indexes = [];
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
                    ExpressionParameterRole.RelationItem => derived.Relation.RightSet,
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
                paths[invariant.Derived.SourceSet].Add(dependency.Path);
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
            object? owner = root;
            for (var index = 0; index < path.Segments.Count - 1 && owner is not null; index++)
            {
                var segment = path.Segments[index];
                if (!IsReferenceNavigation(segment))
                {
                    owner = ReadMember(segment.Member, owner);
                    continue;
                }

                var navigation = _indexes[segment.Member];
                memberships.Add(new NavigationMembership(navigation, owner));
                owner = ReadMember(segment.Member, owner);
            }
        }

        foreach (var membership in memberships)
            membership.Index.AddOwner(membership.Owner, ReadMember(membership.Index.Member, membership.Owner));
        _registrations[set].Add(root, memberships.ToArray());
    }

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
                if (!IsReferenceNavigation(segment) || !_indexes.TryGetValue(segment.Member, out var navigation))
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
            if (IsReferenceNavigation(segment))
                _indexes.TryAdd(segment.Member, new NavigationIndex(segment.Member));
        }
    }

    private static bool IsReferenceNavigation(DependencyPathSegment segment) =>
        !segment.ValueType.IsValueType && segment.ValueType != typeof(string);

    private static object? ReadMember(MemberInfo member, object instance) => member switch
    {
        PropertyInfo property => property.GetValue(instance),
        FieldInfo field => field.GetValue(instance),
        _ => throw new NotSupportedException($"Member '{member.Name}' is not a property or field.")
    };

    private sealed record NavigationMembership(NavigationIndex Index, object Owner);

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
    private readonly HashSet<IRelationDefinition> _affectedRelations = [];
    private readonly HashSet<IRelationDefinition> _invalidatingRelations = [];
    private readonly Dictionary<IObjectSetDefinition, HashSet<object>> _affectedRoots = [];
    private readonly Dictionary<IRelationDefinition, Dictionary<IObjectSetDefinition, HashSet<object>>> _relationRoots = [];

    public IReadOnlyDictionary<IRelationRuntimeState, HashSet<object>> ReindexRoots => _reindexRoots;
    public IReadOnlyCollection<IRelationDefinition> AffectedRelations => _affectedRelations;
    public IReadOnlyCollection<IRelationDefinition> InvalidatingRelations => _invalidatingRelations;
    public IEnumerable<(IObjectSetDefinition Set, object Root)> AffectedRoots =>
        _affectedRoots.SelectMany(pair => pair.Value.Select(root => (pair.Key, root)));

    public IReadOnlyCollection<object> GetAffectedRoots(
        IRelationDefinition relation,
        IObjectSetDefinition set) =>
        _relationRoots.TryGetValue(relation, out var bySet) && bySet.TryGetValue(set, out var roots)
            ? roots
            : [];

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

    public void AddInvalidatingRelation(IRelationDefinition relation) =>
        _invalidatingRelations.Add(relation);

    public void MergeFrom(ResolvedChangeImpact other)
    {
        foreach (var relation in other._affectedRelations)
            _affectedRelations.Add(relation);
        foreach (var relation in other._invalidatingRelations)
            _invalidatingRelations.Add(relation);
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
    }

    public ChangeImpact ToPublic() => new(
        new AccessImpact(
            _reindexRoots.Count(pair => pair.Value.Count > 0),
            _reindexRoots.Sum(pair => pair.Value.Count)),
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
                if (roots.Count > 0 && IsJoinKeyPath(relation, path))
                    impact.AddInvalidatingRelation(relation);
            }

            if (relation.AccessPlan is not HashJoinAccessPlan hashPlan)
                continue;
            foreach (var keyPart in hashPlan.JoinKeyParts)
            {
                var path = new DependencyPath(1, relation.RightSet.ObjectType, keyPart.Right.Members);
                impact.AddAccess(
                    runtimeRelations[relation],
                    navigation.ResolveRoots(relation.RightSet, path, change.Instance, change.Member));
            }
        }
        return impact;
    }

    private static bool IsJoinKeyPath(IRelationDefinition relation, DependencyPath path) =>
        relation.Analysis.JoinKeyParts.Any(part =>
            path.RootParameterIndex == 0 && path.Segments.Select(segment => segment.Member).SequenceEqual(part.Left.Members) ||
            path.RootParameterIndex == 1 && path.Segments.Select(segment => segment.Member).SequenceEqual(part.Right.Members));
}
