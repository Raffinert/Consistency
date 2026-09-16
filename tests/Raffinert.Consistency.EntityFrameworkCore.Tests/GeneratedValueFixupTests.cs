using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class GeneratedValueFixupTests
{
    [Fact]
    public void Existing_dependent_retargeted_to_generated_principal_uses_final_fk()
    {
        using var database = new FixupDatabase(); using var context = database.CreateContext();
        var original = new Parent(); var child = new Child { Id = 10, Parent = original, Quantity = 1 };
        context.AddRange(original, child); context.SaveChanges();
        var setup = CreateRelationModel(original, child);
        var replacement = new Parent(); context.Add(replacement); child.Parent = replacement;
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Parents, setup.Children));
        using var transaction = context.Database.BeginTransaction();
        context.SaveChanges();
        Assert.True(replacement.Id > 0);

        var plan = Assert.IsType<PreparedImpactPlan>(work.PrepareAndPlan());
        Assert.Contains(plan.InvariantEvaluations, evaluation =>
            evaluation.State == InvariantEvaluationState.Valid);
        context.SaveChanges(); transaction.Commit();
        _ = work.CommitAfterDatabaseCommit(); work.Dispatch();

        Assert.Equal(replacement.Id, child.ParentId);
        Assert.Empty(setup.Runtime.Related(setup.Relation, original));
        Assert.Equal([child], setup.Runtime.Related(setup.Relation, replacement));
        Assert.Equal(1, setup.Runtime.Version);
    }

    [Fact]
    public void Generated_fixup_does_not_hide_unrelated_post_capture_drift()
    {
        using var database = new FixupDatabase(); using var context = database.CreateContext();
        var original = new Parent(); var child = new Child { Id = 10, Parent = original, Quantity = 1 };
        context.AddRange(original, child); context.SaveChanges();
        var setup = CreateRelationModel(original, child);
        var replacement = new Parent(); context.Add(replacement); child.Parent = replacement; child.Quantity = 2;
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Parents, setup.Children));
        using var transaction = context.Database.BeginTransaction();
        context.SaveChanges();
        child.Quantity = 3;

        Assert.Throws<InvalidOperationException>(() => work.PrepareAndPlan());
        Assert.Equal(0, setup.Runtime.Version);
        transaction.Rollback();
    }

    [Fact]
    public void Generated_semantic_non_key_requires_first_save_but_unused_generated_value_does_not()
    {
        using var database = new FixupDatabase(); using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var records = model.Objects<SequencedRecord>().Key(x => x.BusinessId);
        var sequence = model.Derived(records).Compute(x => x.DatabaseSequence);
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(records).Materialize(sequence, x => x.Mirror);
        var record = new SequencedRecord { BusinessId = Guid.NewGuid() }; context.Add(record);
        var work = context.CaptureConsistencyUnitOfWork(runtime, mappings);

        var error = Assert.Throws<ConsistencyStoreGeneratedValueNotReadyException>(() => work.PrepareAndPlan());
        Assert.Equal(nameof(SequencedRecord.DatabaseSequence), error.PropertyName);
        Assert.Equal(typeof(SequencedRecord), error.EntityType);

        context.Entry(record).State = EntityState.Detached;
        var unusedModel = new ConsistencyModelBuilder();
        var unusedRecords = unusedModel.Objects<UnusedGeneratedRecord>().Key(x => x.BusinessId);
        var local = unusedModel.Derived(unusedRecords).Compute(x => x.Value);
        var unusedRuntime = unusedModel.Build().CreateRuntime();
        var unused = new UnusedGeneratedRecord { BusinessId = Guid.NewGuid(), Value = 2 };
        context.Add(unused);
        var unusedWork = context.CaptureConsistencyUnitOfWork(unusedRuntime,
            new ConsistencyEfCoreMappings().Map(unusedRecords).Materialize(local, x => x.Mirror));
        Assert.NotNull(unusedWork.PrepareAndPlan());
    }

    [Fact]
    public void Same_clr_member_usage_is_scoped_to_exact_object_set()
    {
        using var database = new FixupDatabase(); using var context = database.CreateContext();
        var activeEntity = new SharedLine { Id = 1, Kind = "active", Value = 3 };
        var archiveEntity = new SharedLine { Id = 2, Kind = "archive", Other = 4 };
        context.AddRange(activeEntity, archiveEntity); context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var active = model.Objects<SharedLine>().Key(x => x.Id);
        var archive = model.Objects<SharedLine>().Key(x => x.Id);
        _ = model.Derived(active).Compute(x => x.Value);
        var archiveValue = model.Derived(archive).Compute(x => x.Other);
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(active, [activeEntity]); seed.Add(archive, [archiveEntity]);
        });
        var mappings = new ConsistencyEfCoreMappings()
            .Map(active, entry => entry.Entity.Kind == "active")
            .Map(archive, entry => entry.Entity.Kind == "archive")
            .Materialize(archiveValue, x => x.Value);
        archiveEntity.Other = 5;

        var work = context.CaptureConsistencyUnitOfWork(runtime, mappings);
        Assert.NotNull(work.PrepareAndPlan());
    }

    private static RelationSetup CreateRelationModel(Parent parent, Child child)
    {
        var model = new ConsistencyModelBuilder();
        var parents = model.Objects<Parent>().Named("parents").Key(x => x.Id);
        var children = model.Objects<Child>().Named("children").Key(x => x.Id);
        var relation = model.Relation(parents, children).Where((left, right) => left.Id == right.ParentId);
        var count = model.Derived(parents).Using(relation).Compute((_, rows) => rows.Count);
        var invariant = model.Invariant(parents).Using(count).Must((_, value) => value <= 1);
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(parents, [parent]); seed.Add(children, [child]);
        });
        var mappings = new ConsistencyEfCoreMappings().Map(parents).Map(children).Enforce(invariant);
        return new RelationSetup(parents, children, relation, runtime, mappings);
    }

    private static ConsistencySaveOptions Complete(ObjectSet<Parent> parents, ObjectSet<Child> children) =>
        new() { Scope = new ConsistencyScope().Complete(parents).Complete(children) };

    private sealed record RelationSetup(ObjectSet<Parent> Parents, ObjectSet<Child> Children,
        Relation<Parent, Child> Relation, ConsistencyRuntime Runtime, ConsistencyEfCoreMappings Mappings);

    private sealed class FixupDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public FixupDatabase() { _connection.Open(); using var context = CreateContext(); context.Database.EnsureCreated(); }
        public FixupContext CreateContext() => new(_connection);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class FixupContext(SqliteConnection connection) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(connection);
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Parent>().Property(x => x.Id).ValueGeneratedOnAdd();
            model.Entity<Child>().HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
            model.Entity<SequencedRecord>().HasKey(x => x.BusinessId);
            model.Entity<SequencedRecord>().Property(x => x.DatabaseSequence).HasDefaultValueSql("42");
            model.Entity<UnusedGeneratedRecord>().HasKey(x => x.BusinessId);
            model.Entity<UnusedGeneratedRecord>().Property(x => x.DatabaseSequence).HasDefaultValueSql("42");
            model.Entity<SharedLine>().HasKey(x => x.Id);
        }
    }

    private sealed class Parent { public int Id { get; set; } }
    private sealed class Child { public int Id { get; set; } public int ParentId { get; set; } public Parent Parent { get; set; } = null!; public int Quantity { get; set; } }
    private sealed class SequencedRecord { public Guid BusinessId { get; set; } public int DatabaseSequence { get; set; } public int Mirror { get; set; } }
    private sealed class UnusedGeneratedRecord { public Guid BusinessId { get; set; } public int DatabaseSequence { get; set; } public int Value { get; set; } public int Mirror { get; set; } }
    private sealed class SharedLine { public int Id { get; set; } public string Kind { get; set; } = ""; public int Value { get; set; } public int Other { get; set; } }
}
