namespace Raffinert.Relations.Tests;

public sealed class ModelValidationTests
{
    [Fact]
    public void Build_rejects_an_object_set_without_a_key()
    {
        var model = new RelationModelBuilder();
        model.Objects<InvoiceLine>();

        var error = Assert.Throws<InvalidOperationException>(() => model.Build());

        Assert.Contains("has no key", error.Message);
    }

    [Fact]
    public void Relation_rejects_a_set_from_another_builder()
    {
        var first = new RelationModelBuilder();
        var second = new RelationModelBuilder();
        var left = first.Objects<InvoiceLine>().Key(x => x.Id);
        var right = second.Objects<PurchaseOrderLine>().Key(x => x.Id);

        Assert.Throws<ArgumentException>(() => first.Relation(left, right));
    }

    [Fact]
    public void Compiled_model_metadata_is_immutable()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<InvoiceLine>().Key(x => x.Id);
        model.Build();

        Assert.Throws<InvalidOperationException>(() => set.Key(x => x.PurchaseOrderNumber));
    }

    [Fact]
    public void Duplicate_runtime_keys_are_rejected()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<InvoiceLine>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();
        var id = Guid.NewGuid();
        runtime.Add(set, new InvoiceLine { Id = id });

        Assert.Throws<InvalidOperationException>(() => runtime.Add(set, new InvoiceLine { Id = id }));
    }

    [Fact]
    public void Notified_key_mutation_is_rejected_with_re_registration_guidance()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<MutableKeyHolder>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();
        var original = Guid.NewGuid();
        var instance = new MutableKeyHolder { Id = original };
        runtime.Add(set, instance);

        instance.Id = Guid.NewGuid();
        var error = Assert.Throws<InvalidOperationException>(() =>
            runtime.Apply(Change.Property(set, instance, x => x.Id, original, instance.Id)));

        Assert.Contains("Remove and re-add", error.Message);
    }

    [Fact]
    public void Removal_uses_the_registered_key_after_an_unreported_key_mutation()
    {
        var model = new RelationModelBuilder();
        var set = model.Objects<MutableKeyHolder>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();
        var registeredKey = Guid.NewGuid();
        var instance = new MutableKeyHolder { Id = registeredKey };
        runtime.Add(set, instance);

        instance.Id = Guid.NewGuid();
        Assert.True(runtime.Remove(set, instance));

        runtime.Add(set, new MutableKeyHolder { Id = registeredKey });
        Assert.Throws<InvalidOperationException>(() =>
            runtime.Add(set, new MutableKeyHolder { Id = registeredKey }));
    }
}
