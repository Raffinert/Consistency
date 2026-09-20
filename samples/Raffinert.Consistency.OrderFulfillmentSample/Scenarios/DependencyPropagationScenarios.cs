using Raffinert.Consistency.OrderFulfillmentSample.Domain;

namespace Raffinert.Consistency.OrderFulfillmentSample.Scenarios;

internal static class DependencyPropagationScenarios
{
    public static void Run()
    {
        var model = new ConsistencyModelBuilder();
        var lines = model.Objects<OrderLine>().Named("order-lines").Key(x => x.Id);
        var fulfillments = model.Objects<Fulfillment>().Named("fulfillments").Key(x => x.Id);
        var allocations = model.Objects<Allocation>().Named("allocations").Key(x => x.Id);

        var matchingFulfillments = model.Relation(lines, fulfillments)
            .Where((l, r) => l.OrderNumber == r.OrderNumber &&
                             l.ItemNumber == r.ItemNumber &&
                             !r.Cancelled)
            .Named("matching-fulfillments");

        var fulfilledQuantity = model.Derived(lines)
            .From(matchingFulfillments)
            .Impact(p => p
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid)
                .ItemChanged(DependencySeverity.Invalid))
            .Sum(x => x.Quantity)
            .Named("fulfilled-quantity");

        var remainingQuantity = model.Derived(lines)
            .From(fulfilledQuantity)
            .Impact(p => p
                .SourceChanged(DependencySeverity.Dirty)
                .SourceMemberChanged(
                    x => x.OrderedQuantity,
                    (oldValue, newValue) => newValue < oldValue
                        ? DependencySeverity.Invalid
                        : DependencySeverity.Dirty))
            .Select((l, fulfilled) => l.OrderedQuantity - fulfilled)
            .Named("remaining-quantity");

        var unitRate = model.Derived(lines)
            .Impact(p => p.SourceChanged(DependencySeverity.Invalid))
            .Select(x => x.UnitRate)
            .Named("unit-rate");

        var allocationValidity = model.Derived(allocations)
            .From(x => x.OrderLine, remainingQuantity, unitRate)
            .Impact(p => p.SourceChanged(DependencySeverity.Invalid))
            .Select((allocation, remaining, rate) =>
                allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate)
            .Named("allocation-validity");

        var allocationInvariant = model.Invariant(allocations)
            .From(allocationValidity)
            .Must((_, value) => value)
            .Named("allocation-validity-invariant")
            .RepairWhenViolated();

        var line = new OrderLine
        {
            OrderNumber = "ORDER-100",
            ItemNumber = 1,
            OrderedQuantity = 10,
            UnitRate = 25
        };
        var fulfillment = new Fulfillment
        {
            OrderNumber = "ORDER-100",
            ItemNumber = 1,
            Quantity = 5
        };
        var allocation = new Allocation
        {
            RequestLineId = Guid.NewGuid(),
            OrderLineId = line.Id,
            OrderLine = line,
            ReservedQuantity = 4,
            CapturedRate = 25
        };

        var runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(lines, [line]);
            seed.Add(fulfillments, [fulfillment]);
            seed.Add(allocations, [allocation]);
        });

        Require(runtime.Version == 0, "bootstrap should not create a mutation version");
        _ = runtime.Evaluate(allocationValidity, allocation);
        _ = runtime.Evaluate(allocationInvariant, allocation);

        line.OrderedQuantity = 12;
        var increase = runtime.ApplyDetailed(
            MutationSet.Create(Change.Property(lines, line, x => x.OrderedQuantity, 10m, 12m)));
        Require(
            SeverityFor(increase.Result, "remaining-quantity", line) == DependencySeverity.Dirty,
            "increase should propagate dirty impact");
        Require(increase.Result.RepairRequests.Count == 0, "valid increase should not request repair");

        line.OrderedQuantity = 8;
        var decrease = runtime.ApplyDetailed(
            MutationSet.Create(Change.Property(lines, line, x => x.OrderedQuantity, 12m, 8m)),
            RuntimeImpactDetailLevel.Causal);
        Require(
            SeverityFor(decrease.Result, "remaining-quantity", line) == DependencySeverity.Invalid,
            "decrease should propagate invalid impact");
        var decreaseRepair = RequireSingleRepair(decrease.Result, "decrease should request repair");
        Require(ReferenceEquals(decreaseRepair.Source, allocation), "decrease should request allocation repair");
        Require(decreaseRepair.Reason == DependencySeverity.Invalid, "decrease repair should carry invalid reason");
        decrease.Dispatch.Invoke();

        fulfillment.Cancelled = true;
        var cancellation = runtime.ApplyDetailed(
            MutationSet.Create(Change.Property(fulfillments, fulfillment, x => x.Cancelled, false, true)));
        Require(
            SeverityFor(cancellation.Result, "fulfilled-quantity", line) == DependencySeverity.Invalid,
            "cancellation should propagate invalid quantity impact");
        Require(
            cancellation.Result.RepairRequests.Count == 0,
            "cancellation should not request repair when reevaluation restores the invariant");

        Console.WriteLine("Order-fulfillment dependency propagation scenarios passed.");
    }

    private static DependencySeverity SeverityFor(
        RuntimeApplyResult result,
        string definitionKey,
        object source)
    {
        var impact = result.DerivedImpacts.Single(value => value.DefinitionKey == definitionKey);
        return impact.Sources.Single(value => ReferenceEquals(value.Source, source)).Severity;
    }

    private static RepairRequestInfo RequireSingleRepair(RuntimeApplyResult result, string message)
    {
        Require(result.RepairRequests.Count == 1, message);
        return result.RepairRequests[0];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
