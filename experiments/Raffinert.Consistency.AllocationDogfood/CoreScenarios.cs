namespace Raffinert.Consistency.AllocationDogfood;

internal static class CoreScenarios
{
    public static void CandidateSupply_IsFound_WhenResourceAndDateMatch()
    {
        var fixture = CoreFixture.Create();
        var candidates = fixture.Runtime.Related(fixture.Model.CandidateSupplies, fixture.Demand1);

        ScenarioAssert.Equal(2, candidates.Count, "Both matching supplies must be candidates.");
        ScenarioAssert.True(candidates.Contains(fixture.Supply1), "S1 must be a candidate.");
        ScenarioAssert.True(candidates.Contains(fixture.Supply2), "S2 must be a candidate.");
        ScenarioAssert.False(candidates.Contains(fixture.UnrelatedSupply),
            "A different resource must not be a candidate.");

        var diagnostic = fixture.Model.Compiled.Diagnostics.Relations
            .Single(value => value.DefinitionKey == "candidate-supplies");
        ScenarioAssert.Equal(RelationAccessPlanKind.HashJoin, diagnostic.AccessPlan,
            "Candidate lookup should use an analyzed hash join.");
        ScenarioAssert.Equal(RelationPropagationPlanKind.ExactMaterialized, diagnostic.PropagationPlan,
            "Projected membership must reuse exact candidate-relation propagation state.");
    }

