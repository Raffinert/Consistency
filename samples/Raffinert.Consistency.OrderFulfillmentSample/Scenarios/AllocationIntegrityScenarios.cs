using Raffinert.Consistency.OrderFulfillmentSample.Domain;
using Raffinert.Consistency.OrderFulfillmentSample.Support;

namespace Raffinert.Consistency.OrderFulfillmentSample.Scenarios;

internal static class AllocationIntegrityScenarios
{
    public static void Run()
    {
        var model = new ConsistencyModelBuilder();
        var requests = model.Objects<RequestLine>().Named("request-lines").Key(x => x.Id);
        var lines = model.Objects<OrderLine>().Named("order-lines").Key(x => x.Id);
        var allocations = model.Objects<Allocation>().Named("allocations").Key(x => x.Id);
        var fulfillments = model.Objects<Fulfillment>().Named("fulfillments").Key(x => x.Id);
        var allocationFulfillments = model.Objects<AllocationFulfillment>().Named("allocation-fulfillments").Key(x => x.Id);
        var fulfillmentBalances = model.Objects<FulfillmentBalance>().Named("fulfillment-balances").Key(x => x.Id);

        var activeAllocationsByRequest = model.Relation(requests, allocations)
            .Where((request, allocation) => request.Id == allocation.RequestLineId && !allocation.IsDeleted)
            .Named("request-active-allocations");
        var activeAllocationsByLine = model.Relation(lines, allocations)
            .Where((line, allocation) => line.Id == allocation.OrderLineId && !allocation.IsDeleted)
            .Named("order-line-active-allocations");
        var activeAllocationFulfillments = model.Relation(allocations, allocationFulfillments)
            .Where((allocation, allocationFulfillment) => allocation.Id == allocationFulfillment.AllocationId && !allocationFulfillment.IsDeleted)
            .Named("allocation-active-fulfillments");
        var fulfillmentBalancesByLine = model.Relation(lines, fulfillmentBalances)
            .Where((line, fulfillmentBalance) => line.Id == fulfillmentBalance.OrderLineId).Named("order-line-fulfillment-balances");
        var matchingFulfillmentBalance = model.Relation(allocationFulfillments, fulfillmentBalances)
            .Where((allocationFulfillment, fulfillmentBalance) => allocationFulfillment.Allocation.OrderLineId == fulfillmentBalance.OrderLineId
                && allocationFulfillment.FulfillmentId == fulfillmentBalance.FulfillmentId).Named("allocation-fulfillment-matching-balance");

        var activeAllocationCount = model.Derived(requests).Using(activeAllocationsByRequest).Incrementally()
            .Compute((_, matches) => matches.Count()).Named("active-allocation-count");
        var deletedRequestIntegrity = model.Derived(requests).Using(activeAllocationCount)
            .Compute((request, count) => !request.IsDeleted || count == 0).Named("deleted-request-allocation-integrity");
        var deletedRequestInvariant = model.Invariant(requests).Using(deletedRequestIntegrity)
            .Must((_, valid) => valid).Named("deleted-request-allocation-integrity-invariant");

        var allocationFulfillmentQuantity = model.Derived(allocations).Using(activeAllocationFulfillments).Incrementally()
            .Compute((_, matches) => matches.Sum(x => x.Quantity)).Named("allocation-fulfillment-quantity");
        var allocationQuantityBalance = model.Derived(allocations).Using(allocationFulfillmentQuantity).Compute((allocation, fulfilled) =>
            allocation.FulfillmentMode == FulfillmentMode.Unknown ? RuleEvaluation.Unknown :
            allocation.FulfillmentMode == FulfillmentMode.Disabled || allocation.AllocatedQuantity == fulfilled ? RuleEvaluation.Valid : RuleEvaluation.Violation)
            .Named("allocation-quantity-balance");
        _ = model.Invariant(allocations).Using(allocationQuantityBalance)
            .Must((_, result) => result != RuleEvaluation.Violation)
            .Named("allocation-quantity-balance-invariant");

        var fulfillmentBalanceConservation = model.Derived(fulfillmentBalances).Compute(fulfillmentBalance =>
            fulfillmentBalance.FulfillmentMode == FulfillmentMode.Unknown || fulfillmentBalance.OrderLine.IsServiceLine == null ? RuleEvaluation.Unknown :
            fulfillmentBalance.FulfillmentMode == FulfillmentMode.Disabled || fulfillmentBalance.OrderLine.IsServiceLine == true ? RuleEvaluation.Valid :
            fulfillmentBalance.AvailableQuantity + fulfillmentBalance.AllocatedQuantity + fulfillmentBalance.ProcessedQuantity == fulfillmentBalance.TotalQuantity
                ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("fulfillment-balance-conservation");
        _ = model.Invariant(fulfillmentBalances).Using(fulfillmentBalanceConservation)
            .Must((_, result) => result != RuleEvaluation.Violation)
            .Named("fulfillment-balance-conservation-invariant");

        var matchingFulfillmentBalanceCount = model.Derived(allocationFulfillments).Using(matchingFulfillmentBalance).Incrementally()
            .Compute((_, matches) => matches.Count()).Named("matching-fulfillment-balance-count");
        var allocationFulfillmentBalanceExistence = model.Derived(allocationFulfillments).Using(matchingFulfillmentBalanceCount).Compute((allocationFulfillment, count) =>
            allocationFulfillment.Allocation.FulfillmentMode == FulfillmentMode.Unknown ? RuleEvaluation.Unknown :
            allocationFulfillment.Allocation.FulfillmentMode == FulfillmentMode.Disabled || allocationFulfillment.IsDeleted || count > 0
                ? RuleEvaluation.Valid : RuleEvaluation.Violation).Named("allocation-fulfillment-balance-existence");
        _ = model.Invariant(allocationFulfillments).Using(allocationFulfillmentBalanceExistence)
            .Must((_, result) => result != RuleEvaluation.Violation)
            .Named("allocation-fulfillment-balance-existence-invariant");

        // Kept in the graph intentionally: these relations prove that allocation and fulfillment-balance rows are
        // reachable from their order line without scenario-side dictionary matching.
        _ = activeAllocationsByLine; _ = fulfillmentBalancesByLine; _ = fulfillments;

        var data = Cases.Create();
        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(requests, data.Requests); seed.Add(lines, data.Lines); seed.Add(allocations, data.Allocations);
            seed.Add(fulfillments, data.Fulfillments); seed.Add(allocationFulfillments, data.AllocationFulfillments); seed.Add(fulfillmentBalances, data.FulfillmentBalances);
        });
        var runner = new ScenarioRunner();

