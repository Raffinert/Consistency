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
var remaining = builder.Derived(lines).Select(x => x.OrderedQuantity - x.FulfilledQuantity)
    .MaterializeTo(x => x.RemainingQuantity);
var nonNegativeRemaining = builder.Invariant(lines).From(remaining).Must((_, value) => value >= 0);
var runtime = builder.Build().CreateRuntime(seed => seed.Add(lines, [line]));
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
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

var generatedBuilder = new ConsistencyModelBuilder();
var generatedOrders = generatedBuilder.Objects<GeneratedOrder>().Key(x => x.Id);
var doubledAmount = generatedBuilder.Derived(generatedOrders).Select(x => x.Amount * 2)
    .MaterializeTo(x => x.AmountMirror);
var generatedRuntime = generatedBuilder.Build().CreateRuntime();
var generatedMappings = new ConsistencyEfCoreMappings().Map(generatedOrders);
var generated = new GeneratedOrder { Amount = 7 };
context.Add(generated);
var work = context.CaptureConsistencyUnitOfWork(generatedRuntime, generatedMappings);

using (var transaction = context.Database.BeginTransaction())
{
    context.SaveChanges(); // Finalize the generated consistency key.
    _ = work.PrepareAndPlan();
    context.SaveChanges(); // Persist the calculated mirror.
    transaction.Commit();
}
work.CommitAfterDatabaseCommit();
work.Dispatch();
Console.WriteLine($"Manual generated-key workflow persisted mirror={generated.AmountMirror}.");

internal sealed class OrdersContext(DbContextOptions<OrdersContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderLine>();
        modelBuilder.Entity<GeneratedOrder>().Property(x => x.Id).ValueGeneratedOnAdd();
    }
}

internal sealed class GeneratedOrder
{
    public int Id { get; set; }
    public int Amount { get; set; }
    public int AmountMirror { get; set; }
}

internal sealed class OrderLine
{
    public Guid Id { get; init; }
    public int OrderedQuantity { get; set; }
    public int FulfilledQuantity { get; set; }
    public int RemainingQuantity { get; set; }
}
