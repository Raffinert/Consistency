using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

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
        var model = new ConsistencyModelBuilder();
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
        var model = new ConsistencyModelBuilder();
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
        var model = new ConsistencyModelBuilder();
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
        var model = new ConsistencyModelBuilder();
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

    [Fact]
    public void Save_helper_prepares_runtime_before_invoking_database_save()
    {
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<CodeHolder>().Key(value => value.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        using var context = new SaveProbeDbContext();
        var absentRemoval = new CodeHolder { Id = Guid.NewGuid(), Code = "REMOVE" };
        context.Attach(absentRemoval);
        context.Remove(absentRemoval);

        Assert.Throws<InvalidOperationException>(() =>
            context.SaveChangesAndApply(runtime, mappings));

        Assert.False(context.SaveWasCalled);
    }

    [Fact]
    public void Manual_unit_of_work_can_commit_causal_details_after_database_success()
    {
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<CodeHolder>().Named("holders").Key(value => value.Id);
        var code = model.Derived(objects).Compute(value => value.Code).Named("code");
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        using var context = new TestDbContext();
        var entity = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        context.Add(entity);
        context.SaveChanges();
        runtime.Add(objects, entity);
        Assert.Equal("A", runtime.Get(code, entity));
        entity.Code = "B";
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);
        context.SaveChanges();
        var result = unit.CommitDetailed(runtime, RuntimeImpactDetailLevel.Causal);

        Assert.NotNull(result);
        Assert.Equal(RuntimeImpactDetailLevel.Causal, result.DetailLevel);
        Assert.Single(result.MutationOrigins);
        unit.Dispatch(runtime);
    }

    [Fact]
    public void Prepared_unit_of_work_can_be_previewed_repeatedly_before_commit()
    {
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<CodeHolder>().Named("holders").Key(value => value.Id);
        model.Derived(objects).Compute(value => value.Code).Named("code");
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        using var context = new TestDbContext();
        var entity = new CodeHolder { Id = Guid.NewGuid(), Code = "A" };
        context.Attach(entity);
        runtime.Add(objects, entity);
        entity.Code = "B";
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);
        var version = runtime.Version;

        var first = unit.PreviewDetailed(runtime, RuntimeImpactDetailLevel.Causal);
        var second = unit.PreviewDetailed(runtime, RuntimeImpactDetailLevel.Causal);

        Assert.NotNull(first);
        Assert.Equal(first.ChangeImpact, second!.ChangeImpact);
        Assert.Equal(version, runtime.Version);
        Assert.NotNull(unit.CommitDetailed(runtime));
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

    private sealed class SaveProbeDbContext : TestDbContext
    {
        public bool SaveWasCalled { get; private set; }

        public override int SaveChanges()
        {
            SaveWasCalled = true;
            return base.SaveChanges();
        }
    }
}