        foreach (var test in data.RequestCases)
            runner.Check(test.Name, test.Expected, runtime.Evaluate(deletedRequestInvariant, test.Value) ? RuleEvaluation.Valid : RuleEvaluation.Violation);
        foreach (var test in data.AllocationCases)
            runner.Check(test.Name, test.Expected, runtime.Get(allocationQuantityBalance, test.Value));
        foreach (var test in data.FulfillmentBalanceCases)
            runner.Check(test.Name, test.Expected, runtime.Get(fulfillmentBalanceConservation, test.Value));
        foreach (var test in data.AllocationFulfillmentCases)
            runner.Check(test.Name, test.Expected, runtime.Get(allocationFulfillmentBalanceExistence, test.Value));

        var removable = data.RemovableFulfillmentBalance;
        _ = runtime.Get(allocationFulfillmentBalanceExistence, data.RemovalAllocationFulfillment);
        var removal = runtime.ApplyDetailed(MutationSet.Create(Change.Remove(fulfillmentBalances, removable)), RuntimeImpactDetailLevel.Causal);
        runner.Check("D4 fulfillment balance removed while allocation fulfillment survives", RuleEvaluation.Violation, runtime.Get(allocationFulfillmentBalanceExistence, data.RemovalAllocationFulfillment), removal.Result);

