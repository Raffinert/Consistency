using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class GeneratedUpdateAndUnmappedDependencyTests
{
    [Fact]
    public void Manual_capture_observes_unmapped_nested_dependency()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var product = new Product { Price = 10m };
        var line = new OrderLine { Product = product, PriceMirror = 10m };
        context.Add(line);
        context.SaveChanges();
        var setup = CreateLineModel(line);
        product.Price = 25m;

        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Lines));

        Assert.True(work.HasChanges);
        var plan = Assert.IsType<PreparedImpactPlan>(work.PrepareAndPlan());
        Assert.Contains(plan.DerivedEvaluations, evaluation =>
            ReferenceEquals(evaluation.Source, line) && Equals(evaluation.Value, 25m));
        Assert.Equal(25m, line.PriceMirror);
        context.SaveChanges();
        _ = work.CommitAfterDatabaseCommit();
        work.Dispatch();
        Assert.Equal(25m, setup.Runtime.Evaluate(setup.CurrentPrice, line));
    }

    [Fact]
    public void Convenience_interceptor_and_manual_paths_observe_unmapped_nested_dependency()
    {
        RunUnmappedDependencyPath(PathKind.Extension);
        RunUnmappedDependencyPath(PathKind.Interceptor);
        RunUnmappedDependencyPath(PathKind.Manual);
        RunUnmappedDependencyPath(PathKind.LowLevel);
    }

    [Fact]
    public void Generated_update_semantic_value_requires_manual_workflow_before_sql()
    {
        using var extensionDatabase = new TestDatabase();
        using var extensionContext = extensionDatabase.CreateContext();
        var extensionRecord = SeedRecord(extensionContext);
        var extension = CreateRecordModel(extensionRecord);
        extensionRecord.Input = 4;
        var extensionError = Assert.Throws<ConsistencyStoreGeneratedValueRequiresManualWorkflowException>(() =>
            extensionContext.SaveChangesConsistently(extension.Runtime, extension.Mappings, Complete(extension.Records)));
        Assert.Equal(nameof(ComputedRecord.DatabaseComputed), extensionError.PropertyName);
        Assert.Equal(3, extensionDatabase.CreateContext().Set<ComputedRecord>().AsNoTracking().Single().Input);
        Assert.Equal(0, extension.Runtime.Version);

        using var interceptorDatabase = new TestDatabase();
        ComputedRecord interceptorRecord;
        using (var seed = interceptorDatabase.CreateContext()) interceptorRecord = SeedRecord(seed);
        var intercepted = CreateRecordModel(interceptorRecord);
        var interceptor = new ConsistencySaveChangesInterceptor(
            intercepted.Runtime, intercepted.Mappings, Complete(intercepted.Records));
        using var interceptorContext = interceptorDatabase.CreateContext(interceptor);
        interceptorContext.Attach(interceptorRecord);
        interceptorRecord.Input = 4;
        Assert.Throws<ConsistencyStoreGeneratedValueRequiresManualWorkflowException>(
            () => interceptorContext.SaveChanges());
        Assert.Equal(3, interceptorDatabase.CreateContext().Set<ComputedRecord>().AsNoTracking().Single().Input);
        Assert.Equal(0, intercepted.Runtime.Version);
    }

    [Fact]
    public async Task Low_level_paths_reject_generated_update_semantic_value_before_sql()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var record = SeedRecord(context);
        var setup = CreateRecordModel(record);
        record.Input = 4;

        Assert.Throws<ConsistencyStoreGeneratedValueRequiresManualWorkflowException>(() =>
            context.SaveChangesAndApply(setup.Runtime, setup.LowLevelMappings));
        Assert.Equal(3, database.CreateContext().Set<ComputedRecord>().AsNoTracking().Single().Input);

        await Assert.ThrowsAsync<ConsistencyStoreGeneratedValueRequiresManualWorkflowException>(() =>
            context.SaveChangesAndApplyAsync(setup.Runtime, setup.LowLevelMappings));
        Assert.Equal(3, database.CreateContext().Set<ComputedRecord>().AsNoTracking().Single().Input);
    }

    [Fact]
    public async Task Convenience_async_rejects_generated_update_semantic_value_before_sql()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var record = SeedRecord(context);
        var setup = CreateRecordModel(record);
        record.Input = 4;

        await Assert.ThrowsAsync<ConsistencyStoreGeneratedValueRequiresManualWorkflowException>(() =>
            context.SaveChangesConsistentlyAsync(
                setup.Runtime, setup.Mappings, Complete(setup.Records)));

        Assert.Equal(3, database.CreateContext().Set<ComputedRecord>().AsNoTracking().Single().Input);
        Assert.Equal(0, setup.Runtime.Version);
    }

    [Fact]
    public void Manual_workflow_reconciles_generated_update_value_after_first_save()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var record = SeedRecord(context);
        var setup = CreateRecordModel(record);
        record.Input = 4;
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Records));

        Assert.Throws<ConsistencyStoreGeneratedValueNotReadyException>(() => work.PrepareAndPlan());
    }

    [Fact]
    public void Manual_workflow_plans_and_commits_final_generated_update_value()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var record = SeedRecord(context);
        var setup = CreateRecordModel(record);
        record.Input = 4;
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Records));
        using var transaction = context.Database.BeginTransaction();
        context.SaveChanges();
        Assert.Equal(8, record.DatabaseComputed);

        var plan = Assert.IsType<PreparedImpactPlan>(work.PrepareAndPlan());
        Assert.Contains(plan.DerivedEvaluations, evaluation =>
            ReferenceEquals(evaluation.Source, record) && Equals(evaluation.Value, 8));
        Assert.Contains(plan.InvariantEvaluations, evaluation =>
            ReferenceEquals(evaluation.Source, record) && evaluation.State == InvariantEvaluationState.Valid);
        Assert.Equal(8, record.Mirror);
        context.SaveChanges();
        transaction.Commit();
        _ = work.CommitAfterDatabaseCommit();
        work.Dispatch();

        Assert.Equal(8, setup.Runtime.Evaluate(setup.Computed, record));
        var stored = database.CreateContext().Set<ComputedRecord>().AsNoTracking().Single();
        Assert.Equal(8, stored.DatabaseComputed);
        Assert.Equal(8, stored.Mirror);
    }

    [Fact]
    public void Generated_update_finalization_does_not_hide_unrelated_drift()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var record = SeedRecord(context);
        var setup = CreateRecordModel(record);
        record.Input = 4;
        record.CallerValue = 2;
        var work = context.CaptureConsistencyUnitOfWork(
            setup.Runtime, setup.Mappings, Complete(setup.Records));
        using var transaction = context.Database.BeginTransaction();
        context.SaveChanges();
        record.CallerValue = 3;

        Assert.Throws<InvalidOperationException>(() => work.PrepareAndPlan());
        Assert.Equal(0, setup.Runtime.Version);
        transaction.Rollback();
    }

    [Fact]
    public void Generated_update_on_existing_consistency_identity_is_explicitly_unsupported()
    {
        using var context = new IdentityContext();
        var entity = new GeneratedIdentity { DatabaseId = 1, Id = 10, Value = 1 };
        context.Attach(entity);
        var model = new ConsistencyModelBuilder();
        var objects = model.Objects<GeneratedIdentity>().Key(x => x.Id);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(objects, [entity]));
        var highLevel = new ConsistencyEfCoreMappings().Map(objects);
        var lowLevel = new ConsistencyUnitOfWorkMappings().Map(objects);
        entity.Value = 2;

        var extension = Assert.Throws<ConsistencyStoreGeneratedIdentityUpdateNotSupportedException>(() =>
            context.SaveChangesConsistently(runtime, highLevel));
        var manual = Assert.Throws<ConsistencyStoreGeneratedIdentityUpdateNotSupportedException>(() =>
            context.CaptureConsistencyUnitOfWork(runtime, highLevel));
        var low = Assert.Throws<ConsistencyStoreGeneratedIdentityUpdateNotSupportedException>(() =>
            context.SaveChangesAndApply(runtime, lowLevel));
        var interceptor = new ConsistencySaveChangesInterceptor(runtime, highLevel, new());
        using var interceptedContext = new IdentityContext(interceptor);
        var interceptedEntity = new GeneratedIdentity { DatabaseId = 2, Id = 20, Value = 1 };
        interceptedContext.Attach(interceptedEntity);
        interceptedEntity.Value = 2;
        var intercepted = Assert.Throws<ConsistencyStoreGeneratedIdentityUpdateNotSupportedException>(() =>
            interceptedContext.SaveChanges());

        Assert.Equal(typeof(GeneratedIdentity), extension.EntityType);
        Assert.Equal(nameof(GeneratedIdentity.Id), extension.PropertyName);
        Assert.Equal(extension.PropertyName, manual.PropertyName);
        Assert.Equal(extension.PropertyName, low.PropertyName);
        Assert.Equal(extension.PropertyName, intercepted.PropertyName);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Generated_but_unused_update_value_does_not_force_manual_workflow()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var record = new UnusedComputedRecord { Input = 3, SemanticValue = 1 };
        context.Add(record);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var records = model.Objects<UnusedComputedRecord>().Key(x => x.Id);
        var semantic = model.Derived(records).Select(x => x.SemanticValue);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(records, [record]));
        record.Input = 4;
        record.SemanticValue = 2;

        context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(records), Complete(records));

        Assert.Equal(8, record.DatabaseComputed);
        Assert.Equal(2, runtime.Evaluate(semantic, record));
    }

    [Fact]
    public void Manual_workflow_reconciles_generated_update_on_unmapped_nested_target()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var product = new GeneratedProduct { Input = 3 };
        var line = new GeneratedLine { Product = product };
        context.Add(line);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<GeneratedLine>().Key(x => x.Id);
        var computed = model.Derived(lines).Select(x => x.Product.DatabaseComputed).MaterializeTo(x => x.Mirror);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(lines, [line]));
        _ = runtime.Evaluate(computed, line);
        var mappings = new ConsistencyEfCoreMappings().Map(lines)
            ;
        product.Input = 4;
        var work = context.CaptureConsistencyUnitOfWork(runtime, mappings, Complete(lines));
        using var transaction = context.Database.BeginTransaction();
        context.SaveChanges();

        var plan = Assert.IsType<PreparedImpactPlan>(work.PrepareAndPlan());
        Assert.Contains(plan.DerivedEvaluations, evaluation => Equals(evaluation.Value, 8));
        Assert.Equal(8, line.Mirror);
        context.SaveChanges();
        transaction.Commit();
        _ = work.CommitAfterDatabaseCommit();
        work.Dispatch();
        Assert.Equal(8, runtime.Evaluate(computed, line));
    }

    [Fact]
    public void Explicit_declared_dependency_flows_to_generated_value_guard()
    {
        using var database = new TestDatabase();
        using var context = database.CreateContext();
        var product = new GeneratedProduct { Input = 3 };
        var line = new GeneratedLine { Product = product };
        context.Add(line);
        context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<GeneratedLine>().Key(x => x.Id);
        var computed = model.Derived(lines)
            .DependsOn(x => x.Product.DatabaseComputed)
            .Select(x => ReadGenerated(x.Product)).MaterializeTo(x => x.Mirror);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(lines, [line]));
        product.Input = 4;

        var error = Assert.Throws<ConsistencyStoreGeneratedValueRequiresManualWorkflowException>(() =>
            context.SaveChangesConsistently(runtime,
                new ConsistencyEfCoreMappings().Map(lines),
                Complete(lines)));

        Assert.Equal(nameof(GeneratedProduct.DatabaseComputed), error.PropertyName);
        Assert.Equal(3, database.CreateContext().Set<GeneratedProduct>().AsNoTracking().Single().Input);
    }

    private static void RunUnmappedDependencyPath(PathKind kind)
    {
        using var database = new TestDatabase();
        OrderLine line;
        using (var seed = database.CreateContext())
        {
            var product = new Product { Price = 10m };
            line = new OrderLine { Product = product, PriceMirror = 10m };
            seed.Add(line);
            seed.SaveChanges();
        }
        var setup = CreateLineModel(line);
        if (kind == PathKind.Interceptor)
        {
            var interceptor = new ConsistencySaveChangesInterceptor(
                setup.Runtime, setup.Mappings, Complete(setup.Lines));
            using var context = database.CreateContext(interceptor);
            context.Attach(line);
            line.Product.Price = 25m;
            context.SaveChanges();
        }
        else
        {
            using var context = database.CreateContext();
            context.Attach(line);
            line.Product.Price = 25m;
            if (kind == PathKind.Extension)
                context.SaveChangesConsistently(setup.Runtime, setup.Mappings, Complete(setup.Lines));
            else if (kind == PathKind.LowLevel)
                context.SaveChangesAndApply(setup.Runtime, setup.LowLevelMappings);
            else
            {
                var work = context.CaptureConsistencyUnitOfWork(
                    setup.Runtime, setup.Mappings, Complete(setup.Lines));
                _ = work.PrepareAndPlan();
                context.SaveChanges();
                _ = work.CommitAfterDatabaseCommit();
                work.Dispatch();
            }
        }
        Assert.Equal(25m, setup.Runtime.Evaluate(setup.CurrentPrice, line));
    }

    private static LineSetup CreateLineModel(OrderLine line)
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<OrderLine>().Key(x => x.Id);
        var currentPrice = model.Derived(lines).Select(x => x.Product.Price).MaterializeTo(x => x.PriceMirror);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(lines, [line]));
        var mappings = new ConsistencyEfCoreMappings()
            .Map(lines)
            ;
        return new LineSetup(lines, currentPrice, runtime, mappings,
            new ConsistencyUnitOfWorkMappings().Map(lines));
    }

    private static RecordSetup CreateRecordModel(ComputedRecord record)
    {
        var model = new ConsistencyModelBuilder();
        var records = model.Objects<ComputedRecord>().Key(x => x.Id);
        var computed = model.Derived(records).Select(x => x.DatabaseComputed).MaterializeTo(x => x.Mirror);
        var valid = model.Invariant(records).From(computed).Must((_, value) => value <= 20);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(records, [record]));
        _ = runtime.Evaluate(computed, record);
        _ = runtime.Evaluate(valid, record);
        var mappings = new ConsistencyEfCoreMappings().Map(records)
            .Enforce(valid);
        return new RecordSetup(records, computed, runtime, mappings,
            new ConsistencyUnitOfWorkMappings().Map(records));
    }

    private static ComputedRecord SeedRecord(TestContext context)
    {
        var record = new ComputedRecord { Input = 3, CallerValue = 1 };
        context.Add(record);
        context.SaveChanges();
        record.Mirror = record.DatabaseComputed;
        context.SaveChanges();
        return record;
    }

    private static int ReadGenerated(GeneratedProduct product) => product.DatabaseComputed;

    private static ConsistencySaveOptions Complete<T>(ObjectSet<T> set) where T : class =>
        new() { Scope = new ConsistencyScope().Complete(set) };

    private sealed record LineSetup(ObjectSet<OrderLine> Lines, Derived<OrderLine, decimal> CurrentPrice,
        ConsistencyRuntime Runtime, ConsistencyEfCoreMappings Mappings,
        ConsistencyUnitOfWorkMappings LowLevelMappings);
    private sealed record RecordSetup(ObjectSet<ComputedRecord> Records, Derived<ComputedRecord, int> Computed,
        ConsistencyRuntime Runtime, ConsistencyEfCoreMappings Mappings,
        ConsistencyUnitOfWorkMappings LowLevelMappings);

    private enum PathKind { Extension, Interceptor, Manual, LowLevel }

    private sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public TestDatabase()
        {
            _connection.Open();
            using var context = CreateContext();
            context.Database.EnsureCreated();
        }
        public TestContext CreateContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
            new(_connection, interceptors);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class TestContext(SqliteConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseSqlite(connection).AddInterceptors(interceptors);
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<OrderLine>().HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
            model.Entity<ComputedRecord>().Property(x => x.DatabaseComputed)
                .HasComputedColumnSql("\"Input\" * 2", stored: true);
            model.Entity<UnusedComputedRecord>().Property(x => x.DatabaseComputed)
                .HasComputedColumnSql("\"Input\" * 2", stored: true);
            model.Entity<GeneratedLine>().HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
            model.Entity<GeneratedProduct>().Property(x => x.DatabaseComputed)
                .HasComputedColumnSql("\"Input\" * 2", stored: true);
        }
    }

    private sealed class IdentityContext(
        params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseInMemoryDatabase($"identity-{Guid.NewGuid()}").AddInterceptors(interceptors);
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<GeneratedIdentity>().HasKey(x => x.DatabaseId);
            model.Entity<GeneratedIdentity>().Property(x => x.Id).ValueGeneratedOnAddOrUpdate();
        }
    }

    private sealed class OrderLine
    {
        public int Id { get; set; }
        public Product Product { get; set; } = null!;
        public int ProductId { get; set; }
        public decimal PriceMirror { get; set; }
    }

    private sealed class Product { public int Id { get; set; } public decimal Price { get; set; } }

    private sealed class ComputedRecord
    {
        public int Id { get; set; }
        public int Input { get; set; }
        public int DatabaseComputed { get; private set; }
        public int Mirror { get; set; }
        public int CallerValue { get; set; }
    }

    private sealed class GeneratedIdentity
    {
        public int DatabaseId { get; set; }
        public int Id { get; set; }
        public int Value { get; set; }
    }

    private sealed class UnusedComputedRecord
    {
        public int Id { get; set; }
        public int Input { get; set; }
        public int DatabaseComputed { get; private set; }
        public int SemanticValue { get; set; }
    }

    private sealed class GeneratedLine
    {
        public int Id { get; set; }
        public GeneratedProduct Product { get; set; } = null!;
        public int ProductId { get; set; }
        public int Mirror { get; set; }
    }

    private sealed class GeneratedProduct
    {
        public int Id { get; set; }
        public int Input { get; set; }
        public int DatabaseComputed { get; private set; }
    }
}
