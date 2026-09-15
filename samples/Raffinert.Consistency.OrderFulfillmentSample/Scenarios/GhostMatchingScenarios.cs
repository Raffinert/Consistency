using Raffinert.Consistency.OrderFulfillmentSample.Domain;
using Raffinert.Consistency.OrderFulfillmentSample.Support;

namespace Raffinert.Consistency.OrderFulfillmentSample.Scenarios;

internal static class GhostMatchingScenarios
{
    public static void Run()
    {
        var model = new ConsistencyModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Named("invoice-lines").Key(x => x.Id);
        var poLines = model.Objects<PurchaseOrderLine>().Named("purchase-order-lines").Key(x => x.Id);
        var poils = model.Objects<PurchaseOrderInvoiceLine>().Named("purchase-order-invoice-lines").Key(x => x.Id);
        var receipts = model.Objects<GoodsReceipt>().Named("goods-receipts").Key(x => x.Id);
        var linkedReceipts = model.Objects<LinkedGoodsReceipt>().Named("linked-goods-receipts").Key(x => x.Id);
        var polgrs = model.Objects<PurchaseOrderLineGoodsReceipt>().Named("purchase-order-line-goods-receipts").Key(x => x.Id);

        var activePoilsByInvoice = model.Relation(invoices, poils)
            .Where((invoice, poil) => invoice.Id == poil.InvoiceLineId && !poil.IsDeleted)
            .Named("invoice-active-poils");
        var activePoilsByLine = model.Relation(poLines, poils)
            .Where((line, poil) => line.Id == poil.PurchaseOrderLineId && !poil.IsDeleted)
            .Named("po-line-active-poils");
        var activeLinkedReceipts = model.Relation(poils, linkedReceipts)
            .Where((poil, lgr) => poil.Id == lgr.PurchaseOrderInvoiceLineId && !lgr.IsDeleted)
            .Named("poil-active-linked-receipts");
        var polgrsByLine = model.Relation(poLines, polgrs)
            .Where((line, polgr) => line.Id == polgr.PurchaseOrderLineId).Named("po-line-polgrs");
        var matchingPolgr = model.Relation(linkedReceipts, polgrs)
            .Where((lgr, polgr) => lgr.PurchaseOrderInvoiceLine.PurchaseOrderLineId == polgr.PurchaseOrderLineId
                && lgr.GoodsReceiptId == polgr.GoodsReceiptId).Named("lgr-matching-polgr");

        var activePoilCount = model.Derived(invoices).Using(activePoilsByInvoice).Incrementally()
            .Compute((_, matches) => matches.Count()).Named("active-poil-count");
        var deletedInvoiceIntegrity = model.Derived(invoices).Using(activePoilCount)
            .Compute((invoice, count) => !invoice.IsDeleted || count == 0).Named("deleted-invoice-poil-integrity");
        var deletedInvoiceInvariant = model.Invariant(invoices).Using(deletedInvoiceIntegrity)
            .Must((_, valid) => valid).Named("deleted-invoice-poil-integrity-invariant");

        var linkedReceiptQuantity = model.Derived(poils).Using(activeLinkedReceipts).Incrementally()
            .Compute((_, matches) => matches.Sum(x => x.Quantity)).Named("linked-receipt-quantity");
        var poilBalance = model.Derived(poils).Using(linkedReceiptQuantity).Compute((poil, linked) =>
            poil.GrnMode == GrnMode.Unknown ? RuleEvaluation.Unknown :
            poil.GrnMode == GrnMode.Disabled || poil.LinkedQuantity == linked ? RuleEvaluation.Valid : RuleEvaluation.Violation)
            .Named("poil-linked-quantity-balance");
        _ = model.Invariant(poils).Using(poilBalance)
            .Must((_, result) => result != RuleEvaluation.Violation)
            .Named("poil-linked-quantity-balance-invariant");

        var polgrBalance = model.Derived(polgrs).Compute(polgr =>
            polgr.GrnMode == GrnMode.Unknown || polgr.PurchaseOrderLine.IsServiceItemLine == null ? RuleEvaluation.Unknown :
            polgr.GrnMode == GrnMode.Disabled || polgr.PurchaseOrderLine.IsServiceItemLine == true ? RuleEvaluation.Valid :
            polgr.QuantityAvailable + polgr.QuantityMatched + polgr.QuantitySentToErp == polgr.QuantityReceived
                ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("polgr-bookkeeping-balance");
        _ = model.Invariant(polgrs).Using(polgrBalance)
            .Must((_, result) => result != RuleEvaluation.Violation)
            .Named("polgr-bookkeeping-balance-invariant");

        var matchingPolgrCount = model.Derived(linkedReceipts).Using(matchingPolgr).Incrementally()
            .Compute((_, matches) => matches.Count()).Named("matching-polgr-count");
        var lgrBookkeeping = model.Derived(linkedReceipts).Using(matchingPolgrCount).Compute((lgr, count) =>
            lgr.PurchaseOrderInvoiceLine.GrnMode == GrnMode.Unknown ? RuleEvaluation.Unknown :
            lgr.PurchaseOrderInvoiceLine.GrnMode == GrnMode.Disabled || lgr.IsDeleted || count > 0
                ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("lgr-bookkeeping-existence");
        _ = model.Invariant(linkedReceipts).Using(lgrBookkeeping)
            .Must((_, result) => result != RuleEvaluation.Violation)
            .Named("lgr-bookkeeping-existence-invariant");

        // Kept in the graph intentionally: these relations prove that POIL and POLGR rows are
        // reachable from their purchase-order line without scenario-side dictionary matching.
        _ = activePoilsByLine; _ = polgrsByLine; _ = receipts;

        var data = Cases.Create();
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(invoices, data.Invoices); seed.Add(poLines, data.Lines); seed.Add(poils, data.Poils);
            seed.Add(receipts, data.Receipts); seed.Add(linkedReceipts, data.LinkedReceipts); seed.Add(polgrs, data.Polgrs);
        });
        var runner = new ScenarioRunner();

        foreach (var test in data.InvoiceCases)
            runner.Check(test.Name, test.Expected, runtime.Evaluate(deletedInvoiceInvariant, test.Value) ? RuleEvaluation.Valid : RuleEvaluation.Violation);
        foreach (var test in data.PoilCases)
            runner.Check(test.Name, test.Expected, runtime.Get(poilBalance, test.Value));
        foreach (var test in data.PolgrCases)
            runner.Check(test.Name, test.Expected, runtime.Get(polgrBalance, test.Value));
        foreach (var test in data.LgrCases)
            runner.Check(test.Name, test.Expected, runtime.Get(lgrBookkeeping, test.Value));

        var removable = data.RemovablePolgr;
        _ = runtime.Get(lgrBookkeeping, data.RemovalLgr);
        var removal = runtime.ApplyDetailed(MutationSet.Create(Change.Remove(polgrs, removable)), RuntimeImpactDetailLevel.Causal);
        runner.Check("D4 POLGR removed while LGR survives", RuleEvaluation.Violation, runtime.Get(lgrBookkeeping, data.RemovalLgr), removal.Result);

        var pairLine = new PurchaseOrderLine { IsServiceItemLine = false };
        var pairPoil = new PurchaseOrderInvoiceLine { InvoiceLineId = Guid.NewGuid(), PurchaseOrderLineId = pairLine.Id, PurchaseOrderLine = pairLine, LinkedQuantity = 2 };
        var pairLgr = new LinkedGoodsReceipt { PurchaseOrderInvoiceLineId = pairPoil.Id, PurchaseOrderInvoiceLine = pairPoil, GoodsReceiptId = 9001, Quantity = 2 };
        var pairPolgr = new PurchaseOrderLineGoodsReceipt { PurchaseOrderLineId = pairLine.Id, PurchaseOrderLine = pairLine, GoodsReceiptId = 9001, QuantityReceived = 2, QuantityMatched = 2 };
        var addition = runtime.ApplyDetailed(MutationSet.Create(Change.Add(poLines, pairLine), Change.Add(poils, pairPoil),
            Change.Add(linkedReceipts, pairLgr), Change.Add(polgrs, pairPolgr)), RuntimeImpactDetailLevel.Causal);
        runner.Check("D6 LGR and POLGR added in one MutationSet", RuleEvaluation.Valid, runtime.Get(lgrBookkeeping, pairLgr), addition.Result);
        var versionBeforeRemoval = runtime.Version;
        var pairRemoval = runtime.ApplyDetailed(
            MutationSet.Create(Change.Remove(linkedReceipts, pairLgr), Change.Remove(polgrs, pairPolgr)),
            RuntimeImpactDetailLevel.Causal);
        var removedBothRelationMemberships = pairRemoval.Result.RelationImpacts
            .SelectMany(impact => impact.RemovedPairs)
            .Any(pair => ReferenceEquals(pair.Left, pairLgr) && ReferenceEquals(pair.Right, pairPolgr));
        var removalWasAtomicAndExplained = runtime.Version == versionBeforeRemoval + 1
            && removedBothRelationMemberships
            && pairRemoval.Result.InvariantImpacts.All(impact => impact.Sources.All(source =>
                !ReferenceEquals(source.Source, pairLgr)));
        runner.Check("D5 LGR and POLGR removed in one MutationSet", RuleEvaluation.Valid,
            removalWasAtomicAndExplained ? RuleEvaluation.Valid : RuleEvaluation.Violation,
            pairRemoval.Result);
        runner.Complete("Ghost matching guard dogfooding");
    }

