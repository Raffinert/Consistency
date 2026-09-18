using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.FinalApiDogfood;

internal static class Scenarios
{
    public static async Task RunAsync()
    {
        PriceRateUnitRateLifecycle();
        NestedOpaqueDependencies();
        ProjectedDataflow();
        RecognizedAggregatesAndImpact();
        DeclarationValidation();
        await EfBridgeAsync();
    }

    private static void PriceRateUnitRateLifecycle()
    {
        var fixture = Fixture.Create();
        Prime(fixture);
        fixture.Definitions.Repairs.Clear();
        fixture.Invoice.Price = 55m;
        var application = fixture.Runtime.Raw.ApplyDetailed(
            MutationSet.Create(Change.Property(fixture.Invoice, x => x.Price, 60m, 55m)),
            RuntimeImpactDetailLevel.Causal);

        Require(fixture.Runtime.Raw.GetState(fixture.Definitions.PriceRate.Raw, fixture.Link) ==
                DerivedValueState.Invalid, "price mutation invalidates PriceRate");
        Require(fixture.Runtime.Raw.GetState(fixture.Definitions.UnitRate.Raw, fixture.Link) ==
                DerivedValueState.Invalid, "From(priceRate) invalidates UnitRate");
        Require(fixture.Link.PriceRate == 6m && fixture.Link.UnitRate == 6m,
            "logical invalidation leaves mirrors stale");

        var rate = fixture.Runtime.Evaluate(fixture.Definitions.PriceRate, fixture.Link);
        Require(rate == 5.5m && fixture.Link.PriceRate == 6m,
            "Evaluate returns Fresh logical PriceRate without mirror write");
        var unit = fixture.Runtime.Evaluate(fixture.Definitions.UnitRate, fixture.Link);
        Require(unit == 5.5m && fixture.Link.PriceRate == 6m,
            "UnitRate consumes logical 5.5 through From, not stale mirror 6");

        var repairsBefore = fixture.Definitions.Repairs.Count;
        fixture.Runtime.Materialize(fixture.Link);
        Require(fixture.Link.PriceRate == 5.5m && fixture.Link.UnitRate == 5.5m,
            "object Materialize synchronizes every link mirror");
        Require(fixture.Definitions.Repairs.Count == repairsBefore &&
                application.Result.RepairRequests.Count == 1,
            "materialization does not consume or dispatch repair");
        application.Dispatch.Invoke();
        Require(fixture.Definitions.Repairs.SequenceEqual([fixture.Link.Id]),
            "normal repair boundary dispatches exactly once");

        fixture.Link.PriceRate = 999m;
        var targeted = fixture.Runtime.Materialize(fixture.Definitions.PriceRate, fixture.Link);
        Require(targeted == 5.5m && fixture.Link.PriceRate == 5.5m && fixture.Link.UnitRate == 5.5m,
            "targeted Materialize returns TValue and synchronizes only its known target");
    }

    private static void NestedOpaqueDependencies()
    {
        var invoiceChange = Fixture.Create();
        Prime(invoiceChange);
        var priceComputations = invoiceChange.Link.PriceRateComputations;
        invoiceChange.Invoice.Price = 54m;
        invoiceChange.Runtime.Raw.Apply(Change.Property(invoiceChange.Invoice, x => x.Price, 60m, 54m));
        Require(invoiceChange.Runtime.Evaluate(invoiceChange.Definitions.PriceRate, invoiceChange.Link) == 5.4m &&
                invoiceChange.Link.PriceRateComputations == priceComputations + 1,
            "DependsOn tracks opaque InvoiceLine.Price read");

        var poChange = Fixture.Create();
        Prime(poChange);
        priceComputations = poChange.Link.PriceRateComputations;
        poChange.PurchaseOrder.Price = 12m;
        poChange.Runtime.Raw.Apply(Change.Property(poChange.PurchaseOrder, x => x.Price, 10m, 12m));
        Require(poChange.Runtime.Evaluate(poChange.Definitions.PriceRate, poChange.Link) == 5m &&
                poChange.Link.PriceRateComputations == priceComputations + 1,
            "DependsOn tracks opaque PurchaseOrderLine.Price read");

        var incomplete = new FinalModel();
        var links = incomplete.Objects<PurchaseOrderInvoiceLine>().Named("missing-links").Key(x => x.Id);
        _ = incomplete.Derived(links)
            .Select(x => PurchaseOrderInvoiceLine.CalculatePriceRate(x))
            .Named("missing-price-dependencies");
        var rejected = Throws<InvalidOperationException>(() => incomplete.Build(), out var error);
        Require(rejected && error!.Message.Contains("incomplete dependency tracking", StringComparison.Ordinal),
            "opaque calculator without DependsOn is rejected by current Build diagnostics");
    }

