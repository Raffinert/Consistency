using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency;
using Raffinert.Consistency.EntityFrameworkCore;

var model = new ConsistencyModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var doubled = model.Derived(values).Compute(value => value.Amount * 2).Named("doubled");
model.Invariant(values).Using(doubled).Must((_, amount) => amount <= 6)
    .ScheduleRepairWith(_ => { }).Named("repair");
await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
await using var context = new ConsumerContext(connection);
await context.Database.EnsureCreatedAsync();
var value = new Value { Amount = 3 };
context.Add(value);
await context.SaveChangesAsync();
var runtime = model.Build().CreateRuntime(seed => seed.Add(values, [value]));
value.Amount = 2;
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(
    context.ChangeTracker, new RelationUnitOfWorkMappings().Map(values));
unit.Prepare(runtime);

await using var transaction = await context.Database.BeginTransactionAsync();
await context.SaveChangesAsync();

// PreviewDetailed is a non-binding diagnostic. PlanDetailed is the binding outbox contract.
var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal,
    PlannedInvariantEvaluationMode.Affected);
if (plan is null)
    return 1;
if (plan.HasInvariantViolations || plan.InvariantEvaluations.Single().State != InvariantEvaluationState.Valid)
    return 1;

await transaction.CommitAsync();
unit.Commit(runtime);
unit.Dispatch(runtime);
return plan.IsCommitted && runtime.Version == 1 ? 0 : 1;

internal sealed class ConsumerContext(SqliteConnection connection) : DbContext
{
    public DbSet<Value> Values => Set<Value>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseSqlite(connection);
}
internal sealed class Value
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Amount { get; set; }
}

internal sealed class OutboxRow
{
    public long Id { get; set; }
    public string Payload { get; set; } = "";
}
