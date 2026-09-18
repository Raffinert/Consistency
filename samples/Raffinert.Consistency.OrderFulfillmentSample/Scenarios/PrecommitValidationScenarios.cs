using Raffinert.Consistency.OrderFulfillmentSample.Domain;
using Raffinert.Consistency.OrderFulfillmentSample.Support;

namespace Raffinert.Consistency.OrderFulfillmentSample.Scenarios;

internal static class PrecommitValidationScenarios
{
    // PlanDetailed restores Raffinert runtime state, not already-mutated domain objects. Rejected
    // scenarios below visibly restore those objects before constructing another independent plan.
    public static void Run()
    {
        var runner = new ScenarioRunner();
        OrphanPlans(runner);
        QuantityPlans(runner);
        ConservationPlans(runner);
        ExistencePlans(runner);
        runner.Complete("Precommit allocation integrity plans");
    }

    private static void OrphanPlans(ScenarioRunner runner)
    {
        var model = new ConsistencyModelBuilder();
        var requests = model.Objects<RequestLine>().Named("guard-requests").Key(x => x.Id);
        var allocations = model.Objects<Allocation>().Named("guard-allocations").Key(x => x.Id);
        var relation = model.Relation(requests, allocations)
            .Where((request, allocation) => request.Id == allocation.RequestLineId && !allocation.IsDeleted).Named("guard-active-allocations");
        var count = model.Derived(requests).From(relation)
            .Count().Named("guard-active-allocation-count");
        var valid = model.Derived(requests).From(count)
            .Select((request, active) => !request.IsDeleted || active == 0).Named("guard-orphan-result");
        var orphanInvariant = model.Invariant(requests).From(valid).Must((_, result) => result).Named("guard-orphan-invariant");
        var request = new RequestLine();
        var line = new OrderLine();
        var allocation = new Allocation { RequestLineId = request.Id, OrderLineId = line.Id, OrderLine = line };
        var compiled = model.Build();
        var runtime = compiled.CreateRuntime(seed => { seed.Add(requests, [request]); seed.Add(allocations, [allocation]); });
        request.IsDeleted = true;
        var violating = Plan(runtime, MutationSet.Create(Change.Property(requests, request, x => x.IsDeleted, false, true)));
        runner.Check("A-P1 request-only deletion rejected", RuleEvaluation.Violation, Decision(violating), violating.Result);
        request.IsDeleted = false;
        request.IsDeleted = true; allocation.IsDeleted = true;
        var coordinated = Plan(runtime, MutationSet.Create(
            Change.Property(requests, request, x => x.IsDeleted, false, true),
            Change.Property(allocations, allocation, x => x.IsDeleted, false, true)));
        runner.Check("A-P2 coordinated request and allocation deletion", RuleEvaluation.Valid, Decision(coordinated));
        runtime.Commit(coordinated);

        var repairRequest = new RequestLine { IsDeleted = true };
        var deleted = new Allocation { RequestLineId = repairRequest.Id, OrderLineId = line.Id, OrderLine = line, IsDeleted = true };
        var active = new Allocation { RequestLineId = repairRequest.Id, OrderLineId = line.Id, OrderLine = line };
        var repairRuntime = compiled.CreateRuntime(seed => { seed.Add(requests, [repairRequest]); seed.Add(allocations, [deleted, active]); });
        active.IsDeleted = true;
        var repaired = Plan(repairRuntime, MutationSet.Create(Change.Property(allocations, active, x => x.IsDeleted, false, true)));
        runner.Check("A-P3 deleting final active allocation repairs orphan", RuleEvaluation.Valid, Decision(repaired));
        repairRuntime.Commit(repaired);
        runner.Check("A-P3 installed invariant is valid", RuleEvaluation.Valid,
            repairRuntime.GetState(orphanInvariant, repairRequest) == InvariantEvaluationState.Valid ? RuleEvaluation.Valid : RuleEvaluation.Violation);
    }

