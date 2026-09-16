using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency;
using Raffinert.Consistency.EntityFrameworkCore;

RequireEqual<decimal?>(null, UnitRateCalculator.Calculate(null, 4m), "null source");
RequireEqual<decimal?>(null, UnitRateCalculator.Calculate(10m, 0m), "zero target");
RequireEqual(2.5m, UnitRateCalculator.Calculate(10m, 4m), "basic rate");
RequireEqual(0.333333m, UnitRateCalculator.Calculate(1m, 3m), "rounded rate");
RequireEqual<decimal?>(null, UnitRateCalculator.Calculate(decimal.MaxValue, 1m), "maximum supported rate");
RequireEqual<decimal?>(null, UnitRateCalculator.Calculate(decimal.MaxValue, 0.1m), "division overflow");

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
var options = new DbContextOptionsBuilder<DependencyMaintenanceContext>()
    .UseSqlite(connection)
    .Options;
using var context = new DependencyMaintenanceContext(options);
context.Database.EnsureCreated();

var sourceA = new SourceItem { Id = 1, UnitValue = 12m };
var sourceB = new SourceItem { Id = 2, UnitValue = 30m };
var targetA = new TargetItem { Id = 10, UnitValue = 4m };
var targetB = new TargetItem { Id = 11, UnitValue = 5m };
var associationA = new Association { Id = 100, SourceItem = sourceA, TargetItem = targetA };
var associationB = new Association { Id = 101, SourceItem = sourceA, TargetItem = targetB };
associationA.UnitRate = UnitRateCalculator.Calculate(sourceA.UnitValue, targetA.UnitValue);
associationB.UnitRate = UnitRateCalculator.Calculate(sourceA.UnitValue, targetB.UnitValue);
context.AddRange(sourceA, sourceB, targetA, targetB, associationA, associationB);
context.SaveChanges();

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
SaveAndVerify(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 5m), (associationB, 4.0m));

// A target mutation remains selective to its consumers.
targetA.UnitValue = 10m;
SaveAndVerify(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2m), (associationB, 4.0m));

// Retargeting moves the reverse-navigation dependency from source A to source B.
associationB.SourceItem = sourceB;
context.ChangeTracker.DetectChanges();
RequireEqual(sourceB.Id, associationB.SourceItemId, "relationship fixup");
SaveAndVerify(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2m), (associationB, 6m));

sourceA.UnitValue = 25m;
SaveAndVerify(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2.5m), (associationB, 6m));

sourceB.UnitValue = null;
SaveAndVerify(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2.5m), (associationB, null));

sourceB.UnitValue = 30m;
targetB.UnitValue = 0m;
SaveAndVerify(context, runtime, mappings, saveOptions, unitRate,
    expectedVersion: ++expectedVersion,
    (associationA, 2.5m), (associationB, null));

Console.WriteLine("Dependency-maintenance dogfood sample passed:");
Console.WriteLine("- shared source changes updated all affected association mirrors");
Console.WriteLine("- target changes preserved correct mirrors for other associations");
Console.WriteLine("- retargeted associations followed their new source");
Console.WriteLine("- null/zero semantics were materialized consistently");

static void SaveAndVerify(
    DependencyMaintenanceContext context,
    ConsistencyRuntime runtime,
    ConsistencyEfCoreMappings mappings,
    ConsistencySaveOptions options,
    Derived<Association, decimal?> unitRate,
    long expectedVersion,
    params (Association Association, decimal? Expected)[] expected)
{
    context.SaveChangesConsistently(runtime, mappings, options);
    RequireEqual(expectedVersion, runtime.Version, "runtime version");

    foreach (var (association, value) in expected)
    {
        RequireEqual(value, association.UnitRate, $"tracked UnitRate for {association.Id}");
        RequireEqual(value, runtime.Get(unitRate, association), $"runtime UnitRate for {association.Id}");
        using var verification = new DependencyMaintenanceContext(context.Database.GetDbConnection());
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

    public DependencyMaintenanceContext(System.Data.Common.DbConnection connection)
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
