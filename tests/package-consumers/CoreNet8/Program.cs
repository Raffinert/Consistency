using Raffinert.Consistency;

var model = new ConsistencyModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var links = model.Objects<Link>().Key(value => value.Id);
Relation<Value, Link> relation = model.Relation(values, links)
    .Where((left, right) => left.Id == right.Value.Id);
var doubled = model.Derived(values)
    .DependsOn(value => value.Amount)
    .Select(Value.Double)
    .MaterializeTo(value => value.Mirror)
    .Named("doubled");
model.Invariant(values).From(doubled).Must((_, amount) => amount <= 6)
    .RepairWhenViolated().Named("repair");
var projected = model.Derived(links).From(link => link.Value, doubled)
    .Select((_, amount) => amount);
var value = new Value { Amount = 3 };
var link = new Link { Value = value };
CompiledConsistencyModel compiled = model.Build();
ConsistencyRuntime runtimeEngine = compiled.CreateRuntime(seed =>
{
    seed.Add(values, [value]);
    seed.Add(links, [link]);
});
IConsistencyRuntime runtime = runtimeEngine;
if (runtime.Evaluate(projected, link) != 6) return 1;
runtime.Materialize(doubled, value);
if (value.Mirror != 6) return 1;
value.Amount = 4;
var prepared = runtimeEngine.Prepare(MutationSet.Create(Change.Property(
    values, value, item => item.Amount, 3, 4)));
var plan = runtimeEngine.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal,
    PlannedInvariantEvaluationMode.Affected);
return plan.HasInvariantViolations && plan.InvariantEvaluations.Single().State == InvariantEvaluationState.Violated &&
    runtimeEngine.Version == 0 ? 0 : 1;

internal sealed class Value
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Amount { get; set; }
    public int Mirror { get; set; }
    public static int Double(Value value) => value.Amount * 2;
}

internal sealed class Link
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Value Value { get; init; }
}
