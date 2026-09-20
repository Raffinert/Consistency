using Raffinert.Consistency.ApiV2Concept.Common;

namespace Raffinert.Consistency.ApiV2Concept.VariantD.ContextDsl;

internal static class Models
{
    public static ScenarioModel Create()
    {
        var context = new DogfoodContext();
        return new ScenarioModel(
            "Variant D",
            context.Build(),
            context.SimpleLines.Raw,
            context.SimpleRemaining.Raw,
            context.Associations.Raw,
            context.AssociationUnitRate.Raw,
            context.Lines.Raw,
            context.Fulfillments.Raw,
            context.Allocations.Raw,
            context.FulfilledQuantity.Raw,
            context.RemainingQuantity.Raw,
            context.AllocationValidity.Raw,
            context.AllocationInvariant.Raw,
            context.Repairs);
    }
}

internal sealed class DogfoodContext : ConsistencyModelContext
{
    public DogfoodContext()
    {
        SimpleLines = Objects<SimpleLine, int>("simple-lines", x => x.Id);
        var simpleOrdered = Select(SimpleLines, "simple-ordered", x => x.OrderedQuantity);
        var simpleFulfilled = Select(SimpleLines, "simple-fulfilled", x => x.FulfilledQuantity);
        SimpleRemaining = Combine(
            SimpleLines,
            "simple-remaining",
            simpleOrdered,
            simpleFulfilled,
            (_, ordered, fulfilled) => ordered - fulfilled);

        Associations = Objects<Association, int>("associations", x => x.Id);
        AssociationUnitRate = DependsOn(Associations, x => x.SourceItem.UnitValue)
            .DependsOn(x => x.TargetItem.UnitValue)
            .Select("association-unit-rate",
                x => UnitRateCalculator.Calculate(x.SourceItem.UnitValue, x.TargetItem.UnitValue));

        Lines = Objects<OrderLine, Guid>("order-lines", x => x.Id);
        Fulfillments = Objects<Fulfillment, Guid>("fulfillments", x => x.Id);
        Allocations = Objects<Allocation, Guid>("allocations", x => x.Id);
        MatchingFulfillments = Relate(
            Lines,
            Fulfillments,
            "matching-fulfillments",
            (line, fulfillment) =>
                line.OrderNumber == fulfillment.OrderNumber &&
                line.ItemNumber == fulfillment.ItemNumber &&
                !fulfillment.Cancelled);
        FulfilledQuantity = SumByLeft(
            MatchingFulfillments,
            "fulfilled-quantity",
            x => x.Quantity,
            impact => impact
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid)
                .ItemChanged(DependencySeverity.Invalid));
        var orderedQuantity = Select(
            Lines,
            "ordered-quantity",
            x => x.OrderedQuantity,
            impact => impact.SourceMemberChanged(x => x.OrderedQuantity, (oldValue, newValue) =>
                newValue < oldValue ? DependencySeverity.Invalid : DependencySeverity.Dirty));
        RemainingQuantity = Combine(
            Lines,
            "remaining-quantity",
            orderedQuantity,
            FulfilledQuantity,
            (_, ordered, fulfilled) => ordered - fulfilled);
        var unitRate = Select(Lines, "order-line-unit-rate", x => x.UnitRate);
        AllocationValidity = Project(
            Allocations,
            "allocation-validity",
            x => x.OrderLine,
            RemainingQuantity,
            unitRate,
            (allocation, remaining, rate) =>
                allocation.ReservedQuantity <= remaining && allocation.CapturedRate == rate,
            impact => impact.SourceChanged(DependencySeverity.Invalid));
        AllocationInvariant = Invariant(
            Allocations,
            AllocationValidity,
            "allocation-validity-invariant");
    }

    public ContextSet<SimpleLine> SimpleLines { get; }
    public ContextValue<SimpleLine, decimal> SimpleRemaining { get; }
    public ContextSet<Association> Associations { get; }
    public ContextValue<Association, decimal?> AssociationUnitRate { get; }
    public ContextSet<OrderLine> Lines { get; }
    public ContextSet<Fulfillment> Fulfillments { get; }
    public ContextSet<Allocation> Allocations { get; }
    public ContextRelation<OrderLine, Fulfillment> MatchingFulfillments { get; }
    public ContextValue<OrderLine, decimal> FulfilledQuantity { get; }
    public ContextValue<OrderLine, decimal> RemainingQuantity { get; }
    public ContextValue<Allocation, bool> AllocationValidity { get; }
    public ContextInvariant<Allocation> AllocationInvariant { get; }
    public List<Guid> Repairs { get; } = [];
}