    private static void QuantityPlans(ScenarioRunner runner)
    {
        var model = new ConsistencyModelBuilder();
        var allocations = model.Objects<Allocation>().Named("guard-quantity-allocations").Key(x => x.Id);
        var allocationFulfillments = model.Objects<AllocationFulfillment>().Named("guard-allocation-fulfillments").Key(x => x.Id);
        var relation = model.Relation(allocations, allocationFulfillments).Where((allocation, allocationFulfillment) => allocation.Id == allocationFulfillment.AllocationId && !allocationFulfillment.IsDeleted).Named("guard-allocation-active-fulfillments");
        var sum = model.Derived(allocations).From(relation).Sum(x => x.Quantity).Named("guard-fulfillment-sum");
        var result = model.Derived(allocations).From(sum).Select((allocation, total) =>
            allocation.FulfillmentMode == FulfillmentMode.Unknown ? RuleEvaluation.Unknown :
            allocation.FulfillmentMode == FulfillmentMode.Disabled || allocation.AllocatedQuantity == total ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("guard-allocation-balance");
        _ = model.Invariant(allocations).From(result).Must((_, value) => value != RuleEvaluation.Violation).Named("guard-allocation-balance-invariant");
        var line = new OrderLine();
        var allocation = new Allocation { RequestLineId = Guid.NewGuid(), OrderLineId = line.Id, OrderLine = line, AllocatedQuantity = 5 };
        var allocationFulfillment = new AllocationFulfillment { AllocationId = allocation.Id, Allocation = allocation, FulfillmentId = 1, Quantity = 5 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(allocations, [allocation]); seed.Add(allocationFulfillments, [allocationFulfillment]); });
        allocationFulfillment.Quantity = 4;
        var bad = Plan(runtime, MutationSet.Create(Change.Property(allocationFulfillments, allocationFulfillment, x => x.Quantity, 5m, 4m)));
        runner.Check("B-P1 uncoordinated allocation fulfillment quantity rejected", RuleEvaluation.Violation, Decision(bad), bad.Result);
        allocation.AllocatedQuantity = 4;
        var good = Plan(runtime, MutationSet.Create(
            Change.Property(allocationFulfillments, allocationFulfillment, x => x.Quantity, 5m, 4m),
            Change.Property(allocations, allocation, x => x.AllocatedQuantity, 5m, 4m)));
        runner.Check("B-P2 coordinated quantity change accepted", RuleEvaluation.Valid, Decision(good));
        runtime.Commit(good);
        allocation.AllocatedQuantity = 3; allocation.FulfillmentMode = FulfillmentMode.Unknown;
        var unknown = Plan(runtime, MutationSet.Create(
            Change.Property(allocations, allocation, x => x.AllocatedQuantity, 4m, 3m),
            Change.Property(allocations, allocation, x => x.FulfillmentMode, FulfillmentMode.Enabled, FulfillmentMode.Unknown)));
        runner.Check("B-P3 unknown fulfillment is non-blocking", RuleEvaluation.Valid, Decision(unknown));
        allocation.AllocatedQuantity = 4; allocation.FulfillmentMode = FulfillmentMode.Enabled; // reconcile rejected domain state
        var added = new AllocationFulfillment { AllocationId = allocation.Id, Allocation = allocation, FulfillmentId = 99, Quantity = 2 };
        var addBad = Plan(runtime, MutationSet.Create(Change.Add(allocationFulfillments, added)));
        runner.Check("B-P4 allocation fulfillment add without correction rejected", RuleEvaluation.Violation, Decision(addBad));
        allocation.AllocatedQuantity = 6;
        var addGood = Plan(runtime, MutationSet.Create(Change.Add(allocationFulfillments, added),
            Change.Property(allocations, allocation, x => x.AllocatedQuantity, 4m, 6m)));
        runner.Check("B-P5 coordinated allocation fulfillment add accepted", RuleEvaluation.Valid, Decision(addGood)); runtime.Commit(addGood);
        var removeBad = Plan(runtime, MutationSet.Create(Change.Remove(allocationFulfillments, added)));
        runner.Check("B-P6 allocation fulfillment removal without correction rejected", RuleEvaluation.Violation, Decision(removeBad));
        allocation.AllocatedQuantity = 4;
        var removeGood = Plan(runtime, MutationSet.Create(Change.Remove(allocationFulfillments, added),
            Change.Property(allocations, allocation, x => x.AllocatedQuantity, 6m, 4m)));
        runner.Check("B-P7 coordinated allocation fulfillment removal accepted", RuleEvaluation.Valid, Decision(removeGood)); runtime.Commit(removeGood);
        allocation.AllocatedQuantity = 3; allocation.FulfillmentMode = FulfillmentMode.Unknown;
        var toUnknown = Plan(runtime, MutationSet.Create(Change.Property(allocations, allocation, x => x.AllocatedQuantity, 4m, 3m),
            Change.Property(allocations, allocation, x => x.FulfillmentMode, FulfillmentMode.Enabled, FulfillmentMode.Unknown)));
        runner.Check("B-P8 inconsistent Enabled to Unknown", RuleEvaluation.Valid, Decision(toUnknown)); runtime.Commit(toUnknown);
        runner.Check("B-P8 domain result remains Unknown", RuleEvaluation.Unknown, runtime.Evaluate(result, allocation));
        allocation.FulfillmentMode = FulfillmentMode.Enabled;
        var enabled = Plan(runtime, MutationSet.Create(Change.Property(allocations, allocation, x => x.FulfillmentMode, FulfillmentMode.Unknown, FulfillmentMode.Enabled)));
        runner.Check("B-P9 inconsistent Unknown to Enabled rejected", RuleEvaluation.Violation, Decision(enabled));
    }