    private static void ProjectedDataflow()
    {
        var fixture = Fixture.Create();
        Prime(fixture);
        Require(fixture.Runtime.Evaluate(fixture.Definitions.ActualQuantity, fixture.Link) == 6m,
            "projected RemainingQuantity and local UnitRate feed ActualQuantity");

        fixture.PurchaseOrder.OrderedQuantity = 8m;
        fixture.Runtime.Raw.Apply(Change.Property(
            fixture.Definitions.PurchaseOrderLines.Raw,
            fixture.PurchaseOrder,
            x => x.OrderedQuantity,
            10m,
            8m));
        Require(fixture.Runtime.Raw.GetState(fixture.Definitions.RemainingQuantity.Raw, fixture.PurchaseOrder) ==
                DerivedValueState.Invalid &&
                fixture.Runtime.Raw.GetState(fixture.Definitions.ActualQuantity.Raw, fixture.Link) ==
                DerivedValueState.Invalid,
            "subtractive source-member impact is Invalid and routes through the projection");
        Require(fixture.Runtime.Evaluate(fixture.Definitions.ActualQuantity, fixture.Link) == 4m,
            "cross-object From selector supplies current PO-line RemainingQuantity");

        var increase = Fixture.Create();
        Prime(increase);
        increase.PurchaseOrder.OrderedQuantity = 12m;
        increase.Runtime.Raw.Apply(Change.Property(
            increase.Definitions.PurchaseOrderLines.Raw,
            increase.PurchaseOrder,
            x => x.OrderedQuantity,
            10m,
            12m));
        Require(increase.Runtime.Raw.GetState(
                increase.Definitions.RemainingQuantity.Raw, increase.PurchaseOrder) == DerivedValueState.Dirty,
            "additive source-member impact remains Dirty");
    }

    private static void RecognizedAggregatesAndImpact()
    {
        var fixture = Fixture.Create();
        Require(fixture.Runtime.Evaluate(fixture.Definitions.ReceivedQuantity, fixture.PurchaseOrder) == 4m,
            "initial Sum");
        Require(fixture.Runtime.Evaluate(fixture.Definitions.ReceiptCount, fixture.PurchaseOrder) == 1,
            "initial Count");
        Require(fixture.Runtime.Evaluate(fixture.Definitions.ReceiptLongCount, fixture.PurchaseOrder) == 1L,
            "initial LongCount");
        Require(fixture.Runtime.Evaluate(fixture.Definitions.HasReceipts, fixture.PurchaseOrder),
            "initial Any");

        var added = new GoodsReceipt
        {
            Id = 2,
            PurchaseOrderLineId = fixture.PurchaseOrder.Id,
            Quantity = 2m
        };
        fixture.Runtime.Raw.Add(fixture.Definitions.GoodsReceipts.Raw, added);
        Require(fixture.Runtime.Raw.GetState(fixture.Definitions.ReceivedQuantity.Raw, fixture.PurchaseOrder) ==
                DerivedValueState.Fresh &&
                fixture.Runtime.Evaluate(fixture.Definitions.ReceivedQuantity, fixture.PurchaseOrder) == 6m &&
                fixture.Runtime.Evaluate(fixture.Definitions.ReceiptCount, fixture.PurchaseOrder) == 2 &&
                fixture.Runtime.Evaluate(fixture.Definitions.ReceiptLongCount, fixture.PurchaseOrder) == 2L &&
                fixture.Runtime.Evaluate(fixture.Definitions.HasReceipts, fixture.PurchaseOrder),
            "additive membership uses existing incremental aggregate semantics");

        added.Cancelled = true;
        fixture.Runtime.Raw.Apply(Change.Property(
            fixture.Definitions.GoodsReceipts.Raw, added, x => x.Cancelled, false, true));
        Require(fixture.Runtime.Raw.GetState(fixture.Definitions.ReceivedQuantity.Raw, fixture.PurchaseOrder) ==
                DerivedValueState.Invalid,
            "subtractive/cancellation policy is visibly Invalid");
        Require(fixture.Runtime.Evaluate(fixture.Definitions.ReceivedQuantity, fixture.PurchaseOrder) == 4m &&
                fixture.Runtime.Evaluate(fixture.Definitions.ReceiptCount, fixture.PurchaseOrder) == 1 &&
                fixture.Runtime.Evaluate(fixture.Definitions.ReceiptLongCount, fixture.PurchaseOrder) == 1L &&
                fixture.Runtime.Evaluate(fixture.Definitions.HasReceipts, fixture.PurchaseOrder),
            "Invalid aggregate falls back to correct recomputation");

        var rawDebug = fixture.Definitions.Compiled.Raw.DebugView;
        Require(rawDebug.Contains("IncrementalSum(GoodsReceipt.Quantity)", StringComparison.Ordinal) &&
                rawDebug.Contains("IncrementalCount", StringComparison.Ordinal) &&
                rawDebug.Contains("IncrementalLongCount", StringComparison.Ordinal) &&
                rawDebug.Contains("IncrementalAny", StringComparison.Ordinal),
            "all four semantic operators honestly select existing optimized plans");
    }

