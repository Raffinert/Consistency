using Microsoft.EntityFrameworkCore;
using Raffinert.Relations.EntityFrameworkCore;

namespace Raffinert.Relations.Tests;

public sealed class EndToEndDomainTests
{
    [Fact]
    public void Goods_receipt_update_and_cancellation_affect_only_matching_purchase_order_line()
    {
        var repairs = new List<PurchaseLine>();
        var scenario = BuildScenario(repairs);
        var runtime = scenario.Model.CreateRuntime();
        var firstLine = new PurchaseLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "PO-1",
            ItemNumber = "A",
            OrderedQuantity = 5m
        };
        var otherLine = new PurchaseLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "PO-2",
            ItemNumber = "A",
            OrderedQuantity = 5m
        };
        var receipt = new GoodsReceipt
        {
            Id = Guid.NewGuid(),
            OrderNumber = "PO-1",
            ItemNumber = "A",
            Quantity = 2m
        };
        runtime.Apply(MutationSet.Create(
            Change.Add(scenario.Lines, firstLine),
            Change.Add(scenario.Lines, otherLine),
            Change.Add(scenario.Receipts, receipt)));
        Assert.Equal(2m, runtime.Get(scenario.Received, firstLine));
        Assert.Equal(0m, runtime.Get(scenario.Received, otherLine));
        Assert.True(runtime.Evaluate(scenario.QuantityInvariant, firstLine));
        repairs.Clear();

        receipt.Quantity = 6m;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(scenario.Receipts, receipt, value => value.Quantity, 2m, 6m)));
        var update = application.Result;

        var impact = Assert.Single(Assert.Single(update.DerivedImpacts).Sources);
        Assert.Same(firstLine, impact.Source);
        Assert.Equal(DependencySeverity.Invalid, impact.Severity);
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(scenario.Received, firstLine));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(scenario.Received, otherLine));
        Assert.Single(update.RepairRequests);
        application.Dispatch.Invoke();
        Assert.Equal([firstLine], repairs);
        Assert.Equal(6m, runtime.Get(scenario.Received, firstLine));
        Assert.False(runtime.Evaluate(scenario.QuantityInvariant, firstLine));

        receipt.Cancelled = true;
        runtime.Apply(Change.Property(
            scenario.Receipts, receipt, value => value.Cancelled, false, true));

        Assert.Empty(runtime.Related(scenario.Matches, firstLine));
        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(scenario.Received, firstLine));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(scenario.Received, otherLine));
    }

    [Fact]
    public void Ef_adapter_drives_the_same_goods_receipt_dependency_flow()
    {
        var repairs = new List<PurchaseLine>();
        var scenario = BuildScenario(repairs);
        var runtime = scenario.Model.CreateRuntime();
        var mappings = new RelationUnitOfWorkMappings()
            .Map(scenario.Lines)
            .Map(scenario.Receipts);
        using var context = new PurchasingContext();
        var line = new PurchaseLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "PO-1",
            ItemNumber = "A",
            OrderedQuantity = 5m
        };
        var receipt = new GoodsReceipt
        {
            Id = Guid.NewGuid(),
            OrderNumber = "PO-1",
            ItemNumber = "A",
            Quantity = 2m
        };
        context.AddRange(line, receipt);
        context.SaveChangesAndApply(runtime, mappings);
        Assert.Equal(2m, runtime.Get(scenario.Received, line));
        Assert.True(runtime.Evaluate(scenario.QuantityInvariant, line));
        repairs.Clear();

        receipt.Quantity = 6m;
        context.SaveChangesAndApply(runtime, mappings);

        Assert.Equal(DerivedValueState.Invalid, runtime.GetState(scenario.Received, line));
        Assert.Equal([line], repairs);
    }

    [Fact]
    public void Ef_unit_of_work_exposes_precommit_invariant_violations()
    {
        var scenario = BuildScenario([]);
        var line = new PurchaseLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "PO-guard",
            ItemNumber = "A",
            OrderedQuantity = 5m
        };
        var receipt = new GoodsReceipt
        {
            Id = Guid.NewGuid(),
            OrderNumber = "PO-guard",
            ItemNumber = "A",
            Quantity = 2m
        };
        using var context = new PurchasingContext();
        context.AddRange(line, receipt);
        context.SaveChanges();
        var runtime = scenario.Model.CreateRuntime(seed =>
        {
            seed.Add(scenario.Lines, [line]);
            seed.Add(scenario.Receipts, [receipt]);
        });
        Assert.True(runtime.Evaluate(scenario.QuantityInvariant, line));
        var version = runtime.Version;

        receipt.Quantity = 6m;
        var mappings = new RelationUnitOfWorkMappings().Map(scenario.Lines).Map(scenario.Receipts);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.Affected)!;

        Assert.True(plan.HasInvariantViolations);
        Assert.Equal(InvariantEvaluationState.Violated, Assert.Single(plan.InvariantEvaluations).State);
        Assert.Equal(version, runtime.Version);
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(scenario.QuantityInvariant, line));
        Assert.Equal(2m, context.GoodsReceipts.AsNoTracking().Single().Quantity);
    }

    private static Scenario BuildScenario(List<PurchaseLine> repairs)
    {
        var model = new RelationModelBuilder();
        var lines = model.Objects<PurchaseLine>().Key(value => value.Id);
        var receipts = model.Objects<GoodsReceipt>().Key(value => value.Id);
        var matches = model.Relation(lines, receipts).Where((line, receipt) =>
            line.OrderNumber == receipt.OrderNumber &&
            line.ItemNumber == receipt.ItemNumber &&
            !receipt.Cancelled);
        var received = model.Derived(lines).Using(matches)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid)
                .ItemChanged(DependencySeverity.Invalid))
            .Incrementally()
            .Compute((line, related) => related.Sum(receipt => receipt.Quantity));
        var invariant = model.Invariant(lines).Using(received)
            .Must((line, quantity) => quantity <= line.OrderedQuantity)
            .ScheduleRepairWith(repairs.Add);
        return new Scenario(model.Build(), lines, receipts, matches, received, invariant);
    }

    private sealed record Scenario(
        CompiledRelationModel Model,
        ObjectSet<PurchaseLine> Lines,
        ObjectSet<GoodsReceipt> Receipts,
        Relation<PurchaseLine, GoodsReceipt> Matches,
        Derived<PurchaseLine, decimal> Received,
        Invariant<PurchaseLine> QuantityInvariant);

    private sealed class PurchasingContext : DbContext
    {
        public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseInMemoryDatabase($"purchasing-{Guid.NewGuid()}");

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<PurchaseLine>();
            model.Entity<GoodsReceipt>();
        }
    }

    private sealed class PurchaseLine
    {
        public Guid Id { get; init; }
        public string OrderNumber { get; set; } = "";
        public string ItemNumber { get; set; } = "";
        public decimal OrderedQuantity { get; set; }
    }

    private sealed class GoodsReceipt
    {
        public Guid Id { get; init; }
        public string OrderNumber { get; set; } = "";
        public string ItemNumber { get; set; } = "";
        public decimal Quantity { get; set; }
        public bool Cancelled { get; set; }
    }
}