    private sealed record Case<T>(string Name, RuleEvaluation Expected, T Value);

    private sealed class Cases
    {
        public List<InvoiceLine> Invoices { get; } = []; public List<PurchaseOrderLine> Lines { get; } = [];
        public List<PurchaseOrderInvoiceLine> Poils { get; } = []; public List<GoodsReceipt> Receipts { get; } = [];
        public List<LinkedGoodsReceipt> LinkedReceipts { get; } = []; public List<PurchaseOrderLineGoodsReceipt> Polgrs { get; } = [];
        public List<Case<InvoiceLine>> InvoiceCases { get; } = []; public List<Case<PurchaseOrderInvoiceLine>> PoilCases { get; } = [];
        public List<Case<PurchaseOrderLineGoodsReceipt>> PolgrCases { get; } = []; public List<Case<LinkedGoodsReceipt>> LgrCases { get; } = [];
        public required PurchaseOrderLineGoodsReceipt RemovablePolgr { get; set; }
        public required LinkedGoodsReceipt RemovalLgr { get; set; }

        public static Cases Create()
        {
            var result = new Cases { RemovablePolgr = null!, RemovalLgr = null! };
            InvoiceCase(result, "A1 deleted invoice with no POIL", true, []);
            InvoiceCase(result, "A2 deleted invoice with deleted POIL", true, [true]);
            InvoiceCase(result, "A3 deleted invoice with active POIL", false, [false]);
            InvoiceCase(result, "A4 mixed POILs leaves active orphan", false, [true, false]);
            InvoiceCase(result, "A5 active invoice permits active POIL", true, [false], deleted: false);
            PoilCase(result, "B1 linked quantity equals LGR total", 10, [4, 6], RuleEvaluation.Valid);
            PoilCase(result, "B2 linked quantity differs from LGR total", 10, [9], RuleEvaluation.Violation);
            PoilCase(result, "B3 zero POIL with no LGR", 0, [], RuleEvaluation.Valid);
            PoilCase(result, "B4 nonzero POIL with no LGR", 2, [], RuleEvaluation.Violation);
            PoilCase(result, "B5 multiple LGR rows aggregate", 10, [2, 3, 5], RuleEvaluation.Valid);
            var deletedLgr = PoilCase(result, "B6 deleted LGR is excluded", 4, [4, 6], RuleEvaluation.Valid);
            deletedLgr.IsDeleted = true;
            PoilCase(result, "G1 GRN disabled skips LGR balance", 10, [], RuleEvaluation.Valid, GrnMode.Disabled);
            PoilCase(result, "G2 unresolved GRN setting", 10, [], RuleEvaluation.Unknown, GrnMode.Unknown);
            PolgrCase(result, "C1 normal line balanced", false, 10, 3, 5, 2, RuleEvaluation.Valid);
            PolgrCase(result, "C2 normal line unbalanced", false, 10, 3, 4, 2, RuleEvaluation.Violation);
            PolgrCase(result, "C3 service line skipped", true, 10, 0, 0, 0, RuleEvaluation.Valid);
            PolgrCase(result, "C4 unresolved service classification", null, 10, 0, 0, 0, RuleEvaluation.Unknown);
            PolgrCase(result, "G3 unresolved GRN for POLGR", false, 10, 0, 0, 0, RuleEvaluation.Unknown, GrnMode.Unknown);
            LgrCase(result, "D1 LGR has corresponding POLGR", true, 5001, RuleEvaluation.Valid);
            LgrCase(result, "D2 LGR has no corresponding POLGR", false, 5002, RuleEvaluation.Violation);
            LgrCase(result, "D3 multiple LGRs share one POLGR", true, 5003, RuleEvaluation.Valid, count: 2);
            var removal = LgrCase(result, "D4 baseline before removal", true, 5004, RuleEvaluation.Valid);
            result.RemovalLgr = removal.lgr; result.RemovablePolgr = removal.polgr!;
            return result;
        }