    private static void DeclarationValidation()
    {
        var mirror = new FinalModel();
        var links = mirror.Objects<PurchaseOrderInvoiceLine>().Named("mirror-links").Key(x => x.Id);
        _ = mirror.Derived(links)
            .DependsOn(x => x.InvoiceLine.Price, x => x.PurchaseOrderLine.Price)
            .Select(PurchaseOrderInvoiceLine.CalculatePriceRate)
            .MaterializeTo(x => x.PriceRate)
            .Named("price-rate");
        _ = mirror.Derived(links)
            .DependsOn(x => x.PriceRate)
            .Select(x => x.PriceRate)
            .Named("bad-property-consumer");
        var mirrorRejected = Throws<InvalidOperationException>(() => mirror.Build(), out var mirrorError);
        Require(mirrorRejected && mirrorError!.Message.Contains("From(price-rate)", StringComparison.Ordinal),
            "mirror-property dependency is rejected at Build with derived-handle fix");

        var duplicate = new FinalModel();
        var duplicateLinks = duplicate.Objects<PurchaseOrderInvoiceLine>().Named("duplicate-links").Key(x => x.Id);
        _ = duplicate.Derived(duplicateLinks).Select(x => (decimal?)1m)
            .MaterializeTo(x => x.PriceRate).Named("first-target");
        var duplicateRejected = Throws<InvalidOperationException>(() =>
            duplicate.Derived(duplicateLinks).Select(x => (decimal?)2m)
                .MaterializeTo(x => x.PriceRate), out _);
        Require(duplicateRejected, "duplicate materialization target is rejected during declaration");

        var firstModel = new FinalModel();
        var firstSet = firstModel.Objects<PurchaseOrderLine>().Named("first").Key(x => x.Id);
        var firstValue = firstModel.Derived(firstSet).Select(x => x.Price).Named("first-price");
        var secondModel = new FinalModel();
        var secondSet = secondModel.Objects<PurchaseOrderLine>().Named("second").Key(x => x.Id);
        Require(Throws<ArgumentException>(() => secondModel.Derived(secondSet).From(firstValue), out _),
            "cross-model handle is rejected at declaration");

        var sameModel = new FinalModel();
        var setA = sameModel.Objects<PurchaseOrderLine>().Named("same-a").Key(x => x.Id);
        var setB = sameModel.Objects<PurchaseOrderLine>().Named("same-b").Key(x => x.Id);
        var valueA = sameModel.Derived(setA).Select(x => x.Price).Named("a-price");
        Require(Throws<ArgumentException>(() => sameModel.Derived(setB).From(valueA), out _),
            "same CLR type but different ObjectSet identity is rejected at declaration");
    }

