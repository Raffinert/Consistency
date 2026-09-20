using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.AllocationDogfood;

public sealed class AllocationConsistencyModel
{
    public AllocationConsistencyModel()
    {
        var model = new ConsistencyModelBuilder();

        Demands = model.Objects<Demand>().Named("demands").Key(value => value.Id);
        Supplies = model.Objects<Supply>().Named("supplies").Key(value => value.Id);
        Allocations = model.Objects<Allocation>().Named("allocations").Key(value => value.Id);
        Fulfillments = model.Objects<Fulfillment>().Named("fulfillments").Key(value => value.Id);

        CandidateSupplies = model.Relation(Demands, Supplies)
            .Where((demand, supply) =>
                demand.ResourceCode == supply.ResourceCode &&
                demand.Date == supply.Date)
            .Named("candidate-supplies");

        SupplyFulfillments = model.Relation(Supplies, Fulfillments)
            .Where((supply, fulfillment) => supply.Id == fulfillment.SupplyId)
            .Named("supply-fulfillments");

        SupplyAllocations = model.Relation(Supplies, Allocations)
            .Where((supply, allocation) => supply.Id == allocation.SupplyId)
            .Named("supply-allocations");

        FulfilledQuantity = model.Derived(Supplies)
            .From(SupplyFulfillments)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Invalid)
                .MembershipRemoved(DependencySeverity.Dirty)
                .ItemMemberChanged(
                    fulfillment => fulfillment.Quantity,
                    (oldValue, newValue) => newValue > oldValue
                        ? DependencySeverity.Invalid
                        : DependencySeverity.Dirty))
            .Sum(fulfillment => fulfillment.Quantity)
            .MaterializeTo(supply => supply.FulfilledQuantity)
            .Named("fulfilled-quantity");

        AllocatedQuantity = model.Derived(Supplies)
            .From(SupplyAllocations)
            .Impact(policy => policy
                .MembershipAdded(DependencySeverity.Invalid)
                .MembershipRemoved(DependencySeverity.Dirty)
                .ItemMemberChanged(
                    allocation => allocation.Quantity,
                    (oldValue, newValue) => newValue > oldValue
                        ? DependencySeverity.Invalid
                        : DependencySeverity.Dirty))
            .Sum(allocation => allocation.Quantity)
            .MaterializeTo(supply => supply.AllocatedQuantity)
            .Named("allocated-quantity");

        RemainingCapacity = model.Derived(Supplies)
            .From(FulfilledQuantity)
            .From(AllocatedQuantity)
            .Impact(policy => policy.SourceMemberChanged(
                supply => supply.Capacity,
                (oldValue, newValue) => newValue < oldValue
                    ? DependencySeverity.Invalid
                    : DependencySeverity.Dirty))
            .Select((supply, fulfilled, allocated) => supply.Capacity - fulfilled - allocated)
            .MaterializeTo(supply => supply.RemainingCapacity)
            .Named("remaining-capacity");

        CapacityInvariant = model.Invariant(Supplies)
            .From(RemainingCapacity)
            .Must((_, remaining) => remaining >= 0m)
            .RepairWhenViolated()
            .Named("supply-capacity-valid");

        HasCompatibleSupply = model.Derived(Allocations)
            .FromMembership(
                CandidateSupplies,
                allocation => allocation.Demand,
                allocation => allocation.Supply)
            .Named("allocation-has-compatible-supply");

        CompatibilityInvariant = model.Invariant(Allocations)
            .From(HasCompatibleSupply)
            .Must((_, compatible) => compatible)
            .RepairWhenViolated()
            .Named("allocation-compatible");

        Compiled = model.Build();
        Mappings = new ConsistencyEfCoreMappings()
            .Map(Demands)
            .Map(Supplies)
            .Map(Allocations)
            .Map(Fulfillments)
            .Enforce(CapacityInvariant)
            .Enforce(CompatibilityInvariant);
    }

    public ObjectSet<Demand> Demands { get; }
    public ObjectSet<Supply> Supplies { get; }
    public ObjectSet<Allocation> Allocations { get; }
    public ObjectSet<Fulfillment> Fulfillments { get; }
    public Relation<Demand, Supply> CandidateSupplies { get; }
    public Relation<Supply, Fulfillment> SupplyFulfillments { get; }
    public Relation<Supply, Allocation> SupplyAllocations { get; }
    public Derived<Supply, decimal> FulfilledQuantity { get; }
    public Derived<Supply, decimal> AllocatedQuantity { get; }
    public Derived<Supply, decimal> RemainingCapacity { get; }
    public Derived<Allocation, bool> HasCompatibleSupply { get; }
    public Invariant<Supply> CapacityInvariant { get; }
    public Invariant<Allocation> CompatibilityInvariant { get; }
    public CompiledConsistencyModel Compiled { get; }
    public ConsistencyEfCoreMappings Mappings { get; }

    public ConsistencyScope CompleteScope() => new ConsistencyScope()
        .Complete(Demands)
        .Complete(Supplies)
        .Complete(Allocations)
        .Complete(Fulfillments);
}
