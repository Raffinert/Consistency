using Raffinert.Relations;

var model = new RelationModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var doubled = model.Derived(values).Compute(value => value.Amount * 2).Named("doubled");
model.Invariant(values).Using(doubled).Must((_, amount) => amount <= 6)
    .ScheduleRepairWith(_ => { }).Named("repair");
var value = new Value { Amount = 3 };
var runtime = model.Build().CreateRuntime(seed => seed.Add(values, [value]));
value.Amount = 4;
var prepared = runtime.Prepare(MutationSet.Create(Change.Property(
    values, value, item => item.Amount, 3, 4)));
var plan = runtime.PlanDetailed(prepared, RuntimeImpactDetailLevel.Causal);
var durable = plan.Result.GetDurablePolicyWork().RepairRequests.Single();
var result = runtime.Commit(plan);
runtime.Dispatch(prepared);
return runtime.Version == 1 && result.DetailLevel == RuntimeImpactDetailLevel.Causal &&
    durable.DefinitionKey == "repair" && durable.Source.ObjectSetKey == "values" &&
    durable.Source.KeyParts.Single().Value == value.Id.ToString("D") &&
    durable.Reason == DependencySeverity.Invalid && runtime.Get(doubled, value) == 8 ? 0 : 1;

internal sealed class Value
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Amount { get; set; }
}