    public static void CandidateSupply_IsRemoved_WhenResourceStopsMatching()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply1.ResourceCode = "B";

        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Supplies, fixture.Supply1,
                value => value.ResourceCode, "A", "B")), RuntimeImpactDetailLevel.Causal);

        ScenarioAssert.False(fixture.Runtime.Related(
            fixture.Model.CandidateSupplies, fixture.Demand1).Contains(fixture.Supply1),
            "A resource change must remove the old candidate pair.");
        ScenarioAssert.True(application.Result.RelationImpacts
            .Single(value => value.DefinitionKey == "candidate-supplies")
            .RemovedPairs.Count == 1,
            "Detailed impact must expose the removed compatibility pair.");
        ScenarioAssert.Equal(DerivedValueState.Fresh,
            fixture.Runtime.GetState(fixture.Model.HasCompatibleSupply, fixture.Allocation1!),
            "Repair-enabled compatibility must be evaluated after the relation change.");
    }

    public static void CandidateRelations_TrackBothSidesAndIgnoreUnrelatedChanges()
    {
        var fixture = CoreFixture.Create();
        var original = fixture.Runtime.Related(fixture.Model.CandidateSupplies, fixture.Demand1).ToArray();
        fixture.Demand1.RequestedQuantity = 7m;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Demands, fixture.Demand1,
            value => value.RequestedQuantity, 5m, 7m));
        ScenarioAssert.Equal(original.Length,
            fixture.Runtime.Related(fixture.Model.CandidateSupplies, fixture.Demand1).Count,
            "An unrelated quantity change must preserve candidate membership.");

        fixture.Demand1.ResourceCode = "B";
        fixture.Runtime.Apply(Change.Property(fixture.Model.Demands, fixture.Demand1,
            value => value.ResourceCode, "A", "B"));
        var movedCandidates = fixture.Runtime.Related(
            fixture.Model.CandidateSupplies, fixture.Demand1).ToArray();
        ScenarioAssert.Equal(1, movedCandidates.Length,
            "Changing the demand resource must replace candidate membership.");
        ScenarioAssert.Same(fixture.UnrelatedSupply, movedCandidates[0],
            "The supply for the new resource must become the sole candidate.");

        fixture.UnrelatedSupply.Date = fixture.Day.AddDays(1);
        fixture.Runtime.Apply(Change.Property(fixture.Model.Supplies, fixture.UnrelatedSupply,
            value => value.Date, fixture.Day, fixture.Day.AddDays(1)));
        ScenarioAssert.Equal(0,
            fixture.Runtime.Related(fixture.Model.CandidateSupplies, fixture.Demand1).Count,
            "Changing the matching supply date must remove reverse membership.");
    }

    public static void FulfilledQuantity_TracksFulfillmentChanges()
    {
        var fixture = CoreFixture.Create(includeAllocation: false, includeFulfillment: false);
        ScenarioAssert.Equal(0m, fixture.Runtime.Evaluate(
            fixture.Model.FulfilledQuantity, fixture.Supply1), "No rows must aggregate to zero.");

        var first = new Fulfillment { Id = 10, SupplyId = 1, Supply = fixture.Supply1, Quantity = 3m };
        fixture.Fulfillments.Add(first);
        fixture.Runtime.Apply(MutationSet.Create(Change.Add(fixture.Model.Fulfillments, first)));
        ScenarioAssert.Equal(3m, fixture.Runtime.Evaluate(
            fixture.Model.FulfilledQuantity, fixture.Supply1), "Adding fulfillment must increase the sum.");

        first.Quantity = 5m;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Fulfillments, first,
            value => value.Quantity, 3m, 5m));
        ScenarioAssert.Equal(5m, fixture.Runtime.Evaluate(
            fixture.Model.FulfilledQuantity, fixture.Supply1), "Increasing quantity must increase the sum.");

        first.Quantity = 2m;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Fulfillments, first,
            value => value.Quantity, 5m, 2m));
        ScenarioAssert.Equal(2m, fixture.Runtime.Evaluate(
            fixture.Model.FulfilledQuantity, fixture.Supply1), "Decreasing quantity must decrease the sum.");

        var unrelated = new Fulfillment
        {
            Id = 11,
            SupplyId = 2,
            Supply = fixture.Supply2,
            Quantity = 7m
        };
        fixture.Fulfillments.Add(unrelated);
        fixture.Runtime.Apply(MutationSet.Create(Change.Add(fixture.Model.Fulfillments, unrelated)));
        ScenarioAssert.Equal(2m, fixture.Runtime.Evaluate(
            fixture.Model.FulfilledQuantity, fixture.Supply1), "Another supply must remain isolated.");

        first.SupplyId = 2;
        first.Supply = fixture.Supply2;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Fulfillments, first,
            value => value.SupplyId, 1, 2));
        ScenarioAssert.Equal(0m, fixture.Runtime.Evaluate(
            fixture.Model.FulfilledQuantity, fixture.Supply1), "Moving a row must debit the old supply.");
        ScenarioAssert.Equal(9m, fixture.Runtime.Evaluate(
            fixture.Model.FulfilledQuantity, fixture.Supply2), "Moving a row must credit the new supply.");

        fixture.Runtime.Apply(MutationSet.Create(Change.Remove(fixture.Model.Fulfillments, first)));
        ScenarioAssert.Equal(7m, fixture.Runtime.Evaluate(
            fixture.Model.FulfilledQuantity, fixture.Supply2), "Removing fulfillment must decrease the sum.");
        AssertIncrementalSum(fixture.Model, "fulfilled-quantity");
    }

    public static void AllocatedQuantity_TracksAllocationChanges()
    {
        var fixture = CoreFixture.Create(includeAllocation: false, includeFulfillment: false);
        ScenarioAssert.Equal(0m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply1), "No rows must aggregate to zero.");

        var first = new Allocation
        {
            Id = 10,
            DemandId = fixture.Demand1.Id,
            Demand = fixture.Demand1,
            SupplyId = fixture.Supply1.Id,
            Supply = fixture.Supply1,
            Quantity = 3m
        };
        fixture.Allocations.Add(first);
        fixture.Runtime.Apply(MutationSet.Create(Change.Add(fixture.Model.Allocations, first)));
        ScenarioAssert.Equal(3m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply1), "Adding allocation must increase the sum.");

        first.Quantity = 5m;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Allocations, first,
            value => value.Quantity, 3m, 5m));
        ScenarioAssert.Equal(5m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply1), "Increasing quantity must increase the sum.");

        first.Quantity = 2m;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Allocations, first,
            value => value.Quantity, 5m, 2m));
        ScenarioAssert.Equal(2m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply1), "Decreasing quantity must decrease the sum.");

        var unrelated = new Allocation
        {
            Id = 11,
            DemandId = fixture.Demand1.Id,
            Demand = fixture.Demand1,
            SupplyId = fixture.Supply2.Id,
            Supply = fixture.Supply2,
            Quantity = 7m
        };
        fixture.Allocations.Add(unrelated);
        fixture.Runtime.Apply(MutationSet.Create(Change.Add(fixture.Model.Allocations, unrelated)));
        ScenarioAssert.Equal(2m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply1), "Another supply must remain isolated.");

        first.SupplyId = 2;
        first.Supply = fixture.Supply2;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Allocations, first,
            value => value.SupplyId, 1, 2));
        ScenarioAssert.Equal(0m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply1), "Moving a row must debit the old supply.");
        ScenarioAssert.Equal(9m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply2), "Moving a row must credit the new supply.");

        fixture.Runtime.Apply(MutationSet.Create(Change.Remove(fixture.Model.Allocations, first)));
        ScenarioAssert.Equal(7m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply2), "Removing allocation must decrease the sum.");
        AssertIncrementalSum(fixture.Model, "allocated-quantity");
    }

    public static void RemainingCapacity_UsesDerivedFulfilledAndAllocatedValues()
    {
        var fixture = CoreFixture.Create();
        fixture.Runtime.ResetDiagnostics();

        ScenarioAssert.Equal(2m, fixture.Runtime.Evaluate(
            fixture.Model.RemainingCapacity, fixture.Supply1),
            "Remaining capacity must consume both logical aggregate handles.");
        var recomputations = fixture.Runtime.Diagnostics.DerivedFullRecomputations;
        ScenarioAssert.Equal(2m, fixture.Runtime.Evaluate(
            fixture.Model.RemainingCapacity, fixture.Supply1), "Repeated evaluation must be stable.");
        ScenarioAssert.Equal(recomputations, fixture.Runtime.Diagnostics.DerivedFullRecomputations,
            "Repeated evaluation without change must reuse fresh logical state.");

        var diagnostic = fixture.Model.Compiled.Diagnostics.DerivedValues
            .Single(value => value.DefinitionKey == "remaining-capacity");
        ScenarioAssert.Equal(2, diagnostic.UpstreamDerivedIds.Count,
            "Remaining capacity must have two derived-value inputs.");
        AssertIncrementalSum(fixture.Model, "fulfilled-quantity");
        AssertIncrementalSum(fixture.Model, "allocated-quantity");
    }

    public static void CapacityIncrease_DoesNotCreateInvariantViolation()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply1.Capacity = 15m;

        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Supplies, fixture.Supply1,
                value => value.Capacity, 10m, 15m)), RuntimeImpactDetailLevel.Causal);

        ScenarioAssert.Equal(DerivedValueState.Fresh,
            fixture.Runtime.GetState(fixture.Model.RemainingCapacity, fixture.Supply1),
            "Repair-enabled evaluation refreshes the safely dirty value.");
        ScenarioAssert.Equal(InvariantEvaluationState.Valid,
            fixture.Runtime.GetState(fixture.Model.CapacityInvariant, fixture.Supply1),
            "A safe increase must remain valid after repair-policy evaluation.");
        ScenarioAssert.Equal(0, application.Result.RepairRequests.Count,
            "A safe increase must not emit repair work.");
        ScenarioAssert.True(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "The increased capacity must remain valid.");
    }

    public static void CapacityDecrease_RevalidatesAndRejectsViolation()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply1.Capacity = 6m;

        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Supplies, fixture.Supply1,
                value => value.Capacity, 10m, 6m)), RuntimeImpactDetailLevel.Causal);

        ScenarioAssert.Equal(DerivedValueState.Fresh,
            fixture.Runtime.GetState(fixture.Model.RemainingCapacity, fixture.Supply1),
            "Repair evaluation must recompute remaining capacity.");
        ScenarioAssert.Equal(InvariantEvaluationState.Violated,
            fixture.Runtime.GetState(fixture.Model.CapacityInvariant, fixture.Supply1),
            "The capacity invariant must record the evaluated violation.");
        ScenarioAssert.Equal(1, application.Result.RepairRequests.Count,
            "The unsafe transition must produce one repair requirement.");
        ScenarioAssert.False(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "Eight units consumed cannot fit in capacity six.");
        var trace = RuntimeImpactTraceRenderer.Render(application.Result);
        ScenarioAssert.True(trace.Contains("remaining-capacity", StringComparison.Ordinal) &&
            trace.Contains("supply-capacity-valid", StringComparison.Ordinal),
            "Causal diagnostics must connect remaining capacity to the invariant.");
    }

    public static void CapacityDecrease_StillValid_RevalidatesSuccessfully()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply1.Capacity = 9m;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Supplies, fixture.Supply1,
            value => value.Capacity, 10m, 9m));

        ScenarioAssert.True(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "Eight units consumed must fit in capacity nine.");
        ScenarioAssert.Equal(1m, fixture.Runtime.Evaluate(
            fixture.Model.RemainingCapacity, fixture.Supply1), "The current remainder must be one.");
    }

    public static void FulfillmentIncrease_CanInvalidateCapacityInvariant()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Fulfillment1!.Quantity = 7m;
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Fulfillments, fixture.Fulfillment1,
                value => value.Quantity, 3m, 7m)));

        ScenarioAssert.Equal(DerivedValueState.Fresh,
            fixture.Runtime.GetState(fixture.Model.FulfilledQuantity, fixture.Supply1),
            "Repair evaluation must refresh an invalidated aggregate.");
        ScenarioAssert.False(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "Twelve units consumed must violate capacity ten.");
        ScenarioAssert.Equal(1, application.Result.RepairRequests.Count,
            "The invalidation must schedule capacity repair.");
    }

    public static void FulfillmentDecrease_ReleasesCapacity_WithDirectionalDirtyImpact()
    {
        var fixture = CoreFixture.Create(capacity: 15m, fulfillmentQuantity: 7m);
        fixture.Prime();
        fixture.Fulfillment1!.Quantity = 3m;
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Fulfillments, fixture.Fulfillment1,
                value => value.Quantity, 7m, 3m)));

        ScenarioAssert.Equal(DerivedValueState.Fresh,
            fixture.Runtime.GetState(fixture.Model.FulfilledQuantity, fixture.Supply1),
            "A directional decrease is dirty and becomes fresh during repair evaluation.");
        ScenarioAssert.Equal(0, application.Result.RepairRequests.Count,
            "A capacity-releasing decrease must not emit repair work.");
        ScenarioAssert.True(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "A fulfillment decrease releases capacity and remains valid after evaluation.");
    }

    public static void AllocationIncrease_CanInvalidateCapacityInvariant()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Allocation1!.Quantity = 8m;
        fixture.Runtime.Apply(Change.Property(fixture.Model.Allocations, fixture.Allocation1,
            value => value.Quantity, 5m, 8m));

        ScenarioAssert.Equal(DerivedValueState.Fresh,
            fixture.Runtime.GetState(fixture.Model.AllocatedQuantity, fixture.Supply1),
            "Repair evaluation must refresh an invalidated allocation aggregate.");
        ScenarioAssert.False(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "Eleven units consumed must violate capacity ten.");
    }

    public static void AllocationDecrease_ReleasesCapacity_WithDirectionalDirtyImpact()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Allocation1!.Quantity = 2m;
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Allocations, fixture.Allocation1,
                value => value.Quantity, 5m, 2m)));

        ScenarioAssert.Equal(DerivedValueState.Fresh,
            fixture.Runtime.GetState(fixture.Model.AllocatedQuantity, fixture.Supply1),
            "A directional decrease is dirty and becomes fresh during repair evaluation.");
        ScenarioAssert.Equal(0, application.Result.RepairRequests.Count,
            "A capacity-releasing decrease must not emit repair work.");
        ScenarioAssert.True(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "An allocation decrease releases capacity and remains valid after evaluation.");
    }

    public static void CompatibilityChange_InvalidatesExistingAllocation()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply1.ResourceCode = "B";
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Supplies, fixture.Supply1,
                value => value.ResourceCode, "A", "B")), RuntimeImpactDetailLevel.Causal);

        ScenarioAssert.Equal(InvariantEvaluationState.Violated,
            fixture.Runtime.GetState(fixture.Model.CompatibilityInvariant, fixture.Allocation1!),
            "Removing selected candidate membership must prove the violation.");
        var request = application.Result.RepairRequests.Single();
        ScenarioAssert.Same(fixture.Allocation1!, request.Source,
            "The repair payload must identify the affected allocation.");
        var trace = RuntimeImpactTraceRenderer.Render(application.Result);
        ScenarioAssert.True(trace.Contains("candidate-supplies", StringComparison.Ordinal),
            "Causal diagnostics must expose selected candidate-membership removal.");
    }

    public static void DemandDateChange_InvalidatesExistingAllocation()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Demand1.Date = fixture.Day.AddDays(1);
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Demands, fixture.Demand1,
                value => value.Date, fixture.Day, fixture.Day.AddDays(1))));

        ScenarioAssert.Equal(InvariantEvaluationState.Violated,
            fixture.Runtime.GetState(fixture.Model.CompatibilityInvariant, fixture.Allocation1!),
            "A nested demand-date dependency must prove incompatibility.");
        ScenarioAssert.Equal(1, application.Result.RepairRequests.Count,
            "The nested change must produce repair work through graph dependencies.");
    }

    public static void InvalidAllocation_CanBeRepairedToAnotherCandidateSupply()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply1.ResourceCode = "B";
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Supplies, fixture.Supply1,
                value => value.ResourceCode, "A", "B")));
        var result = new ReallocateDemand(fixture.Model).Process(
            fixture.Runtime, application.Result.RepairRequests);

        ScenarioAssert.Equal(1, result.Reallocated.Count,
            "One invalid allocation must be reallocated.");
        ScenarioAssert.Equal(0, result.Unresolved.Count, "A valid replacement exists.");
        ScenarioAssert.Same(fixture.Supply2, fixture.Allocation1!.Supply,
            "The deterministic replacement must be the lowest-id valid candidate.");
        ScenarioAssert.True(fixture.Runtime.Evaluate(
            fixture.Model.CompatibilityInvariant, fixture.Allocation1),
            "Compatibility must be restored after repair.");
    }

    public static void InsufficientCapacity_CanTriggerReallocation()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply1.Capacity = 3m;
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Supplies, fixture.Supply1,
                value => value.Capacity, 10m, 3m)));
        var result = new ReallocateDemand(fixture.Model).Process(
            fixture.Runtime, application.Result.RepairRequests);

        ScenarioAssert.Equal(1, result.Reallocated.Count, "The allocation must move once.");
        ScenarioAssert.Equal(0, result.Unresolved.Count, "S2 has sufficient replacement capacity.");
        ScenarioAssert.Same(fixture.Supply2, fixture.Allocation1!.Supply,
            "Capacity repair must choose S2.");
        ScenarioAssert.Equal(0m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply1), "S1 must be debited.");
        ScenarioAssert.Equal(5m, fixture.Runtime.Evaluate(
            fixture.Model.AllocatedQuantity, fixture.Supply2), "S2 must be credited.");
        ScenarioAssert.True(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "S1 must be valid after repair.");
        ScenarioAssert.True(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply2),
            "S2 must remain valid after repair.");
    }

    public static void NoReplacementSupply_DoesNotSilentlyAcceptInvalidAllocation()
    {
        var fixture = CoreFixture.Create(replacementCapacity: 4m);
        fixture.Prime();
        fixture.Supply1.Capacity = 3m;
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Supplies, fixture.Supply1,
                value => value.Capacity, 10m, 3m)));
        var result = new ReallocateDemand(fixture.Model).Process(
            fixture.Runtime, application.Result.RepairRequests);

        ScenarioAssert.Equal(0, result.Reallocated.Count, "No replacement has enough capacity.");
        ScenarioAssert.Equal(1, result.Unresolved.Count,
            "The unresolved repair must remain explicit.");
        ScenarioAssert.Same(fixture.Supply1, fixture.Allocation1!.Supply,
            "The allocation must not silently move to an insufficient supply.");
        ScenarioAssert.False(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "The unresolved invalid state must remain observable.");
    }

    public static void UnrelatedSupplyMutation_DoesNotAffectOtherGraph()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply2.Capacity = 12m;
        var application = fixture.Runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(fixture.Model.Supplies, fixture.Supply2,
                value => value.Capacity, 10m, 12m)), RuntimeImpactDetailLevel.Causal);

        var affectedSources = application.Result.DerivedImpacts
            .SelectMany(value => value.Sources)
            .Select(value => value.Source)
            .Distinct(ReferenceEqualityComparer.Instance)
            .ToArray();
        ScenarioAssert.True(affectedSources.Contains(fixture.Supply2),
            "The changed supply must be affected.");
        ScenarioAssert.False(affectedSources.Contains(fixture.Supply1),
            "An unrelated supply must not be affected.");
    }

    public static void Evaluate_DoesNotWriteMaterializedMirror()
    {
        var fixture = CoreFixture.Create();

        ScenarioAssert.Equal(2m, fixture.Runtime.Evaluate(
            fixture.Model.RemainingCapacity, fixture.Supply1), "Logical value must be current.");
        ScenarioAssert.Equal(0m, fixture.Supply1.FulfilledQuantity,
            "Evaluate must not write fulfilled mirror.");
        ScenarioAssert.Equal(0m, fixture.Supply1.AllocatedQuantity,
            "Evaluate must not write allocated mirror.");
        ScenarioAssert.Equal(0m, fixture.Supply1.RemainingCapacity,
            "Evaluate must not write remaining mirror.");
    }

    public static void Materialize_WritesConfiguredMirrors()
    {
        var fixture = CoreFixture.Create();
        fixture.Runtime.Materialize(fixture.Supply1);

        ScenarioAssert.Equal(3m, fixture.Supply1.FulfilledQuantity,
            "Object materialization must write fulfilled quantity.");
        ScenarioAssert.Equal(5m, fixture.Supply1.AllocatedQuantity,
            "Object materialization must write allocated quantity.");
        ScenarioAssert.Equal(2m, fixture.Supply1.RemainingCapacity,
            "Object materialization must write remaining capacity.");
    }

    public static void RichDomainMutation_StillTriggersConsistencyConsequences()
    {
        var fixture = CoreFixture.Create();
        fixture.Prime();
        fixture.Supply1.ChangeCapacity(6m);
        fixture.Runtime.Apply(Change.Property(fixture.Model.Supplies, fixture.Supply1,
            value => value.Capacity, 10m, 6m));

        ScenarioAssert.False(fixture.Runtime.Evaluate(fixture.Model.CapacityInvariant, fixture.Supply1),
            "A domain method must not hide cross-object consequences from the graph.");
        ScenarioAssert.Throws<ArgumentOutOfRangeException>(
            () => fixture.Supply1.ChangeCapacity(-1m),
            "The rich entity should still own local primitive validation.");
    }

    private static void AssertIncrementalSum(AllocationConsistencyModel model, string definitionKey)
    {
        var diagnostic = model.Compiled.Diagnostics.DerivedValues
            .Single(value => value.DefinitionKey == definitionKey);
        ScenarioAssert.True(diagnostic.ComputationPlan.StartsWith("IncrementalSum", StringComparison.Ordinal),
            $"{definitionKey} must use the incremental sum plan.");
    }
}

