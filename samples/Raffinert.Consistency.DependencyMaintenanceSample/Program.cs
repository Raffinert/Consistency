using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency;
using Raffinert.Consistency.EntityFrameworkCore;

RequireEqual(null, UnitRateCalculator.Calculate(null, 4m), "null source");
RequireEqual(null, UnitRateCalculator.Calculate(10m, 0m), "zero target");
RequireEqual(2.5m, UnitRateCalculator.Calculate(10m, 4m), "basic rate");
RequireEqual(0.333333m, UnitRateCalculator.Calculate(1m, 3m), "rounded rate");
RequireEqual(null, UnitRateCalculator.Calculate(decimal.MaxValue, 1m), "maximum supported rate");
RequireEqual(null, UnitRateCalculator.Calculate(decimal.MaxValue, 0.1m), "division overflow");

await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var options = new DbContextOptionsBuilder<DependencyMaintenanceContext>()
    .UseSqlite(connection)
    .Options;
await using var context = new DependencyMaintenanceContext(options);
await context.Database.EnsureCreatedAsync();

var sourceA = new SourceItem { Id = 1, UnitValue = 12m };
var sourceB = new SourceItem { Id = 2, UnitValue = 30m };
var targetA = new TargetItem { Id = 10, UnitValue = 4m };
var targetB = new TargetItem { Id = 11, UnitValue = 5m };
var associationA = new Association { Id = 100, SourceItem = sourceA, TargetItem = targetA };
var associationB = new Association { Id = 101, SourceItem = sourceA, TargetItem = targetB };
associationA.UnitRate = UnitRateCalculator.Calculate(sourceA.UnitValue, targetA.UnitValue);
associationB.UnitRate = UnitRateCalculator.Calculate(sourceA.UnitValue, targetB.UnitValue);
context.AddRange(sourceA, sourceB, targetA, targetB, associationA, associationB);
await context.SaveChangesAsync();

var builder = new ConsistencyModelBuilder();

// Source and target objects remain ordinary EF-tracked navigation targets; only associations are consistency roots.
var associations = builder.Objects<Association>()
    .Named("associations")
    .Key(x => x.Id);
var unitRate = builder.Derived(associations)
    .DependsOn(a => a.SourceItem.UnitValue)
    .DependsOn(a => a.TargetItem.UnitValue)
    .Compute(association => UnitRateCalculator.Calculate(
        association.SourceItem.UnitValue,
        association.TargetItem.UnitValue))
    .Named("association-unit-rate");

var compiled = builder.Build();
var runtime = compiled.CreateRuntime(seed => seed.Add(associations, [associationA, associationB]));
var mappings = new ConsistencyEfCoreMappings()
    .Map(associations)
    .Materialize(unitRate, association => association.UnitRate);
var saveOptions = new ConsistencySaveOptions
{
    Scope = new ConsistencyScope().Complete(associations)
};

var expectedVersion = 0L;

// A shared source mutation fans out to both associations.
sourceA.UnitValue = 20m;
await SaveAndVerifyAsync(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 5m), (associationB, 4.0m));

// A target mutation remains selective to its consumers.
targetA.UnitValue = 10m;
await SaveAndVerifyAsync(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2m), (associationB, 4.0m));

// Retargeting moves the reverse-navigation dependency from source A to source B.
associationB.SourceItem = sourceB;
context.ChangeTracker.DetectChanges();
RequireEqual(sourceB.Id, associationB.SourceItemId, "relationship fixup");
await SaveAndVerifyAsync(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2m), (associationB, 6m));

sourceA.UnitValue = 25m;
await SaveAndVerifyAsync(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2.5m), (associationB, 6m));

sourceB.UnitValue = null;
await SaveAndVerifyAsync(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2.5m), (associationB, null));

sourceB.UnitValue = 30m;
targetB.UnitValue = 0m;
await SaveAndVerifyAsync(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2.5m), (associationB, null));

