using Raffinert.Consistency.ApiV2Concept.Common;

namespace Raffinert.Consistency.ApiV2Concept.Current;

internal static class CurrentApi
{
    public static ScenarioModel Create()
    {
        var repairs = new List<Guid>();
        var model = new ConsistencyModelBuilder();

        var simpleLines = model.Objects<SimpleLine>().Named("simple-lines").Key(x => x.Id);
        var simpleRemaining = model.Derived(simpleLines)
            .Compute(x => x.OrderedQuantity - x.FulfilledQuantity)
            .Named("simple-remaining");

        var associations = model.Objects<Association>().Named("associations").Key(x => x.Id);
        var associationUnitRate = model.Derived(associations)
            .DependsOn(x => x.SourceItem.UnitValue)
            .DependsOn(x => x.TargetItem.UnitValue)
            .Compute(x => UnitRateCalculator.Calculate(x.SourceItem.UnitValue, x.TargetItem.UnitValue))
            .Named("association-unit-rate");

        var lines = model.Objects<OrderLine>().Named("order-lines").Key(x => x.Id);
        var fulfillments = model.Objects<Fulfillment>().Named("fulfillments").Key(x => x.Id);
        var allocations = model.Objects<Allocation>().Named("allocations").Key(x => x.Id);
        var matching = model.Relation(lines, fulfillments)
            .Where((line, fulfillment) =>
                line.OrderNumber == fulfillment.OrderNumber &&
                line.ItemNumber == fulfillment.ItemNumber &&
                !fulfillment.Cancelled)
            .Named("matching-fulfillments");
        var fulfilledQuantity = model.Derived(lines)
            .Using(matching)
            .Impact(impact => impact
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid)
                .ItemChanged(DependencySeverity.Invalid))
            .Incrementally()
            .Compute((_, rows) => rows.Sum(x => x.Quantity))
            .Named("fulfilled-quantity");
        var remainingQuantity = model.Derived(lines)
            .Using(fulfilledQuantity)
            .Impact(impact => impact
                .SourceChanged(DependencySeverity.Dirty)
                .SourceMemberChanged(x => x.OrderedQuantity, (oldValue, newValue) =>
                    newValue < oldValue ? DependencySeverity.Invalid : DependencySeverity.Dirty))
            .Compute((line, fulfilled) => line.OrderedQuantity - fulfilled)
            .Named("remaining-quantity");
        var unitRate = model.Derived(lines)
            .Compute(x => x.UnitRate)
            .Named("order-line-unit-rate");
        var allocationValidity = model.Derived(allocations)
            .Using(x => x.OrderLine, remainingQuantity, unitRate)
            .Impact(impact => impact.SourceChanged(DependencySeverity.Invalid))
            .Compute((allocation, remaining, rate) =>
                allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate)
            .Named("allocation-validity");
        var allocationInvariant = model.Invariant(allocations)
            .Using(allocationValidity)
            .Must((_, valid) => valid)
            .Named("allocation-validity-invariant")
            .RepairWhenViolated();

        return new ScenarioModel(
            "Current",
            model.Build(),
            simpleLines,
            simpleRemaining,
            associations,
            associationUnitRate,
            lines,
            fulfillments,
            allocations,
            fulfilledQuantity,
            remainingQuantity,
            allocationValidity,
            allocationInvariant,
            repairs);
    }
}
