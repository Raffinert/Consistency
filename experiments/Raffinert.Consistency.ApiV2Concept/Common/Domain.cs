using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.ApiV2Concept.Common;

internal sealed class SimpleLine
{
    public int Id { get; set; }
    public decimal OrderedQuantity { get; set; }
    public decimal FulfilledQuantity { get; set; }
}

internal sealed class SourceItem
{
    public int Id { get; set; }
    public decimal? UnitValue { get; set; }
}

internal sealed class TargetItem
{
    public int Id { get; set; }
    public decimal? UnitValue { get; set; }
}

internal sealed class Association
{
    public int Id { get; set; }
    public int SourceItemId { get; set; }
    public SourceItem SourceItem { get; set; } = null!;
    public int TargetItemId { get; set; }
    public TargetItem TargetItem { get; set; } = null!;
    public decimal? UnitRate { get; set; }
}

internal sealed class OrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = "ORDER-100";
    public int ItemNumber { get; set; } = 1;
    public decimal OrderedQuantity { get; set; }
    public decimal UnitRate { get; set; }
}

internal sealed class Fulfillment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OrderNumber { get; set; } = "ORDER-100";
    public int ItemNumber { get; set; } = 1;
    public decimal Quantity { get; set; }
    public bool Cancelled { get; set; }
}

internal sealed class Allocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderLineId { get; set; }
    public OrderLine OrderLine { get; set; } = null!;
    public decimal ReservedQuantity { get; set; }
    public decimal CapturedRate { get; set; }
}

internal static class UnitRateCalculator
{
    public static decimal? Calculate(decimal? source, decimal? target)
    {
        if (source is null || target is null or 0m)
            return null;
        try
        {
            return decimal.Round(source.Value / target.Value, 6, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}

internal sealed class DogfoodDbContext(DbContextOptions<DogfoodDbContext> options) : DbContext(options)
{
    public DbSet<SimpleLine> SimpleLines => Set<SimpleLine>();
    public DbSet<SourceItem> SourceItems => Set<SourceItem>();
    public DbSet<TargetItem> TargetItems => Set<TargetItem>();
    public DbSet<Association> Associations => Set<Association>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Fulfillment> Fulfillments => Set<Fulfillment>();
    public DbSet<Allocation> Allocations => Set<Allocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SimpleLine>().HasKey(x => x.Id);
        modelBuilder.Entity<SourceItem>().HasKey(x => x.Id);
        modelBuilder.Entity<TargetItem>().HasKey(x => x.Id);
        modelBuilder.Entity<Association>().HasKey(x => x.Id);
        modelBuilder.Entity<OrderLine>().HasKey(x => x.Id);
        modelBuilder.Entity<Fulfillment>().HasKey(x => x.Id);
        modelBuilder.Entity<Allocation>().HasKey(x => x.Id);
        modelBuilder.Entity<Association>().HasOne(x => x.SourceItem).WithMany().HasForeignKey(x => x.SourceItemId);
        modelBuilder.Entity<Association>().HasOne(x => x.TargetItem).WithMany().HasForeignKey(x => x.TargetItemId);
        modelBuilder.Entity<Allocation>().HasOne(x => x.OrderLine).WithMany().HasForeignKey(x => x.OrderLineId);
    }
}
