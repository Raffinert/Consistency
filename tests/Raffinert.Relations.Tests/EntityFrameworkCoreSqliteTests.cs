using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Relations.EntityFrameworkCore;

namespace Raffinert.Relations.Tests;

public sealed class EntityFrameworkCoreSqliteTests
{
    [Fact]
    public void Database_failure_discards_prepared_runtime_mutation()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new RelationModelBuilder();
        var objects = model.Objects<UniqueEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var first = new UniqueEntity { Id = Guid.NewGuid(), Code = "duplicate" };
        var second = new UniqueEntity { Id = Guid.NewGuid(), Code = "duplicate" };
        context.AddRange(first, second);

        Assert.Throws<DbUpdateException>(() => context.SaveChangesAndApply(runtime, mappings));

        Assert.Equal(0, runtime.Version);
        Assert.False(runtime.Remove(objects, first));
        Assert.False(runtime.Remove(objects, second));
    }

    [Fact]
    public void Rolled_back_explicit_transaction_discards_prepared_runtime_mutation()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new RelationModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "rollback" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            transaction.Rollback();
        }

        Assert.True(entity.Id > 0);
        Assert.Equal(0, runtime.Version);
        Assert.False(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Explicit_transaction_applies_prepared_runtime_state_after_commit()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new RelationModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "commit" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        using (var transaction = context.Database.BeginTransaction())
        {
            context.SaveChanges();
            transaction.Commit();
        }
        unit.Commit(runtime);
        unit.Dispatch(runtime);

        Assert.True(entity.Id > 0);
        Assert.True(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Save_without_accept_all_changes_can_commit_runtime_then_accept_tracking_state()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new RelationModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "deferred-accept" };
        context.Add(entity);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);

        context.SaveChanges(acceptAllChangesOnSuccess: false);
        unit.Commit(runtime);
        unit.Dispatch(runtime);

        Assert.Equal(EntityState.Added, context.Entry(entity).State);
        context.ChangeTracker.AcceptAllChanges();
        Assert.Equal(EntityState.Unchanged, context.Entry(entity).State);
        Assert.True(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Concurrency_failure_does_not_commit_prepared_runtime_changes()
    {
        using var database = new SqliteFixture();
        var id = Guid.NewGuid();
        using (var seed = database.CreateContext())
        {
            seed.ConcurrencyEntities.Add(new ConcurrencyEntity { Id = id, Code = "A", Version = 0 });
            seed.SaveChanges();
        }
        using var firstContext = database.CreateContext();
        using var secondContext = database.CreateContext();
        var first = firstContext.ConcurrencyEntities.Single(entity => entity.Id == id);
        var second = secondContext.ConcurrencyEntities.Single(entity => entity.Id == id);
        var model = new RelationModelBuilder();
        var objects = model.Objects<ConcurrencyEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        runtime.Add(objects, first);
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        second.Code = "B";
        second.Version = 1;
        secondContext.SaveChanges();
        first.Code = "C";
        first.Version = 1;

        Assert.Throws<DbUpdateConcurrencyException>(() =>
            firstContext.SaveChangesAndApply(runtime, mappings));

        Assert.Equal(1, runtime.Version);
        Assert.True(runtime.Remove(objects, first));
    }

    [Fact]
    public void Store_generated_key_is_registered_only_after_value_is_available()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new RelationModelBuilder();
        var objects = model.Objects<GeneratedEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new GeneratedEntity { Code = "generated" };
        context.Add(entity);
        Assert.Equal(0, entity.Id);

        context.SaveChangesAndApply(runtime, mappings);

        Assert.True(entity.Id > 0);
        Assert.True(runtime.Remove(objects, entity));
    }

    [Fact]
    public void Cascade_delete_removes_tracked_principal_and_dependents()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var parent = new CascadeParent { Id = Guid.NewGuid() };
        var child = new CascadeChild { Id = Guid.NewGuid(), Parent = parent };
        context.AddRange(parent, child);
        context.SaveChanges();
        var model = new RelationModelBuilder();
        var parents = model.Objects<CascadeParent>().Key(entity => entity.Id);
        var children = model.Objects<CascadeChild>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        runtime.Add(parents, parent);
        runtime.Add(children, child);
        var mappings = new RelationUnitOfWorkMappings().Map(parents).Map(children);

        context.Remove(parent);
        context.SaveChangesAndApply(runtime, mappings);

        Assert.False(runtime.Remove(parents, parent));
        Assert.False(runtime.Remove(children, child));
    }

    [Fact]
    public void Owned_value_change_updates_nested_relation_dependency()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var owner = new OwnedOwner { Id = Guid.NewGuid(), Settings = new OwnedSettings { Code = "A" } };
        context.Add(owner);
        context.SaveChanges();
        var model = new RelationModelBuilder();
        var owners = model.Objects<OwnedOwner>().Key(entity => entity.Id);
        var labels = model.Objects<LabelEntity>().Key(entity => entity.Id);
        var relation = model.Relation(owners, labels).Where((left, right) => left.Settings.Code == right.Code);
        var runtime = model.Build().CreateRuntime();
        var label = new LabelEntity { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(owners, owner);
        runtime.Add(labels, label);
        var mappings = new RelationUnitOfWorkMappings().Map(owners);
        owner.Settings.Code = "B";

        context.SaveChangesAndApply(runtime, mappings);

        Assert.Equal([label], runtime.Related(relation, owner));
    }

    [Fact]
    public void Many_to_many_skip_navigation_change_emits_collection_reset()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var group = new TagGroup { Id = Guid.NewGuid() };
        var tag = new TagEntity { Id = Guid.NewGuid(), Name = "B" };
        context.AddRange(group, tag);
        context.SaveChanges();
        var model = new RelationModelBuilder();
        var groups = model.Objects<TagGroup>().Key(entity => entity.Id);
        var labels = model.Objects<LabelEntity>().Key(entity => entity.Id);
        var relation = model.Relation(groups, labels).Where((left, right) =>
            left.Tags.Any(value => value.Name == right.Code));
        var runtime = model.Build().CreateRuntime();
        var label = new LabelEntity { Id = Guid.NewGuid(), Code = "B" };
        runtime.Add(groups, group);
        runtime.Add(labels, label);
        var mappings = new RelationUnitOfWorkMappings().Map(groups);
        group.Tags.Add(tag);

        context.SaveChangesAndApply(runtime, mappings);

        Assert.Equal([label], runtime.Related(relation, group));
    }

    [Fact]
    public void Multiple_save_changes_calls_advance_one_runtime_consistently()
    {
        using var database = new SqliteFixture();
        using var context = database.CreateContext();
        var model = new RelationModelBuilder();
        var objects = model.Objects<UniqueEntity>().Key(entity => entity.Id);
        var runtime = model.Build().CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings().Map(objects);
        var entity = new UniqueEntity { Id = Guid.NewGuid(), Code = "first" };
        context.Add(entity);
        context.SaveChangesAndApply(runtime, mappings);
        entity.Code = "second";
        context.SaveChangesAndApply(runtime, mappings);

        Assert.Equal(2, runtime.Version);
        Assert.True(runtime.Remove(objects, entity));
    }

    private sealed class SqliteFixture : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        public SqliteFixture()
        {
            _connection.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }

        public SqliteContext CreateContext() => new(_connection);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class SqliteContext(SqliteConnection connection) : DbContext
    {
        public DbSet<ConcurrencyEntity> ConcurrencyEntities => Set<ConcurrencyEntity>();

        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(connection);

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<UniqueEntity>().HasIndex(entity => entity.Code).IsUnique();
            model.Entity<GeneratedEntity>();
            model.Entity<ConcurrencyEntity>().Property(entity => entity.Version).IsConcurrencyToken();
            model.Entity<CascadeChild>().HasOne(entity => entity.Parent).WithMany(entity => entity.Children)
                .HasForeignKey(entity => entity.ParentId).OnDelete(DeleteBehavior.Cascade);
            model.Entity<OwnedOwner>().OwnsOne(entity => entity.Settings);
            model.Entity<TagGroup>().HasMany(entity => entity.Tags).WithMany(entity => entity.Groups);
        }
    }

    private sealed class UniqueEntity
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
    }

    private sealed class GeneratedEntity
    {
        public int Id { get; set; }
        public string Code { get; set; } = "";
    }

    private sealed class ConcurrencyEntity
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
        public int Version { get; set; }
    }

    private sealed class CascadeParent
    {
        public Guid Id { get; init; }
        public List<CascadeChild> Children { get; } = [];
    }

    private sealed class CascadeChild
    {
        public Guid Id { get; init; }
        public Guid ParentId { get; set; }
        public CascadeParent Parent { get; set; } = null!;
    }

    private sealed class OwnedOwner
    {
        public Guid Id { get; init; }
        public OwnedSettings Settings { get; set; } = new();
    }

    private sealed class OwnedSettings
    {
        public string Code { get; set; } = "";
    }

    private sealed class LabelEntity
    {
        public Guid Id { get; init; }
        public string Code { get; set; } = "";
    }

    private sealed class TagGroup
    {
        public Guid Id { get; init; }
        public List<TagEntity> Tags { get; } = [];
    }

    private sealed class TagEntity
    {
        public Guid Id { get; init; }
        public string Name { get; set; } = "";
        public List<TagGroup> Groups { get; } = [];
    }
}