Console.WriteLine("Dependency-maintenance dogfood sample passed:");
Console.WriteLine("- shared source changes updated all affected association mirrors");
Console.WriteLine("- target changes preserved correct mirrors for other associations");
Console.WriteLine("- retargeted associations followed their new source");
Console.WriteLine("- null/zero semantics were materialized consistently");

await RunExternalConsumerDiscoveryScenario();
return;

static async Task RunExternalConsumerDiscoveryScenario()
{
    await using var connection = new SqliteConnection("Data Source=:memory:");
    await connection.OpenAsync();
    var options = new DbContextOptionsBuilder<DependencyMaintenanceContext>()
        .UseSqlite(connection)
        .Options;
    await using (var seed = new DependencyMaintenanceContext(options))
    {
        await seed.Database.EnsureCreatedAsync();
        var source = new SourceItem { Id = 50, UnitValue = 100m };
        var targetA = new TargetItem { Id = 60, UnitValue = 50m };
        var targetB = new TargetItem { Id = 61, UnitValue = 25m };
        var targetC = new TargetItem { Id = 62, UnitValue = 10m };
        var first = new Association { Id = 500, SourceItem = source, TargetItem = targetA };
        var second = new Association { Id = 501, SourceItem = source, TargetItem = targetB };
        var third = new Association { Id = 502, SourceItem = source, TargetItem = targetC };
        first.UnitRate = UnitRateCalculator.Calculate(source.UnitValue, targetA.UnitValue);
        second.UnitRate = UnitRateCalculator.Calculate(source.UnitValue, targetB.UnitValue);
        third.UnitRate = UnitRateCalculator.Calculate(source.UnitValue, targetC.UnitValue);
        seed.AddRange(source, targetA, targetB, targetC, first, second, third);
        await seed.SaveChangesAsync();
    }

    await using var context = new DependencyMaintenanceContext(options);
    var known = context.Associations
        .Include(x => x.SourceItem)
        .Include(x => x.TargetItem)
        .Single(x => x.Id == 500);
    var sourceKnown = known.SourceItem;
    Require(context.ChangeTracker.Entries<Association>().Count() == 1,
        "incomplete operation graph before discovery");

    var builder = new ConsistencyModelBuilder();
    var associations = builder.Objects<Association>().Named("discovery-associations").Key(x => x.Id);
    var unitRate = builder.Derived(associations)
        .DependsOn(x => x.SourceItem.UnitValue)
        .DependsOn(x => x.TargetItem.UnitValue)
        .Compute(x => UnitRateCalculator.Calculate(x.SourceItem.UnitValue, x.TargetItem.UnitValue))
        .Named("discovery-unit-rate");
    var runtime = builder.Build().CreateRuntime(seed => seed.Add(associations, [known]));
    var resolverCalls = 0;
    var mappings = new ConsistencyEfCoreMappings()
        .Map(associations)
        .Materialize(unitRate, x => x.UnitRate)
        .DiscoverConsumers(associations, x => x.SourceItem, (db, sources) =>
        {
            resolverCalls++;
            var ids = sources.Select(x => x.Id).ToList();
            return db.Set<Association>()
                .Where(x => ids.Contains(x.SourceItemId))
                .Include(x => x.SourceItem)
                .Include(x => x.TargetItem);
        })
        .DiscoverConsumers(associations, x => x.TargetItem, (db, targets) =>
        {
            var ids = targets.Select(x => x.Id).ToList();
            return db.Set<Association>()
                .Where(x => ids.Contains(x.TargetItemId))
                .Include(x => x.SourceItem)
                .Include(x => x.TargetItem);
        });

    sourceKnown.UnitValue = 200m;
    await context.SaveChangesConsistentlyAsync(runtime, mappings);

    RequireEqual(1, resolverCalls, "batched source consumer resolver calls");
    var discovered = context.Associations.OrderBy(x => x.Id)
        .Include(association => association.SourceItem)
        .Include(association => association.TargetItem)
        .ToArray();

    RequireEqual(3, discovered.Length, "discovered consumer count");
    foreach (var association in discovered)
    {
        var expected = UnitRateCalculator.Calculate(association.SourceItem.UnitValue, association.TargetItem.UnitValue);
        RequireEqual(expected, association.UnitRate, $"discovered tracked UnitRate for {association.Id}");
        RequireEqual(expected, runtime.Get(unitRate, association), $"discovered runtime UnitRate for {association.Id}");
    }

    await using var verification = new DependencyMaintenanceContext(connection);
    foreach (var association in verification.Associations.AsNoTracking().OrderBy(x => x.Id))
    {
        decimal? expected = association.Id switch { 500 => 4m, 501 => 8m, 502 => 20m, _ => null };
        RequireEqual(expected, association.UnitRate, $"discovered persisted UnitRate for {association.Id}");
    }
}

