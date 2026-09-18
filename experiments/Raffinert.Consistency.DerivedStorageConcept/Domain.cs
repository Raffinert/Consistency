using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.DerivedStorageConcept;

internal sealed class PurchaseOrderInvoiceLine
{
    private decimal _priceRate;
    private decimal _unitRate;
    private decimal _alternateUnitRate;
    private decimal? _nullablePriceRate;
    private RateToken _rateToken = new(6m);

    public int Id { get; set; }
    public int InvoiceLineId { get; set; }
    public required InvoiceLine InvoiceLine { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public required PurchaseOrderLine PurchaseOrderLine { get; set; }
    public bool ThrowPriceRateComputation { get; set; }
    public bool ThrowOnNextPriceRateSet { get; set; }
    public bool NormalizePriceRate { get; set; }
    public int PriceRateAssignments { get; private set; }
    public int UnitRateAssignments { get; private set; }

    public decimal PriceRate
    {
        get => _priceRate;
        set
        {
            if (ThrowOnNextPriceRateSet)
            {
                ThrowOnNextPriceRateSet = false;
                throw new InvalidOperationException("Injected PriceRate setter failure.");
            }
            _priceRate = NormalizePriceRate ? decimal.Round(value, 2) : value;
            PriceRateAssignments++;
        }
    }

    public decimal UnitRate
    {
        get => _unitRate;
        set
        {
            _unitRate = value;
            UnitRateAssignments++;
        }
    }

    public decimal AlternateUnitRate
    {
        get => _alternateUnitRate;
        set => _alternateUnitRate = value;
    }

    public decimal? NullablePriceRate
    {
        get => _nullablePriceRate;
        set => _nullablePriceRate = value;
    }

    public RateToken RateToken
    {
        get => _rateToken;
        set
        {
            _rateToken = value;
            RateTokenAssignments++;
        }
    }

    public int RateTokenAssignments { get; private set; }

    public static decimal CalculatePriceRate(PurchaseOrderInvoiceLine link)
    {
        if (link.ThrowPriceRateComputation)
            throw new InvalidOperationException("Injected PriceRate computation failure.");
        return link.InvoiceLine.Price / link.PurchaseOrderLine.Price;
    }
}

internal sealed class RateToken(decimal value) : IEquatable<RateToken>
{
    public decimal Value { get; } = value;

    public bool Equals(RateToken? other) =>
        other is not null && decimal.Round(Value, 2) == decimal.Round(other.Value, 2);

    public override bool Equals(object? obj) => obj is RateToken other && Equals(other);
    public override int GetHashCode() => decimal.Round(Value, 2).GetHashCode();
}

internal sealed class InvoiceLine
{
    public int Id { get; set; }
    public decimal Price { get; set; }
}

internal sealed class PurchaseOrderLine
{
    public int Id { get; set; }
    public decimal Price { get; set; }
}

internal sealed class OrderLine
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = "PO-1";
    public int ItemNumber { get; set; } = 1;
    public decimal OrderedQuantity { get; set; }
    public decimal FulfilledQuantity { get; set; }
    public decimal RemainingQuantity { get; set; }
}

internal sealed class Fulfillment
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = "PO-1";
    public int ItemNumber { get; set; } = 1;
    public decimal Quantity { get; set; }
    public bool Cancelled { get; set; }
}

internal sealed class Allocation
{
    public int Id { get; set; }
    public int OrderLineId { get; set; }
    public required OrderLine OrderLine { get; set; }
    public decimal ReservedQuantity { get; set; }
    public bool IsValid { get; set; }
}

internal sealed class StorageDbContext(DbContextOptions<StorageDbContext> options) : DbContext(options)
{
    public DbSet<PurchaseOrderInvoiceLine> Links => Set<PurchaseOrderInvoiceLine>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().HasKey(x => x.Id);
        modelBuilder.Entity<InvoiceLine>().HasKey(x => x.Id);
        modelBuilder.Entity<PurchaseOrderLine>().HasKey(x => x.Id);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>()
            .HasOne(x => x.InvoiceLine).WithMany().HasForeignKey(x => x.InvoiceLineId);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>()
            .HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.ThrowPriceRateComputation);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.ThrowOnNextPriceRateSet);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.NormalizePriceRate);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.PriceRateAssignments);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.UnitRateAssignments);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.RateToken);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.RateTokenAssignments);
    }
}
