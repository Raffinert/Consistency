namespace Raffinert.Consistency.Tests;

public sealed partial class RuntimeTests
{
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
