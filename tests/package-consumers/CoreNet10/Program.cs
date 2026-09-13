using Raffinert.Relations;

var model = new RelationModelBuilder();
var values = model.Objects<Value>().Key(value => value.Id);
var doubled = model.Derived(values).Compute(value => value.Amount * 2);
var runtime = model.Build().CreateRuntime();
var value = new Value { Amount = 3 };
runtime.Add(values, value);
return runtime.Get(doubled, value) == 6 ? 0 : 1;

internal sealed class Value
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Amount { get; init; }
}
