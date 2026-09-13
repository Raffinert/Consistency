namespace Raffinert.Relations;

public enum RelationAccessPlanKind
{
    Scan,
    HashJoin
}

[Flags]
public enum DependencyCompletenessIssue
{
    None = 0,
    OpaqueCode = 1,
    ExternalState = 2
}

[Flags]
public enum DerivedLinqSemantics
{
    None = 0,
    Membership = 1,
    Item = 2,
    Ordering = 4
}

public sealed record ObjectSetModelDiagnostics(
    int ObjectSetId,
    Type ObjectType,
    string KeyExpression)
{
    public string? DefinitionKey { get; init; }
}

public sealed record RelationModelDiagnostics(
    int RelationId,
    int LeftObjectSetId,
    int RightObjectSetId,
    Type LeftType,
    Type RightType,
    string Predicate,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> JoinKeys,
    RelationAccessPlanKind AccessPlan,
    RelationAccessPlanKind? ReverseAccessPlan,
    RelationMaterializationMode Materialization,
    DependencyCompletenessIssue CompletenessIssues,
    bool IncompleteDependenciesAllowed)
{
    public string? DefinitionKey { get; init; }
}

public sealed record DerivedModelDiagnostics(
    int DerivedId,
    int SourceObjectSetId,
    int RelationId,
    string Computation,
    IReadOnlyList<string> Dependencies,
    DependencyCompletenessIssue CompletenessIssues,
    bool IncompleteDependenciesAllowed,
    bool UsesRelationMembership,
    DerivedLinqSemantics LinqSemantics,
    string ComputationPlan,
    DependencySeverity MembershipAddedSeverity,
    DependencySeverity MembershipRemovedSeverity,
    DependencySeverity ItemChangedSeverity,
    DependencySeverity SourceChangedSeverity)
{
    public string? DefinitionKey { get; init; }
}

public sealed record InvariantModelDiagnostics(
    int InvariantId,
    int DerivedId,
    string Predicate,
    IReadOnlyList<string> Dependencies,
    DependencyCompletenessIssue CompletenessIssues,
    bool IncompleteDependenciesAllowed,
    InvariantReaction Reaction)
{
    public string? DefinitionKey { get; init; }
}

public sealed record CompiledModelDiagnostics(
    IReadOnlyList<ObjectSetModelDiagnostics> ObjectSets,
    IReadOnlyList<RelationModelDiagnostics> Relations,
    IReadOnlyList<DerivedModelDiagnostics> DerivedValues,
    IReadOnlyList<InvariantModelDiagnostics> Invariants);
