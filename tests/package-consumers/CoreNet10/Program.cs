using Raffinert.Consistency;

var model = new ConsistencyModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var doubled = model.Derived(values).Compute(value => value.Amount * 2).Named("doubled");
model.Invariant(values).Using(doubled).Must((_, amount) => amount <= 6)
    .ScheduleRepairWith(_ => { }).Named("repair");
var value = new Value { Amount = 3 };
var runtime = model.Build().CreateRuntime(seed => seed.Add(values, [value]));
value.Amount = 2;
var prepared = runtime.Prepare(MutationSet.Create(Change.Property(
    values, value, item => item.Amount, 3, 2)));
var plan = runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal,
    PlannedInvariantEvaluationMode.Affected);
var result = runtime.Commit(plan);
runtime.Dispatch(prepared);
return !plan.HasInvariantViolations && plan.InvariantEvaluations.Single().State == InvariantEvaluationState.Valid &&
    runtime.Version == 1 && result.DetailLevel == RuntimeImpactDetailLevel.Causal && runtime.Get(doubled, value) == 4 ? 0 : 1;

internal sealed class Value
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Amount { get; set; }
}
