using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class EndToEndDomainTests
{
    [Fact]
    public void Fulfillment_update_and_cancellation_affect_only_matching_order_line()
    {
        var repairs = new List<OrderLine>();
        var scenario = BuildScenario(repairs);
        var runtime = scenario.Model.CreateRuntime();
        var firstLine = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER-1",
            ItemNumber = "A",
            OrderedQuantity = 5m
        };
        var otherLine = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER-2",
            ItemNumber = "A",
            OrderedQuantity = 5m
        };
        var fulfillment = new Fulfillment
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER-1",
            ItemNumber = "A",
            Quantity = 2m
        };
        runtime.Apply(MutationSet.Create(
            Change.Add(scenario.Lines, firstLine),
            Change.Add(scenario.Lines, otherLine),
            Change.Add(scenario.Fulfillments, fulfillment)));
        Assert.Equal(2m, runtime.Evaluate(scenario.Fulfilled, firstLine));
        Assert.Equal(0m, runtime.Evaluate(scenario.Fulfilled, otherLine));
        Assert.True(runtime.Evaluate(scenario.QuantityInvariant, firstLine));
        repairs.Clear();

        fulfillment.Quantity = 6m;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(scenario.Fulfillments, fulfillment, value => value.Quantity, 2m, 6m)));
        var update = application.Result;

        var impact = Assert.Single(Assert.Single(update.DerivedImpacts).Sources);
        Assert.Same(firstLine, impact.Source);
        Assert.Equal(DependencySeverity.Invalid, impact.Severity);
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(scenario.Fulfilled, firstLine));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(scenario.Fulfilled, otherLine));
        Assert.Single(update.RepairRequests);
        application.Dispatch.Invoke();
        Assert.Empty(repairs);
        Assert.Equal(6m, runtime.Evaluate(scenario.Fulfilled, firstLine));
        Assert.False(runtime.Evaluate(scenario.QuantityInvariant, firstLine));

        fulfillment.Cancelled = true;
        runtime.Apply(Change.Property(
            scenario.Fulfillments, fulfillment, value => value.Cancelled, false, true));

        Assert.Empty(runtime.Related(scenario.Matches, firstLine));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(scenario.Fulfilled, firstLine));
        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(scenario.Fulfilled, otherLine));
    }

    [Fact]
    public void Ef_adapter_drives_the_same_fulfillment_dependency_flow()
    {
        var repairs = new List<OrderLine>();
        var scenario = BuildScenario(repairs);
        var runtime = scenario.Model.CreateRuntime();
        var mappings = new ConsistencyUnitOfWorkMappings()
            .Map(scenario.Lines)
            .Map(scenario.Fulfillments);
        using var context = new FulfillmentContext();
        var line = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER-1",
            ItemNumber = "A",
            OrderedQuantity = 5m
        };
        var fulfillment = new Fulfillment
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER-1",
            ItemNumber = "A",
            Quantity = 2m
        };
        context.AddRange(line, fulfillment);
        context.SaveChangesAndApply(runtime, mappings);
        Assert.Equal(2m, runtime.Evaluate(scenario.Fulfilled, line));
        Assert.True(runtime.Evaluate(scenario.QuantityInvariant, line));
        repairs.Clear();

        fulfillment.Quantity = 6m;
        context.SaveChangesAndApply(runtime, mappings);

        Assert.Equal(DerivedValueState.Fresh, runtime.GetState(scenario.Fulfilled, line));
        Assert.Empty(repairs);
    }

    [Fact]
    public void Ef_unit_of_work_exposes_precommit_invariant_violations()
    {
        var scenario = BuildScenario([]);
        var line = new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER-guard",
            ItemNumber = "A",
            OrderedQuantity = 5m
        };
        var fulfillment = new Fulfillment
        {
            Id = Guid.NewGuid(),
            OrderNumber = "ORDER-guard",
            ItemNumber = "A",
            Quantity = 2m
        };
        using var context = new FulfillmentContext();
        context.AddRange(line, fulfillment);
        context.SaveChanges();
        var runtime = scenario.Model.CreateRuntime(seed =>
        {
            seed.Add(scenario.Lines, [line]);
            seed.Add(scenario.Fulfillments, [fulfillment]);
        });
        Assert.True(runtime.Evaluate(scenario.QuantityInvariant, line));
        var version = runtime.Version;

        fulfillment.Quantity = 6m;
        var mappings = new ConsistencyUnitOfWorkMappings().Map(scenario.Lines).Map(scenario.Fulfillments);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.Affected)!;

        Assert.True(plan.HasInvariantViolations);
        Assert.Equal(InvariantEvaluationState.Violated, Assert.Single(plan.InvariantEvaluations).State);
        Assert.Equal(version, runtime.Version);
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(scenario.QuantityInvariant, line));
        Assert.Equal(2m, context.Fulfillments.AsNoTracking().Single().Quantity);
    }

    [Fact]
    public void Ef_unit_of_work_forwards_derived_evaluation_mode()
    {
        var scenario = BuildScenario([]); var line = new OrderLine { Id = Guid.NewGuid(), OrderNumber = "ORDER", ItemNumber = "A", OrderedQuantity = 5 };
        var fulfillment = new Fulfillment { Id = Guid.NewGuid(), OrderNumber = "ORDER", ItemNumber = "A", Quantity = 2 };
        using var context = new FulfillmentContext(); context.AddRange(line, fulfillment); context.SaveChanges();
        var runtime = scenario.Model.CreateRuntime(seed => { seed.Add(scenario.Lines, [line]); seed.Add(scenario.Fulfillments, [fulfillment]); });
        fulfillment.Quantity = 3;
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker,
            new ConsistencyUnitOfWorkMappings().Map(scenario.Lines).Map(scenario.Fulfillments));
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Summary,
            PlannedInvariantEvaluationMode.Affected, PlannedDerivedEvaluationMode.Affected)!;
        Assert.Contains(plan.DerivedEvaluations, value => ReferenceEquals(value.Source, line) && Equals(value.Value, 3m));
        Assert.NotEmpty(plan.InvariantEvaluations);
    }

    private static Scenario BuildScenario(List<OrderLine> repairs)
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<OrderLine>().Key(value => value.Id);
        var fulfillments = model.Objects<Fulfillment>().Key(value => value.Id);
        var matches = model.Relation(lines, fulfillments).Where((line, fulfillment) =>
            line.OrderNumber == fulfillment.OrderNumber &&
            line.ItemNumber == fulfillment.ItemNumber &&
            !fulfillment.Cancelled);
        var received = model.Derived(lines).From(matches)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid)
                .ItemChanged(DependencySeverity.Invalid))
            .Sum(fulfillment => fulfillment.Quantity);
        var invariant = model.Invariant(lines).From(received)
            .Must((line, quantity) => quantity <= line.OrderedQuantity)
            .RepairWhenViolated();
        return new Scenario(model.Build(), lines, fulfillments, matches, received, invariant);
    }

    private sealed record Scenario(
        CompiledConsistencyModel Model,
        ObjectSet<OrderLine> Lines,
        ObjectSet<Fulfillment> Fulfillments,
        Relation<OrderLine, Fulfillment> Matches,
        Derived<OrderLine, decimal> Fulfilled,
        Invariant<OrderLine> QuantityInvariant);

    private sealed class FulfillmentContext : DbContext
    {
        public DbSet<Fulfillment> Fulfillments => Set<Fulfillment>();
        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options.UseInMemoryDatabase($"fulfillment-{Guid.NewGuid()}");

        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<OrderLine>();
            model.Entity<Fulfillment>();
        }
    }

    private sealed class OrderLine
    {
        public Guid Id { get; init; }
        public string OrderNumber { get; set; } = "";
        public string ItemNumber { get; set; } = "";
        public decimal OrderedQuantity { get; set; }
    }

    private sealed class Fulfillment
    {
        public Guid Id { get; init; }
        public string OrderNumber { get; set; } = "";
        public string ItemNumber { get; set; } = "";
        public decimal Quantity { get; set; }
        public bool Cancelled { get; set; }
    }
}
