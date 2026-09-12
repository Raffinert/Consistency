namespace Raffinert.Relations;

internal sealed record RepairRequest(
    IInvariantDefinition Invariant,
    object Source,
    DependencyImpactKind Reason);

internal sealed record ImmediateInvariantEvaluation(
    IInvariantRuntimeState Invariant,
    object Source);

internal sealed record RuntimeCommitResult(
    ChangeImpact Impact,
    RuntimePolicyActions PolicyActions);

/// <summary>A relation membership pair reported by a detailed runtime result.</summary>
public sealed record RelationPairImpact(object Left, object Right);

/// <summary>Describes the externally meaningful effects on one relation.</summary>
public sealed record RelationMutationImpact(
    int RelationId,
    Type LeftType,
    Type RightType,
    IReadOnlyList<RelationPairImpact> AddedPairs,
    IReadOnlyList<RelationPairImpact> RemovedPairs,
    IReadOnlyList<object> AffectedSources);

/// <summary>Describes a derived definition's affected sources and strongest severity.</summary>
public sealed record DerivedMutationImpact(
    int DerivedId,
    DependencySeverity Severity,
    IReadOnlyList<object> Sources);

/// <summary>Describes an invariant definition's dependency impact.</summary>
public sealed record InvariantMutationImpact(
    int InvariantId,
    DependencySeverity Severity,
    IReadOnlyList<object> Sources);

/// <summary>A request to schedule repair work for an affected source.</summary>
public sealed record RepairRequestInfo(
    int InvariantId,
    object Source,
    DependencySeverity Reason);

/// <summary>A request for an immediate invariant evaluation during policy dispatch.</summary>
public sealed record ImmediateEvaluationRequestInfo(int InvariantId, object Source);

/// <summary>
/// Stable data produced by a committed mutation. Application callbacks are not invoked until
/// <see cref="DispatchPolicies"/> is called.
/// </summary>
public sealed class RuntimeApplyResult
{
    private readonly Action _dispatch;

    internal RuntimeApplyResult(
        ChangeImpact changeImpact,
        IReadOnlyList<RelationMutationImpact> relationImpacts,
        IReadOnlyList<DerivedMutationImpact> derivedImpacts,
        IReadOnlyList<InvariantMutationImpact> invariantImpacts,
        IReadOnlyList<RepairRequestInfo> repairRequests,
        IReadOnlyList<ImmediateEvaluationRequestInfo> immediateEvaluationRequests,
        Action dispatch)
    {
        ChangeImpact = changeImpact;
        RelationImpacts = relationImpacts;
        DerivedImpacts = derivedImpacts;
        InvariantImpacts = invariantImpacts;
        RepairRequests = repairRequests;
        ImmediateEvaluationRequests = immediateEvaluationRequests;
        _dispatch = dispatch;
    }

    public ChangeImpact ChangeImpact { get; }
    public IReadOnlyList<RelationMutationImpact> RelationImpacts { get; }
    public IReadOnlyList<DerivedMutationImpact> DerivedImpacts { get; }
    public IReadOnlyList<InvariantMutationImpact> InvariantImpacts { get; }
    public IReadOnlyList<RepairRequestInfo> RepairRequests { get; }
    public IReadOnlyList<ImmediateEvaluationRequestInfo> ImmediateEvaluationRequests { get; }
    public bool PoliciesDispatched { get; private set; }

    /// <summary>Invokes configured post-commit callbacks once.</summary>
    public void DispatchPolicies()
    {
        if (PoliciesDispatched)
            throw new InvalidOperationException("Policy actions have already been dispatched.");
        PoliciesDispatched = true;
        _dispatch();
    }
}

/// <summary>
/// A validated runtime mutation awaiting commit. Prepared mutations are bound to the runtime version
/// at which they were created and can be committed and dispatched only once.
/// </summary>
public sealed class PreparedMutation
{
    internal PreparedMutation(
        RelationRuntime runtime,
        long baseVersion,
        IReadOnlyList<RuntimeMutation> lifecycleMutations,
        IReadOnlyList<PropertyChange> changes)
    {
        Runtime = runtime;
        BaseVersion = baseVersion;
        LifecycleMutations = lifecycleMutations;
        Changes = changes;
    }

    internal RelationRuntime Runtime { get; }
    internal IReadOnlyList<RuntimeMutation> LifecycleMutations { get; }
    internal IReadOnlyList<PropertyChange> Changes { get; }
    internal RuntimePolicyActions? PolicyActions { get; private set; }

    /// <summary>The runtime version against which this mutation was validated.</summary>
    public long BaseVersion { get; }

    /// <summary>Whether the prepared mutation has been committed.</summary>
    public bool IsCommitted { get; private set; }

    /// <summary>Whether post-commit policy actions have been dispatched.</summary>
    public bool IsDispatched { get; private set; }

    internal void MarkCommitted(RuntimePolicyActions policyActions)
    {
        PolicyActions = policyActions;
        IsCommitted = true;
    }

    internal void MarkDispatched() => IsDispatched = true;
}

internal sealed class RuntimePolicyActions
{
    private readonly List<ImmediateInvariantEvaluation> _immediateEvaluations = [];
    private readonly List<RepairRequest> _repairRequests = [];

    public IReadOnlyList<ImmediateInvariantEvaluation> ImmediateEvaluations => _immediateEvaluations;
    public IReadOnlyList<RepairRequest> RepairRequests => _repairRequests;

    public void AddImmediateEvaluation(IInvariantRuntimeState invariant, object source)
    {
        if (_immediateEvaluations.Any(action =>
                ReferenceEquals(action.Invariant, invariant) && ReferenceEquals(action.Source, source)))
            return;
        _immediateEvaluations.Add(new ImmediateInvariantEvaluation(invariant, source));
    }

    public void AddRepairRequest(
        IInvariantDefinition invariant,
        object source,
        DependencyImpactKind reason)
    {
        if (_repairRequests.Any(request =>
                ReferenceEquals(request.Invariant, invariant) && ReferenceEquals(request.Source, source)))
            return;
        _repairRequests.Add(new RepairRequest(invariant, source, reason));
    }

    public void Dispatch()
    {
        foreach (var evaluation in _immediateEvaluations)
            evaluation.Invariant.EvaluatePolicy(evaluation.Source);
        foreach (var request in _repairRequests)
            request.Invariant.DispatchRepair(request.Source);
    }
}
