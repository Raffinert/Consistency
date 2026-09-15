namespace Raffinert.Relations.PurchaseOrderSample.Domain;

internal enum GrnMode { Unknown, Disabled, Enabled }
internal enum RuleEvaluation { Valid, Violation, Unknown }

internal sealed class InvoiceLine { public Guid Id { get; init; } = Guid.NewGuid(); public bool IsDeleted { get; set; } }
internal sealed class PurchaseOrderLine
{
    public Guid Id { get; init; } = Guid.NewGuid(); public string OrderNumber { get; init; } = ""; public int ItemNumber { get; init; }
    public decimal OrderedQuantity { get; set; }
    public decimal PriceRate { get; set; }
    public bool? IsServiceItemLine { get; set; }
}
internal sealed class PurchaseOrderInvoiceLine
{
    public Guid Id { get; init; } = Guid.NewGuid(); public Guid InvoiceLineId { get; init; }
    public Guid PurchaseOrderLineId { get; init; }
    public required PurchaseOrderLine PurchaseOrderLine { get; init; }
    public decimal LinkedQuantity { get; set; }
    public bool IsDeleted { get; set; }
    public GrnMode GrnMode { get; set; } = GrnMode.Enabled; public decimal ReservedQuantity { get; init; }
    public decimal CapturedRate { get; init; }
}
internal sealed class GoodsReceipt
{
    private static long _nextId; public long Id { get; init; } = Interlocked.Increment(ref _nextId); public string OrderNumber { get; init; } = "";
    public int ItemNumber { get; init; }
    public decimal Quantity { get; set; }
    public bool Cancelled { get; set; }
}
internal sealed class LinkedGoodsReceipt
{
    public Guid Id { get; init; } = Guid.NewGuid(); public Guid PurchaseOrderInvoiceLineId { get; init; }
    public required PurchaseOrderInvoiceLine PurchaseOrderInvoiceLine { get; init; }
    public long GoodsReceiptId { get; set; }
    public decimal Quantity { get; set; }
    public bool IsDeleted { get; set; }
}
internal sealed class PurchaseOrderLineGoodsReceipt
{
    public Guid Id { get; init; } = Guid.NewGuid(); public Guid PurchaseOrderLineId { get; init; }
    public required PurchaseOrderLine PurchaseOrderLine { get; init; }
    public long GoodsReceiptId { get; init; }
    public decimal QuantityReceived { get; set; }
    public decimal QuantityAvailable { get; set; }
    public decimal QuantityMatched { get; set; }
    public decimal QuantitySentToErp { get; set; }
    public GrnMode GrnMode { get; set; } = GrnMode.Enabled;
}
