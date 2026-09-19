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

public sealed record RepairProcessingResult(
    IReadOnlyList<Allocation> Reallocated,
    IReadOnlyList<RepairRequirement> Unresolved);

public sealed class ReallocateDemand(AllocationConsistencyModel model)
{
    public RepairProcessingResult Process(
        ConsistencyRuntime runtime,
        IEnumerable<RepairRequestInfo> requests)
    {
        var reallocated = new List<Allocation>();
        var unresolved = new List<RepairRequirement>();
        var requirements = new Queue<RepairRequirement>(requests.Select(ToRequirement));
        var attempts = 0;

        while (requirements.TryDequeue(out var requirement))
        {
            if (++attempts > 100)
                throw new InvalidOperationException("Repair processing did not converge.");

            switch (requirement.Kind)
            {
                case RepairRequirementKind.SupplyCapacity:
                    ProcessSupply(requirement, runtime, requirements, reallocated, unresolved);
                    break;
                case RepairRequirementKind.AllocationCompatibility:
                    ProcessAllocation(requirement, runtime, requirements, reallocated, unresolved);
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
        Queue<RepairRequirement> requirements,
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
            if (TryReallocate(allocation, runtime, requirements))
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
        Queue<RepairRequirement> requirements,
        List<Allocation> reallocated,
        List<RepairRequirement> unresolved)
    {
        var allocation = requirement.Allocation!;
        if (runtime.Evaluate(model.CompatibilityInvariant, allocation))
            return;
        if (TryReallocate(allocation, runtime, requirements))
            reallocated.Add(allocation);
        else
            unresolved.Add(requirement);
    }

    private bool TryReallocate(
        Allocation allocation,
        ConsistencyRuntime runtime,
        Queue<RepairRequirement> requirements)
    {
        var replacement = runtime.Related(model.CandidateSupplies, allocation.Demand)
            .Where(supply => supply.Id != allocation.SupplyId)
            .OrderBy(supply => supply.Id)
            .FirstOrDefault(supply =>
                runtime.Evaluate(model.RemainingCapacity, supply) >= allocation.Quantity);
        if (replacement is null)
            return false;

        var oldSupplyId = allocation.SupplyId;
        var oldSupply = allocation.Supply;
        allocation.SupplyId = replacement.Id;
        allocation.Supply = replacement;
        var application = runtime.ApplyDetailed(MutationSet.Create(
            Change.Property(model.Allocations, allocation,
                value => value.SupplyId, oldSupplyId, replacement.Id),
            Change.Property(model.Allocations, allocation,
                value => value.Supply, oldSupply, replacement)),
            RuntimeImpactDetailLevel.Causal);
        foreach (var request in application.Result.RepairRequests)
            requirements.Enqueue(ToRequirement(request));
        return true;
    }

    private RepairRequirement ToRequirement(RepairRequestInfo request)
    {
        if (request.DefinitionKey == model.CapacityInvariant.DefinitionKey && request.Source is Supply supply)
            return new RepairRequirement(RepairRequirementKind.SupplyCapacity, supply, null);
        if (request.DefinitionKey == model.CompatibilityInvariant.DefinitionKey &&
            request.Source is Allocation allocation)
            return new RepairRequirement(RepairRequirementKind.AllocationCompatibility, null, allocation);
        throw new InvalidOperationException(
            $"Unsupported repair request '{request.DefinitionKey}' for '{request.Source.GetType().Name}'.");
    }
}
