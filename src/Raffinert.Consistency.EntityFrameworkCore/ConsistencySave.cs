using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
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
            "Either assert closed-world coverage with ConsistencyScope.Complete(set), or register a host-owned " +
            "DiscoverConsumers query for an eligible direct reference-navigation dependency. Raffinert does not " +
            "invent database queries and cannot prove that a host resolver did not under-fetch.";
    }
}

public sealed class ConsistencyInvariantViolationException : Exception
{
    internal ConsistencyInvariantViolationException(
        IReadOnlyList<PlannedInvariantEvaluation> violations,
        IReadOnlyList<RepairRequestInfo> repairRequests,
        PreparedImpactPlan rejectedPlan)
        : base("One or more enforced consistency invariants would be violated.")
    {
        Violations = violations;
        RepairRequests = repairRequests;
        RejectedPlan = rejectedPlan;
    }

    public IReadOnlyList<PlannedInvariantEvaluation> Violations { get; }
    public IReadOnlyList<RepairRequestInfo> RepairRequests { get; }
    internal PreparedImpactPlan RejectedPlan { get; }
    internal EfMutationFingerprint? Fingerprint { get; private set; }
    internal long BaselineRevision { get; private set; }

    internal void BindRejectedState(EfMutationFingerprint fingerprint, long baselineRevision)
    {
        Fingerprint = fingerprint;
        BaselineRevision = baselineRevision;
    }
}

public sealed class ConsistencyMaterializationSourceNotTrackedException : Exception
{
    internal ConsistencyMaterializationSourceNotTrackedException() : base("An affected materialization source is not tracked by this DbContext instance.") { }
}

public sealed class ConsistencyUnsupportedTransactionException : Exception
{
    internal ConsistencyUnsupportedTransactionException() : base(
        "Consistent save does not support ambient or externally controlled transactions; " +
        "use the policy-aware CaptureConsistencyUnitOfWork(...) workflow.")
    { }
}

public sealed class ConsistencyStoreGeneratedKeyRequiresManualWorkflowException : Exception
{
    internal ConsistencyStoreGeneratedKeyRequiresManualWorkflowException(Type entityType, string propertyName)
        : base($"Added entity '{entityType.Name}' uses store-generated consistency key '{propertyName}'; " +
            "use CaptureConsistencyUnitOfWork and plan after the key is generated.")
    { }
}

public sealed class ConsistencyStoreGeneratedValueRequiresManualWorkflowException : Exception
{
    /// <summary>Identifies a semantic consistency input that must be generated before planning.</summary>
    internal ConsistencyStoreGeneratedValueRequiresManualWorkflowException(Type entityType, string propertyName)
        : base($"Store-generated consistency input '{entityType.Name}.{propertyName}' is not final before SQL. " +
            "Use CaptureConsistencyUnitOfWork and plan after generated values are final.")
    {
        EntityType = entityType;
        PropertyName = propertyName;
    }

    public Type EntityType { get; }
    public string PropertyName { get; }
}

public sealed class ConsistencyStoreGeneratedValueNotReadyException : Exception
{
    /// <summary>Identifies a semantic consistency input whose store-generated value is not final.</summary>
    internal ConsistencyStoreGeneratedValueNotReadyException(Type entityType, string propertyName)
        : base($"Store-generated consistency input '{entityType.Name}.{propertyName}' is not final yet. " +
            "Save inside the current database transaction to obtain generated values/fixup before PrepareAndPlan().")
    {
        EntityType = entityType;
        PropertyName = propertyName;
    }

    public Type EntityType { get; }
    public string PropertyName { get; }
}

/// <summary>
/// Indicates that EF metadata may replace the Raffinert identity of an existing object during UPDATE.
/// Runtime identities are immutable, so this operation must use a different persistence design.
/// </summary>
public sealed class ConsistencyStoreGeneratedIdentityUpdateNotSupportedException : Exception
{
    /// <summary>Identifies an existing consistency identity that EF may replace during UPDATE.</summary>
    internal ConsistencyStoreGeneratedIdentityUpdateNotSupportedException(Type entityType, string propertyName)
        : base($"Store-generated UPDATE of consistency identity '{entityType.Name}.{propertyName}' is not supported.")
    {
        EntityType = entityType;
        PropertyName = propertyName;
    }

    /// <summary>The tracked entity type whose consistency identity may change.</summary>
    public Type EntityType { get; }
    /// <summary>The store-generated identity property.</summary>
    public string PropertyName { get; }
}

