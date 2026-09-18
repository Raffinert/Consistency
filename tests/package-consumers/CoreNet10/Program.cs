using Raffinert.Consistency;

var model = new ConsistencyModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var doubled = model.Derived(values)
    .DependsOn(value => value.Amount)
    .Select(Value.Double)
    .MaterializeTo(value => value.Mirror)
    .Named("doubled");
model.Invariant(values).From(doubled).Must((_, amount) => amount <= 6)
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
runtime.Materialize(value);
return !plan.HasInvariantViolations && plan.InvariantEvaluations.Single().State == InvariantEvaluationState.Valid &&
    runtime.Version == 1 && result.DetailLevel == RuntimeImpactDetailLevel.Causal &&
    runtime.Evaluate(doubled, value) == 4 && value.Mirror == 4 ? 0 : 1;

internal sealed class Value
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Amount { get; set; }
    public int Mirror { get; set; }
    public static int Double(Value value) => value.Amount * 2;
}
