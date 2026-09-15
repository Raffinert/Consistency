using Raffinert.Relations.PurchaseOrderSample.Domain;
using Raffinert.Relations.PurchaseOrderSample.Support;

namespace Raffinert.Relations.PurchaseOrderSample.Scenarios;

internal static class PrecommitGuardScenarios
{
    // PlanDetailed restores Raffinert runtime state, not already-mutated domain objects. Rejected
    // scenarios below visibly restore those objects before constructing another independent plan.
    public static void Run()
    {
        var runner = new ScenarioRunner();
        OrphanPlans(runner);
        QuantityPlans(runner);
        ConservationPlans(runner);
        ExistencePlans(runner);
        runner.Complete("Precommit ghost matching guard plans");
    }

    private static void OrphanPlans(ScenarioRunner runner)
    {
        var model = new RelationModelBuilder();
        var invoices = model.Objects<InvoiceLine>().Named("guard-invoices").Key(x => x.Id);
        var poils = model.Objects<PurchaseOrderInvoiceLine>().Named("guard-poils").Key(x => x.Id);
        var relation = model.Relation(invoices, poils)
            .Where((invoice, poil) => invoice.Id == poil.InvoiceLineId && !poil.IsDeleted).Named("guard-active-poils");
        var count = model.Derived(invoices).Using(relation).Incrementally()
            .Compute((_, rows) => rows.Count()).Named("guard-active-poil-count");
        var valid = model.Derived(invoices).Using(count)
            .Compute((invoice, active) => !invoice.IsDeleted || active == 0).Named("guard-orphan-result");
        var orphanInvariant = model.Invariant(invoices).Using(valid).Must((_, result) => result).Named("guard-orphan-invariant");
        var invoice = new InvoiceLine();
        var line = new PurchaseOrderLine();
        var poil = new PurchaseOrderInvoiceLine { InvoiceLineId = invoice.Id, PurchaseOrderLineId = line.Id, PurchaseOrderLine = line };
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime(seed => { seed.Add(invoices, [invoice]); seed.Add(poils, [poil]); });
        invoice.IsDeleted = true;
        var violating = Plan(runtime, MutationSet.Create(Change.Property(invoices, invoice, x => x.IsDeleted, false, true)));
        runner.Check("A-P1 invoice-only deletion rejected", RuleEvaluation.Violation, Decision(violating), violating.Result);
        invoice.IsDeleted = false;
        invoice.IsDeleted = true; poil.IsDeleted = true;
        var coordinated = Plan(runtime, MutationSet.Create(
            Change.Property(invoices, invoice, x => x.IsDeleted, false, true),
            Change.Property(poils, poil, x => x.IsDeleted, false, true)));
        runner.Check("A-P2 coordinated invoice and POIL deletion", RuleEvaluation.Valid, Decision(coordinated));
        runtime.Commit(coordinated);

        var repairInvoice = new InvoiceLine { IsDeleted = true };
        var deleted = new PurchaseOrderInvoiceLine { InvoiceLineId = repairInvoice.Id, PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, IsDeleted = true };
        var active = new PurchaseOrderInvoiceLine { InvoiceLineId = repairInvoice.Id, PurchaseOrderLineId = line.Id, PurchaseOrderLine = line };
        var repairRuntime = compiled.CreateRuntime(seed => { seed.Add(invoices, [repairInvoice]); seed.Add(poils, [deleted, active]); });
        active.IsDeleted = true;
        var repaired = Plan(repairRuntime, MutationSet.Create(Change.Property(poils, active, x => x.IsDeleted, false, true)));
        runner.Check("A-P3 deleting final active POIL repairs orphan", RuleEvaluation.Valid, Decision(repaired));
        repairRuntime.Commit(repaired);
        runner.Check("A-P3 installed invariant is valid", RuleEvaluation.Valid,
            repairRuntime.GetState(orphanInvariant, repairInvoice) == InvariantEvaluationState.Valid ? RuleEvaluation.Valid : RuleEvaluation.Violation);
    }

