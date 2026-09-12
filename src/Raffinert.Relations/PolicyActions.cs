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
