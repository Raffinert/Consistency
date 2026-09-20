using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed record ConsistencyPersistencePolicySnapshot(
    IReadOnlySet<int> EnforcedInvariantIds,
    IReadOnlyList<MaterializationDescriptor> Materializations,
    ConsistencySaveBehavior SaveBehavior,
    RuntimeImpactDetailLevel DetailLevel,
    ConsistencyScope? Scope);

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
            options.DetailLevel,
            options.Scope);
    }

    public static PreparedImpactPlan? PrepareAndPlan(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyUnitOfWork unit,
        ConsistencyPersistencePolicySnapshot policy,
        out MaterializationRollback? materializationRollback,
        bool forceMaterialization = false,
        Func<MaterializationDescriptor, object, bool>? materializationSelector = null)
    {
        materializationRollback = null;
        unit.Prepare(runtime);
        var plan = unit.PlanDetailed(runtime, policy.DetailLevel,
            policy.EnforcedInvariantIds.Count > 0
                ? PlannedInvariantEvaluationMode.Affected
                : PlannedInvariantEvaluationMode.None,
            policy.Materializations.Count > 0 &&
            (forceMaterialization || policy.SaveBehavior == ConsistencySaveBehavior.RecalculateAndValidate)
                ? PlannedDerivedEvaluationMode.Affected
                : PlannedDerivedEvaluationMode.None);
        if (plan is not null)
        {
            var violations = plan.InvariantEvaluations.Where(evaluation =>
                policy.EnforcedInvariantIds.Contains(evaluation.InvariantId) &&
                evaluation.State == InvariantEvaluationState.Violated).ToArray();
            if (violations.Length > 0)
            {
                var repairRequests = plan.Result.RepairRequests.Where(request => violations.Any(violation =>
                    violation.InvariantId == request.InvariantId &&
                    ReferenceEquals(violation.Source, request.Source))).ToArray();
                throw new ConsistencyInvariantViolationException(
                    Array.AsReadOnly(violations),
                    Array.AsReadOnly(repairRequests),
                    plan);
            }
            if (forceMaterialization || policy.SaveBehavior == ConsistencySaveBehavior.RecalculateAndValidate)
                materializationRollback = ApplyMaterializations(
                    context, runtime, policy.Materializations, plan, materializationSelector);
        }
        context.ChangeTracker.DetectChanges();
        return plan;
    }

    internal static MaterializationRollback ApplyPlannedMaterializations(
        DbContext context,
        ConsistencyRuntime runtime,
        IReadOnlyList<MaterializationDescriptor> mappings,
        PreparedImpactPlan plan,
        Func<MaterializationDescriptor, object, bool>? materializationSelector = null) =>
        ApplyMaterializations(context, runtime, mappings, plan, materializationSelector);

    private static MaterializationRollback ApplyMaterializations(
        DbContext context,
        ConsistencyRuntime runtime,
        IReadOnlyList<MaterializationDescriptor> mappings,
        PreparedImpactPlan plan,
        Func<MaterializationDescriptor, object, bool>? materializationSelector)
    {
        var applied = new Stack<(PropertyEntry Entry, MaterializationDescriptor Descriptor,
            object Source, object? Value, bool Modified)>();
        try
        {
            foreach (var mapping in mappings)
            {
                var id = runtime.GetDerivedId(mapping.Definition);
                foreach (var evaluation in plan.DerivedEvaluations.Where(x => x.DerivedId == id))
                {
                    if (evaluation.State != DerivedValueState.Fresh) continue;
                    if (materializationSelector is not null &&
                        !materializationSelector(mapping, evaluation.Source))
                        continue;
                    var entry = context.ChangeTracker.Entries()
                        .SingleOrDefault(x => ReferenceEquals(x.Entity, evaluation.Source));
                    if (entry is null || entry.State == EntityState.Detached)
                        throw new ConsistencyMaterializationSourceNotTrackedException();
                    var property = entry.Property(mapping.Target.Name);
                    var comparer = property.Metadata.GetValueComparer();
                    if (comparer?.Equals(property.CurrentValue, evaluation.Value) ??
                        Equals(property.CurrentValue, evaluation.Value))
                        continue;
                    applied.Push((property, mapping, evaluation.Source,
                        property.CurrentValue, property.IsModified));
                    mapping.Write(evaluation.Source, evaluation.Value);
                    property.IsModified = true;
                }
            }
            return new MaterializationRollback(applied.ToArray());
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            while (applied.TryPop(out var write))
            {
                try
                {
                    if (!write.Descriptor.ValuesEqual(write.Descriptor.Read(write.Source), write.Value))
                        write.Descriptor.Write(write.Source, write.Value);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
                finally
                {
                    write.Entry.IsModified = write.Modified;
                }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException(
                    "EF materialization failed and physical rollback was incomplete.",
                    [error, .. rollbackErrors]);
            if (error is System.Reflection.TargetInvocationException { InnerException: { } inner })
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}

internal sealed class MaterializationRollback
{
    internal static MaterializationRollback Empty { get; } = new([]);

    private IReadOnlyList<(PropertyEntry Entry, MaterializationDescriptor Descriptor,
        object Source, object? Value, bool Modified)> _writes;

    internal MaterializationRollback(
        IReadOnlyList<(PropertyEntry Entry, MaterializationDescriptor Descriptor,
            object Source, object? Value, bool Modified)> writes) =>
        _writes = writes.ToArray();

    internal void Restore()
    {
        foreach (var write in _writes)
        {
            if (!write.Descriptor.ValuesEqual(write.Descriptor.Read(write.Source), write.Value))
                write.Descriptor.Write(write.Source, write.Value);
            write.Entry.IsModified = write.Modified;
        }
    }

    internal void Append(MaterializationRollback additional)
    {
        ArgumentNullException.ThrowIfNull(additional);
        if (additional._writes.Count == 0)
            return;
        _writes = additional._writes.Concat(_writes).ToArray();
    }
}

internal static class ConsistencyGeneratedValueGuard
{
    public static void RejectForConvenienceSave(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyUnitOfWorkMappings mappings)
    {
        var pending = CaptureCandidates(context, runtime, mappings)
            .FirstOrDefault(candidate => candidate.IsNotReady);
        if (pending is not null)
        {
            if (pending.Usage.HasFlag(ConsistencyRuntime.ModelMemberUsageKind.ObjectSetKey))
                throw new ConsistencyStoreGeneratedKeyRequiresManualWorkflowException(
                    pending.EntityType, pending.PropertyName);
            throw new ConsistencyStoreGeneratedValueRequiresManualWorkflowException(
                pending.EntityType, pending.PropertyName);
        }
    }

    public static void RejectForManualPlan(IReadOnlyList<GeneratedValueCandidate> candidates)
    {
        var pending = candidates.FirstOrDefault(candidate => candidate.IsNotReady);
        if (pending is not null)
            throw new ConsistencyStoreGeneratedValueNotReadyException(
                pending.EntityType, pending.PropertyName);
    }

    public static IReadOnlyList<GeneratedValueCandidate> CaptureCandidates(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyUnitOfWorkMappings mappings)
    {
        var candidates = new List<GeneratedValueCandidate>();
        foreach (var entry in context.ChangeTracker.Entries().Where(x =>
                     x.State is EntityState.Added or EntityState.Modified))
        {
            var mapping = mappings.Resolve(entry);
            var operation = entry.State == EntityState.Added
                ? GeneratedValueOperation.OnAdd
                : GeneratedValueOperation.OnUpdate;
            foreach (var propertyEntry in entry.Properties)
            {
                var property = propertyEntry.Metadata;
                var member = property.PropertyInfo ?? (System.Reflection.MemberInfo?)property.FieldInfo;
                var usage = member is null
                    ? ConsistencyRuntime.ModelMemberUsageKind.None
                    : runtime.GetTrackedMemberUsage(mapping?.SetDefinition, member);
                if (member is null || !GeneratesFor(property, operation) ||
                    usage == ConsistencyRuntime.ModelMemberUsageKind.None)
                    continue;
                if (operation == GeneratedValueOperation.OnUpdate &&
                    usage.HasFlag(ConsistencyRuntime.ModelMemberUsageKind.ObjectSetKey))
                    throw new ConsistencyStoreGeneratedIdentityUpdateNotSupportedException(
                        entry.Metadata.ClrType, property.Name);
                candidates.Add(new GeneratedValueCandidate(
                    entry.Metadata.ClrType, property.Name, propertyEntry, property, member,
                    mapping, usage, operation, propertyEntry.CurrentValue));
            }
        }
        return candidates.ToArray();
    }

    private static bool GeneratesFor(IProperty property, GeneratedValueOperation operation) => operation switch
    {
        GeneratedValueOperation.OnAdd =>
            (property.ValueGenerated & ValueGenerated.OnAdd) != ValueGenerated.Never,
        GeneratedValueOperation.OnUpdate =>
            (property.ValueGenerated & ValueGenerated.OnUpdate) != ValueGenerated.Never,
        _ => false
    };
}

internal enum GeneratedValueOperation { OnAdd, OnUpdate }

internal sealed record GeneratedValueCandidate(
    Type EntityType,
    string PropertyName,
    PropertyEntry Entry,
    IProperty Property,
    System.Reflection.MemberInfo Member,
    ConsistencyUnitOfWorkMappings.IEntitySetMapping? Mapping,
    ConsistencyRuntime.ModelMemberUsageKind Usage,
    GeneratedValueOperation Operation,
    object? PreSaveValue)
{
    public bool IsNotReady => Entry.IsTemporary || Operation switch
    {
        GeneratedValueOperation.OnUpdate => Entry.EntityEntry.State != EntityState.Unchanged,
        _ => Entry.EntityEntry.State == EntityState.Added &&
            (Property.GetBeforeSaveBehavior() == PropertySaveBehavior.Ignore || Equals(
                Entry.CurrentValue,
                Property.ClrType.IsValueType ? Activator.CreateInstance(Property.ClrType) : null))
    };

    public PropertyChange? FinalizeUpdate(DbContext context)
    {
        if (Operation != GeneratedValueOperation.OnUpdate) return null;
        if (!ReferenceEquals(Entry.EntityEntry.Context, context))
            throw new InvalidOperationException("The generated value is no longer tracked by the captured DbContext.");
        var current = Entry.CurrentValue;
        if (Property.GetValueComparer()?.Equals(PreSaveValue, current) ?? Equals(PreSaveValue, current))
            return null;
        return Mapping is null
            ? Change.Property(Entry.EntityEntry.Entity, Member, PreSaveValue, current)
            : Mapping.Property(Entry.EntityEntry.Entity, Member, PreSaveValue, current);
    }
}

public sealed class ConsistencyPersistenceUnitOfWork
{
    private readonly DbContext _context;
    private readonly ConsistencyRuntime _runtime;
    private readonly ConsistencyEfCoreMappings _mappings;
    private readonly CapturedEfMutationSnapshot _mutations;
    private readonly ConsistencyPersistencePolicySnapshot _policy;
    private readonly IReadOnlyList<GeneratedValueCandidate> _generatedValues;
    private ConsistencyUnitOfWork? _unit;
    private State _state;

    internal ConsistencyPersistenceUnitOfWork(
        DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyEfCoreMappings mappings,
        CapturedEfMutationSnapshot mutations,
        ConsistencyPersistencePolicySnapshot policy,
        IReadOnlyList<GeneratedValueCandidate> generatedValues)
    {
        _context = context;
        _runtime = runtime;
        _mappings = mappings;
        _mutations = mutations;
        _policy = policy;
        _generatedValues = generatedValues;
    }

    public bool HasChanges => _mutations.HasChanges;

    /// <summary>
    /// Finalizes proven EF-generated relationship fixup and generated INSERT/UPDATE semantic values, then
    /// creates the exact binding persistence plan. Store-generated semantic inputs must be final, and
    /// unrelated post-capture changes are rejected.
    /// This method performs no SQL.
    /// </summary>
    public PreparedImpactPlan? PrepareAndPlan()
    {
        Require(State.Captured, "prepared and planned");
        try
        {
            ConsistencyGeneratedValueGuard.RejectForManualPlan(_generatedValues);
            var captured = _mutations.FinalizeForPlanning(_context, _generatedValues);
            var admissions = ExternalConsumerDiscovery.Discover(
                _context, _runtime, _mappings, captured, _policy.SaveBehavior, _policy.Scope);
            _unit = new ConsistencyUnitOfWork(
                MutationSet.Combine(captured.Mutations.Concat(admissions)));
            var plan = ConsistencyPersistencePolicyEngine.PrepareAndPlan(
                _context, _runtime, _unit, _policy, out _);
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
            var impact = _unit!.Commit(_runtime);
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
        _unit!.Dispatch(_runtime);
        _state = State.Dispatched;
    }

    private void Require(State expected, string operation)
    {
        if (_state != expected)
            throw new InvalidOperationException($"This persistence unit of work cannot be {operation} in state '{_state}'.");
    }

    private enum State { Captured, Planned, Committed, Dispatched, Faulted }
}
