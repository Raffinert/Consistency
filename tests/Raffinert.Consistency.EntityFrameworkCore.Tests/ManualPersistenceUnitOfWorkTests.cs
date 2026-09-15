using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class ManualPersistenceUnitOfWorkTests
{
    [Fact]
    public void Manual_capture_rejects_missing_scope_before_generated_key_sql()
    {
        using var database = new ManualDatabase(); using var context = database.CreateContext();
        var parent = SeedParent(context);
        var setup = CreateModel(parent);
        context.Add(new GeneratedItem { ParentId = parent.Id, Quantity = 1 });

        Assert.Throws<IncompleteConsistencyScopeException>(() =>
            context.CaptureConsistencyUnitOfWork(setup.Runtime, setup.Mappings));

        Assert.Equal(0, database.CreateContext().Items.Count());
        Assert.Equal(0, setup.Runtime.Version);
    }

    [Fact]
    public void Manual_plan_before_generated_key_is_final_is_rejected()
    {
        using var database = new ManualDatabase(); using var context = database.CreateContext();
        var parent = SeedParent(context);
        var setup = CreateModel(parent);
        context.Add(new GeneratedItem { ParentId = parent.Id, Quantity = 1 });
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Parents, setup.Items));

        Assert.Throws<ConsistencyStoreGeneratedKeyNotReadyException>(() => work.PrepareAndPlan());
        Assert.Throws<InvalidOperationException>(() => work.CommitAfterDatabaseCommit());
        Assert.Equal(0, setup.Runtime.Version);
        Assert.Equal(0, parent.Mirror);
    }

    [Fact]
    public void Manual_generated_key_violation_rolls_back_database_and_runtime()
    {
        using var database = new ManualDatabase();
        var parentId = 0;
        var runtimeVersion = -1L;
        using (var context = database.CreateContext())
        {
            var parent = SeedParent(context); parentId = parent.Id;
            var setup = CreateModel(parent, maximumCount: 0);
            context.Add(new GeneratedItem { ParentId = parent.Id, Quantity = 1 });
            var work = context.CaptureConsistencyUnitOfWork(
                setup.Runtime, setup.Mappings, Complete(setup.Parents, setup.Items));
            using var transaction = context.Database.BeginTransaction();
            context.SaveChanges();

            Assert.Throws<ConsistencyInvariantViolationException>(() => work.PrepareAndPlan());
            transaction.Rollback();
            runtimeVersion = setup.Runtime.Version;
        }

        using var verification = database.CreateContext();
        Assert.Equal(0, verification.Items.Count());
        Assert.Equal(0, verification.Parents.Single(x => x.Id == parentId).Mirror);
        Assert.Equal(0, runtimeVersion);
    }

    [Fact]
    public void Manual_generated_key_valid_plan_persists_mirror_and_installs_exact_plan()
    {
        using var database = new ManualDatabase(); using var context = database.CreateContext();
        var parent = SeedParent(context);
        var setup = CreateModel(parent);
        var item = new GeneratedItem { ParentId = parent.Id, Quantity = 1 };
        context.Add(item);
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Parents, setup.Items));
        Assert.True(work.HasChanges);
        using var transaction = context.Database.BeginTransaction();
        context.SaveChanges();

        var plan = Assert.IsType<PreparedImpactPlan>(work.PrepareAndPlan());
        Assert.Contains(plan.InvariantEvaluations, evaluation =>
            evaluation.State == InvariantEvaluationState.Valid);
        Assert.Contains(plan.DerivedEvaluations, evaluation => Equals(evaluation.Value, 1));
        Assert.Equal(1, parent.Mirror);
        context.SaveChanges();
        transaction.Commit();

        var impact = work.CommitAfterDatabaseCommit();
        work.Dispatch();
        Assert.NotNull(impact);
        Assert.True(item.Id > 0);
        Assert.Equal(1, setup.Runtime.Version);
        Assert.Equal(1, database.CreateContext().Parents.AsNoTracking().Single().Mirror);
    }

    [Fact]
    public void Manual_runtime_install_failure_after_database_commit_throws_sync_exception()
    {
        using var database = new ManualDatabase(); using var context = database.CreateContext();
        var parent = SeedParent(context);
        var setup = CreateModel(parent);
        parent.Touch = 1;
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Parents, setup.Items));
        Assert.IsType<PreparedImpactPlan>(work.PrepareAndPlan());
        context.SaveChanges();
        setup.Runtime.Apply(Change.Property(setup.Parents, parent, x => x.Touch, 1, 2));

        var error = Assert.Throws<ConsistencyRuntimeSynchronizationException>(() =>
            work.CommitAfterDatabaseCommit());
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(1, error.RuntimeVersion);
        Assert.Equal(1, database.CreateContext().Parents.AsNoTracking().Single().Touch);
    }

    [Fact]
    public void Manual_persistence_unit_of_work_rejects_out_of_order_calls()
    {
        using var database = new ManualDatabase(); using var context = database.CreateContext();
        var parent = SeedParent(context); var setup = CreateModel(parent); parent.Touch = 1;
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Parents, setup.Items));

        Assert.Throws<InvalidOperationException>(() => work.CommitAfterDatabaseCommit());
        Assert.Throws<InvalidOperationException>(() => work.Dispatch());
        _ = work.PrepareAndPlan();
        Assert.Throws<InvalidOperationException>(() => work.PrepareAndPlan());
        _ = work.CommitAfterDatabaseCommit();
        Assert.Throws<InvalidOperationException>(() => work.CommitAfterDatabaseCommit());
        work.Dispatch();
        Assert.Throws<InvalidOperationException>(() => work.Dispatch());
    }

    [Fact]
    public void Captured_manual_policy_is_immutable_after_mapping_and_scope_changes()
    {
        using var database = new ManualDatabase(); using var context = database.CreateContext();
        var parent = SeedParent(context); var setup = CreateModel(parent); parent.Touch = 1;
        var scope = new ConsistencyScope().Complete(setup.Parents).Complete(setup.Items);
        var work = context.CaptureConsistencyUnitOfWork(setup.Runtime, setup.Mappings,
            new ConsistencySaveOptions { Scope = scope });
        setup.Mappings.Enforce(setup.BlockingInvariant);
        var foreignModel = new ConsistencyModelBuilder();
        var foreign = foreignModel.Objects<Parent>().Key(x => x.Id); _ = foreignModel.Build();
        scope.Complete(foreign);

        var plan = work.PrepareAndPlan();
        Assert.NotNull(plan);
        _ = work.CommitAfterDatabaseCommit();
        work.Dispatch();
        Assert.Equal(1, setup.Runtime.Version);
    }

    private static Parent SeedParent(ManualContext context)
    {
        var parent = new Parent(); context.Add(parent); context.SaveChanges(); return parent;
    }

    private static Setup CreateModel(Parent parent, int maximumCount = 10)
    {
        var model = new ConsistencyModelBuilder();
        var parents = model.Objects<Parent>().Named("parents").Key(x => x.Id);
        var items = model.Objects<GeneratedItem>().Named("items").Key(x => x.Id);
        var relation = model.Relation(parents, items).Where((left, right) => left.Id == right.ParentId);
        var count = model.Derived(parents).Using(relation).Compute((_, rows) => rows.Count);
        var invariant = maximumCount == 0
            ? model.Invariant(parents).Using(count).Must((_, value) => value <= 0)
            : model.Invariant(parents).Using(count).Must((_, value) => value <= 10);
        var blocking = model.Invariant(parents).Using(count).Must((_, _) => false);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(parents, [parent]));
        var mappings = new ConsistencyEfCoreMappings().Map(parents).Map(items)
            .Enforce(invariant).Materialize(count, x => x.Mirror);
        return new Setup(parents, items, count, blocking, runtime, mappings);
    }

    private static ConsistencySaveOptions Complete(ObjectSet<Parent> parents, ObjectSet<GeneratedItem> items) =>
        new() { Scope = new ConsistencyScope().Complete(parents).Complete(items) };

    private sealed record Setup(ObjectSet<Parent> Parents,
        ObjectSet<GeneratedItem> Items, Derived<Parent, int> Count, Invariant<Parent> BlockingInvariant,
        ConsistencyRuntime Runtime, ConsistencyEfCoreMappings Mappings);

    private sealed class ManualDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public ManualDatabase() { _connection.Open(); using var context = CreateContext(); context.Database.EnsureCreated(); }
        public ManualContext CreateContext() => new(_connection);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class ManualContext(SqliteConnection connection) : DbContext
    {
        public DbSet<Parent> Parents => Set<Parent>();
        public DbSet<GeneratedItem> Items => Set<GeneratedItem>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(connection);
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<Parent>().Property(x => x.Id).ValueGeneratedOnAdd();
            model.Entity<GeneratedItem>().Property(x => x.Id).ValueGeneratedOnAdd();
        }
    }

    private sealed class Parent { public int Id { get; set; } public int Mirror { get; set; } public int Touch { get; set; } }
    private sealed class GeneratedItem { public int Id { get; set; } public int ParentId { get; set; } public int Quantity { get; set; } }
}
