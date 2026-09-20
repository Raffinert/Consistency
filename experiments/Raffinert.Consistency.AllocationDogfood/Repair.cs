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
    public RepairProcessingResult ProcessCurrentGraph(
        IEnumerable<RepairRequestInfo> requests,
        IEnumerable<Demand> demands,
        IEnumerable<Supply> supplies,
        IEnumerable<Allocation> allocations,
        IEnumerable<Fulfillment> fulfillments)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(demands);
        ArgumentNullException.ThrowIfNull(supplies);
        ArgumentNullException.ThrowIfNull(allocations);
        ArgumentNullException.ThrowIfNull(fulfillments);
        var currentDemands = demands.ToArray();
        var currentSupplies = supplies.ToArray();
        var currentAllocations = allocations.ToArray();
        var currentFulfillments = fulfillments.ToArray();
        var currentRuntime = model.Compiled.CreateRuntime(seed =>
        {
            seed.Add(model.Demands, currentDemands);
            seed.Add(model.Supplies, currentSupplies);
            seed.Add(model.Allocations, currentAllocations);
            seed.Add(model.Fulfillments, currentFulfillments);
        });
        return Process(currentRuntime, requests);
    }

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

    /// <summary>
    /// Repairs one rejected proposed state. A preview is immutable with respect to its tracked
    /// mutation fingerprint, so the caller must obtain a new preview after this method changes an
    /// allocation and before it performs another consistency query.
    /// </summary>
    internal RepairProcessingResult ProcessProposedState(
        ConsistencyPreview preview,
        IEnumerable<RepairRequestInfo> requests)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(requests);
        var reallocated = new List<Allocation>();
        var unresolved = new List<RepairRequirement>();
        foreach (var requirement in requests.Select(ToRequirement))
        {
            switch (requirement.Kind)
            {
                case RepairRequirementKind.SupplyCapacity:
                    if (preview.Evaluate(model.CapacityInvariant, requirement.Supply!))
                        continue;
                    foreach (var allocation in preview.Related(model.SupplyAllocations, requirement.Supply!))
                        if (TryReallocate(allocation, preview))
                        {
                            reallocated.Add(allocation);
                            return new RepairProcessingResult(reallocated, unresolved);
                        }
                    unresolved.Add(requirement);
                    break;
                case RepairRequirementKind.AllocationCompatibility:
                    if (preview.Evaluate(model.CompatibilityInvariant, requirement.Allocation!))
                        continue;
                    if (TryReallocate(requirement.Allocation!, preview))
                    {
                        reallocated.Add(requirement.Allocation!);
                        return new RepairProcessingResult(reallocated, unresolved);
                    }
                    unresolved.Add(requirement);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
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

    private bool TryReallocate(Allocation allocation, ConsistencyPreview preview)
    {
        var replacement = preview.Related(model.CandidateSupplies, allocation.Demand)
            .Where(supply => supply.Id != allocation.SupplyId)
            .OrderBy(supply => supply.Id)
            .FirstOrDefault(supply =>
                preview.Evaluate(model.RemainingCapacity, supply) >= allocation.Quantity);
        if (replacement is null)
            return false;

        allocation.SupplyId = replacement.Id;
        allocation.Supply = replacement;
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