    private static void QuantityPlans(ScenarioRunner runner)
    {
        var model = new RelationModelBuilder();
        var poils = model.Objects<PurchaseOrderInvoiceLine>().Named("guard-quantity-poils").Key(x => x.Id);
        var lgrs = model.Objects<LinkedGoodsReceipt>().Named("guard-quantity-lgrs").Key(x => x.Id);
        var relation = model.Relation(poils, lgrs).Where((poil, lgr) => poil.Id == lgr.PurchaseOrderInvoiceLineId && !lgr.IsDeleted).Named("guard-poil-lgrs");
        var sum = model.Derived(poils).Using(relation).Incrementally().Compute((_, rows) => rows.Sum(x => x.Quantity)).Named("guard-lgr-sum");
        var result = model.Derived(poils).Using(sum).Compute((poil, total) =>
            poil.GrnMode == GrnMode.Unknown ? RuleEvaluation.Unknown :
            poil.GrnMode == GrnMode.Disabled || poil.LinkedQuantity == total ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("guard-poil-balance");
        _ = model.Invariant(poils).Using(result).Must((_, value) => value != RuleEvaluation.Violation).Named("guard-poil-balance-invariant");
        var line = new PurchaseOrderLine();
        var poil = new PurchaseOrderInvoiceLine { InvoiceLineId = Guid.NewGuid(), PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, LinkedQuantity = 5 };
        var lgr = new LinkedGoodsReceipt { PurchaseOrderInvoiceLineId = poil.Id, PurchaseOrderInvoiceLine = poil, GoodsReceiptId = 1, Quantity = 5 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(poils, [poil]); seed.Add(lgrs, [lgr]); });
        lgr.Quantity = 4;
        var bad = Plan(runtime, MutationSet.Create(Change.Property(lgrs, lgr, x => x.Quantity, 5m, 4m)));
        runner.Check("B-P1 uncoordinated LGR quantity rejected", RuleEvaluation.Violation, Decision(bad), bad.Result);
        poil.LinkedQuantity = 4;
        var good = Plan(runtime, MutationSet.Create(
            Change.Property(lgrs, lgr, x => x.Quantity, 5m, 4m),
            Change.Property(poils, poil, x => x.LinkedQuantity, 5m, 4m)));
        runner.Check("B-P2 coordinated quantity change accepted", RuleEvaluation.Valid, Decision(good));
        runtime.Commit(good);
        poil.LinkedQuantity = 3; poil.GrnMode = GrnMode.Unknown;
        var unknown = Plan(runtime, MutationSet.Create(
            Change.Property(poils, poil, x => x.LinkedQuantity, 4m, 3m),
            Change.Property(poils, poil, x => x.GrnMode, GrnMode.Enabled, GrnMode.Unknown)));
        runner.Check("B-P3 unknown GRN is non-blocking", RuleEvaluation.Valid, Decision(unknown));
        poil.LinkedQuantity = 4; poil.GrnMode = GrnMode.Enabled; // reconcile rejected domain state
        var added = new LinkedGoodsReceipt { PurchaseOrderInvoiceLineId = poil.Id, PurchaseOrderInvoiceLine = poil, GoodsReceiptId = 99, Quantity = 2 };
        var addBad = Plan(runtime, MutationSet.Create(Change.Add(lgrs, added)));
        runner.Check("B-P4 LGR add without correction rejected", RuleEvaluation.Violation, Decision(addBad));
        poil.LinkedQuantity = 6;
        var addGood = Plan(runtime, MutationSet.Create(Change.Add(lgrs, added),
            Change.Property(poils, poil, x => x.LinkedQuantity, 4m, 6m)));
        runner.Check("B-P5 coordinated LGR add accepted", RuleEvaluation.Valid, Decision(addGood)); runtime.Commit(addGood);
        var removeBad = Plan(runtime, MutationSet.Create(Change.Remove(lgrs, added)));
        runner.Check("B-P6 LGR removal without correction rejected", RuleEvaluation.Violation, Decision(removeBad));
        poil.LinkedQuantity = 4;
        var removeGood = Plan(runtime, MutationSet.Create(Change.Remove(lgrs, added),
            Change.Property(poils, poil, x => x.LinkedQuantity, 6m, 4m)));
        runner.Check("B-P7 coordinated LGR removal accepted", RuleEvaluation.Valid, Decision(removeGood)); runtime.Commit(removeGood);
        poil.LinkedQuantity = 3; poil.GrnMode = GrnMode.Unknown;
        var toUnknown = Plan(runtime, MutationSet.Create(Change.Property(poils, poil, x => x.LinkedQuantity, 4m, 3m),
            Change.Property(poils, poil, x => x.GrnMode, GrnMode.Enabled, GrnMode.Unknown)));
        runner.Check("B-P8 inconsistent Enabled to Unknown", RuleEvaluation.Valid, Decision(toUnknown)); runtime.Commit(toUnknown);
        runner.Check("B-P8 domain result remains Unknown", RuleEvaluation.Unknown, runtime.Get(result, poil));
        poil.GrnMode = GrnMode.Enabled;
        var enabled = Plan(runtime, MutationSet.Create(Change.Property(poils, poil, x => x.GrnMode, GrnMode.Unknown, GrnMode.Enabled)));
        runner.Check("B-P9 inconsistent Unknown to Enabled rejected", RuleEvaluation.Violation, Decision(enabled));
    }

