using Microsoft.EntityFrameworkCore;
using System.Transactions;

namespace Raffinert.Consistency.EntityFrameworkCore;

public enum ConsistencySaveBehavior { Validate, RecalculateAndValidate }

public sealed class ConsistencySaveOptions
{
    public ConsistencySaveBehavior SaveBehavior { get; init; } = ConsistencySaveBehavior.RecalculateAndValidate;
    public RuntimeImpactDetailLevel DetailLevel { get; init; } = RuntimeImpactDetailLevel.Summary;
    public ConsistencyScope? Scope { get; init; }
}

public sealed class IncompleteConsistencyScopeException : Exception
{
    internal IncompleteConsistencyScopeException(IReadOnlyList<ConsistencyScopeGap> gaps)
        : base(CreateMessage(gaps)) => Gaps = gaps;

    public IReadOnlyList<ConsistencyScopeGap> Gaps { get; }

    private static string CreateMessage(IReadOnlyList<ConsistencyScopeGap> gaps)
    {
        var missing = string.Join(", ", gaps.Select(gap =>
            $"{gap.ObjectSetDefinitionKey ?? gap.ObjectType.Name} (set {gap.ObjectSetId}, {gap.RequirementKind})"));
        return $"Persistence was not attempted because authoritative consistency scope coverage is missing: {missing}. " +
            "Seed and maintain authoritative runtime coverage, then declare it through ConsistencyScope. " +
            "Raffinert will not auto-load missing objects.";
    }
}

public sealed class ConsistencyInvariantViolationException : Exception
{
    internal ConsistencyInvariantViolationException(IReadOnlyList<PlannedInvariantEvaluation> violations)
        : base("One or more enforced consistency invariants would be violated.") => Violations = violations;
    public IReadOnlyList<PlannedInvariantEvaluation> Violations { get; }
}

public sealed class ConsistencyMaterializationSourceNotTrackedException : Exception
{
    internal ConsistencyMaterializationSourceNotTrackedException() : base("An affected materialization source is not tracked by this DbContext instance.") { }
}

public sealed class ConsistencyUnsupportedTransactionException : Exception
{
    internal ConsistencyUnsupportedTransactionException() : base("Consistent save does not support ambient or externally controlled transactions; use the manual ConsistencyUnitOfWork workflow.") { }
}

public sealed class ConsistencyStoreGeneratedKeyRequiresManualWorkflowException : Exception
{
    internal ConsistencyStoreGeneratedKeyRequiresManualWorkflowException(Type entityType, string propertyName)
        : base($"Added entity '{entityType.Name}' uses store-generated consistency key '{propertyName}'; " +
            "use CaptureConsistencyUnitOfWork and plan after the key is generated.")
    { }
}

public sealed class ConsistencyStoreGeneratedKeyNotReadyException : Exception
{
    internal ConsistencyStoreGeneratedKeyNotReadyException(Type entityType, string propertyName)
        : base($"Added entity '{entityType.Name}' uses store-generated consistency key '{propertyName}' that is not final yet. " +
            "Save inside the current database transaction to obtain final generated values before calling PrepareAndPlan().")
    { }
}

public static class ConsistencyDbContextExtensions
{
    public static ConsistencyPersistenceUnitOfWork CaptureConsistencyUnitOfWork(
        this DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        ConsistencySaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(mappings);
        context.ChangeTracker.DetectChanges();
        var snapshot = ConsistencyPersistencePolicyEngine.CaptureAndValidate(
            context, runtime, mappings, options ?? new());
        var generatedKeys = ConsistencyGeneratedKeyGuard.CaptureCandidates(
            context, mappings.UnitOfWorkMappings);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings.UnitOfWorkMappings);
        return new ConsistencyPersistenceUnitOfWork(context, runtime, unit, snapshot, generatedKeys);
    }

    public static int SaveChangesConsistently(this DbContext context, ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings, ConsistencySaveOptions? options = null)
    {
        var pending = ConsistencyCoordinator.Prepare(context, runtime, mappings, options ?? new());
        int result;
        try { result = context.SaveChanges(); }
        catch { throw; }
        ConsistencyCoordinator.Complete(runtime, pending);
        return result;
    }

    public static async Task<int> SaveChangesConsistentlyAsync(this DbContext context, ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings, ConsistencySaveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var pending = ConsistencyCoordinator.Prepare(context, runtime, mappings, options ?? new());
        var result = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ConsistencyCoordinator.Complete(runtime, pending);
        return result;
    }

}

internal sealed record PendingConsistencySave(ConsistencyUnitOfWork Unit, PreparedImpactPlan? Plan);

internal static class ConsistencyCoordinator
{
    public static void Complete(ConsistencyRuntime runtime, PendingConsistencySave pending)
    {
        try { pending.Unit.Commit(runtime); }
        catch (Exception error) { throw new ConsistencyRuntimeSynchronizationException(runtime.Version, error); }
        pending.Unit.Dispatch(runtime);
    }
    public static PendingConsistencySave Prepare(DbContext context, ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings, ConsistencySaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(mappings); ArgumentNullException.ThrowIfNull(options);
        if (context.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new ConsistencyUnsupportedTransactionException();
        context.ChangeTracker.DetectChanges();
        ConsistencyGeneratedKeyGuard.RejectForConvenienceSave(context, mappings.UnitOfWorkMappings);
        var policy = ConsistencyPersistencePolicyEngine.CaptureAndValidate(context, runtime, mappings, options);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings.UnitOfWorkMappings);
        var plan = ConsistencyPersistencePolicyEngine.PrepareAndPlan(context, runtime, unit, policy);
        return new PendingConsistencySave(unit, plan);
    }
}
