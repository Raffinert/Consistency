using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed record ConsistencyPersistencePolicySnapshot(
    IReadOnlySet<int> EnforcedInvariantIds,
    IReadOnlyList<ConsistencyEfCoreMappings.Materialization> Materializations,
    ConsistencySaveBehavior SaveBehavior,
    RuntimeImpactDetailLevel DetailLevel);

internal static class ConsistencyPersistencePolicyEngine
{
    public static ConsistencyPersistencePolicySnapshot CaptureAndValidate(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        ConsistencySaveOptions options)
    {
        var enforced = mappings.Validate(context, runtime);
        var gaps = mappings.GetScopeGaps(runtime, options.Scope, options.SaveBehavior);
        if (gaps.Count > 0) throw new IncompleteConsistencyScopeException(gaps);
        return new ConsistencyPersistencePolicySnapshot(
            enforced,
            mappings.Materializations.ToArray(),
            options.SaveBehavior,
            options.DetailLevel);
    }

    public static PreparedImpactPlan? PrepareAndPlan(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyUnitOfWork unit,
        ConsistencyPersistencePolicySnapshot policy)
    {
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, policy.DetailLevel,
            policy.EnforcedInvariantIds.Count > 0
                ? PlannedInvariantEvaluationMode.Affected
                : PlannedInvariantEvaluationMode.None,
            policy.SaveBehavior == ConsistencySaveBehavior.RecalculateAndValidate &&
            policy.Materializations.Count > 0
                ? PlannedDerivedEvaluationMode.Affected
                : PlannedDerivedEvaluationMode.None);
        if (plan is not null)
        {
            var violations = plan.InvariantEvaluations.Where(evaluation =>
                policy.EnforcedInvariantIds.Contains(evaluation.InvariantId) &&
                evaluation.State == InvariantEvaluationState.Violated).ToArray();
            if (violations.Length > 0) throw new ConsistencyInvariantViolationException(violations);
            if (policy.SaveBehavior == ConsistencySaveBehavior.RecalculateAndValidate)
                ApplyMaterializations(context, runtime, policy.Materializations, plan);
        }
        context.ChangeTracker.DetectChanges();
        return plan;
    }

    private static void ApplyMaterializations(
        DbContext context,
        ConsistencyRuntime runtime,
        IReadOnlyList<ConsistencyEfCoreMappings.Materialization> mappings,
        PreparedImpactPlan plan)
    {
        var applied = new Stack<(PropertyEntry Entry, System.Reflection.PropertyInfo Property,
            object Source, object? Value, bool Modified)>();
        try
        {
            foreach (var mapping in mappings)
            {
                var id = runtime.GetDerivedId(mapping.Definition);
                foreach (var evaluation in plan.DerivedEvaluations.Where(x => x.DerivedId == id))
                {
                    if (evaluation.State != DerivedValueState.Fresh) continue;
                    var entry = context.ChangeTracker.Entries()
                        .SingleOrDefault(x => ReferenceEquals(x.Entity, evaluation.Source));
                    if (entry is null || entry.State == EntityState.Detached)
                        throw new ConsistencyMaterializationSourceNotTrackedException();
                    var property = entry.Property(mapping.Property.Name);
                    var comparer = property.Metadata.GetValueComparer();
                    if (comparer?.Equals(property.CurrentValue, evaluation.Value) ??
                        Equals(property.CurrentValue, evaluation.Value))
                        continue;
                    applied.Push((property, mapping.Property, evaluation.Source,
                        property.CurrentValue, property.IsModified));
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

internal static class ConsistencyGeneratedKeyGuard
{
    public static void RejectForConvenienceSave(DbContext context, ConsistencyUnitOfWorkMappings mappings)
    {
        var pending = CaptureCandidates(context, mappings).FirstOrDefault(candidate => candidate.IsNotReady);
        if (pending is not null)
            throw new ConsistencyStoreGeneratedKeyRequiresManualWorkflowException(
                pending.EntityType, pending.PropertyName);
    }

    public static void RejectForManualPlan(IReadOnlyList<GeneratedKeyCandidate> candidates)
    {
        var pending = candidates.FirstOrDefault(candidate => candidate.IsNotReady);
        if (pending is not null)
            throw new ConsistencyStoreGeneratedKeyNotReadyException(
                pending.EntityType, pending.PropertyName);
    }

    public static IReadOnlyList<GeneratedKeyCandidate> CaptureCandidates(
        DbContext context,
        ConsistencyUnitOfWorkMappings mappings)
    {
        var candidates = new List<GeneratedKeyCandidate>();
        foreach (var entry in context.ChangeTracker.Entries().Where(x => x.State == EntityState.Added))
        {
            var mapping = mappings.Resolve(entry);
            if (mapping is null) continue;
            foreach (var member in mapping.KeyMembers)
            {
                var property = entry.Metadata.FindProperty(member);
                if (property is null) continue;
                if (property.ValueGenerated != ValueGenerated.Never)
                    candidates.Add(new GeneratedKeyCandidate(
                        entry.Metadata.ClrType, property.Name, entry.Property(property.Name), property.ClrType));
            }
        }
        return candidates.ToArray();
    }
}

internal sealed record GeneratedKeyCandidate(
    Type EntityType,
    string PropertyName,
    PropertyEntry Entry,
    Type PropertyType)
{
    public bool IsNotReady => Entry.IsTemporary || Equals(
        Entry.CurrentValue,
        PropertyType.IsValueType ? Activator.CreateInstance(PropertyType) : null);
}

public sealed class ConsistencyPersistenceUnitOfWork
{
    private readonly DbContext _context;
    private readonly ConsistencyRuntime _runtime;
    private readonly ConsistencyUnitOfWork _unit;
    private readonly ConsistencyPersistencePolicySnapshot _policy;
    private readonly IReadOnlyList<GeneratedKeyCandidate> _generatedKeys;
    private State _state;

    internal ConsistencyPersistenceUnitOfWork(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyUnitOfWork unit,
        ConsistencyPersistencePolicySnapshot policy,
        IReadOnlyList<GeneratedKeyCandidate> generatedKeys)
    {
        _context = context;
        _runtime = runtime;
        _unit = unit;
        _policy = policy;
        _generatedKeys = generatedKeys;
    }

    public bool HasChanges => _unit.HasChanges;

    public PreparedImpactPlan? PrepareAndPlan()
    {
        Require(State.Captured, "prepared and planned");
        try
        {
            ConsistencyGeneratedKeyGuard.RejectForManualPlan(_generatedKeys);
            var plan = ConsistencyPersistencePolicyEngine.PrepareAndPlan(
                _context, _runtime, _unit, _policy);
            _state = State.Planned;
            return plan;
        }
        catch
        {
            _state = State.Faulted;
            throw;
        }
    }

    public ChangeImpact? CommitAfterDatabaseCommit()
    {
        Require(State.Planned, "committed after database commit");
        var version = _runtime.Version;
        try
        {
            var impact = _unit.Commit(_runtime);
            _state = State.Committed;
            return impact;
        }
        catch (Exception error)
        {
            _state = State.Faulted;
            throw new ConsistencyRuntimeSynchronizationException(version, error);
        }
    }

    public void Dispatch()
    {
        Require(State.Committed, "dispatched");
        _unit.Dispatch(_runtime);
        _state = State.Dispatched;
    }

    private void Require(State expected, string operation)
    {
        if (_state != expected)
            throw new InvalidOperationException($"This persistence unit of work cannot be {operation} in state '{_state}'.");
    }

    private enum State { Captured, Planned, Committed, Dispatched, Faulted }
}
