namespace Raffinert.Relations.Tests;

internal sealed class InvoiceLine
{
    public Guid Id { get; init; }
    public string PurchaseOrderNumber { get; set; } = "";
    public string ItemNumber { get; set; } = "";
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