public static class ConsistencyDbContextExtensions
{
    /// <summary>
    /// Captures immutable pre-save relationship and scalar evidence together with EF persistence policy.
    /// Before planning, only value transitions proven to be EF store-generated key propagation are
    /// finalized; arbitrary changes after capture remain subject to strict new-value validation.
    /// </summary>
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
        ConsistencyStoreSideEffectGuard.ThrowIfUnsafe(context, runtime);
        var snapshot = ConsistencyPersistencePolicyEngine.CaptureAndValidate(
            context, runtime, mappings, options ?? new());
        var generatedValues = ConsistencyGeneratedValueGuard.CaptureCandidates(
            context, runtime, mappings.UnitOfWorkMappings);
        var mutations = ChangeTrackerAdapter.CapturePolicyAwareSnapshot(
            context.ChangeTracker, mappings.UnitOfWorkMappings);
        return new ConsistencyPersistenceUnitOfWork(
            context, runtime, mappings, mutations, snapshot, generatedValues);
    }

    public static int SaveChangesConsistently(this DbContext context, ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings, ConsistencySaveOptions? options = null)
    {
        var pending = ConsistencyCoordinator.Prepare(context, runtime, mappings, options ?? new());
        int result;
        try { result = context.SaveChanges(); }
        catch
        {
            pending.MaterializationRollback?.Restore();
            throw;
        }
        ConsistencyCoordinator.Complete(runtime, pending);
        return result;
    }

    public static async Task<int> SaveChangesConsistentlyAsync(this DbContext context, ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings, ConsistencySaveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var pending = await ConsistencyCoordinator.PrepareAsync(
            context, runtime, mappings, options ?? new(), cancellationToken).ConfigureAwait(false);
        int result;
        try
        {
            result = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pending.MaterializationRollback?.Restore();
            throw;
        }
        ConsistencyCoordinator.Complete(runtime, pending);
        return result;
    }

}

internal sealed record PendingConsistencySave(
    ConsistencyUnitOfWork Unit,
    PreparedImpactPlan? Plan,
    MaterializationRollback? MaterializationRollback,
    EfMutationFingerprint Fingerprint,
    long BaselineRevision);

internal static class ConsistencyCoordinator
{
    public static void Complete(ConsistencyRuntime runtime, PendingConsistencySave pending)
    {
        try { pending.Unit.Commit(runtime); }
        catch (Exception error) { throw new ConsistencyRuntimeSynchronizationException(runtime.Version, error); }
        pending.Unit.Dispatch(runtime);
    }
    public static PendingConsistencySave Prepare(DbContext context, ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings, ConsistencySaveOptions options,
        Func<EntityEntry, Microsoft.EntityFrameworkCore.Metadata.IProperty, bool>? includeProperty = null,
        bool forceMaterialization = false,
        Func<MaterializationDescriptor, object, bool>? materializationSelector = null)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(mappings); ArgumentNullException.ThrowIfNull(options);
        if (context.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new ConsistencyUnsupportedTransactionException();
        context.ChangeTracker.DetectChanges();
        ConsistencyStoreSideEffectGuard.ThrowIfUnsafe(context, runtime);
        ConsistencyGeneratedValueGuard.RejectForConvenienceSave(
            context, runtime, mappings.UnitOfWorkMappings);
        var policy = ConsistencyPersistencePolicyEngine.CaptureAndValidate(context, runtime, mappings, options);
        var captured = CaptureUnitOfWork(context, mappings, includeProperty);
        var admissions = ExternalConsumerDiscovery.Discover(
            context, runtime, mappings, captured, options.SaveBehavior, options.Scope);
        var unit = Combine(captured, admissions);
        var fingerprint = EfMutationFingerprint.Create(unit.Mutations);
        PreparedImpactPlan? plan;
        MaterializationRollback? materializationRollback;
        try
        {
            plan = ConsistencyPersistencePolicyEngine.PrepareAndPlan(
                context, runtime, unit, policy, out materializationRollback,
                forceMaterialization, materializationSelector);
        }
        catch (ConsistencyInvariantViolationException error)
        {
            error.BindRejectedState(fingerprint, runtime.BaselineRevision);
            throw;
        }
        return new PendingConsistencySave(
            unit, plan, materializationRollback, fingerprint,
            runtime.BaselineRevision);
    }