internal sealed class CoreFixture
{
    private CoreFixture(
        AllocationConsistencyModel model,
        ConsistencyRuntime runtime,
        DateOnly day,
        Demand demand1,
        Demand demand2,
        Supply supply1,
        Supply supply2,
        Supply unrelatedSupply,
        List<Allocation> allocations,
        List<Fulfillment> fulfillments)
    {
        Model = model;
        Runtime = runtime;
        Day = day;
        Demand1 = demand1;
        Demand2 = demand2;
        Supply1 = supply1;
        Supply2 = supply2;
        UnrelatedSupply = unrelatedSupply;
        Allocations = allocations;
        Fulfillments = fulfillments;
    }

    public AllocationConsistencyModel Model { get; }
    public ConsistencyRuntime Runtime { get; }
    public DateOnly Day { get; }
    public Demand Demand1 { get; }
    public Demand Demand2 { get; }
    public Supply Supply1 { get; }
    public Supply Supply2 { get; }
    public Supply UnrelatedSupply { get; }
    public List<Allocation> Allocations { get; }
    public List<Fulfillment> Fulfillments { get; }
    public Allocation? Allocation1 => Allocations.FirstOrDefault(value => value.Id == 1);
    public Fulfillment? Fulfillment1 => Fulfillments.FirstOrDefault(value => value.Id == 1);

