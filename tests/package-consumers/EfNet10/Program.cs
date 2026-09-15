using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Relations;
using Raffinert.Relations.EntityFrameworkCore;

var model = new RelationModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var runtime = model.Build().CreateRuntime();
await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
await using var context = new ConsumerContext(connection);
await context.Database.EnsureCreatedAsync();
var value = new Value();
context.Add(value);
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(
    context.ChangeTracker, new RelationUnitOfWorkMappings().Map(values));
unit.Prepare(runtime);

await using var transaction = await context.Database.BeginTransactionAsync();
await context.SaveChangesAsync();

// PreviewDetailed is a non-binding diagnostic. PlanDetailed is the binding outbox contract.
var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal);
if (plan is null)
    return 1;
context.Outbox.Add(new OutboxRow { Payload = $"origins:{plan.Result.MutationOrigins.Count}" });
await context.SaveChangesAsync();

await transaction.CommitAsync();
unit.Commit(runtime);
unit.Dispatch(runtime);
return plan.IsCommitted && context.Outbox.Single().Payload == "origins:1" && runtime.Version == 1 ? 0 : 1;

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
}

internal sealed class OutboxRow
{
    public long Id { get; set; }
    public string Payload { get; set; } = "";
}
