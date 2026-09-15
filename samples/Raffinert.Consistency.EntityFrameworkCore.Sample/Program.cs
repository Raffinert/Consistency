using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency;
using Raffinert.Consistency.EntityFrameworkCore;

using var connection = new SqliteConnection("Data Source=:memory:");
connection.Open();
var options = new DbContextOptionsBuilder<OrdersContext>().UseSqlite(connection).Options;
using var context = new OrdersContext(options);
context.Database.EnsureCreated();

var line = new PurchaseOrderLine
{
    Id = Guid.NewGuid(),
    OrderedQuantity = 10,
    ReceivedQuantity = 2,
    AvailableQuantity = 8
};
context.Add(line);
context.SaveChanges();

var builder = new ConsistencyModelBuilder();
var lines = builder.Objects<PurchaseOrderLine>().Key(x => x.Id);
var available = builder.Derived(lines).Compute(x => x.OrderedQuantity - x.ReceivedQuantity);
var availability = builder.Invariant(lines).Using(available).Must((_, value) => value >= 0);
var runtime = builder.Build().CreateRuntime(seed => seed.Add(lines, [line]));
var mappings = new RelationEfCoreMappings()
    .Map(lines)
    .Materialize(available, x => x.AvailableQuantity)
    .Enforce(availability);

line.ReceivedQuantity = 12;
try
{
    context.SaveChangesConsistently(runtime, mappings);
    throw new InvalidOperationException("The enforced invariant should have rejected this save.");
}
catch (RelationInvariantViolationException)
{
    Console.WriteLine("Rejected invalid receipt before SQL; database and runtime remain unchanged.");
}

line.ReceivedQuantity = 4;
context.SaveChangesConsistently(runtime, mappings);
var persisted = context.Set<PurchaseOrderLine>().AsNoTracking().Single();
Console.WriteLine($"Persisted AvailableQuantity={persisted.AvailableQuantity}; runtime version={runtime.Version}.");

internal sealed class OrdersContext(DbContextOptions<OrdersContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<PurchaseOrderLine>();
}

internal sealed class PurchaseOrderLine
{
    public Guid Id { get; init; }
    public int OrderedQuantity { get; set; }
    public int ReceivedQuantity { get; set; }
    public int AvailableQuantity { get; set; }
}
