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
}

internal sealed class CodeHolder
{
    public Guid Id { get; init; }
    public string Code { get; set; } = "";
    public bool Enabled { get; set; }
}