    public static async Task<PendingConsistencySave> PrepareAsync(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        ConsistencySaveOptions options,
        CancellationToken cancellationToken,
        Func<EntityEntry, Microsoft.EntityFrameworkCore.Metadata.IProperty, bool>? includeProperty = null,
        bool forceMaterialization = false,
        Func<MaterializationDescriptor, object, bool>? materializationSelector = null)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(mappings); ArgumentNullException.ThrowIfNull(options);
        if (context.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new ConsistencyUnsupportedTransactionException();
        context.ChangeTracker.DetectChanges();
        ConsistencyStoreSideEffectGuard.ThrowIfUnsafe(context, runtime);
        ConsistencyGeneratedValueGuard.RejectForConvenienceSave(context, runtime, mappings.UnitOfWorkMappings);
        var policy = ConsistencyPersistencePolicyEngine.CaptureAndValidate(context, runtime, mappings, options);
        var captured = CaptureUnitOfWork(context, mappings, includeProperty);
        var admissions = await ExternalConsumerDiscovery.DiscoverAsync(
            context, runtime, mappings, captured, cancellationToken, options.SaveBehavior, options.Scope)
            .ConfigureAwait(false);
        var unit = Combine(captured, admissions);
        var fingerprint = EfMutationFingerprint.Create(unit.Mutations);
        PreparedImpactPlan? plan;
        MaterializationRollback? materializationRollback;
        try
        {
            plan = ConsistencyPersistencePolicyEngine.PrepareAndPlan(
                context, runtime, unit, policy, out materializationRollback,
                forceMaterialization, materializationSelector);
        }
        catch (ConsistencyInvariantViolationException error)
        {
            error.BindRejectedState(fingerprint, runtime.BaselineRevision);
            throw;
        }
        return new PendingConsistencySave(
            unit, plan, materializationRollback, fingerprint,
            runtime.BaselineRevision);
    }

    public static EfMutationFingerprint CaptureFingerprint(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        ConsistencySaveOptions options,
        Func<EntityEntry, Microsoft.EntityFrameworkCore.Metadata.IProperty, bool>? includeProperty = null)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(mappings); ArgumentNullException.ThrowIfNull(options);
        if (context.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new ConsistencyUnsupportedTransactionException();
        context.ChangeTracker.DetectChanges();
        ConsistencyStoreSideEffectGuard.ThrowIfUnsafe(context, runtime);
        ConsistencyGeneratedValueGuard.RejectForConvenienceSave(
            context, runtime, mappings.UnitOfWorkMappings);
        _ = ConsistencyPersistencePolicyEngine.CaptureAndValidate(context, runtime, mappings, options);
        var captured = CaptureUnitOfWork(context, mappings, includeProperty);
        var admissions = ExternalConsumerDiscovery.Discover(
            context, runtime, mappings, captured, options.SaveBehavior, options.Scope);
        return EfMutationFingerprint.Create(Combine(captured, admissions).Mutations);
    }

    internal static ConsistencyUnitOfWork CaptureCurrentUnitOfWork(
        DbContext context,
        ConsistencyEfCoreMappings mappings,
        Func<EntityEntry, Microsoft.EntityFrameworkCore.Metadata.IProperty, bool>? includeProperty = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mappings);
        context.ChangeTracker.DetectChanges();
        return CaptureUnitOfWork(context, mappings, includeProperty);
    }

    public static async Task<EfMutationFingerprint> CaptureFingerprintAsync(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        ConsistencySaveOptions options,
        CancellationToken cancellationToken,
        Func<EntityEntry, Microsoft.EntityFrameworkCore.Metadata.IProperty, bool>? includeProperty = null)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(mappings); ArgumentNullException.ThrowIfNull(options);
        if (context.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new ConsistencyUnsupportedTransactionException();
        context.ChangeTracker.DetectChanges();
        ConsistencyStoreSideEffectGuard.ThrowIfUnsafe(context, runtime);
        ConsistencyGeneratedValueGuard.RejectForConvenienceSave(
            context, runtime, mappings.UnitOfWorkMappings);
        _ = ConsistencyPersistencePolicyEngine.CaptureAndValidate(context, runtime, mappings, options);
        var captured = CaptureUnitOfWork(context, mappings, includeProperty);
        var admissions = await ExternalConsumerDiscovery.DiscoverAsync(
            context, runtime, mappings, captured, cancellationToken, options.SaveBehavior, options.Scope)
            .ConfigureAwait(false);
        return EfMutationFingerprint.Create(Combine(captured, admissions).Mutations);
    }

    private static ConsistencyUnitOfWork CaptureUnitOfWork(
        DbContext context,
        ConsistencyEfCoreMappings mappings,
        Func<EntityEntry, Microsoft.EntityFrameworkCore.Metadata.IProperty, bool>? includeProperty)
    {
        return includeProperty is null
            ? ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings.UnitOfWorkMappings)
            : ChangeTrackerAdapter.CaptureUnitOfWork(
                context.ChangeTracker, mappings.UnitOfWorkMappings, includeProperty);
    }

    private static ConsistencyUnitOfWork Combine(
        ConsistencyUnitOfWork captured,
        IReadOnlyList<RuntimeMutation> admissions) =>
        new(MutationSet.Combine(captured.Mutations.Concat(admissions)));
}
