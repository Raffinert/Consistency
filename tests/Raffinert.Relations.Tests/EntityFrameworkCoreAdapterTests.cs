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

    [Fact]
    public void Save_changes_and_apply_maps_added_modified_and_deleted_entities()
    {
        var model = new RelationModelBuilder();
        var objects = model.Objects<CodeHolder>().Key(value => value.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        using var context = new TestDbContext();
        var entity = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };

        context.Add(entity);
        context.SaveChangesAndApply(runtime, mappings);
        Assert.True(runtime.Remove(objects, entity));
        runtime.Add(objects, entity);

        entity.Code = "B";
        context.SaveChangesAndApply(runtime, mappings);

        context.Remove(entity);
        context.SaveChangesAndApply(runtime, mappings);
        Assert.False(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Failed_database_save_does_not_advance_runtime_state()
    {
        var model = new RelationModelBuilder();
        var objects = model.Objects<CodeHolder>().Key(value => value.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        using var context = new FailingDbContext();
        var entity = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        context.Add(entity);

        Assert.Throws<InvalidOperationException>(() =>
            context.SaveChangesAndApply(runtime, mappings));

        Assert.False(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Multiple_sets_for_one_clr_type_require_disambiguating_selectors()
    {
        var model = new RelationModelBuilder();
        var first = model.Objects<CodeHolder>().Key(value => value.Id);
        var second = model.Objects<CodeHolder>().Key(value => value.Id);
        model.Build();
        var mappings = new RelationUnitOfWorkMappings().Map(first).Map(second);
        using var context = new TestDbContext();
        context.Add(new CodeHolder { Id = Guid.NewGuid() });

        Assert.Throws<InvalidOperationException>(() =>
            ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings));
    }

    [Fact]
    public void Captured_unit_of_work_is_validated_before_any_runtime_mutation_is_applied()
    {
        var model = new RelationModelBuilder();
        var objects = model.Objects<CodeHolder>().Key(value => value.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        using var context = new TestDbContext();
        var added = new CodeHolder { Id = Guid.NewGuid(), Code = "ADD" };
        var absentRemoval = new CodeHolder { Id = Guid.NewGuid(), Code = "REMOVE" };
        context.Add(added);
        context.Attach(absentRemoval);
        context.Remove(absentRemoval);
        var unitOfWork = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);

        Assert.Throws<InvalidOperationException>(() => unitOfWork.Apply(runtime));

        Assert.False(runtime.Remove(objects, added));
    }

    private class TestDbContext : DbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<CodeHolder>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseInMemoryDatabase($"relations-{Guid.NewGuid()}");
        }
    }

    private sealed class FailingDbContext : TestDbContext
    {
        public override int SaveChanges() => throw new InvalidOperationException("Database failure.");
    }
}
