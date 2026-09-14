namespace Raffinert.Relations;

internal sealed record RelationPair(object Left, object Right);
internal sealed record RelationRouteTrigger(
    object Left,
    object Trigger,
    RelationImpactCauseKind Kind,
    ImpactCausePrecision Precision);

internal sealed class RelationDelta
{
    private readonly List<RelationPair> _addedPairs = [];
    private readonly List<RelationPair> _removedPairs = [];
    private readonly HashSet<object> _affectedLefts = new(ReferenceEqualityComparer.Instance);
    private readonly List<RelationRouteTrigger> _routeTriggers = [];

    public IReadOnlyList<RelationPair> AddedPairs => _addedPairs;
    public IReadOnlyList<RelationPair> RemovedPairs => _removedPairs;
    public IReadOnlyCollection<object> AffectedLefts => _affectedLefts;
    public IReadOnlyList<RelationRouteTrigger> RouteTriggers => _routeTriggers;

    public void Add(object left, object right)
    {
        _addedPairs.Add(new RelationPair(left, right));
        _affectedLefts.Add(left);
        _routeTriggers.Add(new RelationRouteTrigger(
            left, right, RelationImpactCauseKind.MembershipAdded, ImpactCausePrecision.Exact));
    }

    public void Remove(object left, object right)
    {
        _removedPairs.Add(new RelationPair(left, right));
        _affectedLefts.Add(left);
        _routeTriggers.Add(new RelationRouteTrigger(
            left, right, RelationImpactCauseKind.MembershipRemoved, ImpactCausePrecision.Exact));
    }

    public void Affect(object left) => _affectedLefts.Add(left);

    public void Affect(object left, object trigger)
    {
        _affectedLefts.Add(left);
        _routeTriggers.Add(new RelationRouteTrigger(
            left, trigger, RelationImpactCauseKind.ConservativeCandidate, ImpactCausePrecision.Conservative));
    }

    public void MergeFrom(RelationDelta other)
    {
        _addedPairs.AddRange(other._addedPairs);
        _removedPairs.AddRange(other._removedPairs);
        _affectedLefts.UnionWith(other._affectedLefts);
        _routeTriggers.AddRange(other._routeTriggers);
    }
}

internal sealed class RelationImpact
{
    public RelationImpact(
        IRelationDefinition relation,
        RelationDelta delta,
        IEnumerable<object> semanticLefts,
        IEnumerable<object> semanticRights,
        IEnumerable<object> reindexedLefts,
        IEnumerable<object> reindexedRights)
    {
        Relation = relation;
        AddedPairs = Array.AsReadOnly(delta.AddedPairs.ToArray());
        RemovedPairs = Array.AsReadOnly(delta.RemovedPairs.ToArray());
        AffectedLefts = Distinct(delta.AffectedLefts);
        SemanticLefts = Distinct(semanticLefts);
        SemanticRights = Distinct(semanticRights);
        ReindexedLefts = Distinct(reindexedLefts);
        ReindexedRights = Distinct(reindexedRights);
        RouteTriggers = Array.AsReadOnly(delta.RouteTriggers.ToArray());
        var affectedRights = new HashSet<object>(SemanticRights, ReferenceEqualityComparer.Instance);
        affectedRights.UnionWith(AddedPairs.Select(pair => pair.Right));
        affectedRights.UnionWith(RemovedPairs.Select(pair => pair.Right));
        AffectedRights = Distinct(affectedRights);
    }

    public IRelationDefinition Relation { get; }
    public IReadOnlyList<RelationPair> AddedPairs { get; }
    public IReadOnlyList<RelationPair> RemovedPairs { get; }
    public IReadOnlyCollection<object> AffectedLefts { get; }
    public IReadOnlyCollection<object> AffectedRights { get; }
    public IReadOnlyCollection<object> SemanticLefts { get; }
    public IReadOnlyCollection<object> SemanticRights { get; }
    public IReadOnlyCollection<object> ReindexedLefts { get; }
    public IReadOnlyCollection<object> ReindexedRights { get; }
    public IReadOnlyList<RelationRouteTrigger> RouteTriggers { get; }
    public bool HasMembershipChanges => AddedPairs.Count > 0 || RemovedPairs.Count > 0;

    public static RelationImpact FromDelta(IRelationDefinition relation, RelationDelta delta) =>
        new(relation, delta, [], [], [], []);

    private static IReadOnlyCollection<object> Distinct(IEnumerable<object> values) =>
        Array.AsReadOnly(values.Distinct(ReferenceEqualityComparer.Instance).ToArray());
}
