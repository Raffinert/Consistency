namespace Raffinert.Relations.Tests;

public sealed class RuntimeTests
{
    [Fact]
    public void Adds_and_queries_with_a_composite_hash_index_and_full_predicate()
    {
        var model = CreateLineModel(out var invoices, out var poLines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var matching = Line("PO-100", "A", enabled: true);
        var sameKeyButDisabled = Line("PO-100", "A", enabled: false);
        var other = Line("PO-200", "A", enabled: true);

        runtime.Add(invoices, invoice);
        runtime.Add(poLines, matching);
        runtime.Add(poLines, sameKeyButDisabled);
        runtime.Add(poLines, other);

        Assert.Equal([matching], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Indexed_property_change_removes_and_then_adds_a_match()
    {
        var model = CreateLineModel(out var invoices, out var poLines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var line = Line("PO-100", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(poLines, line);

        line.ItemNumber = "B";
        runtime.Apply(Change.Property(line, x => x.ItemNumber, "A", "B"));
        Assert.Empty(runtime.Related(relation, invoice));

        line.ItemNumber = "A";
        runtime.Apply(Change.Property(poLines, line, x => x.ItemNumber, "B", "A"));
        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Removing_a_right_object_removes_it_from_relation_results()
    {
        var model = CreateLineModel(out var invoices, out var poLines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var line = Line("PO-100", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(poLines, line);

        Assert.True(runtime.Remove(poLines, line));

        Assert.Empty(runtime.Related(invoice, relation));
        Assert.False(runtime.Remove(poLines, line));
    }

    [Fact]
    public void Mutation_set_applies_lifecycle_and_property_changes_as_one_operation()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-2", "B");
        var line = Line("PO-1", "A");
        line.PurchaseOrderNumber = "PO-2";
        line.ItemNumber = "B";

        runtime.Apply(MutationSet.Create(
            Change.Add(invoices, invoice),
            Change.Add(lines, line),
            Change.Property(lines, line, x => x.PurchaseOrderNumber, "PO-1", "PO-2"),
            Change.Property(lines, line, x => x.ItemNumber, "A", "B")),
            ChangeValidationMode.StrictNewValue);

        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Invalid_mutation_rejects_the_whole_batch_before_runtime_updates()
    {
        var model = CreateLineModel(out var invoices, out var lines, out _);
        var runtime = model.Build().CreateRuntime();
        var added = Line("PO", "A");
        var absent = Invoice("PO", "A");

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
        var line = Line("PO", "A");

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
        var preparedLine = Line("PO", "A");
        var interveningLine = Line("PO", "B");
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
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items)
            .Where((source, item) => gate.Match(source, item))
            .AllowIncompleteDependencies();
        var count = model.Derived(sources).Using(relation)
            .Compute((_, matches) => matches.Count)
            .AllowIncompleteDependencies();
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(sources, source);
        Assert.Equal(0, runtime.Get(count, source));
        var version = runtime.Version;
        var diagnostics = runtime.Diagnostics;
        gate.Throw = true;
        var prepared = runtime.Prepare(MutationSet.Create(Change.Add(items, item)));

        Assert.Throws<DeliberateTestException>(() => runtime.Commit(prepared));

        Assert.Equal(version, runtime.Version);
        Assert.Equal(0, runtime.Get(count, source));
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
        var invoice = Invoice("PO", "A");
        var line = Line("PO", "A");
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

    [Fact]
    public void Runtime_diagnostics_distinguish_query_indexes_from_exact_materialization()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(x => x.Id);
        var items = model.Objects<CodeHolder>().Key(x => x.Id);
        var queryOnly = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        var materialized = model.Relation(sources, items).Where((source, item) => source.Enabled == item.Enabled);
        model.Derived(sources).Using(materialized).Compute((source, matches) => matches.Count);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime(new RuntimeDiagnosticOptions
        {
            MaterializedPairWarningThreshold = 5,
            AverageFanOutWarningThreshold = 10
        });
        var lefts = new[]
        {
            new CodeHolder { Id = Guid.NewGuid(), Code = "A", Enabled = true },
            new CodeHolder { Id = Guid.NewGuid(), Code = "B", Enabled = true }
        };
        var rights = Enumerable.Range(0, 3).Select(index => new CodeHolder
        {
            Id = Guid.NewGuid(),
            Code = index == 0 ? "A" : "X",
            Enabled = true
        }).ToArray();
        foreach (var source in lefts)
            runtime.Add(sources, source);
        foreach (var item in rights)
            runtime.Add(items, item);

        var diagnostics = runtime.Diagnostics;

        var query = diagnostics.Relations[0];
        Assert.Equal(0, query.RelationId);
        Assert.Equal(RelationMaterializationMode.None, query.Materialization);
        Assert.Equal(3, query.ForwardIndexEntries);
        Assert.Equal(0, query.ReverseIndexEntries);
        Assert.Equal(0, query.MaterializedPairCount);
        Assert.False(query.HasDensityWarning);

        var exact = diagnostics.Relations[1];
        Assert.Equal(RelationMaterializationMode.ExactPropagation, exact.Materialization);
        Assert.Equal(3, exact.ForwardIndexEntries);
        Assert.Equal(2, exact.ReverseIndexEntries);
        Assert.Equal(6, exact.MaterializedPairCount);
        Assert.Equal(3, exact.AverageFanOut);
        Assert.True(exact.HasDensityWarning);
        Assert.Contains("Relation materialization: None", compiled.DebugView);
        Assert.Contains("Relation materialization: ExactPropagation", compiled.DebugView);
        _ = queryOnly;
    }

    [Fact]
    public void Opaque_predicate_falls_back_to_a_semantically_correct_scan()
    {
        var model = new RelationModelBuilder();
        var left = model.Objects<CodeHolder>().Key(x => x.Id);
        var right = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(left, right).Where((a, b) => MatchesPrefix(a.Code, b.ItemNumber));
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "ITEM-123" };
        var match = Line("PO", "ITEM");
        var miss = Line("PO", "OTHER");
        runtime.Add(left, source);
        runtime.Add(right, match);
        runtime.Add(right, miss);

        Assert.Equal([match], runtime.Related(relation, source));
        Assert.Contains("Access plan: Scan", compiled.DebugView);
        Assert.Contains("Dependency analysis: ContainsOpaqueCode", compiled.DebugView);
    }

    [Fact]
    public void Nested_property_change_reindexes_referencing_roots()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var orders = model.Objects<PurchaseOrder>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            invoice.PurchaseOrderNumber == line.PurchaseOrder!.Number &&
            invoice.ItemNumber == line.ItemNumber);
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var order = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-100" };
        var line = Line("ignored", "A");
        line.PurchaseOrder = order;
        runtime.Add(invoices, invoice);
        runtime.Add(orders, order);
        runtime.Add(lines, line);

        Assert.Equal([line], runtime.Related(relation, invoice));

        order.Number = "PO-200";
        runtime.Apply(Change.Property(orders, order, x => x.Number, "PO-100", "PO-200"));

        Assert.Empty(runtime.Related(relation, invoice));
        Assert.Contains("PurchaseOrderLine.PurchaseOrder.Number", compiled.DebugView);
    }

    [Fact]
    public void Reference_navigation_change_updates_reverse_navigation_and_index()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var orders = model.Objects<PurchaseOrder>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines)
            .Where((invoice, line) => invoice.PurchaseOrderNumber == line.PurchaseOrder!.Number);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-2", "A");
        var first = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-1" };
        var second = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-2" };
        var line = Line("", "A");
        line.PurchaseOrder = first;
        runtime.Add(invoices, invoice);
        runtime.Add(orders, first);
        runtime.Add(orders, second);
        runtime.Add(lines, line);

        line.PurchaseOrder = second;
        runtime.Apply(Change.Property(lines, line, x => x.PurchaseOrder, first, second));

        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Type_inference_works_when_a_type_has_one_object_set()
    {
        var model = CreateLineModel(out _, out _, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO", "A");
        var line = Line("PO", "A");

        runtime.Add(invoice);
        runtime.Add(line);

        Assert.Equal([line], runtime.Related(relation, invoice));
        Assert.True(runtime.Remove(line));
    }

    [Fact]
    public void Null_guarded_nested_relation_is_safe_and_reindexes_when_navigation_is_assigned()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            line.PurchaseOrder != null &&
            invoice.PurchaseOrderNumber == line.PurchaseOrder.Number);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-100", "A");
        var line = Line("ignored", "A");

        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        Assert.Empty(runtime.Related(relation, invoice));

        var order = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-100" };
        line.PurchaseOrder = order;
        runtime.Apply(Change.Property(lines, line, x => x.PurchaseOrder, null, order));

        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Hash_and_scan_plans_remain_equivalent_after_runtime_changes()
    {
        var hashModel = new RelationModelBuilder();
        var hashLeft = hashModel.Objects<CodeHolder>().Key(x => x.Id);
        var hashRight = hashModel.Objects<CodeHolder>().Key(x => x.Id);
        var hashRelation = hashModel.Relation(hashLeft, hashRight).Where((a, b) => a.Code == b.Code);
        var hashRuntime = hashModel.Build().CreateRuntime();

        var scanModel = new RelationModelBuilder();
        var scanLeft = scanModel.Objects<CodeHolder>().Key(x => x.Id);
        var scanRight = scanModel.Objects<CodeHolder>().Key(x => x.Id);
        var scanRelation = scanModel.Relation(scanLeft, scanRight).Where((a, b) => CodesEqual(a, b));
        var scanRuntime = scanModel.Build().CreateRuntime();

        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        hashRuntime.Add(hashLeft, source);
        scanRuntime.Add(scanLeft, source);
        foreach (var candidate in new[] { first, second })
        {
            hashRuntime.Add(hashRight, candidate);
            scanRuntime.Add(scanRight, candidate);
        }

        AssertEquivalent();

        second.Code = "A";
        hashRuntime.Apply(Change.Property(hashRight, second, x => x.Code, "B", "A"));
        scanRuntime.Apply(Change.Property(scanRight, second, x => x.Code, "B", "A"));
        AssertEquivalent();

        Assert.True(hashRuntime.Remove(hashRight, first));
        Assert.True(scanRuntime.Remove(scanRight, first));
        AssertEquivalent();

        void AssertEquivalent() => Assert.Equal(
            scanRuntime.Related(scanRelation, source).Select(item => item.Id).Order(),
            hashRuntime.Related(hashRelation, source).Select(item => item.Id).Order());
    }

    [Fact]
    public void Arbitrary_depth_change_from_an_unregistered_nested_object_reindexes_all_roots()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            line.PurchaseOrder != null &&
            line.PurchaseOrder.Supplier != null &&
            line.PurchaseOrder.Supplier.Country != null &&
            invoice.PurchaseOrderNumber == line.PurchaseOrder.Supplier.Country.Code);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("NO", "A");
        var country = new Country { Id = Guid.NewGuid(), Code = "MATCH" };
        var supplier = new Supplier { Id = Guid.NewGuid(), Country = country };
        var order = new PurchaseOrder { Id = Guid.NewGuid(), Supplier = supplier };
        var first = Line("", "A");
        var second = Line("", "B");
        first.PurchaseOrder = order;
        second.PurchaseOrder = order;
        runtime.Add(invoices, invoice);
        runtime.Add(lines, first);
        runtime.Add(lines, second);

        Assert.Empty(runtime.Related(relation, invoice));

        invoice.PurchaseOrderNumber = "MATCH";
        runtime.Apply(Change.Property(invoices, invoice, x => x.PurchaseOrderNumber, "NO", "MATCH"));
        Assert.Equal(2, runtime.Related(relation, invoice).Count);

        country.Code = "OTHER";
        var impact = runtime.Apply(Change.Property(country, x => x.Code, "MATCH", "OTHER"));

        Assert.Empty(runtime.Related(relation, invoice));
        Assert.Equal(1, impact.Access.ReindexedRelations);
        Assert.Equal(2, impact.Access.ReindexedRoots);
        Assert.Equal(1, impact.Semantic.AffectedRelations);
        Assert.Equal(2, impact.Semantic.AffectedRoots);
    }

    [Fact]
    public void Reference_navigation_can_change_from_object_to_null()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            line.PurchaseOrder != null && invoice.PurchaseOrderNumber == line.PurchaseOrder.Number);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO", "A");
        var order = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO" };
        var line = Line("", "A");
        line.PurchaseOrder = order;
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);
        Assert.Equal([line], runtime.Related(relation, invoice));

        line.PurchaseOrder = null;
        runtime.Apply(Change.Property(lines, line, x => x.PurchaseOrder, order, null));

        Assert.Empty(runtime.Related(relation, invoice));
    }

    [Fact]
    public void Residual_property_change_has_semantic_but_not_access_impact()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO", "A");
        var line = Line("PO", "A", enabled: true);
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        line.Enabled = false;
        var impact = runtime.Apply(Change.Property(lines, line, x => x.Enabled, true, false));

        Assert.Empty(runtime.Related(relation, invoice));
        Assert.Equal(0, impact.Access.ReindexedRelations);
        Assert.Equal(1, impact.Semantic.AffectedRelations);
    }

    [Fact]
    public void Change_set_reindexes_a_root_once_when_two_key_fields_change()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-2", "B");
        var line = Line("PO-1", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        line.PurchaseOrderNumber = "PO-2";
        line.ItemNumber = "B";
        var impact = runtime.Apply(ChangeSet.Create(
            Change.Property(lines, line, x => x.PurchaseOrderNumber, "PO-1", "PO-2"),
            Change.Property(lines, line, x => x.ItemNumber, "A", "B")));

        Assert.Equal([line], runtime.Related(relation, invoice));
        Assert.Equal(1, impact.Access.ReindexedRelations);
        Assert.Equal(1, impact.Access.ReindexedRoots);
    }

    [Fact]
    public void Contiguous_repeated_changes_are_normalized_to_their_net_effect()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO", "C");
        var line = Line("PO", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        line.ItemNumber = "C";
        var impact = runtime.Apply(ChangeSet.Create(
            Change.Property(lines, line, x => x.ItemNumber, "A", "B"),
            Change.Property(lines, line, x => x.ItemNumber, "B", "C")),
            ChangeValidationMode.StrictNewValue);

        Assert.Equal([line], runtime.Related(relation, invoice));
        Assert.Equal(1, impact.Access.ReindexedRoots);
    }

    [Fact]
    public void Conflicting_repeated_changes_are_rejected_before_runtime_updates()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO", "C");
        var line = Line("PO", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        line.ItemNumber = "C";
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(ChangeSet.Create(
            Change.Property(lines, line, x => x.ItemNumber, "A", "B"),
            Change.Property(lines, line, x => x.ItemNumber, "A", "C"))));

        line.ItemNumber = "A";
        runtime.Apply(Change.Property(lines, line, x => x.ItemNumber, "C", "A"));
        Assert.Empty(runtime.Related(relation, invoice));
    }

    [Fact]
    public void Strict_validation_rejects_an_unapplied_domain_mutation()
    {
        var model = CreateLineModel(out var invoices, out var lines, out var relation);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO", "B");
        var line = Line("PO", "A");
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        Assert.Throws<InvalidOperationException>(() => runtime.Apply(
            Change.Property(lines, line, x => x.ItemNumber, "A", "B"),
            ChangeValidationMode.StrictNewValue));

        line.ItemNumber = "B";
        runtime.Apply(Change.Property(lines, line, x => x.ItemNumber, "A", "B"),
            ChangeValidationMode.StrictNewValue);
        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Invalid_change_rejects_the_entire_change_set_before_index_updates()
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var relation = model.Relation(invoices, lines).Where((invoice, line) =>
            line.PurchaseOrder != null && invoice.PurchaseOrderNumber == line.PurchaseOrder.Number);
        var runtime = model.Build().CreateRuntime();
        var invoice = Invoice("PO-2", "A");
        var first = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-1" };
        var second = new PurchaseOrder { Id = Guid.NewGuid(), Number = "PO-2" };
        var line = Line("", "A");
        line.PurchaseOrder = first;
        runtime.Add(invoices, invoice);
        runtime.Add(lines, line);

        var oldId = line.Id;
        line.PurchaseOrder = second;
        Assert.Throws<InvalidOperationException>(() => runtime.Apply(ChangeSet.Create(
            Change.Property(lines, line, x => x.PurchaseOrder, first, second),
            Change.Property(lines, line, x => x.Id, oldId, line.Id))));

        runtime.Apply(Change.Property(lines, line, x => x.PurchaseOrder, first, second));
        Assert.Equal([line], runtime.Related(relation, invoice));
    }

    [Fact]
    public void Randomized_hash_results_equal_forced_scan_after_mutations()
    {
        var hashModel = new RelationModelBuilder();
        var hashLeft = hashModel.Objects<CodeHolder>().Key(x => x.Id);
        var hashRight = hashModel.Objects<CodeHolder>().Key(x => x.Id);
        var hashRelation = hashModel.Relation(hashLeft, hashRight).Where((a, b) => a.Code == b.Code && b.Enabled);
        var hashRuntime = hashModel.Build().CreateRuntime();

        var scanModel = new RelationModelBuilder().UseScanPlansForTesting();
        var scanLeft = scanModel.Objects<CodeHolder>().Key(x => x.Id);
        var scanRight = scanModel.Objects<CodeHolder>().Key(x => x.Id);
        var scanRelation = scanModel.Relation(scanLeft, scanRight).Where((a, b) => a.Code == b.Code && b.Enabled);
        var scanRuntime = scanModel.Build().CreateRuntime();

        var random = new Random(7319);
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var active = new List<CodeHolder>();
        hashRuntime.Add(hashLeft, source);
        scanRuntime.Add(scanLeft, source);

        for (var operation = 0; operation < 250; operation++)
        {
            switch (random.Next(active.Count == 0 ? 1 : 4))
            {
                case 0:
                    {
                        var item = new CodeHolder
                        {
                            Id = Guid.NewGuid(),
                            Code = RandomCode(),
                            Enabled = random.Next(2) == 0
                        };
                        active.Add(item);
                        hashRuntime.Add(hashRight, item);
                        scanRuntime.Add(scanRight, item);
                        break;
                    }
                case 1:
                    {
                        var item = active[random.Next(active.Count)];
                        var oldCode = item.Code;
                        item.Code = RandomCode();
                        hashRuntime.Apply(Change.Property(hashRight, item, x => x.Code, oldCode, item.Code));
                        scanRuntime.Apply(Change.Property(scanRight, item, x => x.Code, oldCode, item.Code));
                        break;
                    }
                case 2:
                    {
                        var item = active[random.Next(active.Count)];
                        item.Enabled = !item.Enabled;
                        hashRuntime.Apply(Change.Property(hashRight, item, x => x.Enabled, !item.Enabled, item.Enabled));
                        scanRuntime.Apply(Change.Property(scanRight, item, x => x.Enabled, !item.Enabled, item.Enabled));
                        break;
                    }
                default:
                    {
                        var index = random.Next(active.Count);
                        var item = active[index];
                        active.RemoveAt(index);
                        hashRuntime.Remove(hashRight, item);
                        scanRuntime.Remove(scanRight, item);
                        break;
                    }
            }

            Assert.Equal(
                scanRuntime.Related(scanRelation, source).Select(item => item.Id).Order(),
                hashRuntime.Related(hashRelation, source).Select(item => item.Id).Order());
        }

        string RandomCode() => ((char)('A' + random.Next(4))).ToString();
    }

    [Fact]
    public void Ordinal_ignore_case_string_equality_uses_matching_hash_semantics()
    {
        var model = new RelationModelBuilder();
        var left = model.Objects<CodeHolder>().Key(x => x.Id);
        var right = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(left, right).Where((a, b) =>
            string.Equals(a.Code, b.Code, StringComparison.OrdinalIgnoreCase));
        var runtime = model.Build().CreateRuntime();
        var source = new CodeHolder { Id = Guid.NewGuid(), Code = "AbC" };
        var match = new CodeHolder { Id = Guid.NewGuid(), Code = "aBc" };
        runtime.Add(left, source);
        runtime.Add(right, match);

        Assert.Equal([match], runtime.Related(relation, source));
    }

    [Fact]
    public void Relation_can_be_queried_from_the_right_without_a_reverse_hash_index()
    {
        var model = new RelationModelBuilder();
        var left = model.Objects<CodeHolder>().Key(x => x.Id);
        var right = model.Objects<CodeHolder>().Key(x => x.Id);
        var relation = model.Relation(left, right).Where((a, b) => a.Code == b.Code);
        var runtime = model.Build().CreateRuntime();
        var first = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        var second = new CodeHolder { Id = Guid.NewGuid(), Code = "B" };
        var target = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(left, first);
        runtime.Add(left, second);
        runtime.Add(right, target);

        Assert.Equal([first], runtime.RelatedFromRight(relation, target));
    }

    [Fact]
    public void Runtime_diagnostics_report_local_predicate_work_and_affected_sources()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<CodeHolder>().Key(value => value.Id);
        var items = model.Objects<CodeHolder>().Key(value => value.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count + 1);
        var runtime = model.Build().CreateRuntime();
        runtime.Add(sources, new CodeHolder { Id = Guid.NewGuid(), Code = "A" });
        runtime.Add(sources, new CodeHolder { Id = Guid.NewGuid(), Code = "B" });
        var item = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        runtime.Add(items, item);
        runtime.ResetDiagnostics();

        item.Code = "B";
        runtime.Apply(Change.Property(items, item, value => value.Code, "A", "B"));

        Assert.Equal(1, runtime.Diagnostics.PredicateEvaluations);
        Assert.Equal(2, runtime.Diagnostics.AffectedSources);
    }

    private static RelationModelBuilder CreateLineModel(
        out ObjectSet<InvoiceLine> invoices,
        out ObjectSet<PurchaseOrderLine> poLines,
        out Relation<InvoiceLine, PurchaseOrderLine> relation)
    {
        var model = new RelationModelBuilder();
        invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
        poLines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        relation = model.Relation(invoices, poLines).Where((invoice, line) =>
            invoice.PurchaseOrderNumber == line.PurchaseOrderNumber &&
            invoice.ItemNumber == line.ItemNumber &&
            line.Enabled);
        return model;
    }

    private static InvoiceLine Invoice(string order, string item) => new()
    {
        Id = Guid.NewGuid(),
        PurchaseOrderNumber = order,
        ItemNumber = item
    };

    private static PurchaseOrderLine Line(string order, string item, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        PurchaseOrderNumber = order,
        ItemNumber = item,
        Enabled = enabled
    };

    private static bool MatchesPrefix(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal);

    private static bool CodesEqual(CodeHolder left, CodeHolder right) => left.Code == right.Code;

    private sealed class ThrowingPredicate
    {
        public bool Throw { get; set; }

        public bool Match(CodeHolder source, CodeHolder item) =>
            Throw ? throw new DeliberateTestException() : source.Code == item.Code;
    }

    private sealed class DeliberateTestException : Exception;
}
