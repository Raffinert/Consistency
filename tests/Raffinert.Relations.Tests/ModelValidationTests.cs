namespace Raffinert.Relations.Tests;

public sealed class ModelValidationTests
{
    [Fact]
    public void Build_rejects_an_object_set_without_a_key()
    {
        var model = new InvariantModelBuilder();
        model.Objects<InvoiceLine>();

        var error = Assert.Throws<InvalidOperationException>(() => model.Build());

        Assert.Contains("has no key", error.Message);
    }

    [Fact]
    public void Relation_rejects_a_set_from_another_builder()
    {
        var first = new InvariantModelBuilder();
        var second = new InvariantModelBuilder();
        var left = first.Objects<InvoiceLine>().Key(x => x.Id);
        var right = second.Objects<PurchaseOrderLine>().Key(x => x.Id);

        Assert.Throws<ArgumentException>(() => first.Relation(left, right));
    }

    [Fact]
    public void Compiled_model_metadata_is_immutable()
    {
        var model = new InvariantModelBuilder();
        var set = model.Objects<InvoiceLine>().Key(x => x.Id);
        model.Build();

        Assert.Throws<InvalidOperationException>(() => set.Key(x => x.PurchaseOrderNumber));
    }

    [Fact]
    public void Duplicate_runtime_keys_are_rejected()
    {
        var model = new InvariantModelBuilder();
        var set = model.Objects<InvoiceLine>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();
        var id = Guid.NewGuid();
        runtime.Add(set, new InvoiceLine { Id = id });

        Assert.Throws<InvalidOperationException>(() => runtime.Add(set, new InvoiceLine { Id = id }));
    }
}