    public static CoreFixture Create(
        bool includeAllocation = true,
        bool includeFulfillment = true,
        decimal capacity = 10m,
        decimal replacementCapacity = 10m,
        decimal fulfillmentQuantity = 3m)
    {
        var model = new AllocationConsistencyModel();
        var day = new DateOnly(2026, 1, 1);
        var demand1 = new Demand
        {
            Id = 1,
            ResourceCode = "A",
            Date = day,
            RequestedQuantity = 5m
        };
        var demand2 = new Demand
        {
            Id = 2,
            ResourceCode = "C",
            Date = day,
            RequestedQuantity = 1m
        };
        var supply1 = new Supply
        {
            Id = 1,
            ResourceCode = "A",
            Date = day,
            Capacity = capacity
        };
        var supply2 = new Supply
        {
            Id = 2,
            ResourceCode = "A",
            Date = day,
            Capacity = replacementCapacity
        };
        var unrelatedSupply = new Supply
        {
            Id = 3,
            ResourceCode = "B",
            Date = day,
            Capacity = 20m
        };
        List<Allocation> allocations = includeAllocation
            ?
            [
                new Allocation
                {
                    Id = 1,
                    DemandId = demand1.Id,
                    Demand = demand1,
                    SupplyId = supply1.Id,
                    Supply = supply1,
                    Quantity = 5m
                }
            ]
            : [];
        List<Fulfillment> fulfillments = includeFulfillment
            ?
            [
                new Fulfillment
                {
                    Id = 1,
                    SupplyId = supply1.Id,
                    Supply = supply1,
                    Quantity = fulfillmentQuantity
                }
            ]
            : [];
        var runtime = model.Compiled.CreateRuntime(seed =>
        {
            seed.Add(model.Demands, [demand1, demand2]);
            seed.Add(model.Supplies, [supply1, supply2, unrelatedSupply]);
            seed.Add(model.Allocations, allocations);
            seed.Add(model.Fulfillments, fulfillments);
        });
        return new CoreFixture(model, runtime, day, demand1, demand2,
            supply1, supply2, unrelatedSupply, allocations, fulfillments);
    }

    public void Prime()
    {
        foreach (var supply in new[] { Supply1, Supply2, UnrelatedSupply })
        {
            _ = Runtime.Evaluate(Model.RemainingCapacity, supply);
            _ = Runtime.Evaluate(Model.CapacityInvariant, supply);
        }
        foreach (var allocation in Allocations)
            _ = Runtime.Evaluate(Model.CompatibilityInvariant, allocation);
    }
}
