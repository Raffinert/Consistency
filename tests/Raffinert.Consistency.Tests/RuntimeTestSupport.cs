namespace Raffinert.Consistency.Tests;

public sealed partial class RuntimeTests
{
    private static ConsistencyModelBuilder CreateLineModel(
        out ObjectSet<RequestLine> invoices,
        out ObjectSet<OrderLine> poLines,
        out Relation<RequestLine, OrderLine> relation)
    {
        var model = new ConsistencyModelBuilder();
        invoices = model.Objects<RequestLine>().Key(x => x.Id);
        poLines = model.Objects<OrderLine>().Key(x => x.Id);
        relation = model.Relation(invoices, poLines).Where((invoice, line) =>
            invoice.OrderNumber == line.OrderNumber &&
            invoice.ItemNumber == line.ItemNumber &&
            line.Enabled);
        return model;
    }

    private static RequestLine Invoice(string order, string item) => new()
    {
        Id = Guid.NewGuid(),
        OrderNumber = order,
        ItemNumber = item
    };

    private static OrderLine Line(string order, string item, bool enabled = true) => new()
    {
        Id = Guid.NewGuid(),
        OrderNumber = order,
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
