namespace Raffinert.Consistency.AllocationDogfood;

public enum RepairRequirementKind
{
    SupplyCapacity,
    AllocationCompatibility
}

public sealed record RepairRequirement(
    RepairRequirementKind Kind,
    Supply? Supply,
    Allocation? Allocation);

public sealed class RepairQueue
{
    private readonly Queue<RepairRequirement> _requirements = new();

    public int Count => _requirements.Count;

    public IReadOnlyList<RepairRequirement> Snapshot => _requirements.ToArray();

    public void RequireCapacityRepair(Supply supply) =>
        _requirements.Enqueue(new RepairRequirement(RepairRequirementKind.SupplyCapacity, supply, null));

    public void RequireCompatibilityRepair(Allocation allocation) =>
        _requirements.Enqueue(new RepairRequirement(
            RepairRequirementKind.AllocationCompatibility, null, allocation));

    public bool TryDequeue(out RepairRequirement requirement) => _requirements.TryDequeue(out requirement!);

    public void Clear() => _requirements.Clear();
}

public sealed record RepairProcessingResult(
    IReadOnlyList<Allocation> Reallocated,
    IReadOnlyList<RepairRequirement> Unresolved);

public sealed class ReallocateDemand(AllocationConsistencyModel model)
{
    public RepairProcessingResult Process(ConsistencyRuntime runtime)
    {
        var reallocated = new List<Allocation>();
        var unresolved = new List<RepairRequirement>();
        var attempts = 0;

        while (model.Repairs.TryDequeue(out var requirement))
        {
            if (++attempts > 100)
                throw new InvalidOperationException("Repair processing did not converge.");

            switch (requirement.Kind)
            {
                case RepairRequirementKind.SupplyCapacity:
                    ProcessSupply(requirement, runtime, reallocated, unresolved);
                    break;
                case RepairRequirementKind.AllocationCompatibility:
                    ProcessAllocation(requirement, runtime, reallocated, unresolved);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(requirement));
            }
        }

        return new RepairProcessingResult(reallocated, unresolved);
    }

    private void ProcessSupply(
        RepairRequirement requirement,
        ConsistencyRuntime runtime,
        List<Allocation> reallocated,
        List<RepairRequirement> unresolved)
    {
        var supply = requirement.Supply!;
        if (runtime.Evaluate(model.CapacityInvariant, supply))
            return;

        var allocations = runtime.Related(model.SupplyAllocations, supply)
            .OrderBy(allocation => allocation.Id)
            .ToArray();
        foreach (var allocation in allocations)
        {
            if (TryReallocate(allocation, runtime))
            {
                reallocated.Add(allocation);
                if (runtime.Evaluate(model.CapacityInvariant, supply))
                    return;
            }
        }

        unresolved.Add(requirement);
    }

    private void ProcessAllocation(
        RepairRequirement requirement,
        ConsistencyRuntime runtime,
        List<Allocation> reallocated,
        List<RepairRequirement> unresolved)
    {
        var allocation = requirement.Allocation!;
        if (runtime.Evaluate(model.CompatibilityInvariant, allocation))
            return;
        if (TryReallocate(allocation, runtime))
            reallocated.Add(allocation);
        else
            unresolved.Add(requirement);
    }

    private bool TryReallocate(Allocation allocation, ConsistencyRuntime runtime)
    {
        var replacement = runtime.Related(model.CandidateSupplies, allocation.Demand)
            .Where(supply => supply.Id != allocation.SupplyId)
            .OrderBy(supply => supply.Id)
            .FirstOrDefault(supply =>
                runtime.Evaluate(model.RemainingCapacity, supply) >= allocation.Quantity);
        if (replacement is null)
            return false;

        var oldSupplyId = allocation.SupplyId;
        allocation.SupplyId = replacement.Id;
        allocation.Supply = replacement;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(model.Allocations, allocation,
                value => value.SupplyId, oldSupplyId, replacement.Id)),
            RuntimeImpactDetailLevel.Causal);
        application.Dispatch.Invoke();
        return true;
    }
}