    private static async Task EfBridgeAsync()
    {
        var fixture = Fixture.Create();
        var mappings = new ConsistencyEfCoreMappings()
            .Map(fixture.Definitions.InvoiceLines.Raw)
            .Map(fixture.Definitions.PurchaseOrderLines.Raw)
            .Map(fixture.Definitions.Links.Raw)
            .Map(fixture.Definitions.GoodsReceipts.Raw)
            .Map(fixture.Definitions.Allocations.Raw);
        fixture.Definitions.Compiled.AddMaterializationsTo(mappings);
        mappings.Enforce(fixture.Definitions.LinkInvariant.Raw);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<DogfoodDbContext>().UseSqlite(connection).Options;
        await using var db = new DogfoodDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Attach(fixture.Link);
        db.ChangeTracker.AcceptAllChanges();
        fixture.Invoice.Price = 55m;
        fixture.Runtime.Raw.Apply(Change.Property(fixture.Invoice, x => x.Price, 60m, 55m));
        fixture.Runtime.Materialize(fixture.Link);
        db.ChangeTracker.DetectChanges();
        Require(db.Entry(fixture.Link).Property(x => x.PriceRate).IsModified &&
                db.Entry(fixture.Link).Property(x => x.UnitRate).IsModified,
            "facade metadata bridges intentionally to EF mappings and runtime materialization");
    }

    private static void Prime(Fixture fixture)
    {
        _ = fixture.Runtime.Evaluate(fixture.Definitions.ReceivedQuantity, fixture.PurchaseOrder);
        _ = fixture.Runtime.Evaluate(fixture.Definitions.RemainingQuantity, fixture.PurchaseOrder);
        _ = fixture.Runtime.Evaluate(fixture.Definitions.PriceRate, fixture.Link);
        _ = fixture.Runtime.Evaluate(fixture.Definitions.UnitRate, fixture.Link);
        _ = fixture.Runtime.Evaluate(fixture.Definitions.ActualQuantity, fixture.Link);
        _ = fixture.Runtime.Evaluate(fixture.Definitions.LinkValidity, fixture.Link);
        _ = fixture.Runtime.Raw.Evaluate(fixture.Definitions.LinkInvariant.Raw, fixture.Link);
    }

    private static bool Throws<TException>(Action action, out TException? error) where TException : Exception
    {
        try
        {
            action();
            error = null;
            return false;
        }
        catch (TException caught)
        {
            error = caught;
            return true;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"Final API dogfood: {message}");
    }

    private sealed record Fixture(
        DogfoodDefinitions Definitions,
        FinalRuntime Runtime,
        InvoiceLine Invoice,
        PurchaseOrderLine PurchaseOrder,
        PurchaseOrderInvoiceLine Link,
        GoodsReceipt Receipt,
        Allocation Allocation)
    {
        public static Fixture Create()
        {
            var definitions = DogfoodDefinitions.Create();
            var invoice = new InvoiceLine { Id = 1, Price = 60m, Quantity = 10m };
            var po = new PurchaseOrderLine
            {
                Id = 1,
                Price = 10m,
                OrderedQuantity = 10m
            };
            var link = new PurchaseOrderInvoiceLine
            {
                Id = 1,
                InvoiceLineId = invoice.Id,
                InvoiceLine = invoice,
                PurchaseOrderLineId = po.Id,
                PurchaseOrderLine = po,
                PriceRate = 6m,
                UnitRate = 6m,
                LinkedQuantity = 8m
            };
            var receipt = new GoodsReceipt
            {
                Id = 1,
                PurchaseOrderLineId = po.Id,
                Quantity = 4m
            };
            var allocation = new Allocation
            {
                Id = 1,
                PurchaseOrderLineId = po.Id,
                PurchaseOrderLine = po,
                RequestedQuantity = 3m
            };
            var runtime = definitions.Compiled.CreateRuntime(seed => seed
                .Add(definitions.InvoiceLines, [invoice])
                .Add(definitions.PurchaseOrderLines, [po])
                .Add(definitions.Links, [link])
                .Add(definitions.GoodsReceipts, [receipt])
                .Add(definitions.Allocations, [allocation]));
            return new Fixture(definitions, runtime, invoice, po, link, receipt, allocation);
        }
    }
}