static async Task SaveAndVerifyAsync(
    DependencyMaintenanceContext context,
    ConsistencyRuntime runtime,
    ConsistencyEfCoreMappings mappings,
    ConsistencySaveOptions options,
    Derived<Association, decimal?> unitRate,
    long expectedVersion,
    params (Association Association, decimal? Expected)[] expected)
{
    await context.SaveChangesConsistentlyAsync(runtime, mappings, options);
    RequireEqual(expectedVersion, runtime.Version, "runtime version");

    foreach (var (association, value) in expected)
    {
        RequireEqual(value, association.UnitRate, $"tracked UnitRate for {association.Id}");
        RequireEqual(value, runtime.Get(unitRate, association), $"runtime UnitRate for {association.Id}");
        await using var verification = new DependencyMaintenanceContext(context.Database.GetDbConnection());
        var persisted = verification.Set<Association>().AsNoTracking().Single(x => x.Id == association.Id);
        RequireEqual(value, persisted.UnitRate, $"persisted UnitRate for {association.Id}");
    }
}

static void Require(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException($"Self-check failed: {description}.");
}

static void RequireEqual<T>(T expected, T actual, string description)
{
    Require(EqualityComparer<T>.Default.Equals(expected, actual),
        $"{description}; expected {expected}, actual {actual}");
}

internal sealed class DependencyMaintenanceContext : DbContext
{
    public DependencyMaintenanceContext(DbContextOptions<DependencyMaintenanceContext> options)
        : base(options)
    {
    }

    public DependencyMaintenanceContext(DbConnection connection)
        : base(new DbContextOptionsBuilder<DependencyMaintenanceContext>().UseSqlite(connection).Options)
    {
    }

    public DbSet<SourceItem> SourceItems => Set<SourceItem>();
    public DbSet<TargetItem> TargetItems => Set<TargetItem>();
    public DbSet<Association> Associations => Set<Association>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Association>()
            .HasOne(x => x.SourceItem)
            .WithMany()
            .HasForeignKey(x => x.SourceItemId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<Association>()
            .HasOne(x => x.TargetItem)
            .WithMany()
            .HasForeignKey(x => x.TargetItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SourceItem
{
    public long Id { get; set; }
    public decimal? UnitValue { get; set; }
}

internal sealed class TargetItem
{
    public long Id { get; set; }
    public decimal UnitValue { get; set; }
}

internal sealed class Association
{
    public long Id { get; set; }
    public long SourceItemId { get; set; }
    public SourceItem SourceItem { get; set; } = null!;
    public long TargetItemId { get; set; }
    public TargetItem TargetItem { get; set; } = null!;
    public decimal? UnitRate { get; set; }
}

internal static class UnitRateCalculator
{
    public const int Scale = 6;
    public const decimal MaximumSupportedRate = 9999999999999999999999.999999m;

    public static decimal? Calculate(decimal? sourceValue, decimal targetValue)
    {
        if (sourceValue is null || targetValue == 0m)
            return null;

        decimal roundedRate;
        try
        {
            roundedRate = decimal.Round(
                sourceValue.Value / targetValue,
                Scale,
                MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return null;
        }

        return roundedRate == decimal.MinValue || decimal.Abs(roundedRate) > MaximumSupportedRate
            ? null
            : roundedRate;
    }
}
