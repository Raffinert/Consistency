using Raffinert.Consistency;

namespace Raffinert.Consistency.FinalApiDogfood;

internal sealed record DogfoodDefinitions(
    FinalCompiledModel Compiled,
    FinalSet<InvoiceLine> InvoiceLines,
    FinalSet<PurchaseOrderLine> PurchaseOrderLines,
    FinalSet<PurchaseOrderInvoiceLine> Links,
    FinalSet<GoodsReceipt> GoodsReceipts,
    FinalSet<Allocation> Allocations,
    FinalRelation<PurchaseOrderLine, GoodsReceipt> MatchingReceipts,
    FinalValue<PurchaseOrderInvoiceLine, decimal?> PriceRate,
    FinalValue<PurchaseOrderInvoiceLine, decimal?> UnitRate,
    FinalValue<PurchaseOrderLine, decimal> ReceivedQuantity,
    FinalValue<PurchaseOrderLine, int> ReceiptCount,
    FinalValue<PurchaseOrderLine, long> ReceiptLongCount,
    FinalValue<PurchaseOrderLine, bool> HasReceipts,
    FinalValue<PurchaseOrderLine, decimal> RemainingQuantity,
    FinalValue<PurchaseOrderInvoiceLine, decimal> ActualQuantity,
    FinalValue<PurchaseOrderInvoiceLine, bool> LinkValidity,
    FinalInvariant<PurchaseOrderInvoiceLine> LinkInvariant,
    List<int> Repairs)
{
    public static DogfoodDefinitions Create()
    {
        var repairs = new List<int>();
        var model = new FinalModel();
        var invoiceLines = model.Objects<InvoiceLine>().Named("invoice-lines").Key(x => x.Id);
        var poLines = model.Objects<PurchaseOrderLine>().Named("purchase-order-lines").Key(x => x.Id);
        var links = model.Objects<PurchaseOrderInvoiceLine>().Named("po-invoice-links").Key(x => x.Id);
        var receipts = model.Objects<GoodsReceipt>().Named("goods-receipts").Key(x => x.Id);
        var allocations = model.Objects<Allocation>().Named("allocations").Key(x => x.Id);

        var priceRate = model
            .Derived(links)
            .DependsOn(
                x => x.InvoiceLine.Price,
                x => x.PurchaseOrderLine.Price)
            .Impact(p => p.SourceChanged(DependencySeverity.Invalid))
            .Select(PurchaseOrderInvoiceLine.CalculatePriceRate)
            .MaterializeTo(x => x.PriceRate)
            .Named("price-rate");

        var unitRate = model
            .Derived(links)
            .From(priceRate)
            .DependsOn(
                x => x.InvoiceLine.Quantity,
                x => x.PurchaseOrderLine.OrderedQuantity)
            .Impact(p => p.SourceChanged(DependencySeverity.Invalid))
            .Select((link, rate) => PurchaseOrderInvoiceLine.CalculateUnitRate(link, rate))
            .MaterializeTo(x => x.UnitRate)
            .Named("unit-rate");

        var matchingReceipts = model
            .Relation(poLines, receipts)
            .Where((line, receipt) =>
                line.Id == receipt.PurchaseOrderLineId && !receipt.Cancelled)
            .Named("matching-receipts");

        static void ReceiptImpact(DerivedImpactPolicyBuilder<PurchaseOrderLine> p) => p
            .MembershipAdded(DependencySeverity.Dirty)
            .MembershipRemoved(DependencySeverity.Invalid)
            .ItemChanged(DependencySeverity.Invalid);

        var receivedQuantity = model.Derived(poLines).From(matchingReceipts)
            .Impact(ReceiptImpact)
            .Sum(x => x.Quantity)
            .Named("received-quantity");
        var receiptCount = model.Derived(poLines).From(matchingReceipts)
            .Impact(ReceiptImpact)
            .Count()
            .Named("receipt-count");
        var receiptLongCount = model.Derived(poLines).From(matchingReceipts)
            .Impact(ReceiptImpact)
            .LongCount()
            .Named("receipt-long-count");
        var hasReceipts = model.Derived(poLines).From(matchingReceipts)
            .Impact(ReceiptImpact)
            .Any()
            .Named("has-receipts");

        var orderedQuantity = model.Derived(poLines)
            .DependsOn(x => x.OrderedQuantity)
            .Impact(p => p.SourceMemberChanged(
                x => x.OrderedQuantity,
                (oldValue, newValue) => newValue < oldValue
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty))
            .Select(x => x.OrderedQuantity)
            .Named("ordered-quantity");
        var remainingQuantity = model.Derived(poLines)
            .From(orderedQuantity, receivedQuantity)
            .Select((_, ordered, received) => ordered - received)
            .Named("remaining-quantity");

        var actualQuantity = model.Derived(links)
            .From(x => x.PurchaseOrderLine, remainingQuantity)
            .From(unitRate)
            .Impact(p => p.SourceChanged(DependencySeverity.Invalid))
            .Select((link, remaining, rate) =>
                rate == null
                    ? 0m
                    : link.LinkedQuantity < (remaining > 0m ? remaining : 0m)
                        ? link.LinkedQuantity
                        : remaining > 0m ? remaining : 0m)
            .Named("actual-quantity");
        var linkValidity = model.Derived(links)
            .From(unitRate)
            .Select((_, rate) => rate != null && rate > 0m)
            .Named("link-validity");
        var linkInvariant = model.Invariant(links)
            .From(linkValidity)
            .Must((_, valid) => valid)
            .Named("link-validity-invariant")
            .RepairWhenViolated();

        return new DogfoodDefinitions(
            model.Build(), invoiceLines, poLines, links, receipts, allocations, matchingReceipts,
            priceRate, unitRate, receivedQuantity, receiptCount, receiptLongCount, hasReceipts,
            remainingQuantity, actualQuantity, linkValidity, linkInvariant, repairs);
    }
}
