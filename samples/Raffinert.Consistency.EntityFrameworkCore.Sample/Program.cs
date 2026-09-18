using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raffinert.Consistency;
using Raffinert.Consistency.EntityFrameworkCore;

await using var connection = new SqliteConnection("Data Source=:memory:");
await connection.OpenAsync();

var builder = new ConsistencyModelBuilder();
var lines = builder.Objects<OrderLine>().Key(x => x.Id);
var remaining = builder.Derived(lines)
    .Select(x => x.OrderedQuantity - x.FulfilledQuantity)
    .MaterializeTo(x => x.RemainingQuantity)
    .Named("remaining-quantity");
var nonNegativeRemaining = builder.Invariant(lines)
    .From(remaining)
    .Must((_, value) => value >= 0)
    .Named("non-negative-remaining");
var compiled = builder.Build();
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Enforce(nonNegativeRemaining);

await using (var seed = new OrdersContext(
                 new DbContextOptionsBuilder<OrdersContext>().UseSqlite(connection).Options))
{
    await seed.Database.EnsureCreatedAsync();
    seed.Add(new OrderLine
    {
        Id = Guid.NewGuid(),
        OrderedQuantity = 10,
        FulfilledQuantity = 2,
        RemainingQuantity = 8
    });
    await seed.SaveChangesAsync();
}

var services = new ServiceCollection();
services.AddDbContext<OrdersContext>(options => options.UseSqlite(connection));
services.AddRaffinertConsistency<OrdersContext>(compiled, mappings);
services.AddScoped<OrderLineService>();
await using var provider = services.BuildServiceProvider();

await using (var scope = provider.CreateAsyncScope())
{
    var service = scope.ServiceProvider.GetRequiredService<OrderLineService>();
    var id = await scope.ServiceProvider.GetRequiredService<OrdersContext>().OrderLines
        .Select(x => x.Id)
        .SingleAsync();

    try
    {
        await service.ChangeFulfilledQuantityAsync(id, 12, CancellationToken.None);
    }
    catch (ConsistencyInvariantViolationException)
    {
        Console.WriteLine("Rejected invalid fulfillment before SQL; database and runtime remain unchanged.");
    }

    await service.ChangeFulfilledQuantityAsync(id, 4, CancellationToken.None);
    var runtimeVersion = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>().Version;
    Console.WriteLine($"Observed RemainingQuantity={service.ObservedRemaining}; runtime version={runtimeVersion}.");
}

await using (var verification = new OrdersContext(
                 new DbContextOptionsBuilder<OrdersContext>().UseSqlite(connection).Options))
{
    var persisted = await verification.OrderLines.AsNoTracking().SingleAsync();
    Console.WriteLine($"Persisted RemainingQuantity={persisted.RemainingQuantity}.");
}

public sealed class OrderLineService(OrdersContext db, ConsistencyRuntime consistency)
{
    public int ObservedRemaining { get; private set; }

    public async Task ChangeFulfilledQuantityAsync(
        Guid id,
        int fulfilledQuantity,
        CancellationToken cancellationToken)
    {
        var line = await db.OrderLines.SingleAsync(x => x.Id == id, cancellationToken);
        line.FulfilledQuantity = fulfilledQuantity;
        consistency.Materialize(line);
        ObservedRemaining = line.RemainingQuantity;
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class OrdersContext(DbContextOptions<OrdersContext> options) : DbContext(options)
{
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
}

public sealed class OrderLine
{
    public Guid Id { get; set; }
    public int OrderedQuantity { get; set; }
    public int FulfilledQuantity { get; set; }
    public int RemainingQuantity { get; set; }
}
