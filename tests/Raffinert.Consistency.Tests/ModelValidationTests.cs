namespace Raffinert.Consistency.Tests;

public sealed class ModelValidationTests
{
    [Fact]
    public void Key_rejects_nested_navigation_and_opaque_expressions()
    {
        var nestedModel = new ConsistencyModelBuilder();
        var nested = nestedModel.Objects<PurchaseOrderLine>();
        var methodModel = new ConsistencyModelBuilder();
        var method = methodModel.Objects<PurchaseOrderLine>();

        var nestedError = Assert.Throws<ArgumentException>(() => nested.Key(value => value.PurchaseOrder!.Id));
        var methodError = Assert.Throws<ArgumentException>(() => method.Key(value => value.Id.ToString()));

        Assert.Contains("direct scalar", nestedError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("method calls", methodError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Key_accepts_direct_and_composite_value_members()
    {
        var directModel = new ConsistencyModelBuilder();
        _ = directModel.Objects<PurchaseOrderLine>().Key(value => value.Id);
        var compositeModel = new ConsistencyModelBuilder();
        _ = compositeModel.Objects<PurchaseOrderLine>()
            .Key(value => new { value.PurchaseOrderNumber, value.ItemNumber });

        _ = directModel.Build();
        _ = compositeModel.Build();
    }

    [Fact]
    public void Build_rejects_an_object_set_without_a_key()
    {
        var model = new ConsistencyModelBuilder();
        model.Objects<InvoiceLine>();

        var error = Assert.Throws<InvalidOperationException>(() => model.Build());

        Assert.Contains("has no key", error.Message);
    }

    [Fact]
    public void Relation_rejects_a_set_from_another_builder()
    {
        var first = new ConsistencyModelBuilder();
        var second = new ConsistencyModelBuilder();
        var left = first.Objects<InvoiceLine>().Key(x => x.Id);
        var right = second.Objects<PurchaseOrderLine>().Key(x => x.Id);

        Assert.Throws<ArgumentException>(() => first.Relation(left, right));
    }

    [Fact]
    public void Compiled_model_metadata_is_immutable()
    {
        var model = new ConsistencyModelBuilder();
        var builder = model.Objects<InvoiceLine>();
        builder.Key(x => x.Id);
        model.Build();

        Assert.Throws<InvalidOperationException>(() => builder.Key(x => x.PurchaseOrderNumber));
    }

    [Fact]
    public void Duplicate_runtime_keys_are_rejected()
    {
        var model = new ConsistencyModelBuilder();
        var set = model.Objects<InvoiceLine>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime();
        var id = Guid.NewGuid();
        runtime.Add(set, new InvoiceLine { Id = id });

        Assert.Throws<InvalidOperationException>(() => runtime.Add(set, new InvoiceLine { Id = id }));
    }

    [Fact]
    public void Notified_key_mutation_is_rejected_with_re_registration_guidance()
    {
        var model = new ConsistencyModelBuilder();
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
        var model = new ConsistencyModelBuilder();
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

    [Fact]
    public void Opaque_invariant_predicate_is_rejected_unless_explicitly_allowed()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var count = model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        var invariant = model.Invariant(sources).Using(count)
            .Must((source, value) => OpaqueInvariant(source, value));

        var error = Assert.Throws<InvalidOperationException>(() => model.Build());

        Assert.Contains("Invariant predicate", error.Message);
        Assert.Contains("ContainsOpaqueCode", error.Message);

        invariant.AllowIncompleteDependencies();
        Assert.Contains("Incomplete, explicitly allowed", model.Build().DebugView);
    }

    [Fact]
    public void Opaque_materialized_relation_is_rejected_unless_explicitly_allowed()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => OpaqueRelation(source, item));
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);

        var error = Assert.Throws<InvalidOperationException>(() => model.Build());

        Assert.Contains("Materialized relation", error.Message);
        Assert.Contains("ContainsOpaqueCode", error.Message);
    }

    [Fact]
    public void Opaque_direct_query_relation_remains_valid_and_does_not_claim_cached_freshness()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => OpaqueRelation(source, item));
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        runtime.Add(items, item);

        Assert.Equal([item], runtime.Related(relation, source));
        Assert.Contains("Incomplete direct-query evaluation", compiled.DebugView);
    }

    [Fact]
    public void Explicit_opt_in_allows_an_incomplete_materialized_relation_and_marks_diagnostics()
    {
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => OpaqueRelation(source, item))
            .AllowIncompleteDependencies();
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);

        var compiled = model.Build();

        Assert.Contains("Incomplete, explicitly allowed", compiled.DebugView);
        Assert.Contains("cached freshness is not guaranteed", compiled.DebugView);
    }

    private static bool OpaqueInvariant(CodeHolder source, int value) =>
        source.Enabled || value <= 1;

    private static bool OpaqueRelation(CodeHolder source, CodeHolder item) =>
        source.Code == item.Code;
}
