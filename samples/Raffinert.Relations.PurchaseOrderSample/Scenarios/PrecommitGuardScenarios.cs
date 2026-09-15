using Raffinert.Relations.PurchaseOrderSample.Domain;
using Raffinert.Relations.PurchaseOrderSample.Support;

namespace Raffinert.Relations.PurchaseOrderSample.Scenarios;

internal static class PrecommitGuardScenarios
{
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
        _ = model.Invariant(invoices).Using(valid).Must((_, result) => result).Named("guard-orphan-invariant");
        var invoice = new InvoiceLine();
        var line = new PurchaseOrderLine();
        var poil = new PurchaseOrderInvoiceLine { InvoiceLineId = invoice.Id, PurchaseOrderLineId = line.Id, PurchaseOrderLine = line };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(invoices, [invoice]); seed.Add(poils, [poil]); });
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
    }

    private static void ConservationPlans(ScenarioRunner runner)
    {
        var model = new RelationModelBuilder();
        var rows = model.Objects<PurchaseOrderLineGoodsReceipt>().Named("guard-polgrs").Key(x => x.Id);
        var result = model.Derived(rows).Compute(row =>
            row.GrnMode == GrnMode.Unknown || row.PurchaseOrderLine.IsServiceItemLine == null ? RuleEvaluation.Unknown :
            row.GrnMode == GrnMode.Disabled || row.PurchaseOrderLine.IsServiceItemLine == true ? RuleEvaluation.Valid :
            row.QuantityAvailable + row.QuantityMatched + row.QuantitySentToErp == row.QuantityReceived ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("guard-polgr-balance");
        _ = model.Invariant(rows).Using(result).Must((_, value) => value != RuleEvaluation.Violation).Named("guard-polgr-balance-invariant");
        var line = new PurchaseOrderLine { IsServiceItemLine = false };
        var row = new PurchaseOrderLineGoodsReceipt { PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, GoodsReceiptId = 2, QuantityReceived = 10, QuantityAvailable = 4, QuantityMatched = 6 };
        var runtime = model.Build().CreateRuntime(seed => seed.Add(rows, [row]));
        row.QuantityMatched = 5;
        var bad = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.QuantityMatched, 6m, 5m)));
        runner.Check("C-P1 broken conservation rejected", RuleEvaluation.Violation, Decision(bad), bad.Result);
        row.QuantityAvailable = 5;
        var good = Plan(runtime, MutationSet.Create(
            Change.Property(rows, row, x => x.QuantityMatched, 6m, 5m),
            Change.Property(rows, row, x => x.QuantityAvailable, 4m, 5m)));
        runner.Check("C-P2 coordinated conservation accepted", RuleEvaluation.Valid, Decision(good));
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
    }

    private static PreparedImpactPlan Plan(RelationRuntime runtime, MutationSet mutations) => runtime.PlanDetailed(
        runtime.Prepare(mutations), RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);

    private static RuleEvaluation Decision(PreparedImpactPlan plan) =>
        plan.HasInvariantViolations ? RuleEvaluation.Violation : RuleEvaluation.Valid;
}
