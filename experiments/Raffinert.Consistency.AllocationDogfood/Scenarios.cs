namespace Raffinert.Consistency.AllocationDogfood;

internal static class Scenarios
{
    private static readonly (string Name, Func<Task> Run)[] Core =
    [
        Sync(nameof(CoreScenarios.CandidateSupply_IsFound_WhenResourceAndDateMatch),
            CoreScenarios.CandidateSupply_IsFound_WhenResourceAndDateMatch),
        Sync(nameof(CoreScenarios.CandidateSupply_IsRemoved_WhenResourceStopsMatching),
            CoreScenarios.CandidateSupply_IsRemoved_WhenResourceStopsMatching),
        Sync(nameof(CoreScenarios.CandidateRelations_TrackBothSidesAndIgnoreUnrelatedChanges),
            CoreScenarios.CandidateRelations_TrackBothSidesAndIgnoreUnrelatedChanges),
        Sync(nameof(CoreScenarios.FulfilledQuantity_TracksFulfillmentChanges),
            CoreScenarios.FulfilledQuantity_TracksFulfillmentChanges),
        Sync(nameof(CoreScenarios.AllocatedQuantity_TracksAllocationChanges),
            CoreScenarios.AllocatedQuantity_TracksAllocationChanges),
        Sync(nameof(CoreScenarios.RemainingCapacity_UsesDerivedFulfilledAndAllocatedValues),
            CoreScenarios.RemainingCapacity_UsesDerivedFulfilledAndAllocatedValues),
        Sync(nameof(CoreScenarios.CapacityIncrease_DoesNotCreateInvariantViolation),
            CoreScenarios.CapacityIncrease_DoesNotCreateInvariantViolation),
        Sync(nameof(CoreScenarios.CapacityDecrease_StillValid_RevalidatesSuccessfully),
            CoreScenarios.CapacityDecrease_StillValid_RevalidatesSuccessfully),
        Sync(nameof(CoreScenarios.CapacityDecrease_RevalidatesAndRejectsViolation),
            CoreScenarios.CapacityDecrease_RevalidatesAndRejectsViolation),
        Sync(nameof(CoreScenarios.FulfillmentIncrease_CanInvalidateCapacityInvariant),
            CoreScenarios.FulfillmentIncrease_CanInvalidateCapacityInvariant),
        Sync(nameof(CoreScenarios.FulfillmentDecrease_ReleasesCapacity_WithDirectionalDirtyImpact),
            CoreScenarios.FulfillmentDecrease_ReleasesCapacity_WithDirectionalDirtyImpact),
        Sync(nameof(CoreScenarios.AllocationIncrease_CanInvalidateCapacityInvariant),
            CoreScenarios.AllocationIncrease_CanInvalidateCapacityInvariant),
        Sync(nameof(CoreScenarios.AllocationDecrease_ReleasesCapacity_WithDirectionalDirtyImpact),
            CoreScenarios.AllocationDecrease_ReleasesCapacity_WithDirectionalDirtyImpact),
        Sync(nameof(CoreScenarios.CompatibilityChange_InvalidatesExistingAllocation),
            CoreScenarios.CompatibilityChange_InvalidatesExistingAllocation),
        Sync(nameof(CoreScenarios.DemandDateChange_InvalidatesExistingAllocation),
            CoreScenarios.DemandDateChange_InvalidatesExistingAllocation),
        Sync(nameof(CoreScenarios.InvalidAllocation_CanBeRepairedToAnotherCandidateSupply),
            CoreScenarios.InvalidAllocation_CanBeRepairedToAnotherCandidateSupply),
        Sync(nameof(CoreScenarios.InsufficientCapacity_CanTriggerReallocation),
            CoreScenarios.InsufficientCapacity_CanTriggerReallocation),
        Sync(nameof(CoreScenarios.NoReplacementSupply_DoesNotSilentlyAcceptInvalidAllocation),
            CoreScenarios.NoReplacementSupply_DoesNotSilentlyAcceptInvalidAllocation),
        Sync(nameof(CoreScenarios.UnrelatedSupplyMutation_DoesNotAffectOtherGraph),
            CoreScenarios.UnrelatedSupplyMutation_DoesNotAffectOtherGraph),
        Sync(nameof(CoreScenarios.Evaluate_DoesNotWriteMaterializedMirror),
            CoreScenarios.Evaluate_DoesNotWriteMaterializedMirror),
        Sync(nameof(CoreScenarios.Materialize_WritesConfiguredMirrors),
            CoreScenarios.Materialize_WritesConfiguredMirrors),
        Sync(nameof(CoreScenarios.RichDomainMutation_StillTriggersConsistencyConsequences),
            CoreScenarios.RichDomainMutation_StillTriggersConsistencyConsequences)
    ];

    private static readonly (string Name, Func<Task> Run)[] Ef =
    [
        (nameof(EfScenarios.EfSave_EnforcesConfiguredInvariant),
            EfScenarios.EfSave_EnforcesConfiguredInvariant),
        (nameof(EfScenarios.EfMaterializeBeforeRead_UsesInjectedRuntimeAndOrdinarySave),
            EfScenarios.EfMaterializeBeforeRead_UsesInjectedRuntimeAndOrdinarySave),
        (nameof(EfScenarios.EfSaveWithoutPreRead_MaterializesAtBoundary),
            EfScenarios.EfSaveWithoutPreRead_MaterializesAtBoundary),
        (nameof(EfScenarios.EfSqlFailure_DoesNotCommitRuntime_AndRetrySucceeds),
            EfScenarios.EfSqlFailure_DoesNotCommitRuntime_AndRetrySucceeds),
        (nameof(EfScenarios.EfLateTracking_InvalidatesPendingPlanBeforeSave),
            EfScenarios.EfLateTracking_InvalidatesPendingPlanBeforeSave),
        (nameof(EfScenarios.PartialTrackedGraph_IsNotTreatedAsAuthoritativelyComplete),
            EfScenarios.PartialTrackedGraph_IsNotTreatedAsAuthoritativelyComplete),
        (nameof(EfScenarios.EfRejectedSave_CanBeRepairedAndRetried),
            EfScenarios.EfRejectedSave_CanBeRepairedAndRetried),
        (nameof(EfScenarios.EfRejectedPreview_InvalidatesAfterTrackedChange),
            EfScenarios.EfRejectedPreview_InvalidatesAfterTrackedChange),
        (nameof(EfScenarios.EfRejectedSave_MultiStepRepair_ReplansUntilConsistent),
            EfScenarios.EfRejectedSave_MultiStepRepair_ReplansUntilConsistent),
        (nameof(EfScenarios.EfRejectedSave_RepairCanCreateViolationOnAnotherSupply),
            EfScenarios.EfRejectedSave_RepairCanCreateViolationOnAnotherSupply)
    ];

    public static async Task RunAsync()
    {
        foreach (var scenario in Core)
        {
            await scenario.Run();
            Console.WriteLine($"PASS {scenario.Name}");
        }
        foreach (var scenario in Ef)
        {
            await scenario.Run();
            Console.WriteLine($"PASS {scenario.Name}");
        }
        ProposedStateBenchmark.Run();
        RejectedEfPreviewBenchmark.Run();
        Console.WriteLine($"Allocation dogfood scenarios passed: {Core.Length + Ef.Length}.");
    }

    private static (string Name, Func<Task> Run) Sync(string name, Action action) =>
        (name, () =>
        {
            action();
            return Task.CompletedTask;
        }
    );
}
