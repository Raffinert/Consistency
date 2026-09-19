using Raffinert.Consistency.ApiV2Concept.Common;

namespace Raffinert.Consistency.ApiV2Concept.VariantC.HybridBuilderPipeline;

internal static class Models
{
    public static ScenarioModel Create()
    {
        var repairs = new List<Guid>();
        var model = new HybridModel();

        var simpleLines = model.Objects<SimpleLine>().Named("simple-lines").Key(x => x.Id);
        var ordered = model.Derived(simpleLines).Select(x => x.OrderedQuantity).Named("simple-ordered");
        var fulfilled = model.Derived(simpleLines).Select(x => x.FulfilledQuantity).Named("simple-fulfilled");
        var simpleRemaining = model.Derived(simpleLines).From(ordered, fulfilled)
            .Select((_, orderedQuantity, fulfilledQuantity) => orderedQuantity - fulfilledQuantity)
            .Named("simple-remaining");

        var associations = model.Objects<Association>().Named("associations").Key(x => x.Id);
        var associationUnitRate = model.Derived(associations)
            .DependsOn(x => x.SourceItem.UnitValue)
            .DependsOn(x => x.TargetItem.UnitValue)
            .Select(x => UnitRateCalculator.Calculate(x.SourceItem.UnitValue, x.TargetItem.UnitValue))
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
        var fulfilledQuantity = model.Derived(lines).From(matching)
            .Impact(impact => impact
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid)
                .ItemChanged(DependencySeverity.Invalid))
            .Sum(x => x.Quantity)
            .Named("fulfilled-quantity");
        var orderedQuantity = model.Derived(lines).Select(
                x => x.OrderedQuantity,
                impact => impact.SourceMemberChanged(x => x.OrderedQuantity, (oldValue, newValue) =>
                    newValue < oldValue ? DependencySeverity.Invalid : DependencySeverity.Dirty))
            .Named("ordered-quantity");
        var remainingQuantity = model.Derived(lines).From(orderedQuantity, fulfilledQuantity)
            .Select((_, orderedValue, fulfilledValue) => orderedValue - fulfilledValue)
            .Named("remaining-quantity");
        var unitRate = model.Derived(lines).Select(x => x.UnitRate).Named("order-line-unit-rate");
        var allocationValidity = model.Derived(allocations)
            .From(x => x.OrderLine, remainingQuantity, unitRate)
            .Impact(impact => impact.SourceChanged(DependencySeverity.Invalid))
            .Select((allocation, remaining, rate) =>
                allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate)
            .Named("allocation-validity");
        var allocationInvariant = model.Invariant(allocations, allocationValidity)
            .Must((_, valid) => valid)
            .Named("allocation-validity-invariant")
            .RepairWhenViolated();

        return new ScenarioModel(
            "Variant C",
            model.Build(),
            simpleLines.Raw,
            simpleRemaining.Raw,
            associations.Raw,
            associationUnitRate.Raw,
            lines.Raw,
            fulfillments.Raw,
            allocations.Raw,
            fulfilledQuantity.Raw,
            remainingQuantity.Raw,
            allocationValidity.Raw,
            allocationInvariant.Raw,
            repairs);
    }
}
