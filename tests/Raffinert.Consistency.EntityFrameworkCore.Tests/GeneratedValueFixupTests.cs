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
        Assert.Equal(1, replacement.ChildCountMirror);
        Assert.Equal(1, database.CreateContext().Set<Parent>().AsNoTracking()
            .Single(parent => parent.Id == replacement.Id).ChildCountMirror);
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
        var sequence = model.Derived(records).Select(x => x.DatabaseSequence).MaterializeTo(x => x.Mirror);
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(records);
        var record = new SequencedRecord { BusinessId = Guid.NewGuid() }; context.Add(record);
        var work = context.CaptureConsistencyUnitOfWork(runtime, mappings);

        var error = Assert.Throws<ConsistencyStoreGeneratedValueNotReadyException>(() => work.PrepareAndPlan());
        Assert.Equal(nameof(SequencedRecord.DatabaseSequence), error.PropertyName);
        Assert.Equal(typeof(SequencedRecord), error.EntityType);

        context.Entry(record).State = EntityState.Detached;
        var unusedModel = new ConsistencyModelBuilder();
        var unusedRecords = unusedModel.Objects<UnusedGeneratedRecord>().Key(x => x.BusinessId);
        var local = unusedModel.Derived(unusedRecords).Select(x => x.Value).MaterializeTo(x => x.Mirror);
        var unusedRuntime = unusedModel.Build().CreateRuntime();
        var unused = new UnusedGeneratedRecord { BusinessId = Guid.NewGuid(), Value = 2 };
        context.Add(unused);
        var unusedWork = context.CaptureConsistencyUnitOfWork(unusedRuntime,
            new ConsistencyEfCoreMappings().Map(unusedRecords));
        Assert.NotNull(unusedWork.PrepareAndPlan());
    }

    [Fact]
    public void Generated_semantic_non_key_plans_with_final_value_after_first_save()
    {
        using var database = new FixupDatabase(); using var context = database.CreateContext();
        var (_, runtime, mappings) = CreateSequencedModel();
        var record = new SequencedRecord { BusinessId = Guid.NewGuid() }; context.Add(record);
        var work = context.CaptureConsistencyUnitOfWork(runtime, mappings);
        using var transaction = context.Database.BeginTransaction();
        context.SaveChanges();
        Assert.Equal(42, record.DatabaseSequence);

        var plan = Assert.IsType<PreparedImpactPlan>(work.PrepareAndPlan());
        Assert.Contains(plan.DerivedEvaluations, evaluation => Equals(evaluation.Value, 42));
        Assert.Equal(42, record.Mirror);
        context.SaveChanges(); transaction.Commit();
        _ = work.CommitAfterDatabaseCommit(); work.Dispatch();
        Assert.Equal(1, runtime.Version);
        Assert.Equal(42, database.CreateContext().Set<SequencedRecord>().AsNoTracking().Single().Mirror);
    }

    [Fact]
    public void Convenience_and_interceptor_reject_generated_semantic_input_equally()
    {
        using var extensionDatabase = new FixupDatabase(); using var extensionContext = extensionDatabase.CreateContext();
        var (_, extensionRuntime, extensionMappings) = CreateSequencedModel();
        extensionContext.Add(new SequencedRecord { BusinessId = Guid.NewGuid() });
        var extension = Assert.Throws<ConsistencyStoreGeneratedValueRequiresManualWorkflowException>(() =>
            extensionContext.SaveChangesConsistently(extensionRuntime, extensionMappings));

        using var interceptorDatabase = new FixupDatabase();
        var (_, interceptorRuntime, interceptorMappings) = CreateSequencedModel();
        var interceptor = new ConsistencySaveChangesInterceptor(interceptorRuntime, interceptorMappings, new());
        using var interceptorContext = interceptorDatabase.CreateContext(interceptor);
        interceptorContext.Add(new SequencedRecord { BusinessId = Guid.NewGuid() });
        var intercepted = Assert.Throws<ConsistencyStoreGeneratedValueRequiresManualWorkflowException>(() =>
            interceptorContext.SaveChanges());

        Assert.Equal(extension.EntityType, intercepted.EntityType);
        Assert.Equal(extension.PropertyName, intercepted.PropertyName);
        Assert.Equal(0, extensionDatabase.CreateContext().Set<SequencedRecord>().Count());
        Assert.Equal(0, interceptorDatabase.CreateContext().Set<SequencedRecord>().Count());
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
        _ = model.Derived(active).Select(x => x.GeneratedOnly);
        var archiveValue = model.Derived(archive).Select(x => x.Other).MaterializeTo(x => x.Value);
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(active, [activeEntity]); seed.Add(archive, [archiveEntity]);
        });
        var mappings = new ConsistencyEfCoreMappings()
            .Map(active, entry => entry.Entity.Kind == "active")
            .Map(archive, entry => entry.Entity.Kind == "archive")
            ;
        archiveEntity.Other = 5;
        context.Add(new SharedLine { Id = 3, Kind = "archive", Other = 6 });

        var work = context.CaptureConsistencyUnitOfWork(runtime, mappings);
        Assert.NotNull(work.PrepareAndPlan());
    }

    [Fact]
    public void Generated_invariant_source_dependency_requires_first_save()
    {
        using var database = new FixupDatabase(); using var context = database.CreateContext();
        var model = new ConsistencyModelBuilder();
        var records = model.Objects<SequencedRecord>().Key(x => x.BusinessId);
        var local = model.Derived(records).Select(x => x.BusinessId);
        var invariant = model.Invariant(records).From(local)
            .Must((source, _) => source.DatabaseSequence >= 0);
        var runtime = model.Build().CreateRuntime();
        var mappings = new ConsistencyEfCoreMappings().Map(records).Enforce(invariant);
        context.Add(new SequencedRecord { BusinessId = Guid.NewGuid() });

        var work = context.CaptureConsistencyUnitOfWork(runtime, mappings);
        var error = Assert.Throws<ConsistencyStoreGeneratedValueNotReadyException>(() =>
            work.PrepareAndPlan());
        Assert.Equal(nameof(SequencedRecord.DatabaseSequence), error.PropertyName);
    }

    private static RelationSetup CreateRelationModel(Parent parent, Child child)
    {
        var model = new ConsistencyModelBuilder();
        var parents = model.Objects<Parent>().Named("parents").Key(x => x.Id);
        var children = model.Objects<Child>().Named("children").Key(x => x.Id);
        var relation = model.Relation(parents, children).Where((left, right) => left.Id == right.ParentId);
        var count = model.Derived(parents).From(relation).Select((_, rows) => rows.Count).MaterializeTo(parent => parent.ChildCountMirror);
        var invariant = model.Invariant(parents).From(count).Must((_, value) => value <= 1);
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(parents, [parent]); seed.Add(children, [child]);
        });
        var mappings = new ConsistencyEfCoreMappings().Map(parents).Map(children)
            .Enforce(invariant);
        return new RelationSetup(parents, children, relation, runtime, mappings);
    }

    private static (ObjectSet<SequencedRecord> Records, ConsistencyRuntime Runtime,
        ConsistencyEfCoreMappings Mappings) CreateSequencedModel()
    {
        var model = new ConsistencyModelBuilder();
        var records = model.Objects<SequencedRecord>().Key(x => x.BusinessId);
        var sequence = model.Derived(records).Select(x => x.DatabaseSequence).MaterializeTo(x => x.Mirror);
        var runtime = model.Build().CreateRuntime();
        return (records, runtime,
            new ConsistencyEfCoreMappings().Map(records));
    }

    private static ConsistencySaveOptions Complete(ObjectSet<Parent> parents, ObjectSet<Child> children) =>
        new() { Scope = new ConsistencyScope().Complete(parents).Complete(children) };

    private sealed record RelationSetup(ObjectSet<Parent> Parents, ObjectSet<Child> Children,
        Relation<Parent, Child> Relation, ConsistencyRuntime Runtime, ConsistencyEfCoreMappings Mappings);

    private sealed class FixupDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public FixupDatabase() { _connection.Open(); using var context = CreateContext(); context.Database.EnsureCreated(); }
        public FixupContext CreateContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new(_connection, interceptors);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class FixupContext(SqliteConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlite(connection).AddInterceptors(interceptors);
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Parent>().Property(x => x.Id).ValueGeneratedOnAdd();
            model.Entity<Child>().HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
            model.Entity<SequencedRecord>().HasKey(x => x.BusinessId);
            model.Entity<SequencedRecord>().Property(x => x.DatabaseSequence).HasDefaultValueSql("42");
            model.Entity<UnusedGeneratedRecord>().HasKey(x => x.BusinessId);
            model.Entity<UnusedGeneratedRecord>().Property(x => x.DatabaseSequence).HasDefaultValueSql("42");
            model.Entity<SharedLine>().HasKey(x => x.Id);
            model.Entity<SharedLine>().Property(x => x.GeneratedOnly).HasDefaultValueSql("7");
        }
    }

    private sealed class Parent { public int Id { get; set; } public int ChildCountMirror { get; set; } }
    private sealed class Child { public int Id { get; set; } public int ParentId { get; set; } public Parent Parent { get; set; } = null!; public int Quantity { get; set; } }
    private sealed class SequencedRecord { public Guid BusinessId { get; set; } public int DatabaseSequence { get; set; } public int Mirror { get; set; } }
    private sealed class UnusedGeneratedRecord { public Guid BusinessId { get; set; } public int DatabaseSequence { get; set; } public int Value { get; set; } public int Mirror { get; set; } }
    private sealed class SharedLine { public int Id { get; set; } public string Kind { get; set; } = ""; public int GeneratedOnly { get; set; } public int Value { get; set; } public int Other { get; set; } }
}
