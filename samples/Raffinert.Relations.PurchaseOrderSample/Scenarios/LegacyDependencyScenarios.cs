using Raffinert.Relations.PurchaseOrderSample.Domain;
namespace Raffinert.Relations.PurchaseOrderSample.Scenarios;

internal static class LegacyDependencyScenarios
{
    public static void Run()
    {
        var repairs = new List<Guid>(); var model = new RelationModelBuilder();
        var lines = model.Objects<PurchaseOrderLine>().Named("po-lines").Key(x => x.Id);
        var receipts = model.Objects<GoodsReceipt>().Named("goods-receipts").Key(x => x.Id);
        var links = model.Objects<PurchaseOrderInvoiceLine>().Named("invoice-links").Key(x => x.Id);
        var matching = model.Relation(lines, receipts).Where((l, r) => l.OrderNumber == r.OrderNumber && l.ItemNumber == r.ItemNumber && !r.Cancelled).Named("matching-receipts");
        var received = model.Derived(lines).Using(matching).Impact(p => p.MembershipAdded(DependencySeverity.Dirty).MembershipRemoved(DependencySeverity.Invalid).ItemChanged(DependencySeverity.Invalid)).Incrementally().Compute((_, rows) => rows.Sum(x => x.Quantity)).Named("received-quantity");
        var available = model.Derived(lines).Using(received).Impact(p => p.SourceChanged(DependencySeverity.Dirty).SourceMemberChanged(x => x.OrderedQuantity, (oldValue, newValue) => newValue < oldValue ? DependencySeverity.Invalid : DependencySeverity.Dirty)).Compute((l, value) => l.OrderedQuantity - value).Named("available-quantity");
        var rate = model.Derived(lines).Impact(p => p.SourceChanged(DependencySeverity.Invalid)).Compute(x => x.PriceRate).Named("unit-rate");
        var valid = model.Derived(links).Using(x => x.PurchaseOrderLine, available, rate).Impact(p => p.SourceChanged(DependencySeverity.Invalid)).Compute((link, capacity, unitRate) => link.ReservedQuantity <= capacity && link.CapturedRate == unitRate).Named("link-validity");
        var invariant = model.Invariant(links).Using(valid).Must((_, value) => value).Named("link-validity-invariant").ScheduleRepairWith(x => repairs.Add(x.Id));
        var line = new PurchaseOrderLine { OrderNumber = "PO-100", ItemNumber = 1, OrderedQuantity = 10, PriceRate = 25 };
        var receipt = new GoodsReceipt { OrderNumber = "PO-100", ItemNumber = 1, Quantity = 5 };
        var link = new PurchaseOrderInvoiceLine { InvoiceLineId = Guid.NewGuid(), PurchaseOrderLineId = line.Id, PurchaseOrderLine = line, ReservedQuantity = 4, CapturedRate = 25 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(receipts, [receipt]); seed.Add(links, [link]); });
        Require(runtime.Version == 0, "bootstrap should not create a mutation version"); _ = runtime.Get(valid, link); _ = runtime.Evaluate(invariant, link);
        line.OrderedQuantity = 12; runtime.Apply(Change.Property(lines, line, x => x.OrderedQuantity, 10m, 12m));
        Require(runtime.GetState(available, line) == DerivedValueState.Dirty, "increase should be dirty"); _ = runtime.Get(valid, link); _ = runtime.Evaluate(invariant, link);
        line.OrderedQuantity = 8; var decrease = runtime.ApplyDetailed(MutationSet.Create(Change.Property(lines, line, x => x.OrderedQuantity, 12m, 8m)), RuntimeImpactDetailLevel.Causal);
        Require(runtime.GetState(available, line) == DerivedValueState.Invalid, "decrease should be invalid"); Require(decrease.Result.RepairRequests.Count == 1, "decrease should request repair"); decrease.Dispatch.Invoke();
        _ = runtime.Get(valid, link); _ = runtime.Evaluate(invariant, link); repairs.Clear(); receipt.Cancelled = true;
        runtime.Apply(Change.Property(receipts, receipt, x => x.Cancelled, false, true)); Require(runtime.GetState(received, line) == DerivedValueState.Invalid, "cancellation should invalidate quantity"); Require(repairs.SequenceEqual([link.Id]), "cancellation should schedule repair");
        Console.WriteLine("Legacy procurement dependency scenarios passed.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
