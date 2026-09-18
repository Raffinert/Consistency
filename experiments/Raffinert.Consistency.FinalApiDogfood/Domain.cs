using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.FinalApiDogfood;

internal sealed class InvoiceLine
{
    public int Id { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
}

internal sealed class PurchaseOrderLine
{
    public int Id { get; set; }
    public string OrderNumber { get; set; } = "PO-1";
    public decimal Price { get; set; }
    public decimal OrderedQuantity { get; set; }
}

internal sealed class PurchaseOrderInvoiceLine
{
    private decimal? _priceRate;
    private decimal? _unitRate;

    public int Id { get; set; }
    public int InvoiceLineId { get; set; }
    public InvoiceLine InvoiceLine { get; set; } = null!;
    public int PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine PurchaseOrderLine { get; set; } = null!;
    public decimal LinkedQuantity { get; set; }
    public decimal? PriceRate
    {
        get => _priceRate;
        set
        {
            _priceRate = value;
            PriceRateAssignments++;
        }
    }
    public decimal? UnitRate
    {
        get => _unitRate;
        set
        {
            _unitRate = value;
            UnitRateAssignments++;
        }
    }
    public int PriceRateAssignments { get; private set; }
    public int UnitRateAssignments { get; private set; }
    public int PriceRateComputations { get; private set; }
    public int UnitRateComputations { get; private set; }

    public static decimal? CalculatePriceRate(PurchaseOrderInvoiceLine link)
    {
        link.PriceRateComputations++;
        return link.PurchaseOrderLine.Price == 0m
            ? null
            : link.InvoiceLine.Price / link.PurchaseOrderLine.Price;
    }

    public static decimal? CalculateUnitRate(PurchaseOrderInvoiceLine link, decimal? rate)
    {
        link.UnitRateComputations++;
        if (rate is null || link.PurchaseOrderLine.OrderedQuantity == 0m)
            return null;
        return rate * link.InvoiceLine.Quantity / link.PurchaseOrderLine.OrderedQuantity;
    }
}

internal sealed class GoodsReceipt
{
    public int Id { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public decimal Quantity { get; set; }
    public bool Cancelled { get; set; }
}

internal sealed class Allocation
{
    public int Id { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine PurchaseOrderLine { get; set; } = null!;
    public decimal RequestedQuantity { get; set; }
}

internal sealed class DogfoodDbContext(DbContextOptions<DogfoodDbContext> options) : DbContext(options)
{
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<PurchaseOrderInvoiceLine> Links => Set<PurchaseOrderInvoiceLine>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<Allocation> Allocations => Set<Allocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InvoiceLine>().HasKey(x => x.Id);
        modelBuilder.Entity<PurchaseOrderLine>().HasKey(x => x.Id);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().HasKey(x => x.Id);
        modelBuilder.Entity<GoodsReceipt>().HasKey(x => x.Id);
        modelBuilder.Entity<Allocation>().HasKey(x => x.Id);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>()
            .HasOne(x => x.InvoiceLine).WithMany().HasForeignKey(x => x.InvoiceLineId);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>()
            .HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId);
        modelBuilder.Entity<Allocation>()
            .HasOne(x => x.PurchaseOrderLine).WithMany().HasForeignKey(x => x.PurchaseOrderLineId);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.PriceRateAssignments);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.UnitRateAssignments);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.PriceRateComputations);
        modelBuilder.Entity<PurchaseOrderInvoiceLine>().Ignore(x => x.UnitRateComputations);
    }
}
