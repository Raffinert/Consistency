namespace Raffinert.Consistency;

public enum RelationAccessPlanKind
{
    Scan,
    HashJoin
}

public enum RelationPropagationPlanKind
{
    None,
    ExactMaterialized,
    ConservativeInvalidation
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

public enum DerivedDependencyKind
{
    DirectMember,
    DerivedValue,
    ProjectedDerivedValue,
    RelationValue
}

public sealed record DerivedDependencyModelDiagnostics(
    DerivedDependencyKind Kind,
    string? Path,
    int? UpstreamDerivedId,
    int? RelationId,
    bool HasSourceMemberClassifier,
    DependencySeverity SourceChangedSeverity,
    DependencySeverity MembershipAddedSeverity,
    DependencySeverity MembershipRemovedSeverity,
    DependencySeverity ItemChangedSeverity);

public sealed record MaterializationModelDiagnostics(
    int SourceObjectSetId,
    int DerivedId,
    Type SourceType,
    Type ValueType,
    string TargetMember);

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
    RelationPropagationPlanKind PropagationPlan,
    bool RetainsPairMembership,
    string PropagationReason,
    DependencyCompletenessIssue CompletenessIssues,
    bool IncompleteDependenciesAllowed)
{
    public string? DefinitionKey { get; init; }
}

public sealed record DerivedModelDiagnostics(
    int DerivedId,
    int SourceObjectSetId,
    IReadOnlyList<int> RelationIds,
    IReadOnlyList<int> UpstreamDerivedIds,
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
    public bool HasConditionalSourcePolicy { get; init; }
    public int SourceMemberRuleCount { get; init; }
    public IReadOnlyList<string> SourceMemberRuleNames { get; init; } = [];
    public IReadOnlyList<DerivedDependencyModelDiagnostics> SemanticDependencies { get; init; } = [];
}

public sealed record InvariantModelDiagnostics(
    int InvariantId,
    int SourceObjectSetId,
    IReadOnlyList<int> UpstreamDerivedIds,
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
    IReadOnlyList<InvariantModelDiagnostics> Invariants)
{
    public IReadOnlyList<MaterializationModelDiagnostics> Materializations { get; init; } = [];
}
