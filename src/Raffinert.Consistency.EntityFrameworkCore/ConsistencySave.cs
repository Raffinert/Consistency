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
        : base("One or more enforced relation invariants would be violated.") => Violations = violations;
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
        : base($"Added entity '{entityType.Name}' uses store-generated Relations key '{propertyName}'; use the manual ConsistencyUnitOfWork workflow after the key is generated.") { }
}

public static class ConsistencyDbContextExtensions
{
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
        RejectStoreGeneratedRelationKeys(context, mappings.UnitOfWorkMappings);
        var enforced = mappings.Validate(context, runtime);
        var scopeGaps = mappings.GetScopeGaps(runtime, options.Scope, options.SaveBehavior);
        if (scopeGaps.Count > 0) throw new IncompleteConsistencyScopeException(scopeGaps);
        var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings.UnitOfWorkMappings);
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, options.DetailLevel,
            mappings.HasEnforced ? PlannedInvariantEvaluationMode.Affected : PlannedInvariantEvaluationMode.None,
            options.SaveBehavior == ConsistencySaveBehavior.RecalculateAndValidate && mappings.HasMaterializations
                ? PlannedDerivedEvaluationMode.Affected : PlannedDerivedEvaluationMode.None);
        if (plan is not null)
        {
            var violations = plan.InvariantEvaluations.Where(x => enforced.Contains(x.InvariantId) &&
                x.State == InvariantEvaluationState.Violated).ToArray();
            if (violations.Length > 0) throw new ConsistencyInvariantViolationException(violations);
            if (options.SaveBehavior == ConsistencySaveBehavior.RecalculateAndValidate)
                ApplyMaterializations(context, runtime, mappings, plan);
        }
        context.ChangeTracker.DetectChanges();
        return new PendingConsistencySave(unit, plan);
    }

    private static void RejectStoreGeneratedRelationKeys(DbContext context, ConsistencyUnitOfWorkMappings mappings)
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(x => x.State == EntityState.Added))
        {
            var mapping = mappings.Resolve(entry);
            if (mapping is null) continue;
            foreach (var member in mapping.KeyMembers)
            {
                var property = entry.Metadata.FindProperty(member);
                if (property is null) continue;
                var propertyEntry = entry.Property(property.Name);
                var value = propertyEntry.CurrentValue;
                var defaultValue = property.ClrType.IsValueType ? Activator.CreateInstance(property.ClrType) : null;
                if (propertyEntry.IsTemporary ||
                    (property.ValueGenerated != Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never &&
                     Equals(value, defaultValue)))
                    throw new ConsistencyStoreGeneratedKeyRequiresManualWorkflowException(entry.Metadata.ClrType, property.Name);
            }
        }
    }

    private static void ApplyMaterializations(DbContext context, ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings, PreparedImpactPlan plan)
    {
        var applied = new Stack<(Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry Entry,
            System.Reflection.PropertyInfo Property, object Source, object? Value, bool Modified)>();
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
                        throw new ConsistencyMaterializationSourceNotTrackedException();
                    var property = entry.Property(mapping.Property.Name);
                    var comparer = property.Metadata.GetValueComparer();
                    if (comparer?.Equals(property.CurrentValue, evaluation.Value) ?? Equals(property.CurrentValue, evaluation.Value))
                        continue;
                    applied.Push((property, mapping.Property, evaluation.Source, property.CurrentValue, property.IsModified));
                    mapping.Property.SetValue(evaluation.Source, evaluation.Value);
                    property.IsModified = true;
                }
            }
        }
        catch (Exception error)
        {
            while (applied.TryPop(out var write))
            {
                write.Property.SetValue(write.Source, write.Value);
                write.Entry.IsModified = write.Modified;
            }
            if (error is System.Reflection.TargetInvocationException { InnerException: { } inner })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}
