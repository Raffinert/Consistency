namespace Raffinert.Relations;

internal sealed record RepairRequest(
    IInvariantDefinition Invariant,
    object Source,
    DependencyImpactKind Reason);

internal sealed record ImmediateInvariantEvaluation(
    IInvariantRuntimeState Invariant,
    object Source);

internal sealed record RuntimeApplyResult(
    ChangeImpact Impact,
    RuntimePolicyActions PolicyActions);

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
