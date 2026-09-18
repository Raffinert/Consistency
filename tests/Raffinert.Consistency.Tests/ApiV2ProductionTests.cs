namespace Raffinert.Consistency.Tests;

public sealed class ApiV2ProductionTests
{
    [Fact]
    public void PriceRate_lifecycle_keeps_logical_values_mirrors_and_repair_separate()
    {
        var fixture = Fixture.Create();
        fixture.Prime();
        fixture.Repairs.Clear();

        fixture.Invoice.Price = 55m;
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.InvoiceLines, fixture.Invoice, x => x.Price, 60m, 55m)));

        Assert.Equal(DerivedValueState.Invalid, fixture.Runtime.GetState(fixture.PriceRate, fixture.Link));
        Assert.Equal(DerivedValueState.Invalid, fixture.Runtime.GetState(fixture.UnitRate, fixture.Link));
        Assert.Equal(6m, fixture.Link.PriceRate);
        Assert.Equal(6m, fixture.Link.UnitRate);

        Assert.Equal(5.5m, fixture.Runtime.Evaluate(fixture.PriceRate, fixture.Link));
        Assert.Equal(6m, fixture.Link.PriceRate);
        Assert.Equal(5.5m, fixture.Runtime.Evaluate(fixture.UnitRate, fixture.Link));
        Assert.Equal(6m, fixture.Link.PriceRate);

        var requests = Assert.Single(application.Result.RepairRequests);
        Assert.Same(fixture.Link, requests.Source);
        fixture.Runtime.Materialize(fixture.Link);
        Assert.Equal(5.5m, fixture.Link.PriceRate);
        Assert.Equal(5.5m, fixture.Link.UnitRate);
        Assert.Empty(fixture.Repairs);

        application.Dispatch.Invoke();
        Assert.Equal([fixture.Link], fixture.Repairs);
    }

    [Fact]
    public void Targeted_and_object_materialization_have_exact_physical_scope()
    {
        var fixture = Fixture.Create();
        fixture.Prime();
        fixture.Link.PriceRate = 999m;
        fixture.Link.UnitRate = 998m;
        fixture.PurchaseOrder.ReceivedMirror = 997m;

        Assert.Equal(6m, fixture.Runtime.Materialize(fixture.PriceRate, fixture.Link));
        Assert.Equal(6m, fixture.Link.PriceRate);
        Assert.Equal(998m, fixture.Link.UnitRate);

        var priceComputations = fixture.Link.PriceComputations;
        var unitComputations = fixture.Link.UnitComputations;
        fixture.Runtime.Materialize(fixture.Link);
        Assert.Equal(6m, fixture.Link.PriceRate);
        Assert.Equal(6m, fixture.Link.UnitRate);
        Assert.Equal(997m, fixture.PurchaseOrder.ReceivedMirror);
        Assert.Equal(priceComputations, fixture.Link.PriceComputations);
        Assert.Equal(unitComputations, fixture.Link.UnitComputations);

        fixture.Runtime.Materialize(fixture.Invoice);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Runtime.Materialize(fixture.RemainingQuantity, fixture.PurchaseOrder));
        Assert.Throws<InvalidOperationException>(() => fixture.Runtime.Materialize(new InvoiceLine()));
    }

    [Fact]
    public void Object_materialization_prepares_first_rolls_back_writes_and_reuses_logical_values()
    {
        var fixture = Fixture.Create();
        fixture.Invoice.Price = 55m;
        fixture.Runtime.Apply(Change.Property(
            fixture.InvoiceLines, fixture.Invoice, x => x.Price, 60m, 55m));
        fixture.Link.ThrowUnitRateWrites = true;

        Assert.Throws<InvalidOperationException>(() => fixture.Runtime.Materialize(fixture.Link));
        Assert.Equal(6m, fixture.Link.PriceRate);
        Assert.Equal(6m, fixture.Link.UnitRate);
        var priceComputations = fixture.Link.PriceComputations;
        var unitComputations = fixture.Link.UnitComputations;
        Assert.Equal(DerivedValueState.Fresh, fixture.Runtime.GetState(fixture.PriceRate, fixture.Link));
        Assert.Equal(DerivedValueState.Fresh, fixture.Runtime.GetState(fixture.UnitRate, fixture.Link));

        fixture.Link.ThrowUnitRateWrites = false;
        fixture.Runtime.Materialize(fixture.Link);
        Assert.Equal(5.5m, fixture.Link.PriceRate);
        Assert.Equal(5.5m, fixture.Link.UnitRate);
        Assert.Equal(priceComputations, fixture.Link.PriceComputations);
        Assert.Equal(unitComputations, fixture.Link.UnitComputations);
    }

    [Fact]
    public void Projected_and_relation_value_flow_uses_existing_indexed_and_incremental_plans()
    {
        var fixture = Fixture.Create();
        fixture.Prime();
        Assert.Equal(6m, fixture.Runtime.Evaluate(fixture.ActualQuantity, fixture.Link));
        Assert.Equal(4m, fixture.Runtime.Evaluate(fixture.ReceivedQuantity, fixture.PurchaseOrder));
        Assert.Equal(1, fixture.Runtime.Evaluate(fixture.ReceiptCount, fixture.PurchaseOrder));
        Assert.Equal(1L, fixture.Runtime.Evaluate(fixture.ReceiptLongCount, fixture.PurchaseOrder));
        Assert.True(fixture.Runtime.Evaluate(fixture.HasReceipts, fixture.PurchaseOrder));

        var added = new GoodsReceipt
        {
            Id = 2,
            PurchaseOrderLineId = fixture.PurchaseOrder.Id,
            Quantity = 2m
        };
        fixture.Runtime.Add(fixture.Receipts, added);
        Assert.Equal(DerivedValueState.Fresh,
            fixture.Runtime.GetState(fixture.ReceivedQuantity, fixture.PurchaseOrder));
        Assert.Equal(6m, fixture.Runtime.Evaluate(fixture.ReceivedQuantity, fixture.PurchaseOrder));
        Assert.Equal(2, fixture.Runtime.Evaluate(fixture.ReceiptCount, fixture.PurchaseOrder));

        added.Cancelled = true;
        fixture.Runtime.Apply(Change.Property(
            fixture.Receipts, added, x => x.Cancelled, false, true));
        Assert.Equal(DerivedValueState.Invalid,
            fixture.Runtime.GetState(fixture.ReceivedQuantity, fixture.PurchaseOrder));
        Assert.Equal(4m, fixture.Runtime.Evaluate(fixture.ReceivedQuantity, fixture.PurchaseOrder));

        fixture.PurchaseOrder.OrderedQuantity = 8m;
        fixture.Runtime.Apply(Change.Property(
            fixture.PurchaseOrderLines,
            fixture.PurchaseOrder,
            x => x.OrderedQuantity,
            10m,
            8m));
        Assert.Equal(DerivedValueState.Invalid,
            fixture.Runtime.GetState(fixture.ActualQuantity, fixture.Link));
        Assert.Equal(4m, fixture.Runtime.Evaluate(fixture.ActualQuantity, fixture.Link));
    }

    [Fact]
    public void Compiled_metadata_and_debug_view_retain_v2_semantics()
    {
        var fixture = Fixture.Create();
        var diagnostics = fixture.Compiled.Diagnostics;
        var price = diagnostics.DerivedValues.Single(value => value.DefinitionKey == "price-rate");
        var unit = diagnostics.DerivedValues.Single(value => value.DefinitionKey == "unit-rate");
        var actual = diagnostics.DerivedValues.Single(value => value.DefinitionKey == "actual-quantity");
        var aggregate = diagnostics.DerivedValues.Single(value => value.DefinitionKey == "received-quantity");
        var ordered = diagnostics.DerivedValues.Single(value => value.DefinitionKey == "ordered-quantity");

        Assert.Contains(price.SemanticDependencies, value =>
            value.Kind == DerivedDependencyKind.DirectMember && value.Path == "InvoiceLine.Price");
        Assert.Contains(unit.SemanticDependencies, value =>
            value.Kind == DerivedDependencyKind.DerivedValue &&
            value.UpstreamDerivedId == price.DerivedId);
        Assert.Contains(actual.SemanticDependencies, value =>
            value.Kind == DerivedDependencyKind.ProjectedDerivedValue);
        Assert.Contains(aggregate.SemanticDependencies, value =>
            value.Kind == DerivedDependencyKind.RelationValue);
        Assert.Contains(ordered.SemanticDependencies, value =>
            value.Path == nameof(PurchaseOrderLine.OrderedQuantity) && value.HasSourceMemberClassifier);
        Assert.Equal(3, diagnostics.Materializations.Count);
        Assert.Contains(diagnostics.Materializations, value =>
            value.DerivedId == price.DerivedId && value.TargetMember == nameof(Link.PriceRate));

        Assert.Contains("price-rate [Derived<Link", fixture.Compiled.DebugView, StringComparison.Ordinal);
        Assert.Contains("depends-on InvoiceLine.Price", fixture.Compiled.DebugView, StringComparison.Ordinal);
        Assert.Contains("from price-rate", fixture.Compiled.DebugView, StringComparison.Ordinal);
        Assert.Contains("materializes-to PriceRate", fixture.Compiled.DebugView, StringComparison.Ordinal);
        Assert.Contains("operator IncrementalSum(GoodsReceipt.Quantity)", fixture.Compiled.DebugView,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DependsOn_group_member_specific_and_projected_impacts_are_preserved()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var links = model.Objects<ProjectionLink>().Key(x => x.Id);
        var shared = model.Derived(lines)
            .DependsOn(x => x.Price, x => x.OrderedQuantity)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(x => x.Price * x.OrderedQuantity);
        var asymmetric = model.Derived(lines)
            .DependsOn(x => x.Price, x => x.OrderedQuantity)
            .Impact(policy => policy
                .SourceChanged(DependencySeverity.Dirty)
                .SourceMemberChanged(x => x.Price, (oldValue, newValue) =>
                    newValue < oldValue ? DependencySeverity.Invalid : DependencySeverity.Dirty))
            .Select(x => x.Price + x.OrderedQuantity);
        var ordered = model.Derived(lines).Select(x => x.OrderedQuantity);
        var projected = model.Derived(links)
            .From(x => x.PurchaseOrderLine, ordered)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select((_, quantity) => quantity);
        var first = new PurchaseOrderLine { Id = 1, Price = 10m, OrderedQuantity = 4m };
        var second = new PurchaseOrderLine { Id = 2, Price = 12m, OrderedQuantity = 7m };
        var link = new ProjectionLink { Id = 1, PurchaseOrderLine = first };
        var runtime = model.Build().CreateRuntime(seed => seed
            .Add(lines, [first, second])
            .Add(links, [link]));
        _ = runtime.Evaluate(shared, first);
        _ = runtime.Evaluate(asymmetric, first);
        _ = runtime.Evaluate(projected, link);

        first.OrderedQuantity = 5m;
        runtime.Apply(Change.Property(lines, first, x => x.OrderedQuantity, 4m, 5m));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(shared, first));
        Assert.Equal(DerivedValueState.Dirty, runtime.GetState(asymmetric, first));
        _ = runtime.Evaluate(asymmetric, first);

        first.Price = 8m;
        runtime.Apply(Change.Property(lines, first, x => x.Price, 10m, 8m));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(asymmetric, first));

        link.PurchaseOrderLine = second;
        runtime.Apply(Change.Property(links, link, x => x.PurchaseOrderLine, first, second));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(projected, link));
        Assert.Equal(7m, runtime.Evaluate(projected, link));
    }

    [Fact]
    public void V2_validation_rejects_wrong_handles_targets_mirrors_and_opaque_gaps()
    {
        var model = new ConsistencyModelBuilder();
        var first = model.Objects<Link>().Key(x => x.Id);
        var second = model.Objects<Link>().Key(x => x.Id);
        var value = model.Derived(first).Select(x => x.LinkedQuantity);
        Assert.Throws<ArgumentException>(() => model.Derived(second).From(value));

        var foreignModel = new ConsistencyModelBuilder();
        var foreignSet = foreignModel.Objects<Link>().Key(x => x.Id);
        var foreign = foreignModel.Derived(foreignSet).Select(x => x.LinkedQuantity);
        Assert.Throws<ArgumentException>(() => model.Derived(first).From(foreign));

        var duplicate = new ConsistencyModelBuilder();
        var duplicateSet = duplicate.Objects<Link>().Key(x => x.Id);
        _ = duplicate.Derived(duplicateSet).Select(x => (decimal?)1m)
            .MaterializeTo(x => x.PriceRate);
        Assert.Throws<InvalidOperationException>(() => duplicate.Derived(duplicateSet)
            .Select(x => (decimal?)2m).MaterializeTo(x => x.PriceRate));
        Assert.Throws<ArgumentException>(() => value.MaterializeTo(x => x.PurchaseOrderLine.Price));
        Assert.Throws<ArgumentException>(() => value.MaterializeTo(x => x.ReadOnlyMirror));
        Assert.Throws<ArgumentException>(() => value.MaterializeTo(x => x.InitOnlyMirror));
        Assert.Throws<ArgumentException>(() => value.MaterializeTo(x => x.PrivateMirror));

        var mirror = new ConsistencyModelBuilder();
        var mirrorSet = mirror.Objects<Link>().Key(x => x.Id);
        _ = mirror.Derived(mirrorSet).Select(x => (decimal?)x.LinkedQuantity)
            .MaterializeTo(x => x.PriceRate).Named("price-rate");
        _ = mirror.Derived(mirrorSet).DependsOn(x => x.PriceRate)
            .Select(x => x.PriceRate).Named("bad-mirror-reader");
        var mirrorError = Assert.Throws<InvalidOperationException>(() => mirror.Build());
        Assert.Contains("From(price-rate)", mirrorError.Message, StringComparison.Ordinal);

        var opaque = new ConsistencyModelBuilder();
        var opaqueSet = opaque.Objects<Link>().Key(x => x.Id);
        _ = opaque.Derived(opaqueSet).Select(Link.CalculatePriceRate);
        Assert.Contains("incomplete dependency tracking",
            Assert.Throws<InvalidOperationException>(() => opaque.Build()).Message,
            StringComparison.Ordinal);

        var projectedIdentity = new ConsistencyModelBuilder();
        var selectedOwners = projectedIdentity.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var otherOwners = projectedIdentity.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var projectedLinks = projectedIdentity.Objects<ProjectionLink>().Key(x => x.Id);
        var otherValue = projectedIdentity.Derived(otherOwners).Select(x => x.OrderedQuantity);
        var projectedValue = projectedIdentity.Derived(projectedLinks)
            .From(x => x.PurchaseOrderLine, otherValue)
            .Select((_, quantity) => quantity);
        var selectedOwner = new PurchaseOrderLine { Id = 1, OrderedQuantity = 3m };
        var projectedLink = new ProjectionLink { Id = 1, PurchaseOrderLine = selectedOwner };
        _ = projectedValue;
        Assert.Contains("projected", Assert.Throws<InvalidOperationException>(() =>
            projectedIdentity.Build().CreateRuntime(seed => seed
                .Add(selectedOwners, [selectedOwner])
                .Add(projectedLinks, [projectedLink]))).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Recognized_aggregates_match_old_incremental_syntax()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var receipts = model.Objects<GoodsReceipt>().Key(x => x.Id);
        var relation = model.Relation(lines, receipts)
            .Where((line, receipt) => line.Id == receipt.PurchaseOrderLineId && !receipt.Cancelled);

        static void Impact(DerivedImpactPolicyBuilder<PurchaseOrderLine> policy) => policy
            .MembershipAdded(DependencySeverity.Dirty)
            .MembershipRemoved(DependencySeverity.Invalid)
            .ItemChanged(DependencySeverity.Invalid);

        var oldSum = model.Derived(lines).Using(relation).Impact(Impact).Incrementally()
            .Compute((_, matches) => matches.Sum(x => x.Quantity));
        var newSum = model.Derived(lines).From(relation).Impact(Impact).Sum(x => x.Quantity);
        var oldCount = model.Derived(lines).Using(relation).Impact(Impact).Incrementally()
            .Compute((_, matches) => matches.Count);
        var newCount = model.Derived(lines).From(relation).Impact(Impact).Count();
        var oldLongCount = model.Derived(lines).Using(relation).Impact(Impact).Incrementally()
            .Compute((_, matches) => matches.LongCount());
        var newLongCount = model.Derived(lines).From(relation).Impact(Impact).LongCount();
        var oldAny = model.Derived(lines).Using(relation).Impact(Impact).Incrementally()
            .Compute((_, matches) => matches.Any());
        var newAny = model.Derived(lines).From(relation).Impact(Impact).Any();
        var compiled = model.Build();
        var line = new PurchaseOrderLine { Id = 1 };
        var first = new GoodsReceipt { Id = 1, PurchaseOrderLineId = 1, Quantity = 4m };
        var runtime = compiled.CreateRuntime(seed => seed.Add(lines, [line]).Add(receipts, [first]));

        AssertParity();
        var added = new GoodsReceipt { Id = 2, PurchaseOrderLineId = 1, Quantity = 2m };
        runtime.Add(receipts, added);
        Assert.Equal(runtime.GetState(oldSum, line), runtime.GetState(newSum, line));
        AssertParity();

        added.Quantity = 3m;
        runtime.Apply(Change.Property(receipts, added, x => x.Quantity, 2m, 3m));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(newSum, line));
        Assert.Equal(runtime.GetState(oldSum, line), runtime.GetState(newSum, line));
        AssertParity();

        added.Quantity = 1m;
        runtime.Apply(Change.Property(receipts, added, x => x.Quantity, 3m, 1m));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(newSum, line));
        Assert.Equal(runtime.GetState(oldSum, line), runtime.GetState(newSum, line));
        AssertParity();

        added.Cancelled = true;
        runtime.Apply(Change.Property(receipts, added, x => x.Cancelled, false, true));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(newSum, line));
        Assert.Equal(runtime.GetState(oldSum, line), runtime.GetState(newSum, line));
        AssertParity();

        Assert.All(compiled.Diagnostics.DerivedValues, value =>
            Assert.StartsWith("Incremental", value.ComputationPlan, StringComparison.Ordinal));

        void AssertParity()
        {
            Assert.Equal(runtime.Evaluate(oldSum, line), runtime.Evaluate(newSum, line));
            Assert.Equal(runtime.Evaluate(oldCount, line), runtime.Evaluate(newCount, line));
            Assert.Equal(runtime.Evaluate(oldLongCount, line), runtime.Evaluate(newLongCount, line));
            Assert.Equal(runtime.Evaluate(oldAny, line), runtime.Evaluate(newAny, line));
        }
    }

    [Fact]
    public void Direct_transitive_and_projected_v2_values_match_legacy_declarations()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var links = model.Objects<ProjectionLink>().Key(x => x.Id);
        var oldDirect = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(x => x.Price * 2m);
        var newDirect = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(x => x.Price * 2m);
        var oldMultiple = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(x => x.Price + x.OrderedQuantity);
        var newMultiple = model.Derived(lines)
            .DependsOn(x => x.Price, x => x.OrderedQuantity)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(x => x.Price + x.OrderedQuantity);
        var oldTransitive = model.Derived(lines).Using(oldDirect)
            .Compute((_, value) => value + 1m);
        var newTransitive = model.Derived(lines).From(newDirect)
            .Select((_, value) => value + 1m);
        var oldProjected = model.Derived(links).Using(x => x.PurchaseOrderLine, oldTransitive)
            .Compute((_, value) => value);
        var newProjected = model.Derived(links).From(x => x.PurchaseOrderLine, newTransitive)
            .Select((_, value) => value);
        var line = new PurchaseOrderLine { Id = 1, Price = 5m, OrderedQuantity = 8m };
        var link = new ProjectionLink { Id = 1, PurchaseOrderLine = line };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(lines, [line]).Add(links, [link]));

        AssertParity();
        line.Price = 4m;
        runtime.Apply(Change.Property(lines, line, x => x.Price, 5m, 4m));
        Assert.Equal(runtime.GetState(oldDirect, line), runtime.GetState(newDirect, line));
        Assert.Equal(runtime.GetState(oldMultiple, line), runtime.GetState(newMultiple, line));
        Assert.Equal(runtime.GetState(oldTransitive, line), runtime.GetState(newTransitive, line));
        Assert.Equal(runtime.GetState(oldProjected, link), runtime.GetState(newProjected, link));
        AssertParity();

        void AssertParity()
        {
            Assert.Equal(runtime.Evaluate(oldDirect, line), runtime.Evaluate(newDirect, line));
            Assert.Equal(runtime.Evaluate(oldMultiple, line), runtime.Evaluate(newMultiple, line));
            Assert.Equal(runtime.Evaluate(oldTransitive, line), runtime.Evaluate(newTransitive, line));
            Assert.Equal(runtime.Evaluate(oldProjected, link), runtime.Evaluate(newProjected, link));
        }
    }

    [Fact]
    public void V2_invariant_facade_matches_old_value_flow_and_repair_behavior()
    {
        var oldRepairs = new List<PurchaseOrderLine>();
        var newRepairs = new List<PurchaseOrderLine>();
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
        var oldValue = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Compute(x => x.Price > 0m);
        var newValue = model.Derived(lines)
            .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
            .Select(x => x.Price > 0m);
        var oldInvariant = model.Invariant(lines).Using(oldValue)
            .Must((_, valid) => valid).ScheduleRepairWith(oldRepairs.Add);
        var newInvariant = model.Invariant(lines).From(newValue)
            .Must((_, valid) => valid).ScheduleRepairWith(newRepairs.Add);
        var line = new PurchaseOrderLine { Id = 1, Price = 10m };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(lines, [line]));
        Assert.True(runtime.Evaluate(oldInvariant, line));
        Assert.True(runtime.Evaluate(newInvariant, line));

        line.Price = 0m;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(lines, line, x => x.Price, 10m, 0m)));
        Assert.Equal(runtime.GetState(oldInvariant, line), runtime.GetState(newInvariant, line));
        Assert.Equal(2, application.Result.RepairRequests.Count);
        application.Dispatch.Invoke();
        Assert.Equal([line], oldRepairs);
        Assert.Equal([line], newRepairs);
    }

    private sealed record Fixture(
        CompiledConsistencyModel Compiled,
        ConsistencyRuntime Runtime,
        ObjectSet<InvoiceLine> InvoiceLines,
        ObjectSet<PurchaseOrderLine> PurchaseOrderLines,
        ObjectSet<Link> Links,
        ObjectSet<GoodsReceipt> Receipts,
        Derived<Link, decimal?> PriceRate,
        Derived<Link, decimal?> UnitRate,
        Derived<PurchaseOrderLine, decimal> ReceivedQuantity,
        Derived<PurchaseOrderLine, int> ReceiptCount,
        Derived<PurchaseOrderLine, long> ReceiptLongCount,
        Derived<PurchaseOrderLine, bool> HasReceipts,
        Derived<PurchaseOrderLine, decimal> RemainingQuantity,
        Derived<Link, decimal> ActualQuantity,
        Derived<Link, bool> Validity,
        Invariant<Link> Invariant,
        InvoiceLine Invoice,
        PurchaseOrderLine PurchaseOrder,
        Link Link,
        GoodsReceipt Receipt,
        List<Link> Repairs)
    {
        public static Fixture Create()
        {
            var repairs = new List<Link>();
            var model = new ConsistencyModelBuilder();
            var invoices = model.Objects<InvoiceLine>().Named("invoice-lines").Key(x => x.Id);
            var purchaseOrders = model.Objects<PurchaseOrderLine>().Named("po-lines").Key(x => x.Id);
            var links = model.Objects<Link>().Named("links").Key(x => x.Id);
            var receipts = model.Objects<GoodsReceipt>().Named("receipts").Key(x => x.Id);

            var priceRate = model.Derived(links)
                .DependsOn(x => x.InvoiceLine.Price, x => x.PurchaseOrderLine.Price)
                .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
                .Select(Link.CalculatePriceRate)
                .MaterializeTo(x => x.PriceRate)
                .Named("price-rate");
            var unitRate = model.Derived(links)
                .From(priceRate)
                .DependsOn(x => x.InvoiceLine.Quantity, x => x.PurchaseOrderLine.OrderedQuantity)
                .Impact(policy => policy.SourceChanged(DependencySeverity.Invalid))
                .Select((link, rate) => Link.CalculateUnitRate(link, rate))
                .MaterializeTo(x => x.UnitRate)
                .Named("unit-rate");
            var relation = model.Relation(purchaseOrders, receipts)
                .Where((line, receipt) =>
                    line.Id == receipt.PurchaseOrderLineId && !receipt.Cancelled)
                .Named("matching-receipts");

            static void ConfigureReceiptImpact(DerivedImpactPolicyBuilder<PurchaseOrderLine> policy) => policy
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid)
                .ItemChanged(DependencySeverity.Invalid);

            var received = model.Derived(purchaseOrders).From(relation)
                .Impact(ConfigureReceiptImpact).Sum(x => x.Quantity)
                .MaterializeTo(x => x.ReceivedMirror).Named("received-quantity");
            var count = model.Derived(purchaseOrders).From(relation)
                .Impact(ConfigureReceiptImpact).Count().Named("receipt-count");
            var longCount = model.Derived(purchaseOrders).From(relation)
                .Impact(ConfigureReceiptImpact).LongCount().Named("receipt-long-count");
            var any = model.Derived(purchaseOrders).From(relation)
                .Impact(ConfigureReceiptImpact).Any().Named("has-receipts");
            var ordered = model.Derived(purchaseOrders)
                .DependsOn(x => x.OrderedQuantity)
                .Impact(policy => policy.SourceMemberChanged(
                    x => x.OrderedQuantity,
                    (oldValue, newValue) => newValue < oldValue
                        ? DependencySeverity.Invalid
                        : DependencySeverity.Dirty))
                .Select(x => x.OrderedQuantity)
                .Named("ordered-quantity");
            var remaining = model.Derived(purchaseOrders).From(ordered).From(received)
                .Select((_, orderedValue, receivedValue) => orderedValue - receivedValue)
                .Named("remaining-quantity");
            var actual = model.Derived(links)
                .From(x => x.PurchaseOrderLine, remaining)
                .From(unitRate)
                .Select((link, remainingValue, rate) =>
                    rate == null
                        ? 0m
                        : link.LinkedQuantity < (remainingValue > 0m ? remainingValue : 0m)
                            ? link.LinkedQuantity
                            : remainingValue > 0m ? remainingValue : 0m)
                .Named("actual-quantity");
            var validity = model.Derived(links).From(unitRate)
                .Select((_, rate) => rate != null && rate > 0m)
                .Named("link-validity");
            var invariant = model.Invariant(links).From(validity)
                .Must((_, valid) => valid)
                .Named("link-validity-invariant")
                .ScheduleRepairWith(repairs.Add);
            var compiled = model.Build();

            var invoice = new InvoiceLine { Id = 1, Price = 60m, Quantity = 10m };
            var purchaseOrder = new PurchaseOrderLine { Id = 1, Price = 10m, OrderedQuantity = 10m };
            var link = new Link
            {
                Id = 1,
                InvoiceLine = invoice,
                PurchaseOrderLine = purchaseOrder,
                LinkedQuantity = 8m,
                PriceRate = 6m,
                UnitRate = 6m
            };
            var receipt = new GoodsReceipt
            {
                Id = 1,
                PurchaseOrderLineId = purchaseOrder.Id,
                Quantity = 4m
            };
            var runtime = compiled.CreateRuntime(seed => seed
                .Add(invoices, [invoice])
                .Add(purchaseOrders, [purchaseOrder])
                .Add(links, [link])
                .Add(receipts, [receipt]));
            return new Fixture(
                compiled, runtime, invoices, purchaseOrders, links, receipts,
                priceRate, unitRate, received, count, longCount, any, remaining, actual,
                validity, invariant, invoice, purchaseOrder, link, receipt, repairs);
        }

        public void Prime()
        {
            _ = Runtime.Evaluate(ReceivedQuantity, PurchaseOrder);
            _ = Runtime.Evaluate(RemainingQuantity, PurchaseOrder);
            _ = Runtime.Evaluate(PriceRate, Link);
            _ = Runtime.Evaluate(UnitRate, Link);
            _ = Runtime.Evaluate(ActualQuantity, Link);
            _ = Runtime.Evaluate(Validity, Link);
            _ = Runtime.Evaluate(Invariant, Link);
        }
    }

    private sealed class InvoiceLine
    {
        public int Id { get; init; }
        public decimal Price { get; set; }
        public decimal Quantity { get; set; }
    }

    private sealed class PurchaseOrderLine
    {
        public int Id { get; init; }
        public decimal Price { get; set; }
        public decimal OrderedQuantity { get; set; }
        public decimal ReceivedMirror { get; set; }
    }

    private sealed class Link
    {
        private decimal? _priceRate;
        private decimal? _unitRate;

        public int Id { get; init; }
        public InvoiceLine InvoiceLine { get; init; } = null!;
        public PurchaseOrderLine PurchaseOrderLine { get; init; } = null!;
        public decimal LinkedQuantity { get; init; }
        public decimal? PriceRate { get => _priceRate; set => _priceRate = value; }
        public decimal ReadOnlyMirror => LinkedQuantity;
        public decimal InitOnlyMirror { get; init; }
        public decimal PrivateMirror { get; private set; }
        public decimal? UnitRate
        {
            get => _unitRate;
            set
            {
                if (ThrowUnitRateWrites)
                    throw new InvalidOperationException("Injected UnitRate setter failure.");
                _unitRate = value;
            }
        }
        public bool ThrowUnitRateWrites { get; set; }
        public int PriceComputations { get; private set; }
        public int UnitComputations { get; private set; }

        public static decimal? CalculatePriceRate(Link link)
        {
            link.PriceComputations++;
            return link.PurchaseOrderLine.Price == 0m
                ? null
                : link.InvoiceLine.Price / link.PurchaseOrderLine.Price;
        }

        public static decimal? CalculateUnitRate(Link link, decimal? rate)
        {
            link.UnitComputations++;
            return rate is null || link.PurchaseOrderLine.OrderedQuantity == 0m
                ? null
                : rate * link.InvoiceLine.Quantity / link.PurchaseOrderLine.OrderedQuantity;
        }
    }

    private sealed class ProjectionLink
    {
        public int Id { get; init; }
        public PurchaseOrderLine PurchaseOrderLine { get; set; } = null!;
    }

    private sealed class GoodsReceipt
    {
        public int Id { get; init; }
        public int PurchaseOrderLineId { get; init; }
        public decimal Quantity { get; set; }
        public bool Cancelled { get; set; }
    }
}
