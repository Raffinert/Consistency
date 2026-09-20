using Microsoft.EntityFrameworkCore;

namespace Raffinert.Consistency.AllocationDogfood;

internal sealed class AllocationDbContext(DbContextOptions<AllocationDbContext> options)
    : DbContext(options)
{
    public DbSet<Demand> Demands => Set<Demand>();
    public DbSet<Supply> Supplies => Set<Supply>();
    public DbSet<Allocation> Allocations => Set<Allocation>();
    public DbSet<Fulfillment> Fulfillments => Set<Fulfillment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Demand>().Property(value => value.Id).ValueGeneratedNever();
        modelBuilder.Entity<Supply>(entity =>
        {
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_Supply_FailureMarker_NonNegative", "FailureMarker >= 0"));
        });
        modelBuilder.Entity<Allocation>(entity =>
        {
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.HasOne(value => value.Demand).WithMany()
                .HasForeignKey(value => value.DemandId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(value => value.Supply).WithMany()
                .HasForeignKey(value => value.SupplyId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Fulfillment>(entity =>
        {
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.HasOne(value => value.Supply).WithMany()
                .HasForeignKey(value => value.SupplyId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