    private static void ConservationPlans(ScenarioRunner runner)
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<OrderLine>().Named("guard-conservation-lines").Key(x => x.Id);
        var rows = model.Objects<FulfillmentBalance>().Named("guard-fulfillment-balances").Key(x => x.Id);
        var result = model.Derived(rows).Select(row =>
            row.FulfillmentMode == FulfillmentMode.Unknown || row.OrderLine.IsServiceLine == null ? RuleEvaluation.Unknown :
            row.FulfillmentMode == FulfillmentMode.Disabled || row.OrderLine.IsServiceLine == true ? RuleEvaluation.Valid :
            row.AvailableQuantity + row.AllocatedQuantity + row.ProcessedQuantity == row.TotalQuantity ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("guard-fulfillment-balance");
        _ = model.Invariant(rows).From(result).Must((_, value) => value != RuleEvaluation.Violation).Named("guard-fulfillment-balance-invariant");
        var line = new OrderLine { IsServiceLine = false };
        var row = new FulfillmentBalance { OrderLineId = line.Id, OrderLine = line, FulfillmentId = 2, TotalQuantity = 10, AvailableQuantity = 4, AllocatedQuantity = 6 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(lines, [line]); seed.Add(rows, [row]); });
        row.AllocatedQuantity = 5;
        var bad = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.AllocatedQuantity, 6m, 5m)));
        runner.Check("C-P1 broken conservation rejected", RuleEvaluation.Violation, Decision(bad), bad.Result);
        row.AvailableQuantity = 5;
        var good = Plan(runtime, MutationSet.Create(
            Change.Property(rows, row, x => x.AllocatedQuantity, 6m, 5m),
            Change.Property(rows, row, x => x.AvailableQuantity, 4m, 5m)));
        runner.Check("C-P2 coordinated conservation accepted", RuleEvaluation.Valid, Decision(good));
        runtime.Commit(good);
        row.AllocatedQuantity = 4; line.IsServiceLine = true;
        var service = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.AllocatedQuantity, 5m, 4m),
            Change.Property(lines, line, x => x.IsServiceLine, false, true)));
        runner.Check("C-P3 service line makes imbalance non-blocking", RuleEvaluation.Valid, Decision(service)); runtime.Commit(service);
        line.IsServiceLine = null;
        var unresolved = Plan(runtime, MutationSet.Create(Change.Property(lines, line, x => x.IsServiceLine, true, null)));
        runner.Check("C-P4 unresolved service status is non-blocking", RuleEvaluation.Valid, Decision(unresolved)); runtime.Commit(unresolved);
        runner.Check("C-P4 domain result remains Unknown", RuleEvaluation.Unknown, runtime.Evaluate(result, row));
        line.IsServiceLine = false;
        var normal = Plan(runtime, MutationSet.Create(Change.Property(lines, line, x => x.IsServiceLine, null, false)));
        runner.Check("C-P5 unresolved to normal exposes violation", RuleEvaluation.Violation, Decision(normal));
        line.IsServiceLine = null; row.FulfillmentMode = FulfillmentMode.Unknown; // reconcile rejected line change
        var fulfillmentUnknown = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.FulfillmentMode, FulfillmentMode.Enabled, FulfillmentMode.Unknown)));
        runner.Check("C-P6 unknown fulfillment is non-blocking", RuleEvaluation.Valid, Decision(fulfillmentUnknown)); runtime.Commit(fulfillmentUnknown);
        runner.Check("C-P6 domain result remains Unknown", RuleEvaluation.Unknown, runtime.Evaluate(result, row));
        row.FulfillmentMode = FulfillmentMode.Enabled; line.IsServiceLine = false;
        var fulfillmentEnabled = Plan(runtime, MutationSet.Create(Change.Property(rows, row, x => x.FulfillmentMode, FulfillmentMode.Unknown, FulfillmentMode.Enabled),
            Change.Property(lines, line, x => x.IsServiceLine, null, false)));
        runner.Check("C-P7 enabling fulfillment exposes violation", RuleEvaluation.Violation, Decision(fulfillmentEnabled));
    }

    private static void ExistencePlans(ScenarioRunner runner)
    {
        var model = new ConsistencyModelBuilder();
        var allocationFulfillments = model.Objects<AllocationFulfillment>().Named("guard-allocation-fulfillments").Key(x => x.Id);
        var fulfillmentBalances = model.Objects<FulfillmentBalance>().Named("guard-fulfillment-balances").Key(x => x.Id);
        var relation = model.Relation(allocationFulfillments, fulfillmentBalances).Where((allocationFulfillment, row) =>
            allocationFulfillment.Allocation.OrderLineId == row.OrderLineId && allocationFulfillment.FulfillmentId == row.FulfillmentId).Named("guard-matching-fulfillment-balance");
        var count = model.Derived(allocationFulfillments).From(relation).Count().Named("guard-matching-fulfillment-balance-count");
        var result = model.Derived(allocationFulfillments).From(count).Select((allocationFulfillment, matches) =>
            allocationFulfillment.Allocation.FulfillmentMode == FulfillmentMode.Unknown ? RuleEvaluation.Unknown :
            allocationFulfillment.Allocation.FulfillmentMode == FulfillmentMode.Disabled || allocationFulfillment.IsDeleted || matches > 0 ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("guard-allocation-fulfillment-existence");
        _ = model.Invariant(allocationFulfillments).From(result).Must((_, value) => value != RuleEvaluation.Violation).Named("guard-allocation-fulfillment-existence-invariant");
        var line = new OrderLine();
        var allocation = new Allocation { RequestLineId = Guid.NewGuid(), OrderLineId = line.Id, OrderLine = line };
        var allocationFulfillment = new AllocationFulfillment { AllocationId = allocation.Id, Allocation = allocation, FulfillmentId = 3, Quantity = 1 };
        var fulfillmentBalance = new FulfillmentBalance { OrderLineId = line.Id, OrderLine = line, FulfillmentId = 3 };
        var runtime = model.Build().CreateRuntime(seed => { seed.Add(allocationFulfillments, [allocationFulfillment]); seed.Add(fulfillmentBalances, [fulfillmentBalance]); });
        var bad = Plan(runtime, MutationSet.Create(Change.Remove(fulfillmentBalances, fulfillmentBalance)));
        runner.Check("D-P1 surviving allocation fulfillment blocks fulfillment balance removal", RuleEvaluation.Violation, Decision(bad), bad.Result);
        allocationFulfillment.IsDeleted = true;
        var good = Plan(runtime, MutationSet.Create(Change.Remove(fulfillmentBalances, fulfillmentBalance), Change.Property(allocationFulfillments, allocationFulfillment, x => x.IsDeleted, false, true)));
        runner.Check("D-P2 coordinated allocation fulfillment and fulfillment balance removal", RuleEvaluation.Valid, Decision(good));
        runtime.Commit(good);
        var active = new AllocationFulfillment { AllocationId = allocation.Id, Allocation = allocation, FulfillmentId = 7, Quantity = 1 };
        var matching = new FulfillmentBalance { OrderLineId = line.Id, OrderLine = line, FulfillmentId = 7 };
        var addPair = Plan(runtime, MutationSet.Create(Change.Add(allocationFulfillments, active), Change.Add(fulfillmentBalances, matching)));
        runner.Check("D-P3 allocation fulfillment and fulfillment balance pair add accepted", RuleEvaluation.Valid, Decision(addPair)); runtime.Commit(addPair);
        active.FulfillmentId = 8;
        var retargetBad = Plan(runtime, MutationSet.Create(Change.Property(allocationFulfillments, active, x => x.FulfillmentId, 7L, 8L)));
        runner.Check("D-P5 composite retarget without replacement rejected", RuleEvaluation.Violation, Decision(retargetBad));
        var replacement = new FulfillmentBalance { OrderLineId = line.Id, OrderLine = line, FulfillmentId = 8 };
        var retargetGood = Plan(runtime, MutationSet.Create(Change.Property(allocationFulfillments, active, x => x.FulfillmentId, 7L, 8L), Change.Add(fulfillmentBalances, replacement)));
        runner.Check("D-P6 composite retarget with replacement accepted", RuleEvaluation.Valid, Decision(retargetGood));
    }

    private static PreparedImpactPlan Plan(ConsistencyRuntime runtime, MutationSet mutations) => runtime.PlanDetailed(
        runtime.Prepare(mutations), RuntimeImpactDetailLevel.Causal, PlannedInvariantEvaluationMode.Affected);

    private static RuleEvaluation Decision(PreparedImpactPlan plan) =>
        plan.HasInvariantViolations ? RuleEvaluation.Violation : RuleEvaluation.Valid;
}
