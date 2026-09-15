using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency;
using Raffinert.Consistency.EntityFrameworkCore;

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
var options = new DbContextOptionsBuilder<OrdersContext>().UseSqlite(connection).Options;
using var context = new OrdersContext(options);
context.Database.EnsureCreated();

var line = new OrderLine
{
    Id = Guid.NewGuid(),
    OrderedQuantity = 10,
    FulfilledQuantity = 2,
    RemainingQuantity = 8
};
context.Add(line);
context.SaveChanges();

var builder = new ConsistencyModelBuilder();
var lines = builder.Objects<OrderLine>().Key(x => x.Id);
var remaining = builder.Derived(lines).Compute(x => x.OrderedQuantity - x.FulfilledQuantity);
var nonNegativeRemaining = builder.Invariant(lines).Using(remaining).Must((_, value) => value >= 0);
var runtime = builder.Build().CreateRuntime(seed => seed.Add(lines, [line]));
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Materialize(remaining, x => x.RemainingQuantity)
    .Enforce(nonNegativeRemaining);

line.FulfilledQuantity = 12;
try
{
    context.SaveChangesConsistently(runtime, mappings);
    throw new InvalidOperationException("The enforced invariant should have rejected this save.");
}
catch (ConsistencyInvariantViolationException)
{
    Console.WriteLine("Rejected invalid fulfillment before SQL; database and runtime remain unchanged.");
}

line.FulfilledQuantity = 4;
context.SaveChangesConsistently(runtime, mappings);
var persisted = context.Set<OrderLine>().AsNoTracking().Single();
Console.WriteLine($"Persisted RemainingQuantity={persisted.RemainingQuantity}; runtime version={runtime.Version}.");

internal sealed class OrdersContext(DbContextOptions<OrdersContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<OrderLine>();
}

internal sealed class OrderLine
{
    public Guid Id { get; init; }
    public int OrderedQuantity { get; set; }
    public int FulfilledQuantity { get; set; }
    public int RemainingQuantity { get; set; }
}
