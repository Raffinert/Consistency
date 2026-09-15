namespace Raffinert.Consistency.OrderFulfillmentSample.Domain;

internal enum FulfillmentMode { Unknown, Disabled, Enabled }
internal enum RuleEvaluation { Valid, Violation, Unknown }

internal sealed class RequestLine
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool IsDeleted { get; set; }
}

internal sealed class OrderLine
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string OrderNumber { get; init; } = "";
    public int ItemNumber { get; init; }
    public decimal OrderedQuantity { get; set; }
    public decimal UnitRate { get; set; }
    public bool? IsServiceLine { get; set; }
}

internal sealed class Allocation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RequestLineId { get; init; }
    public Guid OrderLineId { get; init; }
    public required OrderLine OrderLine { get; init; }
    public decimal AllocatedQuantity { get; set; }
    public bool IsDeleted { get; set; }
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Enabled;
    public decimal ReservedQuantity { get; init; }
    public decimal CapturedRate { get; init; }
}

internal sealed class Fulfillment
{
    private static long _nextId;
    public long Id { get; init; } = Interlocked.Increment(ref _nextId);
    public string OrderNumber { get; init; } = "";
    public int ItemNumber { get; init; }
    public decimal Quantity { get; set; }
    public bool Cancelled { get; set; }
}

internal sealed class AllocationFulfillment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AllocationId { get; init; }
    public required Allocation Allocation { get; init; }
    public long FulfillmentId { get; set; }
    public decimal Quantity { get; set; }
    public bool IsDeleted { get; set; }
}

internal sealed class FulfillmentBalance
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid OrderLineId { get; init; }
    public required OrderLine OrderLine { get; init; }
    public long FulfillmentId { get; init; }
    public decimal TotalQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal AllocatedQuantity { get; set; }
    public decimal ProcessedQuantity { get; set; }
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Enabled;
}
