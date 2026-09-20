using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raffinert.Consistency;
using Raffinert.Consistency.EntityFrameworkCore;

var model = new ConsistencyModelBuilder();
var values = model.Objects<Value>().Named("values").Key(value => value.Id);
var doubled = model.Derived(values).Select(value => value.Amount * 2)
    .MaterializeTo(value => value.Mirror).Named("doubled");
var invariant = model.Invariant(values).From(doubled).Must((_, amount) => amount <= 6)
    .RepairWhenViolated().Named("repair");
await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();
var contextOptions = new DbContextOptionsBuilder<ConsumerContext>().UseSqlite(connection).Options;
await using var context = new ConsumerContext(contextOptions);
await context.Database.EnsureCreatedAsync();
var value = new Value { Amount = 3 };
context.Add(value);
await context.SaveChangesAsync();
var compiled = model.Build();
var runtime = compiled.CreateRuntime(seed => seed.Add(values, [value]));
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

var services = new ServiceCollection();
services.AddDbContext<ConsumerContext>(options => options.UseSqlite(connection));
services.AddRaffinertConsistency<ConsumerContext>(compiled, mappings);
await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions
             {
                 ValidateOnBuild = true,
                 ValidateScopes = true
             }))
await using (var scope = provider.CreateAsyncScope())
{
    var concrete = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
    var application = scope.ServiceProvider.GetRequiredService<IConsistencyRuntime>();
    var repeated = scope.ServiceProvider.GetRequiredService<IConsistencyRuntime>();
    if (!ReferenceEquals(concrete, application) || !ReferenceEquals(application, repeated))
        return 1;

    var injectedContext = scope.ServiceProvider.GetRequiredService<ConsumerContext>();
    var injectedValue = await injectedContext.Values.SingleAsync();
    injectedValue.Amount = 1;
    application.Materialize(injectedValue);
    if (injectedValue.Mirror != 2)
        return 1;
    await injectedContext.SaveChangesAsync();
}

await using var verification = new ConsumerContext(contextOptions);
var persisted = await verification.Values.AsNoTracking().SingleAsync();
return runtime.Version == 1 && persisted.Amount == 1 && persisted.Mirror == 2 ? 0 : 1;

internal sealed class ConsumerContext(DbContextOptions<ConsumerContext> options) : DbContext(options)
{
    public DbSet<Value> Values => Set<Value>();
    public DbSet<OutboxRow> Outbox => Set<OutboxRow>();
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
