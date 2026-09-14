using Microsoft.EntityFrameworkCore;
using Raffinert.Relations;
using Raffinert.Relations.EntityFrameworkCore;

var model = new RelationModelBuilder();
var values = model.Objects<Value>().Key(value => value.Id);
var runtime = model.Build().CreateRuntime();
await using var context = new ConsumerContext();
var value = new Value();
context.Add(value);
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(
    context.ChangeTracker, new RelationUnitOfWorkMappings().Map(values));
unit.Prepare(runtime);
var result = unit.CommitDetailed(runtime, RuntimeImpactDetailLevel.Causal);
unit.Dispatch(runtime);
return result is not null && runtime.Version == 1 ? 0 : 1;

internal sealed class ConsumerContext : DbContext
{
    public DbSet<Value> Values => Set<Value>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseInMemoryDatabase("packed-consumer");
}
internal sealed class Value
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