    private static void ConservationPlans(ScenarioRunner runner)
    {
        var model = new RelationModelBuilder();
        var lines = model.Objects<PurchaseOrderLine>().Named("guard-conservation-lines").Key(x => x.Id);
        var rows = model.Objects<PurchaseOrderLineGoodsReceipt>().Named("guard-polgrs").Key(x => x.Id);
        var result = model.Derived(rows).Compute(row =>
            row.GrnMode == GrnMode.Unknown || row.PurchaseOrderLine.IsServiceItemLine == null ? RuleEvaluation.Unknown :
            row.GrnMode == GrnMode.Disabled || row.PurchaseOrderLine.IsServiceItemLine == true ? RuleEvaluation.Valid :
            row.QuantityAvailable + row.QuantityMatched + row.QuantitySentToErp == row.QuantityReceived ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("guard-polgr-balance");
        _ = model.Invariant(rows).Using(result).Must((_, value) => value != RuleEvaluation.Violation).Named("guard-polgr-balance-invariant");
        var line = new PurchaseOrderLine { IsServiceItemLine = false };
        var row = new PurchaseOrderLineGoodsReceipt { PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, GoodsReceiptId = 2, QuantityReceived = 10, QuantityAvailable = 4, QuantityMatched = 6 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(rows, [row]); });
        row.QuantityMatched = 5;
        var bad = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.QuantityMatched, 6m, 5m)));
        runner.Check("C-P1 broken conservation rejected", RuleEvaluation.Violation, Decision(bad), bad.Result);
        row.QuantityAvailable = 5;
        var good = Plan(runtime, MutationSet.Create(
            Change.Property(rows, row, x => x.QuantityMatched, 6m, 5m),
            Change.Property(rows, row, x => x.QuantityAvailable, 4m, 5m)));
        runner.Check("C-P2 coordinated conservation accepted", RuleEvaluation.Valid, Decision(good));
        runtime.Commit(good);
        row.QuantityMatched = 4; line.IsServiceItemLine = true;
        var service = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.QuantityMatched, 5m, 4m),
            Change.Property(lines, line, x => x.IsServiceItemLine, false, true)));
        runner.Check("C-P3 service line makes imbalance non-blocking", RuleEvaluation.Valid, Decision(service)); runtime.Commit(service);
        line.IsServiceItemLine = null;
        var unresolved = Plan(runtime, MutationSet.Create(Change.Property(lines, line, x => x.IsServiceItemLine, true, null)));
        runner.Check("C-P4 unresolved service status is non-blocking", RuleEvaluation.Valid, Decision(unresolved)); runtime.Commit(unresolved);
        runner.Check("C-P4 domain result remains Unknown", RuleEvaluation.Unknown, runtime.Get(result, row));
        line.IsServiceItemLine = false;
        var normal = Plan(runtime, MutationSet.Create(Change.Property(lines, line, x => x.IsServiceItemLine, null, false)));
        runner.Check("C-P5 unresolved to normal exposes violation", RuleEvaluation.Violation, Decision(normal));
        line.IsServiceItemLine = null; row.GrnMode = GrnMode.Unknown; // reconcile rejected line change
        var grnUnknown = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.GrnMode, GrnMode.Enabled, GrnMode.Unknown)));
        runner.Check("C-P6 unknown GRN is non-blocking", RuleEvaluation.Valid, Decision(grnUnknown)); runtime.Commit(grnUnknown);
        runner.Check("C-P6 domain result remains Unknown", RuleEvaluation.Unknown, runtime.Get(result, row));
        row.GrnMode = GrnMode.Enabled; line.IsServiceItemLine = false;
        var grnEnabled = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.GrnMode, GrnMode.Unknown, GrnMode.Enabled),
            Change.Property(lines, line, x => x.IsServiceItemLine, null, false)));
        runner.Check("C-P7 enabling GRN exposes violation", RuleEvaluation.Violation, Decision(grnEnabled));
    }

    private static void ExistencePlans(ScenarioRunner runner)
    {
        var model = new RelationModelBuilder();
        var lgrs = model.Objects<LinkedGoodsReceipt>().Named("guard-existence-lgrs").Key(x => x.Id);
        var polgrs = model.Objects<PurchaseOrderLineGoodsReceipt>().Named("guard-existence-polgrs").Key(x => x.Id);
        var relation = model.Relation(lgrs, polgrs).Where((lgr, row) =>
            lgr.PurchaseOrderInvoiceLine.PurchaseOrderLineId == row.PurchaseOrderLineId && lgr.GoodsReceiptId == row.GoodsReceiptId).Named("guard-matching-polgr");
        var count = model.Derived(lgrs).Using(relation).Incrementally().Compute((_, rows) => rows.Count()).Named("guard-polgr-count");
        var result = model.Derived(lgrs).Using(count).Compute((lgr, matches) =>
            lgr.PurchaseOrderInvoiceLine.GrnMode == GrnMode.Unknown ? RuleEvaluation.Unknown :
            lgr.PurchaseOrderInvoiceLine.GrnMode == GrnMode.Disabled || lgr.IsDeleted || matches > 0 ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("guard-lgr-existence");
        _ = model.Invariant(lgrs).Using(result).Must((_, value) => value != RuleEvaluation.Violation).Named("guard-lgr-existence-invariant");
        var line = new PurchaseOrderLine();
        var poil = new PurchaseOrderInvoiceLine { InvoiceLineId = Guid.NewGuid(), PurchaseOrderLineId = line.Id, PurchaseOrderLine = line };
        var lgr = new LinkedGoodsReceipt { PurchaseOrderInvoiceLineId = poil.Id, PurchaseOrderInvoiceLine = poil, GoodsReceiptId = 3, Quantity = 1 };
        var polgr = new PurchaseOrderLineGoodsReceipt { PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, GoodsReceiptId = 3 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lgrs, [lgr]); seed.Add(polgrs, [polgr]); });
        var bad = Plan(runtime, MutationSet.Create(Change.Remove(polgrs, polgr)));
        runner.Check("D-P1 surviving LGR blocks POLGR removal", RuleEvaluation.Violation, Decision(bad), bad.Result);
        lgr.IsDeleted = true;
        var good = Plan(runtime, MutationSet.Create(Change.Remove(polgrs, polgr), Change.Property(lgrs, lgr, x => x.IsDeleted, false, true)));
        runner.Check("D-P2 coordinated LGR and POLGR removal", RuleEvaluation.Valid, Decision(good));
        runtime.Commit(good);
        var active = new LinkedGoodsReceipt { PurchaseOrderInvoiceLineId = poil.Id, PurchaseOrderInvoiceLine = poil, GoodsReceiptId = 7, Quantity = 1 };
        var matching = new PurchaseOrderLineGoodsReceipt { PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, GoodsReceiptId = 7 };
        var addPair = Plan(runtime, MutationSet.Create(Change.Add(lgrs, active), Change.Add(polgrs, matching)));
        runner.Check("D-P3 LGR and POLGR pair add accepted", RuleEvaluation.Valid, Decision(addPair)); runtime.Commit(addPair);
        active.GoodsReceiptId = 8;
        var retargetBad = Plan(runtime, MutationSet.Create(Change.Property(lgrs, active, x => x.GoodsReceiptId, 7L, 8L)));
        runner.Check("D-P5 composite retarget without replacement rejected", RuleEvaluation.Violation, Decision(retargetBad));
        var replacement = new PurchaseOrderLineGoodsReceipt { PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, GoodsReceiptId = 8 };
        var retargetGood = Plan(runtime, MutationSet.Create(Change.Property(lgrs, active, x => x.GoodsReceiptId, 7L, 8L), Change.Add(polgrs, replacement)));
        runner.Check("D-P6 composite retarget with replacement accepted", RuleEvaluation.Valid, Decision(retargetGood));
    }

    private static PreparedImpactPlan Plan(RelationRuntime runtime, MutationSet mutations) => runtime.PlanDetailed(
        runtime.Prepare(mutations), RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);

    private static RuleEvaluation Decision(PreparedImpactPlan plan) =>
        plan.HasInvariantViolations ? RuleEvaluation.Violation : RuleEvaluation.Valid;
}