        private static void InvoiceCase(Cases c, string name, bool valid, bool[] poilDeleted, bool deleted = true)
        {
            var invoice = new InvoiceLine { IsDeleted = deleted }; c.Invoices.Add(invoice);
            foreach (var isDeleted in poilDeleted) { var line = new PurchaseOrderLine(); c.Lines.Add(line); c.Poils.Add(new PurchaseOrderInvoiceLine { InvoiceLineId = invoice.Id, PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, IsDeleted = isDeleted }); }
            c.InvoiceCases.Add(new(name, valid ? RuleEvaluation.Valid : RuleEvaluation.Violation, invoice));
        }
        private static LinkedGoodsReceipt PoilCase(Cases c, string name, decimal quantity, decimal[] lgrs, RuleEvaluation expected, GrnMode mode = GrnMode.Enabled)
        {
            var line = new PurchaseOrderLine(); var poil = new PurchaseOrderInvoiceLine { InvoiceLineId = Guid.NewGuid(), PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, LinkedQuantity = quantity, GrnMode = mode };
            c.Lines.Add(line); c.Poils.Add(poil); LinkedGoodsReceipt last = null!;
            foreach (var amount in lgrs) { last = new LinkedGoodsReceipt { PurchaseOrderInvoiceLineId = poil.Id, PurchaseOrderInvoiceLine = poil, GoodsReceiptId = 1000 + c.LinkedReceipts.Count, Quantity = amount }; c.LinkedReceipts.Add(last); }
            c.PoilCases.Add(new(name, expected, poil));
            return last;
        }
        private static void PolgrCase(Cases c, string name, bool? service, decimal received, decimal available, decimal matched, decimal sent, RuleEvaluation expected, GrnMode mode = GrnMode.Enabled)
        {
            var line = new PurchaseOrderLine { IsServiceItemLine = service }; var polgr = new PurchaseOrderLineGoodsReceipt { PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, GoodsReceiptId = 2000 + c.Polgrs.Count, QuantityReceived = received, QuantityAvailable = available, QuantityMatched = matched, QuantitySentToErp = sent, GrnMode = mode };
            c.Lines.Add(line); c.Polgrs.Add(polgr); c.PolgrCases.Add(new(name, expected, polgr));
        }
        private static (LinkedGoodsReceipt lgr, PurchaseOrderLineGoodsReceipt? polgr) LgrCase(Cases c, string name, bool hasPolgr, long receiptId, RuleEvaluation expected, int count = 1)
        {
            var line = new PurchaseOrderLine { IsServiceItemLine = false }; var poil = new PurchaseOrderInvoiceLine { InvoiceLineId = Guid.NewGuid(), PurchaseOrderLineId = line.Id, PurchaseOrderLine = line };
            c.Lines.Add(line); c.Poils.Add(poil); PurchaseOrderLineGoodsReceipt? polgr = null;
            if (hasPolgr) { polgr = new PurchaseOrderLineGoodsReceipt { PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, GoodsReceiptId = receiptId }; c.Polgrs.Add(polgr); }
            LinkedGoodsReceipt first = null!;
            for (var i = 0; i < count; i++) { var lgr = new LinkedGoodsReceipt { PurchaseOrderInvoiceLineId = poil.Id, PurchaseOrderInvoiceLine = poil, GoodsReceiptId = receiptId, Quantity = 1 }; c.LinkedReceipts.Add(lgr); c.LgrCases.Add(new($"{name}{(count > 1 ? $" row {i + 1}" : "")}", expected, lgr)); first ??= lgr; }
            return (first, polgr);
        }
    }
}
