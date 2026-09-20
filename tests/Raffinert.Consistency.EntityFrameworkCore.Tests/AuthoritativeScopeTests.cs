using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class AuthoritativeScopeTests
{
    [Fact]
    public void Source_local_enforcement_and_materialization_need_no_scope()
    {
        using var database = new ScopeDatabase();
        using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 10, Touch = 2 };
        context.Add(line); context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<ScopeLine>().Key(x => x.Id);
        var doubled = model.Derived(lines).Select(x => x.Touch * 2).MaterializeTo(x => x.Mirror);
        var valid = model.Invariant(lines).From(doubled).Must((_, value) => value >= 0);
        var runtime = model.Build().CreateRuntime(seed => seed.Add(lines, [line]));
        line.Touch = 3;

        context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(lines).Enforce(valid));

        Assert.Equal(6, line.Mirror);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Relation_enforcement_reports_missing_sets_and_accepts_complete_scope()
    {
        using var database = new ScopeDatabase();
        using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 10 };
        var allocation = new ScopeAllocation { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 4 };
        context.AddRange(line, allocation); context.SaveChanges();
        var model = CreateAllocationModel(out var lines, out var allocations, out _, out var invariant);
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(allocations, [allocation]); });
        var mappings = new ConsistencyEfCoreMappings().Map(lines).Map(allocations).Enforce(invariant);
        line.Touch = 1;

        var none = Assert.Throws<IncompleteConsistencyScopeException>(() =>
            context.SaveChangesConsistently(runtime, mappings));
        Assert.Equal(2, none.Gaps.Count);
        Assert.Equal([ConsistencyScopeRequirementKind.RelationSourceCoverage,
            ConsistencyScopeRequirementKind.RelationTargetCoverage], none.Gaps.Select(x => x.RequirementKind));

        var leftOnly = Assert.Throws<IncompleteConsistencyScopeException>(() =>
            context.SaveChangesConsistently(runtime, mappings,
                new ConsistencySaveOptions { Scope = new ConsistencyScope().Complete(lines).Complete(lines) }));
        Assert.Single(leftOnly.Gaps);
        Assert.Equal(ConsistencyScopeRequirementKind.RelationTargetCoverage, leftOnly.Gaps[0].RequirementKind);

        var rightOnly = Assert.Throws<IncompleteConsistencyScopeException>(() =>
            context.SaveChangesConsistently(runtime, mappings,
                new ConsistencySaveOptions { Scope = new ConsistencyScope().Complete(allocations) }));
        Assert.Single(rightOnly.Gaps);
        Assert.Equal(ConsistencyScopeRequirementKind.RelationSourceCoverage, rightOnly.Gaps[0].RequirementKind);

        context.SaveChangesConsistently(runtime, mappings, Complete(lines, allocations));
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Only_selected_policies_contribute_scope_requirements()
    {
        using var database = new ScopeDatabase();
        using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 10 };
        var allocation = new ScopeAllocation { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 4 };
        context.AddRange(line, allocation); context.SaveChanges();
        var model = CreateAllocationModel(out var lines, out var allocations, out var allocated, out _);
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(allocations, [allocation]); });
        line.Touch = 1;

        context.SaveChangesConsistently(runtime, new ConsistencyEfCoreMappings().Map(lines).Map(allocations),
            new ConsistencySaveOptions { SaveBehavior = ConsistencySaveBehavior.Validate });
        line.Touch = 2;
        context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(lines).Map(allocations),
            new ConsistencySaveOptions { SaveBehavior = ConsistencySaveBehavior.Validate });

        line.Touch = 3;
        Assert.Throws<IncompleteConsistencyScopeException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(lines).Map(allocations)));
    }

    [Fact]
    public void Incomplete_materialization_scope_fails_before_setter_runtime_and_sql()
    {
        using var database = new ScopeDatabase();
        using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 10 };
        var allocation = new ScopeAllocation { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 4 };
        context.AddRange(line, allocation); context.SaveChanges();
        var model = CreateAllocationModel(out var lines, out var allocations, out var allocated, out _);
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(allocations, [allocation]); });
        line.Touch = 7;
        var state = context.Entry(line).State;

        Assert.Throws<IncompleteConsistencyScopeException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(lines).Map(allocations)));

        Assert.Equal(0, line.MirrorWrites);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(state, context.Entry(line).State);
        Assert.Equal(7, line.Touch);
        Assert.Equal(0, database.CreateContext().Lines.AsNoTracking().Single().Touch);
    }

    [Fact]
    public void Multiple_enforced_invariants_merge_duplicate_gaps_and_do_not_dispatch_repairs()
    {
        using var database = new ScopeDatabase(); using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 10 };
        var allocation = new ScopeAllocation { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 4 };
        context.AddRange(line, allocation); context.SaveChanges();
        var repairs = 0;
        var model = CreateAllocationModel(out var lines, out var allocations, out var allocated, out var first);
        var second = model.Invariant(lines).From(allocated).Must((_, value) => value <= 20)
            .RepairWhenViolated();
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(allocations, [allocation]); });
        line.Touch = 1;

        var error = Assert.Throws<IncompleteConsistencyScopeException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(lines).Map(allocations).Enforce(first).Enforce(second)));

        Assert.Equal(2, error.Gaps.Count);
        Assert.Equal(0, repairs);
        Assert.Equal(0, runtime.Version);
    }

    [Fact]
    public void Projected_invariant_also_requires_complete_consumers()
    {
        using var database = new ScopeDatabase(); using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 10 };
        var allocation = new ScopeAllocation { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 4, Line = line };
        var fulfillment = new ScopeFulfillment { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 2 };
        context.AddRange(line, allocation, fulfillment); context.SaveChanges();
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<ScopeLine>().Key(x => x.Id);
        var allocations = model.Objects<ScopeAllocation>().Key(x => x.Id);
        var fulfillments = model.Objects<ScopeFulfillment>().Key(x => x.Id);
        var relation = model.Relation(lines, fulfillments).Where((left, right) => left.Id == right.LineId);
        var total = model.Derived(lines).From(relation).Select((_, rows) => rows.Sum(x => x.Quantity));
        var projected = model.Derived(allocations).From(x => x.Line, total).Select((_, value) => value);
        var invariant = model.Invariant(allocations).From(projected).Must((_, value) => value >= 0);
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(allocations, [allocation]); seed.Add(fulfillments, [fulfillment]); });
        line.Touch = 1;

        var error = Assert.Throws<IncompleteConsistencyScopeException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(lines).Map(allocations).Map(fulfillments).Enforce(invariant)));

        Assert.Equal(3, error.Gaps.Count);
        Assert.Contains(error.Gaps, x => x.ObjectType == typeof(ScopeAllocation) &&
            x.RequirementKind == ConsistencyScopeRequirementKind.ProjectedConsumerCoverage);
    }

    [Fact]
    public void Foreign_scope_set_is_rejected_even_when_policy_is_source_local()
    {
        using var database = new ScopeDatabase(); using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 10 }; context.Add(line); context.SaveChanges();
        var first = new ConsistencyModelBuilder(); var lines = first.Objects<ScopeLine>().Key(x => x.Id);
        var value = first.Derived(lines).Select(x => x.Capacity);
        var invariant = first.Invariant(lines).From(value).Must((_, x) => x >= 0);
        var runtime = first.Build().CreateRuntime(seed => seed.Add(lines, [line]));
        var second = new ConsistencyModelBuilder(); var foreign = second.Objects<ScopeLine>().Key(x => x.Id); _ = second.Build();
        line.Touch = 1;

        var error = Assert.Throws<ArgumentException>(() => context.SaveChangesConsistently(runtime,
            new ConsistencyEfCoreMappings().Map(lines).Enforce(invariant),
            new ConsistencySaveOptions { Scope = new ConsistencyScope().Complete(foreign) }));
        Assert.Contains("another compiled model", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extension_and_interceptor_report_identical_incomplete_scope()
    {
        using var database = new ScopeDatabase();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 10 };
        var allocation = new ScopeAllocation { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 4 };
        using (var seed = database.CreateContext()) { seed.AddRange(line, allocation); seed.SaveChanges(); seed.Entry(line).State = EntityState.Detached; seed.Entry(allocation).State = EntityState.Detached; }
        var model = CreateAllocationModel(out var lines, out var allocations, out _, out var invariant);
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(allocations, [allocation]); });
        var mappings = new ConsistencyEfCoreMappings().Map(lines).Map(allocations).Enforce(invariant);
        IncompleteConsistencyScopeException extension;
        using (var context = database.CreateContext()) { context.AttachRange(line, allocation); line.Touch = 1; extension = Assert.Throws<IncompleteConsistencyScopeException>(() => context.SaveChangesConsistently(runtime, mappings)); }
        line.Touch = 0;
        var interceptor = new ConsistencySaveChangesInterceptor(runtime, mappings, new());
        using var intercepted = database.CreateContext(interceptor); intercepted.AttachRange(line, allocation); line.Touch = 2;

        var interceptedError = Assert.Throws<IncompleteConsistencyScopeException>(() => intercepted.SaveChanges());
        Assert.Equal(extension.Gaps, interceptedError.Gaps);
    }

    [Fact]
    public void Sqlite_partial_runtime_cannot_make_false_valid_claim()
    {
        using var database = new ScopeDatabase(); using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 121 };
        var first = new ScopeAllocation { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 60 };
        var second = new ScopeAllocation { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 60 };
        context.AddRange(line, first, second); context.SaveChanges();
        var model = CreateAllocationModel(out var lines, out var allocations, out _, out var invariant);
        var compiled = model.Build();
        var partial = compiled.CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(allocations, [first]); });
        var mappings = new ConsistencyEfCoreMappings().Map(lines).Map(allocations).Enforce(invariant);
        line.Capacity = 100;

        Assert.Throws<IncompleteConsistencyScopeException>(() => context.SaveChangesConsistently(partial, mappings));
        Assert.Equal(121, database.CreateContext().Lines.AsNoTracking().Single().Capacity);
        Assert.Equal(0, partial.Version);

        var authoritative = compiled.CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(allocations, [first, second]); });
        Assert.Throws<ConsistencyInvariantViolationException>(() =>
            context.SaveChangesConsistently(authoritative, mappings, Complete(lines, allocations)));
        Assert.Equal(121, database.CreateContext().Lines.AsNoTracking().Single().Capacity);
    }

    [Fact]
    public void Sqlite_partial_runtime_cannot_persist_false_mirror()
    {
        using var database = new ScopeDatabase(); using var context = database.CreateContext();
        var line = new ScopeLine { Id = Guid.NewGuid(), Capacity = 100 };
        var first = new ScopeFulfillment { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 4 };
        var second = new ScopeFulfillment { Id = Guid.NewGuid(), LineId = line.Id, Quantity = 6 };
        context.AddRange(line, first, second); context.SaveChanges();
        var model = new ConsistencyModelBuilder(); var lines = model.Objects<ScopeLine>().Key(x => x.Id);
        var fulfillments = model.Objects<ScopeFulfillment>().Key(x => x.Id);
        var relation = model.Relation(lines, fulfillments).Where((left, right) => left.Id == right.LineId);
        var fulfilled = model.Derived(lines).From(relation).Select((source, rows) =>
            rows.Sum(x => x.Quantity) + (source.Touch * 0)).MaterializeTo(x => x.Mirror);
        var compiled = model.Build();
        var mappings = new ConsistencyEfCoreMappings().Map(lines).Map(fulfillments);
        var partial = compiled.CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(fulfillments, [first]); });
        line.Touch = 1;

        Assert.Throws<IncompleteConsistencyScopeException>(() => context.SaveChangesConsistently(partial, mappings));
        Assert.Equal(0, database.CreateContext().Lines.AsNoTracking().Single().Mirror);

        var authoritative = compiled.CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(fulfillments, [first, second]); });
        line.Touch = 2;
        context.SaveChangesConsistently(authoritative, mappings, Complete(lines, fulfillments));
        Assert.Equal(10, database.CreateContext().Lines.AsNoTracking().Single().Mirror);
    }

    private static ConsistencyModelBuilder CreateAllocationModel(out ObjectSet<ScopeLine> lines,
        out ObjectSet<ScopeAllocation> allocations, out Derived<ScopeLine, int> allocated,
        out Invariant<ScopeLine> invariant)
    {
        var model = new ConsistencyModelBuilder(); lines = model.Objects<ScopeLine>().Key(x => x.Id);
        allocations = model.Objects<ScopeAllocation>().Key(x => x.Id);
        var relation = model.Relation(lines, allocations).Where((left, right) => left.Id == right.LineId);
        allocated = model.Derived(lines).From(relation).Select((_, rows) => rows.Sum(x => x.Quantity))
            .MaterializeTo(x => x.Mirror);
        invariant = model.Invariant(lines).From(allocated).Must((line, value) => value <= line.Capacity);
        return model;
    }

    private static ConsistencySaveOptions Complete<TLeft, TRight>(ObjectSet<TLeft> left, ObjectSet<TRight> right)
        where TLeft : class where TRight : class =>
        new() { Scope = new ConsistencyScope().Complete(left).Complete(right) };

    private sealed class ScopeDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        public ScopeDatabase() { _connection.Open(); using var context = CreateContext(); context.Database.EnsureCreated(); }
        public ScopeContext CreateContext(params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => new(_connection, interceptors);
        public void Dispose() => _connection.Dispose();
    }

    private sealed class ScopeContext(SqliteConnection connection,
        Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) : DbContext
    {
        public DbSet<ScopeLine> Lines => Set<ScopeLine>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(connection).AddInterceptors(interceptors);
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<ScopeLine>().HasKey(x => x.Id);
            model.Entity<ScopeAllocation>().HasKey(x => x.Id);
            model.Entity<ScopeAllocation>().Ignore(x => x.Line);
            model.Entity<ScopeFulfillment>().HasKey(x => x.Id);
        }
    }

    private sealed class ScopeLine
    {
        private int _mirror;
        public Guid Id { get; set; }
        public int Capacity { get; set; }
        public int Touch { get; set; }
        public int Mirror { get => _mirror; set { _mirror = value; MirrorWrites++; } }
        public int MirrorWrites { get; private set; }
    }
    private sealed class ScopeAllocation { public Guid Id { get; set; } public Guid LineId { get; set; } public int Quantity { get; set; } public ScopeLine Line { get; set; } = null!; }
    private sealed class ScopeFulfillment { public Guid Id { get; set; } public Guid LineId { get; set; } public int Quantity { get; set; } }
}
