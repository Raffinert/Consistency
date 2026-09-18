using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency;
using Raffinert.Consistency.EntityFrameworkCore;

var model = new ConsistencyModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var doubled = model.Derived(values).Select(value => value.Amount * 2)
    .MaterializeTo(value => value.Mirror).Named("doubled");
var invariant = model.Invariant(values).From(doubled).Must((_, amount) => amount <= 6)
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
var mappings = new ConsistencyEfCoreMappings().Map(values).Enforce(invariant);
var work = context.CaptureConsistencyUnitOfWork(runtime, mappings);

await using var transaction = await context.Database.BeginTransactionAsync();
var plan = work.PrepareAndPlan();
if (plan is null)
    return 1;
if (plan.HasInvariantViolations || plan.InvariantEvaluations.Single().State != InvariantEvaluationState.Valid)
    return 1;
if (value.Mirror != 4)
    return 1;

await context.SaveChangesAsync();
await transaction.CommitAsync();
work.CommitAfterDatabaseCommit();
work.Dispatch();
return runtime.Version == 1 ? 0 : 1;

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
    public int Mirror { get; set; }
}

internal sealed class OutboxRow
{
    public long Id { get; set; }
    public string Payload { get; set; } = "";
}