        var pairLine = new OrderLine { IsServiceLine = false };
        var pairAllocation = new Allocation { RequestLineId = Guid.NewGuid(), OrderLineId = pairLine.Id, OrderLine = pairLine, AllocatedQuantity = 2 };
        var pairAllocationFulfillment = new AllocationFulfillment { AllocationId = pairAllocation.Id, Allocation = pairAllocation, FulfillmentId = 9001, Quantity = 2 };
        var pairFulfillmentBalance = new FulfillmentBalance { OrderLineId = pairLine.Id, OrderLine = pairLine, FulfillmentId = 9001, TotalQuantity = 2, AllocatedQuantity = 2 };
        var addition = runtime.ApplyDetailed(MutationSet.Create(Change.Add(lines, pairLine), Change.Add(allocations, pairAllocation),
            Change.Add(allocationFulfillments, pairAllocationFulfillment), Change.Add(fulfillmentBalances, pairFulfillmentBalance)), RuntimeImpactDetailLevel.Causal);
        runner.Check("D6 allocation fulfillment and fulfillment balance added in one MutationSet", RuleEvaluation.Valid, runtime.Get(allocationFulfillmentBalanceExistence, pairAllocationFulfillment), addition.Result);
        var versionBeforeRemoval = runtime.Version;
        var pairRemoval = runtime.ApplyDetailed(
            MutationSet.Create(Change.Remove(allocationFulfillments, pairAllocationFulfillment), Change.Remove(fulfillmentBalances, pairFulfillmentBalance)),
            RuntimeImpactDetailLevel.Causal);
        var removedBothRelationMemberships = pairRemoval.Result.RelationImpacts
            .SelectMany(impact => impact.RemovedPairs)
            .Any(pair => ReferenceEquals(pair.Left, pairAllocationFulfillment) && ReferenceEquals(pair.Right, pairFulfillmentBalance));
        var removalWasAtomicAndExplained = runtime.Version == versionBeforeRemoval + 1
            && removedBothRelationMemberships
            && pairRemoval.Result.InvariantImpacts.All(impact => impact.Sources.All(source =>
                !ReferenceEquals(source.Source, pairAllocationFulfillment)));
        runner.Check("D5 allocation fulfillment and fulfillment balance removed in one MutationSet", RuleEvaluation.Valid,
            removalWasAtomicAndExplained ? RuleEvaluation.Valid : RuleEvaluation.Violation,
            pairRemoval.Result);
        runner.Complete("Allocation integrity dogfooding");
    }

    private sealed record Case<T>(string Name, RuleEvaluation Expected, T Value);

    private sealed class Cases
    {
        public List<RequestLine> Requests { get; } = []; public List<OrderLine> Lines { get; } = [];
        public List<Allocation> Allocations { get; } = []; public List<Fulfillment> Fulfillments { get; } = [];
        public List<AllocationFulfillment> AllocationFulfillments { get; } = []; public List<FulfillmentBalance> FulfillmentBalances { get; } = [];
        public List<Case<RequestLine>> RequestCases { get; } = []; public List<Case<Allocation>> AllocationCases { get; } = [];
        public List<Case<FulfillmentBalance>> FulfillmentBalanceCases { get; } = []; public List<Case<AllocationFulfillment>> AllocationFulfillmentCases { get; } = [];
        public required FulfillmentBalance RemovableFulfillmentBalance { get; set; }
        public required AllocationFulfillment RemovalAllocationFulfillment { get; set; }

        public static Cases Create()
        {
            var result = new Cases { RemovableFulfillmentBalance = null!, RemovalAllocationFulfillment = null! };
            RequestCase(result, "A1 deleted request with no allocation", true, []);
            RequestCase(result, "A2 deleted request with deleted allocation", true, [true]);
            RequestCase(result, "A3 deleted request with active allocation", false, [false]);
            RequestCase(result, "A4 mixed allocations leaves active orphan", false, [true, false]);
            RequestCase(result, "A5 active request permits active allocation", true, [false], deleted: false);
            AllocationCase(result, "B1 allocated quantity equals allocation fulfillment total", 10, [4, 6], RuleEvaluation.Valid);
            AllocationCase(result, "B2 allocated quantity differs from allocation fulfillment total", 10, [9], RuleEvaluation.Violation);
            AllocationCase(result, "B3 zero allocation with no allocation fulfillment", 0, [], RuleEvaluation.Valid);
            AllocationCase(result, "B4 nonzero allocation with no allocation fulfillment", 2, [], RuleEvaluation.Violation);
            AllocationCase(result, "B5 multiple allocation fulfillment rows aggregate", 10, [2, 3, 5], RuleEvaluation.Valid);
            var deletedAllocationFulfillment = AllocationCase(result, "B6 deleted allocation fulfillment is excluded", 4, [4, 6], RuleEvaluation.Valid);
            deletedAllocationFulfillment.IsDeleted = true;
            AllocationCase(result, "G1 fulfillment disabled skips allocation fulfillment balance", 10, [], RuleEvaluation.Valid, FulfillmentMode.Disabled);
            AllocationCase(result, "G2 unresolved fulfillment setting", 10, [], RuleEvaluation.Unknown, FulfillmentMode.Unknown);
            FulfillmentBalanceCase(result, "C1 normal line balanced", false, 10, 3, 5, 2, RuleEvaluation.Valid);
            FulfillmentBalanceCase(result, "C2 normal line unbalanced", false, 10, 3, 4, 2, RuleEvaluation.Violation);
            FulfillmentBalanceCase(result, "C3 service line skipped", true, 10, 0, 0, 0, RuleEvaluation.Valid);
            FulfillmentBalanceCase(result, "C4 unresolved service classification", null, 10, 0, 0, 0, RuleEvaluation.Unknown);
            FulfillmentBalanceCase(result, "G3 unresolved fulfillment for fulfillment balance", false, 10, 0, 0, 0, RuleEvaluation.Unknown, FulfillmentMode.Unknown);
            AllocationFulfillmentCase(result, "D1 allocation fulfillment has corresponding fulfillment balance", true, 5001, RuleEvaluation.Valid);
            AllocationFulfillmentCase(result, "D2 allocation fulfillment has no corresponding fulfillment balance", false, 5002, RuleEvaluation.Violation);
            AllocationFulfillmentCase(result, "D3 multiple allocation fulfillments share one fulfillment balance", true, 5003, RuleEvaluation.Valid, count: 2);
            var removal = AllocationFulfillmentCase(result, "D4 baseline before removal", true, 5004, RuleEvaluation.Valid);
            result.RemovalAllocationFulfillment = removal.allocationFulfillment; result.RemovableFulfillmentBalance = removal.fulfillmentBalance!;
            return result;
        }

        private static void RequestCase(Cases c, string name, bool valid, bool[] allocationDeleted, bool deleted = true)
        {
            var request = new RequestLine { IsDeleted = deleted }; c.Requests.Add(request);
            foreach (var isDeleted in allocationDeleted) { var line = new OrderLine(); c.Lines.Add(line); c.Allocations.Add(new Allocation { RequestLineId = request.Id, OrderLineId = line.Id, OrderLine = line, IsDeleted = isDeleted }); }
            c.RequestCases.Add(new(name, valid ? RuleEvaluation.Valid : RuleEvaluation.Violation, request));
        }
        private static AllocationFulfillment AllocationCase(Cases c, string name, decimal quantity, decimal[] allocationFulfillments, RuleEvaluation expected, FulfillmentMode mode = FulfillmentMode.Enabled)
        {
            var line = new OrderLine(); var allocation = new Allocation { RequestLineId = Guid.NewGuid(), OrderLineId = line.Id, OrderLine = line, AllocatedQuantity = quantity, FulfillmentMode = mode };
            c.Lines.Add(line); c.Allocations.Add(allocation); AllocationFulfillment last = null!;
            foreach (var amount in allocationFulfillments) { last = new AllocationFulfillment { AllocationId = allocation.Id, Allocation = allocation, FulfillmentId = 1000 + c.AllocationFulfillments.Count, Quantity = amount }; c.AllocationFulfillments.Add(last); }
            c.AllocationCases.Add(new(name, expected, allocation));
            return last;
        }
        private static void FulfillmentBalanceCase(Cases c, string name, bool? service, decimal total, decimal available, decimal allocated, decimal processed, RuleEvaluation expected, FulfillmentMode mode = FulfillmentMode.Enabled)
        {
            var line = new OrderLine { IsServiceLine = service }; var fulfillmentBalance = new FulfillmentBalance { OrderLineId = line.Id, OrderLine = line, FulfillmentId = 2000 + c.FulfillmentBalances.Count, TotalQuantity = total, AvailableQuantity = available, AllocatedQuantity = allocated, ProcessedQuantity = processed, FulfillmentMode = mode };
            c.Lines.Add(line); c.FulfillmentBalances.Add(fulfillmentBalance); c.FulfillmentBalanceCases.Add(new(name, expected, fulfillmentBalance));
        }
        private static (AllocationFulfillment allocationFulfillment, FulfillmentBalance? fulfillmentBalance) AllocationFulfillmentCase(Cases c, string name, bool hasFulfillmentBalance, long fulfillmentId, RuleEvaluation expected, int count = 1)
        {
            var line = new OrderLine { IsServiceLine = false }; var allocation = new Allocation { RequestLineId = Guid.NewGuid(), OrderLineId = line.Id, OrderLine = line };
            c.Lines.Add(line); c.Allocations.Add(allocation); FulfillmentBalance? fulfillmentBalance = null;
            if (hasFulfillmentBalance) { fulfillmentBalance = new FulfillmentBalance { OrderLineId = line.Id, OrderLine = line, FulfillmentId = fulfillmentId }; c.FulfillmentBalances.Add(fulfillmentBalance); }
            AllocationFulfillment first = null!;
            for (var i = 0; i < count; i++) { var allocationFulfillment = new AllocationFulfillment { AllocationId = allocation.Id, Allocation = allocation, FulfillmentId = fulfillmentId, Quantity = 1 }; c.AllocationFulfillments.Add(allocationFulfillment); c.AllocationFulfillmentCases.Add(new($"{name}{(count > 1 ? $" row {i + 1}" : "")}", expected, allocationFulfillment)); first ??= allocationFulfillment; }
            return (first, fulfillmentBalance);
        }
    }
}
