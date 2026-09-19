namespace Raffinert.Consistency.AllocationDogfood;

public sealed class Demand
{
    public int Id { get; set; }
    public string ResourceCode { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal RequestedQuantity { get; set; }
}

public sealed class Supply
{
    public int Id { get; set; }
    public string ResourceCode { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Capacity { get; set; }
    public decimal FulfilledQuantity { get; set; }
    public decimal AllocatedQuantity { get; set; }
    public decimal RemainingCapacity { get; set; }
    public int FailureMarker { get; set; }

    public void ChangeCapacity(decimal newCapacity)
    {
        if (newCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(newCapacity));
        Capacity = newCapacity;
    }
}

public sealed class Allocation
{
    public int Id { get; set; }
    public int DemandId { get; set; }
    public Demand Demand { get; set; } = null!;
    public int SupplyId { get; set; }
    public Supply Supply { get; set; } = null!;
    public decimal Quantity { get; set; }
}

public sealed class Fulfillment
{
    public int Id { get; set; }
    public int SupplyId { get; set; }
    public Supply Supply { get; set; } = null!;
    public decimal Quantity { get; set; }
}
