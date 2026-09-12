namespace Raffinert.Relations.Tests;

internal sealed class InvoiceLine
{
    public Guid Id { get; init; }
    public string PurchaseOrderNumber { get; set; } = "";
    public string ItemNumber { get; set; } = "";
    public object? Tag { get; set; }
}

internal sealed class PurchaseOrderLine
{
    public Guid Id { get; init; }
    public string PurchaseOrderNumber { get; set; } = "";
    public string ItemNumber { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public PurchaseOrder? PurchaseOrder { get; set; }
}

internal sealed class PurchaseOrder
{
    public Guid Id { get; init; }
    public string Number { get; set; } = "";
    public Supplier? Supplier { get; set; }
}

internal sealed class Supplier
{
    public Guid Id { get; init; }
    public Country? Country { get; set; }
}

internal sealed class Country
{
    public Guid Id { get; init; }
    public string Code { get; set; } = "";
}

internal sealed class CodeHolder
{
    public Guid Id { get; init; }
    public string Code { get; set; } = "";
    public bool Enabled { get; set; }
}

internal sealed class MutableKeyHolder
{
    public Guid Id { get; set; }
}

internal sealed class DateRangeHolder
{
    public DateTime Date { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public bool Enabled { get; set; }
}

internal sealed class DerivedSourceRecord
{
    public Guid Id { get; init; }
    public string Code { get; set; } = "";
    public decimal Adjustment { get; set; }
    public ReceiptPolicy? Policy { get; set; }
}

internal sealed class DerivedItemRecord
{
    public Guid Id { get; init; }
    public string Code { get; set; } = "";
    public decimal Quantity { get; set; }
    public bool Enabled { get; set; }
    public DerivedItemDetails? Details { get; set; }
}

internal sealed class DerivedItemDetails
{
    public string Code { get; set; } = "";
    public decimal Quantity { get; set; }
}

internal sealed class ReceiptPolicy
{
    public decimal Maximum { get; set; }
}

internal sealed class CollectionOrder
{
    public Guid Id { get; init; }
    public List<CollectionOrderLine> Lines { get; } = [];
}

internal sealed class CollectionOrderLine
{
    public string ItemNumber { get; set; } = "";
}

internal sealed class ReplaceableCollectionOrder
{
    public Guid Id { get; init; }
    public List<ValueEqualCollectionLine> Lines { get; set; } = [];
    public CollectionContainer Container { get; set; } = new();
}

internal sealed class CollectionContainer
{
    public List<ValueEqualCollectionLine> Lines { get; } = [];
}

internal sealed class ValueEqualCollectionLine
{
    public string ItemNumber { get; set; } = "";

    public override bool Equals(object? obj) =>
        obj is ValueEqualCollectionLine other && other.ItemNumber == ItemNumber;

    public override int GetHashCode() => ItemNumber.GetHashCode(StringComparison.Ordinal);
}
