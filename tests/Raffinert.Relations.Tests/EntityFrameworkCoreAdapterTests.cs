using Microsoft.EntityFrameworkCore;
using Raffinert.Relations.EntityFrameworkCore;

namespace Raffinert.Relations.Tests;

public sealed class EntityFrameworkCoreAdapterTests
{
    [Fact]
    public void Converts_modified_scalar_properties_to_a_change_set()
    {
        using var context = new TestDbContext();
        var entity = new CodeHolder { Id = Guid.NewGuid(), Code = "OLD" };
        context.Attach(entity);

        entity.Code = "NEW";
        var changeSet = ChangeTrackerAdapter.CreateChangeSet(context.ChangeTracker);

        var change = Assert.Single(Assert.IsType<ChangeSet>(changeSet).Changes);
        Assert.Same(entity, change.Instance);
        Assert.Equal(nameof(CodeHolder.Code), change.Member.Name);
        Assert.Equal("OLD", change.OldValue);
        Assert.Equal("NEW", change.NewValue);
    }

    private sealed class TestDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CodeHolder>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseInMemoryDatabase($"relations-{Guid.NewGuid()}");
        }
    }
}
