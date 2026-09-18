namespace Raffinert.Consistency.Tests;

public sealed partial class RuntimeTests
{
    [Fact]
    public void Mutation_set_applies_lifecycle_and_property_changes_as_one_operation()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("ORDER-2", "B");
        var line = Line("ORDER-1", "A");
        line.OrderNumber = "ORDER-2";
        line.ItemNumber = "B";

        runtime.Apply(MutationSet.Create(
            Change.Add(invoices, invoice),
            Change.Add(lines, line),
            Change.Property(lines, line, x => x.OrderNumber, "ORDER-1", "ORDER-2"),
            Change.Property(lines, line, x => x.ItemNumber, "A", "B")),
            ChangeValidationMode.StrictNewValue);

        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Invalid_mutation_rejects_the_whole_batch_before_runtime_updates()
    {
        var model = CreateLineModel(out var invoices, out var lines, out _);
        var runtime = model.Build().CreateRuntime();
        var added = Line("ORDER", "A");
        var absent = Invoice("ORDER", "A");

        Assert.Throws<InvalidOperationException>(() => runtime.Apply(MutationSet.Create(
            Change.Add(lines, added),
            Change.Remove(invoices, absent))));

        Assert.False(runtime.Remove(lines, added));
    }

    [Fact]
    public void Prepared_mutation_does_not_change_state_until_commit()
    {
        var model = CreateLineModel(out _, out var lines, out _);
        var runtime = model.Build().CreateRuntime();
        var line = Line("ORDER", "A");

        var prepared = runtime.Prepare(MutationSet.Create(Change.Add(lines, line)));

        Assert.Equal(0, runtime.Version);
        Assert.False(prepared.IsCommitted);
        Assert.False(runtime.Remove(lines, line));

        runtime.Commit(prepared);

        Assert.Equal(1, runtime.Version);
        Assert.True(prepared.IsCommitted);
        Assert.True(runtime.Remove(lines, line));
    }

    [Fact]
    public void Prepared_mutation_is_rejected_when_runtime_version_has_advanced()
    {
        var model = CreateLineModel(out _, out var lines, out _);
        var runtime = model.Build().CreateRuntime();
        var preparedLine = Line("ORDER", "A");
        var interveningLine = Line("ORDER", "B");
        var prepared = runtime.Prepare(MutationSet.Create(Change.Add(lines, preparedLine)));
        runtime.Add(lines, interveningLine);

        var error = Assert.Throws<InvalidOperationException>(() => runtime.Commit(prepared));

        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(runtime.Remove(lines, preparedLine));
    }

    [Fact]
    public void Failed_commit_restores_all_runtime_owned_state()
    {
        var gate = new ThrowingPredicate();
        var model = new ConsistencyModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items)
            .Where((source, item) => gate.Match(source, item))
            .AllowIncompleteDependencies();
        var count = model.Derived(sources).From(relation)
            .Select((_, matches) => matches.Count)
            .AllowIncompleteDependencies();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Evaluate(count, source));
        var version = runtime.Version;
        var diagnostics = runtime.Diagnostics;
        gate.Throw = true;
        var prepared = runtime.Prepare(MutationSet.Create(Change.Add(items, item)));

        Assert.Throws<DeliberateTestException>(() => runtime.Commit(prepared));

        Assert.Equal(version, runtime.Version);
        Assert.Equal(0, runtime.Evaluate(count, source));
        Assert.False(runtime.Remove(items, item));
        Assert.Equal(diagnostics.RelationPairsAdded, runtime.Diagnostics.RelationPairsAdded);
        gate.Throw = false;
        runtime.Add(items, item);
        Assert.Equal([item], runtime.Related(relation, source));
    }

    [Fact]
    public void Prepared_mutation_is_rejected_when_domain_member_drifted()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("ORDER", "A");
        var line = Line("ORDER", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);
        line.ItemNumber = "B";
        var prepared = runtime.Prepare(MutationSet.Create(
            Change.Property(lines, line, value => value.ItemNumber, "A", "B")));
        line.ItemNumber = "C";
        var version = runtime.Version;

        var error = Assert.Throws<InvalidOperationException>(() => runtime.Commit(prepared));

        Assert.Contains("domain state drifted", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(version, runtime.Version);
        line.ItemNumber = "A";
        runtime.Apply(Change.Property(lines, line, value => value.ItemNumber, "C", "A"));
        Assert.Equal([line], runtime.Related(relation, invoice));
    }

}
