using Raffinert.Consistency;

var model = new ConsistencyModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var links = model.Objects<Link>().Key(value => value.Id);
var doubled = model.Derived(values).Compute(value => value.Amount * 2).Named("doubled");
model.Invariant(values).Using(doubled).Must((_, amount) => amount <= 6)
    .ScheduleRepairWith(_ => { }).Named("repair");
var projected = model.Derived(links).Using(link => link.Value, doubled)
    .Compute((_, amount) => amount);
var value = new Value { Amount = 3 };
var link = new Link { Value = value };
var runtime = model.Build().CreateRuntime(seed =>
{
    seed.Add(values, [value]);
    seed.Add(links, [link]);
});
if (runtime.Get(projected, link) != 6) return 1;
value.Amount = 4;
var prepared = runtime.Prepare(MutationSet.Create(Change.Property(
    values, value, item => item.Amount, 3, 4)));
var plan = runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal,
    PlannedInvariantEvaluationMode.Affected);
return plan.HasInvariantViolations && plan.InvariantEvaluations.Single().State == InvariantEvaluationState.Violated &&
    runtime.Version == 0 ? 0 : 1;

internal sealed class Value
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Amount { get; set; }
}

internal sealed class Link
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Value Value { get; init; }
}
