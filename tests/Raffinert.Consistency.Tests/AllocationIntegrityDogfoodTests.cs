namespace Raffinert.Consistency.Tests;

public sealed class AllocationIntegrityDogfoodTests
{
    [Fact]
    public void Allocation_fulfillment_sum_reports_precommit_violation_and_discard_preserves_runtime()
    {
        var model = new ConsistencyModelBuilder();
        var allocations = model.Objects<Allocation>().Named("allocations").Key(x => x.Id);
        var allocationFulfillments = model.Objects<AllocationFulfillment>().Named("allocationFulfillments").Key(x => x.Id);
        var links = model.Relation(allocations, allocationFulfillments).Where((allocation, allocationFulfillment) => allocation.Id == allocationFulfillment.AllocationId).Named("allocation-allocationFulfillments");
        var sum = model.Derived(allocations).From(links)
            .Sum(x => x.Quantity).Named("allocationFulfillment-sum");
        var balanced = model.Derived(allocations).From(sum)
            .Select((allocation, total) => allocation.Unknown || allocation.AllocatedQuantity == total).Named("allocation-balanced");
        var invariant = model.Invariant(allocations).From(balanced)
            .Must((_, valid) => valid).Named("allocation-balance-invariant");
        var allocation = new Allocation { AllocatedQuantity = 5 };
        var allocationFulfillment = new AllocationFulfillment { AllocationId = allocation.Id, Quantity = 5 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(allocations, [allocation]); seed.Add(allocationFulfillments, [allocationFulfillment]); });
        Assert.True(runtime.Evaluate(invariant, allocation));
        var diagnostics = runtime.Diagnostics;
        allocationFulfillment.Quantity = 4;

        var rejected = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(allocationFulfillments, allocationFulfillment, x => x.Quantity, 5m, 4m))), RuntimeImpactDetailLevel.Causal,
            PlannedInvariantEvaluationMode.Affected);

        Assert.True(rejected.HasInvariantViolations);
        Assert.Equal(0, runtime.Version);
        Assert.Equal(diagnostics.PredicateEvaluations, runtime.Diagnostics.PredicateEvaluations);
        Assert.Equal(diagnostics.DerivedFullRecomputations, runtime.Diagnostics.DerivedFullRecomputations);
        Assert.Equal(InvariantEvaluationState.Valid, runtime.GetState(invariant, allocation));

        allocation.AllocatedQuantity = 4;
        var accepted = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(allocationFulfillments, allocationFulfillment, x => x.Quantity, 5m, 4m),
            Change.Property(allocations, allocation, x => x.AllocatedQuantity, 5m, 4m))),
            RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);
        Assert.False(accepted.HasInvariantViolations);
        runtime.Commit(accepted);
        Assert.Equal(1, runtime.Version);
    }

    [Fact]
    public void Composite_fulfillment_balance_relation_retargets_in_planned_final_state()
    {
        var model = new ConsistencyModelBuilder();
        var allocationFulfillments = model.Objects<AllocationFulfillment>().Named("retarget-allocation-fulfillments").Key(x => x.Id);
        var rows = model.Objects<FulfillmentBalance>().Named("poallocationFulfillments").Key(x => x.Id);
        var matches = model.Relation(allocationFulfillments, rows).Where((allocationFulfillment, row) =>
            allocationFulfillment.OrderLineId == row.OrderLineId && allocationFulfillment.FulfillmentId == row.FulfillmentId)
            .Named("matching-poallocationFulfillment");
        var count = model.Derived(allocationFulfillments).From(matches)
            .Count().Named("matching-poallocationFulfillment-count");
        var invariant = model.Invariant(allocationFulfillments).From(count)
            .Must((_, value) => value > 0).Named("allocationFulfillment-existence-invariant");
        var lineId = Guid.NewGuid();
        var allocationFulfillment = new AllocationFulfillment { AllocationId = Guid.NewGuid(), OrderLineId = lineId, FulfillmentId = 10 };
        var oldRow = new FulfillmentBalance { OrderLineId = lineId, FulfillmentId = 10 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(allocationFulfillments, [allocationFulfillment]); seed.Add(rows, [oldRow]); });
        Assert.True(runtime.Evaluate(invariant, allocationFulfillment));
        allocationFulfillment.FulfillmentId = 11;

        var rejected = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(allocationFulfillments, allocationFulfillment, x => x.FulfillmentId, 10L, 11L))),
            RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);
        Assert.True(rejected.HasInvariantViolations);

        var replacement = new FulfillmentBalance { OrderLineId = lineId, FulfillmentId = 11 };
        var accepted = runtime.PlanDetailed(runtime.Prepare(MutationSet.Create(
            Change.Property(allocationFulfillments, allocationFulfillment, x => x.FulfillmentId, 10L, 11L), Change.Add(rows, replacement))),
            RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);
        Assert.False(accepted.HasInvariantViolations);
    }

    private sealed class Allocation { public Guid Id { get; } = Guid.NewGuid(); public decimal AllocatedQuantity { get; set; } public bool Unknown { get; set; } }
    private sealed class AllocationFulfillment { public Guid Id { get; } = Guid.NewGuid(); public Guid AllocationId { get; init; } public Guid OrderLineId { get; init; } public long FulfillmentId { get; set; } public decimal Quantity { get; set; } }
    private sealed class FulfillmentBalance { public Guid Id { get; } = Guid.NewGuid(); public Guid OrderLineId { get; init; } public long FulfillmentId { get; init; } }
}
