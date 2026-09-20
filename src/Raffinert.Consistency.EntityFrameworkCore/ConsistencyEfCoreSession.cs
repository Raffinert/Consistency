using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Reflection;

namespace Raffinert.Consistency.EntityFrameworkCore;

internal sealed class ConsistencyEfCoreSession<TDbContext> : IRuntimeMaterializationIntegration, IDisposable
    where TDbContext : DbContext
{
    private readonly TDbContext _context;
    private readonly ConsistencyEfCoreMappings _mappings;
    private readonly ConsistencySaveOptions _options;
    private readonly Func<ConsistencyRuntime> _runtimeFactory;
    private readonly List<OwnedMaterializationWrite> _ownedWrites = [];
    private ConsistencyRuntime? _runtime;
    private PendingConsistencySave? _pending;
    private RejectedConsistencySave? _rejected;
    private bool _baselineMayNeedStabilization;

    public ConsistencyEfCoreSession(
        TDbContext context,
        ConsistencyEfCoreMappings mappings,
        ConsistencySaveOptions options,
        Func<ConsistencyRuntime> runtimeFactory)
    {
        _context = context;
        _mappings = mappings;
        _options = options;
        _runtimeFactory = runtimeFactory;
        _context.ChangeTracker.Tracked += Tracked;
    }

    internal TDbContext Context => _context;
    internal ConsistencyRuntime Runtime => _runtime ??= Bind(_runtimeFactory());
    internal long RejectedPreviewValidationCount { get; private set; }

    internal void BindRuntime(ConsistencyRuntime runtime) => Bind(runtime);

    private ConsistencyRuntime Bind(ConsistencyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (_runtime is not null && !ReferenceEquals(_runtime, runtime))
            throw new InvalidOperationException("This EF session is already bound to another consistency runtime.");
        if (_runtime is not null)
            return _runtime;

        var current = ConsistencyCoordinator.CaptureCurrentUnitOfWork(
            _context, _mappings, IncludeSemanticProperty);
        if (HasRelevantChanges(runtime, current.Mutations))
            throw new InvalidOperationException(
                "The consistency runtime must be resolved before mutating tracked entities; " +
                "first runtime binding cannot admit dirty EF state as a baseline.");

        _runtime = runtime;
        runtime.AttachMaterializationIntegration(this);
        foreach (var entry in _context.ChangeTracker.Entries())
            AdmitBaseline(entry);
        _baselineMayNeedStabilization = true;
        StabilizeUntouchedTrackedBaselines(EfTouchedSources.Create([]));
        runtime.ValidateBaseline();
        _baselineMayNeedStabilization = false;
        return runtime;
    }

    internal void PrepareForSave()
    {
        _ = PrepareForSaveCore();
    }

    internal async Task PrepareForSaveAsync(CancellationToken cancellationToken)
    {
        await PrepareForSaveCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void CompleteSave()
    {
        var pending = _pending;
        if (pending is null)
            return;
        try
        {
            ConsistencyCoordinator.Complete(Runtime, pending);
        }
        finally
        {
            _pending = null;
            _rejected = null;
            _ownedWrites.Clear();
        }
    }

    internal void FailSave()
    {
        var pending = _pending;
        _pending = null;
        _ownedWrites.Clear();
        pending?.MaterializationRollback?.Restore();
    }

    public bool TryMaterializeObject(ConsistencyRuntime runtime, object source)
    {
        if (!OwnsTrackedMappedSource(runtime, source))
            return false;
        var descriptors = runtime.Materializations
            .Where(descriptor => descriptor.SourceType.IsInstanceOfType(source) &&
                runtime.IsRegistered(descriptor.SourceSet, source))
            .ToArray();
        if (descriptors.Length == 0)
            return false;

        _ = PreparePending((descriptor, candidateSource) =>
            descriptors.Contains(descriptor) && ReferenceEquals(candidateSource, source));
        return true;
    }

    public bool TryMaterializeDerived(
        ConsistencyRuntime runtime,
        IDerivedDefinition definition,
        object source,
        out object? value)
    {
        value = null;
        if (!OwnsTrackedMappedSource(runtime, source))
            return false;
        var descriptor = runtime.Materializations.SingleOrDefault(candidate =>
            ReferenceEquals(candidate.Definition, definition) &&
            runtime.IsRegistered(candidate.SourceSet, source));
        if (descriptor is null)
            return false;

        var pending = PreparePending((candidate, candidateSource) =>
            ReferenceEquals(candidate.Definition, definition) && ReferenceEquals(candidateSource, source));
        if (pending.Plan is null)
            return false;
        var id = runtime.GetDerivedId(definition);
        var evaluation = pending.Plan.DerivedEvaluations.SingleOrDefault(candidate =>
            candidate.DerivedId == id && ReferenceEquals(candidate.Source, source));
        if (evaluation is null)
            return false;
        value = evaluation.Value;
        return true;
    }

    internal void ApplyAllPendingMaterializations()
    {
        if (_pending?.Plan is not { } plan)
            return;
        var additional = ConsistencyPersistencePolicyEngine.ApplyPlannedMaterializations(
            _context, Runtime, _mappings.Materializations, plan);
        _pending.MaterializationRollback?.Append(additional);
        RecordOwnedWrites(plan, null);
    }

    internal ConsistencyPreview CreateRejectedPreview(ConsistencyInvariantViolationException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var rejected = _rejected;
        if (rejected is null || !ReferenceEquals(rejected.Error, error))
            throw new InvalidOperationException(
                "The invariant rejection is not the current rejected save for this EF session.");
        ValidateRejected(rejected);
        return Runtime.CreatePreview(rejected.Plan, () => ValidateRejected(rejected));
    }

    private PendingConsistencySave PrepareForSaveCore()
    {
        var runtime = Runtime;
        PrepareBaselineForPlanning(runtime);
        if (_pending is not null)
        {
            var fingerprint = ConsistencyCoordinator.CaptureFingerprint(
                _context, runtime, _mappings, _options, IncludeSemanticProperty);
            if (!_baselineMayNeedStabilization &&
                _pending.BaselineRevision == runtime.BaselineRevision &&
                _pending.Fingerprint.Equals(fingerprint))
            {
                ApplyAllPendingMaterializations();
                return _pending;
            }
            DiscardPending();
            StabilizeCurrentTrackedBaselines();
        }

        try
        {
            _pending = PrepareStable(runtime, () => ConsistencyCoordinator.Prepare(
                _context, runtime, _mappings, _options, IncludeSemanticProperty));
            _rejected = null;
        }
        catch (ConsistencyInvariantViolationException error)
        {
            RememberRejected(error);
            throw;
        }
        return _pending;
    }

    private async Task<PendingConsistencySave> PrepareForSaveCoreAsync(CancellationToken cancellationToken)
    {
        var runtime = Runtime;
        PrepareBaselineForPlanning(runtime);
        if (_pending is not null)
        {
            var fingerprint = await ConsistencyCoordinator.CaptureFingerprintAsync(
                _context, runtime, _mappings, _options, cancellationToken, IncludeSemanticProperty)
                .ConfigureAwait(false);
            if (!_baselineMayNeedStabilization &&
                _pending.BaselineRevision == runtime.BaselineRevision &&
                _pending.Fingerprint.Equals(fingerprint))
            {
                ApplyAllPendingMaterializations();
                return _pending;
            }
            DiscardPending();
            StabilizeCurrentTrackedBaselines();
        }

        try
        {
            _pending = await PrepareStableAsync(runtime, () => ConsistencyCoordinator.PrepareAsync(
                _context, runtime, _mappings, _options, cancellationToken, IncludeSemanticProperty))
                .ConfigureAwait(false);
            _rejected = null;
        }
        catch (ConsistencyInvariantViolationException error)
        {
            RememberRejected(error);
            throw;
        }
        return _pending;
    }

    private PendingConsistencySave PreparePending(
        Func<MaterializationDescriptor, object, bool> selector)
    {
        var runtime = Runtime;
        PrepareBaselineForPlanning(runtime);
        if (_pending is not null)
        {
            var fingerprint = ConsistencyCoordinator.CaptureFingerprint(
                _context, runtime, _mappings, _options, IncludeSemanticProperty);
            if (!_baselineMayNeedStabilization &&
                _pending.BaselineRevision == runtime.BaselineRevision &&
                _pending.Fingerprint.Equals(fingerprint))
            {
                ApplySelectedMaterializations(_pending, selector);
                return _pending;
            }
            DiscardPending();
            StabilizeCurrentTrackedBaselines();
        }

        _pending = PrepareStable(runtime, () => ConsistencyCoordinator.Prepare(
            _context, runtime, _mappings, _options, IncludeSemanticProperty,
            forceMaterialization: true, materializationSelector: selector));
        RecordOwnedWrites(_pending.Plan, selector);
        return _pending;
    }

    private void ApplySelectedMaterializations(
        PendingConsistencySave pending,
        Func<MaterializationDescriptor, object, bool> selector)
    {
        if (pending.Plan is null)
            return;
        var additional = ConsistencyPersistencePolicyEngine.ApplyPlannedMaterializations(
            _context, Runtime, _mappings.Materializations, pending.Plan, selector);
        pending.MaterializationRollback?.Append(additional);
        RecordOwnedWrites(pending.Plan, selector);
    }

    private void RecordOwnedWrites(
        PreparedImpactPlan? plan,
        Func<MaterializationDescriptor, object, bool>? selector)
    {
        if (plan is null)
            return;
        foreach (var descriptor in _mappings.Materializations)
        {
            var id = Runtime.GetDerivedId(descriptor.Definition);
            foreach (var evaluation in plan.DerivedEvaluations.Where(value =>
                         value.DerivedId == id && value.State == DerivedValueState.Fresh &&
                         (selector is null || selector(descriptor, value.Source))))
            {
                if (_context.ChangeTracker.Entries().SingleOrDefault(entry =>
                        ReferenceEquals(entry.Entity, evaluation.Source)) is not { } entry)
                    continue;
                if (!_ownedWrites.Any(write => ReferenceEquals(write.Source, evaluation.Source) &&
                        write.Member == descriptor.Target))
                    _ownedWrites.Add(new OwnedMaterializationWrite(
                        evaluation.Source, descriptor.Target, descriptor, evaluation.Value));
            }
        }
    }

    private bool IncludeSemanticProperty(EntityEntry entry, IProperty property)
    {
        var member = property.PropertyInfo;
        if (member is null)
            return true;
        if (_runtime is not null)
        {
            var mapping = _mappings.UnitOfWorkMappings.Resolve(entry);
            if (_runtime.GetTrackedMemberUsage(mapping?.SetDefinition, member) ==
                ConsistencyRuntime.ModelMemberUsageKind.None)
                return false;
        }
        var owned = _ownedWrites.LastOrDefault(write =>
            ReferenceEquals(write.Source, entry.Entity) && write.Member == member);
        return owned is null || !owned.Descriptor.ValuesEqual(
            entry.Property(property.Name).CurrentValue, owned.Value);
    }

    private bool OwnsTrackedMappedSource(ConsistencyRuntime runtime, object source)
    {
        if (!ReferenceEquals(Runtime, runtime))
            throw new InvalidOperationException("The runtime is not bound to this EF Core session.");
        StabilizeCurrentTrackedBaselines();
        var entry = _context.ChangeTracker.Entries().SingleOrDefault(value =>
            ReferenceEquals(value.Entity, source));
        return entry is not null && entry.State != EntityState.Detached &&
            _mappings.UnitOfWorkMappings.Resolve(entry) is not null;
    }

    private void Tracked(object? sender, EntityTrackedEventArgs eventArgs)
    {
        if (_runtime is null)
            return;
        var mapping = _mappings.UnitOfWorkMappings.Resolve(eventArgs.Entry);
        if (mapping is null)
            return;
        _baselineMayNeedStabilization = true;
        if (eventArgs.Entry.State == EntityState.Added)
            return;
        AdmitBaseline(eventArgs.Entry);
    }

    private void AdmitBaseline(EntityEntry entry)
    {
        if (entry.State == EntityState.Added)
            return;
        var mapping = _mappings.UnitOfWorkMappings.Resolve(entry);
        if (mapping is not null)
            _runtime!.AdmitBaseline(mapping.SetDefinition, entry.Entity);
    }

    private void StabilizeCurrentTrackedBaselines()
    {
        if (_runtime is null || !_baselineMayNeedStabilization)
            return;
        _context.ChangeTracker.DetectChanges();
        var current = ConsistencyCoordinator.CaptureCurrentUnitOfWork(
            _context, _mappings, IncludeSemanticProperty);
        StabilizeUntouchedTrackedBaselines(EfTouchedSources.Create(current.Mutations));
        _runtime.ValidateBaseline();
        _baselineMayNeedStabilization = false;
    }

    private void PrepareBaselineForPlanning(ConsistencyRuntime runtime)
    {
        if (_pending is not null && (_baselineMayNeedStabilization ||
                _pending.BaselineRevision != runtime.BaselineRevision))
            DiscardPending();
        StabilizeCurrentTrackedBaselines();
        if (_pending is not null && _pending.BaselineRevision != runtime.BaselineRevision)
            DiscardPending();
    }

    private PendingConsistencySave PrepareStable(
        ConsistencyRuntime runtime,
        Func<PendingConsistencySave> prepare)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var revision = runtime.BaselineRevision;
            var candidate = prepare();
            if (!_baselineMayNeedStabilization && revision == runtime.BaselineRevision &&
                candidate.BaselineRevision == runtime.BaselineRevision)
                return candidate;
            candidate.MaterializationRollback?.Restore();
            _ownedWrites.Clear();
            StabilizeCurrentTrackedBaselines();
        }
        throw new InvalidOperationException(
            "The consistency runtime baseline changed repeatedly while preparing the EF save plan.");
    }

    private async Task<PendingConsistencySave> PrepareStableAsync(
        ConsistencyRuntime runtime,
        Func<Task<PendingConsistencySave>> prepare)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var revision = runtime.BaselineRevision;
            var candidate = await prepare().ConfigureAwait(false);
            if (!_baselineMayNeedStabilization && revision == runtime.BaselineRevision &&
                candidate.BaselineRevision == runtime.BaselineRevision)
                return candidate;
            candidate.MaterializationRollback?.Restore();
            _ownedWrites.Clear();
            StabilizeCurrentTrackedBaselines();
        }
        throw new InvalidOperationException(
            "The consistency runtime baseline changed repeatedly while preparing the EF save plan.");
    }

    private static bool HasRelevantChanges(
        ConsistencyRuntime runtime,
        IReadOnlyList<RuntimeMutation> mutations) =>
        mutations.Any(mutation => IsFirstBindingRelevant(runtime, mutation));

    private static bool IsFirstBindingRelevant(
        ConsistencyRuntime runtime,
        RuntimeMutation mutation) => mutation switch
        {
            PropertyChange change => IsRelevantMemberChange(runtime, change.Set, change.Member),
            CollectionChange change => IsRelevantMemberChange(runtime, change.Set, change.Member),
            ObjectAdded => true,
            ObjectRemoved => true,
            CoverageAdmission => true,
            _ => throw new InvalidOperationException(
                $"Unsupported EF first-binding mutation type '{mutation.GetType().Name}'.")
        };

    private static bool IsRelevantMemberChange(
        ConsistencyRuntime runtime,
        IObjectSetDefinition? set,
        MemberInfo member) =>
        runtime.GetTrackedMemberUsage(set, member) != ConsistencyRuntime.ModelMemberUsageKind.None;

    private void StabilizeUntouchedTrackedBaselines(EfTouchedSources touchedSources)
    {
        if (_runtime is null)
            return;
        foreach (var entry in _context.ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Deleted)
                continue;
            var mapping = _mappings.UnitOfWorkMappings.Resolve(entry);
            if (mapping is null || touchedSources.Contains(entry.Entity) ||
                !_runtime.IsRegistered(mapping.SetDefinition, entry.Entity))
                continue;
            _runtime.RefreshBaseline(mapping.SetDefinition, entry.Entity);
        }
    }

    private void DiscardPending()
    {
        _pending?.MaterializationRollback?.Restore();
        _pending = null;
        _ownedWrites.Clear();
    }

    private void RememberRejected(ConsistencyInvariantViolationException error)
    {
        if (error.Fingerprint is null)
            throw new InvalidOperationException(
                "The rejected EF plan is missing its tracked mutation fingerprint.", error);
        _rejected = new RejectedConsistencySave(
            error,
            error.RejectedPlan,
            error.Fingerprint,
            error.BaselineRevision);
    }

    private void ValidateRejected(RejectedConsistencySave rejected)
    {
        if (!ReferenceEquals(_rejected, rejected))
            throw new InvalidOperationException(
                "The proposed-state view is stale because its rejected save is no longer current.");
        if (Runtime.BaselineRevision != rejected.BaselineRevision)
            throw new InvalidOperationException(
                "The proposed-state view is stale because the tracked runtime baseline changed.");
        RejectedPreviewValidationCount++;
        var current = ConsistencyCoordinator.CaptureFingerprint(
            _context, Runtime, _mappings, _options, IncludeSemanticProperty);
        if (!rejected.Fingerprint.Equals(current))
            throw new InvalidOperationException(
                "The proposed-state view is stale because relevant tracked state changed after rejection.");
    }

    public void Dispose()
    {
        _context.ChangeTracker.Tracked -= Tracked;
        _pending = null;
        _rejected = null;
        _ownedWrites.Clear();
    }

    private sealed record OwnedMaterializationWrite(
        object Source,
        System.Reflection.MemberInfo Member,
        MaterializationDescriptor Descriptor,
        object? Value);

    private sealed record RejectedConsistencySave(
        ConsistencyInvariantViolationException Error,
        PreparedImpactPlan Plan,
        EfMutationFingerprint Fingerprint,
        long BaselineRevision);
}
