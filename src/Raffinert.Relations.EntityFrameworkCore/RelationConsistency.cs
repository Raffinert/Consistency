using Microsoft.EntityFrameworkCore;
using System.Transactions;

namespace Raffinert.Relations.EntityFrameworkCore;

public enum RelationEfCoreSaveBehavior { Validate, RecalculateAndValidate }

public sealed class RelationEfCoreConsistencyOptions
{
    public RelationEfCoreSaveBehavior SaveBehavior { get; init; } = RelationEfCoreSaveBehavior.RecalculateAndValidate;
    public RuntimeImpactDetailLevel DetailLevel { get; init; } = RuntimeImpactDetailLevel.Summary;
}

public sealed class RelationInvariantViolationException : Exception
{
    internal RelationInvariantViolationException(IReadOnlyList<PlannedInvariantEvaluation> violations)
        : base("One or more enforced relation invariants would be violated.") => Violations = violations;
    public IReadOnlyList<PlannedInvariantEvaluation> Violations { get; }
}

public sealed class RelationMaterializationSourceNotTrackedException : Exception
{
    internal RelationMaterializationSourceNotTrackedException() : base("An affected materialization source is not tracked by this DbContext instance.") { }
}

public sealed class RelationUnsupportedTransactionException : Exception
{
    internal RelationUnsupportedTransactionException() : base("Consistent save does not support ambient or externally controlled transactions; use the manual RelationUnitOfWork workflow.") { }
}

public sealed class RelationStoreGeneratedKeyRequiresManualWorkflowException : Exception
{
    internal RelationStoreGeneratedKeyRequiresManualWorkflowException(Type entityType, string propertyName)
        : base($"Added entity '{entityType.Name}' uses store-generated Relations key '{propertyName}'; use the manual RelationUnitOfWork workflow after the key is generated.") { }
}

public static class RelationConsistencyDbContextExtensions
{
    public static int SaveChangesConsistently(this DbContext context, RelationRuntime runtime,
        RelationEfCoreMappings mappings, RelationEfCoreConsistencyOptions? options = null)
    {
        var pending = ConsistencyCoordinator.Prepare(context, runtime, mappings, options ?? new());
        int result;
        try { result = context.SaveChanges(); }
        catch { throw; }
        ConsistencyCoordinator.Complete(runtime, pending);
        return result;
    }

    public static async Task<int> SaveChangesConsistentlyAsync(this DbContext context, RelationRuntime runtime,
        RelationEfCoreMappings mappings, RelationEfCoreConsistencyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var pending = ConsistencyCoordinator.Prepare(context, runtime, mappings, options ?? new());
        var result = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        ConsistencyCoordinator.Complete(runtime, pending);
        return result;
    }

}

internal sealed record PendingConsistencySave(RelationUnitOfWork Unit, PreparedImpactPlan? Plan);

internal static class ConsistencyCoordinator
{
    public static void Complete(RelationRuntime runtime, PendingConsistencySave pending)
    {
        try { pending.Unit.Commit(runtime); }
        catch (Exception error) { throw new RelationRuntimeSynchronizationException(runtime.Version, error); }
        pending.Unit.Dispatch(runtime);
    }
    public static PendingConsistencySave Prepare(DbContext context, RelationRuntime runtime,
        RelationEfCoreMappings mappings, RelationEfCoreConsistencyOptions options)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(mappings); ArgumentNullException.ThrowIfNull(options);
        if (context.Database.CurrentTransaction is not null || Transaction.Current is not null)
            throw new RelationUnsupportedTransactionException();
        context.ChangeTracker.DetectChanges();
        RejectStoreGeneratedRelationKeys(context, mappings.UnitOfWorkMappings);
        var enforced = mappings.Validate(context, runtime);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings.UnitOfWorkMappings);
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, options.DetailLevel,
            mappings.HasEnforced ? PlannedInvariantEvaluationMode.Affected : PlannedInvariantEvaluationMode.None,
            options.SaveBehavior == RelationEfCoreSaveBehavior.RecalculateAndValidate && mappings.HasMaterializations
                ? PlannedDerivedEvaluationMode.Affected : PlannedDerivedEvaluationMode.None);
        if (plan is not null)
        {
            var violations = plan.InvariantEvaluations.Where(x => enforced.Contains(x.InvariantId) &&
                x.State == InvariantEvaluationState.Violated).ToArray();
            if (violations.Length > 0) throw new RelationInvariantViolationException(violations);
            if (options.SaveBehavior == RelationEfCoreSaveBehavior.RecalculateAndValidate)
                ApplyMaterializations(context, runtime, mappings, plan);
        }
        context.ChangeTracker.DetectChanges();
        return new PendingConsistencySave(unit, plan);
    }

    private static void RejectStoreGeneratedRelationKeys(DbContext context, RelationUnitOfWorkMappings mappings)
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(x => x.State == EntityState.Added))
        {
            var mapping = mappings.Resolve(entry);
            if (mapping is null) continue;
            foreach (var member in mapping.KeyMembers)
            {
                var property = entry.Metadata.FindProperty(member);
                if (property is not null && property.ValueGenerated != Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never)
                    throw new RelationStoreGeneratedKeyRequiresManualWorkflowException(entry.Metadata.ClrType, property.Name);
            }
        }
    }

    private static void ApplyMaterializations(DbContext context, RelationRuntime runtime,
        RelationEfCoreMappings mappings, PreparedImpactPlan plan)
    {
        var applied = new Stack<(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry Entry, object? Value, bool Modified)>();
        try
        {
            foreach (var mapping in mappings.Materializations)
            {
                var id = runtime.GetDerivedId(mapping.Definition);
                foreach (var evaluation in plan.DerivedEvaluations.Where(x => x.DerivedId == id))
                {
                    if (evaluation.State != DerivedValueState.Fresh) continue;
                    var entry = context.ChangeTracker.Entries().SingleOrDefault(x => ReferenceEquals(x.Entity, evaluation.Source));
                    if (entry is null || entry.State == EntityState.Detached)
                        throw new RelationMaterializationSourceNotTrackedException();
                    var property = entry.Property(mapping.Property.Name);
                    var comparer = property.Metadata.GetValueComparer();
                    if (comparer?.Equals(property.CurrentValue, evaluation.Value) ?? Equals(property.CurrentValue, evaluation.Value))
                        continue;
                    applied.Push((property, property.CurrentValue, property.IsModified));
                    property.CurrentValue = evaluation.Value;
                }
            }
        }
        catch
        {
            while (applied.TryPop(out var write))
            {
                write.Entry.CurrentValue = write.Value;
                write.Entry.IsModified = write.Modified;
            }
            throw;
        }
    }
}
