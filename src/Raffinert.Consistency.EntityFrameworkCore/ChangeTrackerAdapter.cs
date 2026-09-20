using System.Reflection;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raffinert.Consistency.EntityFrameworkCore;

/// <summary>
/// Indicates that the database operation succeeded but synchronizing the committed domain state into
/// the consistency runtime failed. The runtime retains its pre-commit state and must be reconciled or rebuilt.
/// </summary>
public sealed class ConsistencyRuntimeSynchronizationException : Exception
{
    internal ConsistencyRuntimeSynchronizationException(
        long runtimeVersion,
        Exception innerException)
        : base(
            "The database operation succeeded, but consistency runtime synchronization failed. " +
            "Do not retry the database command; reconcile or rebuild the runtime from authoritative state.",
            innerException)
    {
        RuntimeVersion = runtimeVersion;
    }

    public long RuntimeVersion { get; }
}

/// <summary>Maps EF entity entries to specific Raffinert object sets.</summary>
public sealed class ConsistencyUnitOfWorkMappings
{
    private readonly List<IEntitySetMapping> _mappings = [];

    public ConsistencyUnitOfWorkMappings Map<TEntity>(
        ObjectSet<TEntity> set,
        Func<EntityEntry<TEntity>, bool>? selector = null) where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(set);
        _mappings.Add(new EntitySetMapping<TEntity>(set, selector));
        return this;
    }

    internal IEntitySetMapping? Resolve(EntityEntry entry)
    {
        var matches = _mappings.Where(mapping => mapping.Matches(entry)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException(
                $"Entity '{entry.Metadata.ClrType.Name}' matches multiple object-set mappings. " +
                "Use selectors that identify exactly one set.");
        return matches.SingleOrDefault();
    }

    internal interface IEntitySetMapping
    {
        IObjectSetDefinition SetDefinition { get; }
        IReadOnlySet<MemberInfo> KeyMembers { get; }
        bool Matches(EntityEntry entry);
        ObjectAdded Add(object entity);
        ObjectRemoved Remove(object entity);
        PropertyChange Property(object entity, MemberInfo member, object? oldValue, object? newValue);
        CollectionChange CollectionReset(object entity, MemberInfo member);
    }

    private sealed class EntitySetMapping<TEntity>(
        ObjectSet<TEntity> set,
        Func<EntityEntry<TEntity>, bool>? selector) : IEntitySetMapping where TEntity : class
    {
        public IObjectSetDefinition SetDefinition => set.Definition;
        public IReadOnlySet<MemberInfo> KeyMembers => set.Definition.KeyMembers;

        public bool Matches(EntityEntry entry) =>
            entry.Entity is TEntity entity &&
            (selector is null || selector(entry.Context.Entry(entity)));

        public ObjectAdded Add(object entity) => Change.Add(set, (TEntity)entity);

        public ObjectRemoved Remove(object entity) => Change.Remove(set, (TEntity)entity);

        public PropertyChange Property(
            object entity,
            MemberInfo member,
            object? oldValue,
            object? newValue) =>
            Change.Property(set, (TEntity)entity, member, oldValue, newValue);

        public CollectionChange CollectionReset(object entity, MemberInfo member) =>
            CollectionChange.Create(set.Definition, (TEntity)entity, member, CollectionChangeKind.Reset, null);
    }
}

/// <summary>
/// A captured EF unit of work. Capture before SaveChanges and apply only after the database operation
/// succeeds; a failed database operation therefore never advances Raffinert runtime state. This is a
/// runtime binding primitive and does not apply <see cref="ConsistencyEfCoreMappings"/> enforcement,
/// materialization, or <see cref="ConsistencyScope"/> policy. Use
/// <see cref="ConsistencyDbContextExtensions.CaptureConsistencyUnitOfWork"/> for authoritative EF persistence.
/// Save-and-apply convenience methods reject semantic values that EF can finalize only after SQL.
/// Callers that use this capture primitive and execute SQL themselves own detection and reconciliation of
/// database triggers, referential actions, raw SQL, bulk operations, and other external mutations.
/// </summary>
public sealed class ConsistencyUnitOfWork
{
    private readonly MutationSet? _mutations;
    private PreparedMutation? _prepared;
    private PreparedImpactPlan? _plan;
    private bool _isPrepared;
    private bool _emptyCommitted;
    private bool _emptyDispatched;

    internal ConsistencyUnitOfWork(MutationSet? mutations) => _mutations = mutations;

    internal IReadOnlyList<RuntimeMutation> Mutations => _mutations?.Mutations ?? [];

    public bool HasChanges => _mutations is not null;

    /// <summary>Validates all captured mutations before the database operation begins.</summary>
    public void Prepare(ConsistencyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (_isPrepared)
            throw new InvalidOperationException("This unit of work has already been prepared.");
        _prepared = _mutations is null
            ? null
            : runtime.Prepare(_mutations, ChangeValidationMode.StrictNewValue);
        _isPrepared = true;
    }

    /// <summary>Commits prepared runtime state after the database operation succeeds.</summary>
    public ChangeImpact? Commit(ConsistencyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_isPrepared)
            throw new InvalidOperationException("This unit of work must be prepared before it is committed.");
        if (_mutations is null)
        {
            if (_emptyCommitted)
                throw new InvalidOperationException("This unit of work has already been committed.");
            _emptyCommitted = true;
            return null;
        }
        return _plan is null
            ? runtime.Commit(_prepared!)
            : runtime.Commit(_plan).ChangeImpact;
    }

    /// <summary>
    /// Commits prepared runtime state and returns detailed data before policy dispatch. Empty units
    /// return <see langword="null"/>.
    /// </summary>
    public RuntimeApplyResult? CommitDetailed(
        ConsistencyRuntime runtime,
        RuntimeImpactDetailLevel detailLevel = RuntimeImpactDetailLevel.Summary)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_isPrepared)
            throw new InvalidOperationException("This unit of work must be prepared before it is committed.");
        if (_mutations is null)
        {
            if (_emptyCommitted)
                throw new InvalidOperationException("This unit of work has already been committed.");
            _emptyCommitted = true;
            return null;
        }
        if (_plan is not null)
        {
            if (_plan.DetailLevel != detailLevel)
                throw new InvalidOperationException("The requested detail level differs from the binding plan.");
            return runtime.Commit(_plan);
        }
        return runtime.CommitDetailed(_prepared!, detailLevel);
    }

    /// <summary>
    /// Produces a non-binding diagnostic preview without committing or dispatching. A later normal commit
    /// executes semantics again. Use
    /// <see cref="PlanDetailed(ConsistencyRuntime, RuntimeImpactDetailLevel)"/> for durability-sensitive parity. Empty units
    /// return <see langword="null"/>.
    /// </summary>
    public RuntimeApplyResult? PreviewDetailed(
        ConsistencyRuntime runtime,
        RuntimeImpactDetailLevel detailLevel = RuntimeImpactDetailLevel.Summary)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_isPrepared)
            throw new InvalidOperationException("This unit of work must be prepared before it is previewed.");
        return _mutations is null ? null : runtime.PreviewDetailed(_prepared!, detailLevel);
    }

    /// <summary>
    /// Creates a binding impact plan that can be persisted before database durability and later
    /// committed without rerunning semantic model code. This low-level operation does not apply EF
    /// enforcement, materialization, or consistency-scope policy. Empty units return <see langword="null"/>.
    /// </summary>
#pragma warning disable RS0027 // Preserve the shipped optional-parameter overload exactly.
    public PreparedImpactPlan? PlanDetailed(
        ConsistencyRuntime runtime,
        RuntimeImpactDetailLevel detailLevel = RuntimeImpactDetailLevel.Summary)
        => PlanDetailed(runtime, detailLevel, PlannedInvariantEvaluationMode.None);
#pragma warning restore RS0027

    public PreparedImpactPlan? PlanDetailed(
        ConsistencyRuntime runtime,
        RuntimeImpactDetailLevel detailLevel,
        PlannedInvariantEvaluationMode invariantEvaluationMode)
        => PlanDetailed(runtime, detailLevel, invariantEvaluationMode, PlannedDerivedEvaluationMode.None);

    public PreparedImpactPlan? PlanDetailed(
        ConsistencyRuntime runtime,
        RuntimeImpactDetailLevel detailLevel,
        PlannedInvariantEvaluationMode invariantEvaluationMode,
        PlannedDerivedEvaluationMode derivedEvaluationMode)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_isPrepared)
            throw new InvalidOperationException("This unit of work must be prepared before it is planned.");
        if (_plan is not null)
            throw new InvalidOperationException("This unit of work already has a binding impact plan.");
        return _mutations is null ? null : _plan = runtime.PlanDetailed(
            _prepared!, detailLevel, invariantEvaluationMode, derivedEvaluationMode);
    }

    /// <summary>Dispatches post-commit policy callbacks.</summary>
    public void Dispatch(ConsistencyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (_mutations is null)
        {
            if (!_emptyCommitted)
                throw new InvalidOperationException("This unit of work must be committed before it is dispatched.");
            if (_emptyDispatched)
                throw new InvalidOperationException("This unit of work has already been dispatched.");
            _emptyDispatched = true;
            return;
        }
        runtime.Dispatch(_prepared!);
    }

    /// <summary>Prepares, commits, and dispatches the captured mutations.</summary>
    public void Apply(ConsistencyRuntime runtime)
    {
        Prepare(runtime);
        Commit(runtime);
        Dispatch(runtime);
    }
}

/// <summary>
/// Translates EF Core change tracking into a policy-agnostic post-database-commit runtime unit of work.
/// Use <see cref="ConsistencyDbContextExtensions.CaptureConsistencyUnitOfWork"/> when EF persistence policy
/// must be enforced.
/// </summary>
public static class ChangeTrackerAdapter
{
    internal static CapturedEfMutationSnapshot CapturePolicyAwareSnapshot(
        ChangeTracker changeTracker,
        ConsistencyUnitOfWorkMappings mappings,
        Func<EntityEntry, IProperty, bool>? includeProperty = null,
        EfFingerprintDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        ArgumentNullException.ThrowIfNull(mappings);
        var navigationCapture = CaptureStabilizedNavigationSnapshot(
            changeTracker, mappings, diagnostics);
        var snapshot = navigationCapture.Snapshot;
        var navigationChanges = navigationCapture.Changes;
        var additions = new List<RuntimeMutation>();
        var removals = new List<RuntimeMutation>();
        var properties = new List<CapturedEfPropertyMutation>();
        foreach (var entry in snapshot.Entries)
        {
            var mapping = mappings.Resolve(entry);
            if (mapping is not null && entry.State == EntityState.Added)
                additions.Add(mapping.Add(entry.Entity));
            else if (mapping is not null && entry.State == EntityState.Deleted)
                removals.Add(mapping.Remove(entry.Entity));
            if (entry.State != EntityState.Modified) continue;
            foreach (var property in entry.Properties.Where(property => property.IsModified))
            {
                var member = GetMember(property.Metadata);
                if (member is null) continue;
                if (includeProperty is not null && !includeProperty(entry, property.Metadata)) continue;
                var mutation = mapping is null
                    ? Change.Property(entry.Entity, member, property.OriginalValue, property.CurrentValue)
                    : mapping.Property(entry.Entity, member, property.OriginalValue, property.CurrentValue);
                properties.Add(new CapturedEfPropertyMutation(
                    mutation,
                    mapping,
                    entry,
                    property.Metadata,
                    CaptureGeneratedFixupEvidence(snapshot, entry, property.Metadata, diagnostics)));
            }
        }
        return new CapturedEfMutationSnapshot(
            additions,
            properties,
            navigationChanges,
            removals);
    }

    public static ChangeSet? CreateChangeSet(ChangeTracker changeTracker)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        var navigationChanges = CaptureNavigationChanges(changeTracker, null);
        var changes = ReadModifiedProperties(changeTracker, null)
            .Concat(navigationChanges.OfType<PropertyChange>())
            .ToArray();
        return changes.Length == 0 ? null : ChangeSet.Create(changes);
    }

    public static ConsistencyUnitOfWork CaptureUnitOfWork(
        ChangeTracker changeTracker,
        ConsistencyUnitOfWorkMappings mappings)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        ArgumentNullException.ThrowIfNull(mappings);
        var navigationChanges = CaptureNavigationChanges(changeTracker, mappings);
        var additions = new List<RuntimeMutation>();
        var removals = new List<RuntimeMutation>();
        foreach (var entry in changeTracker.Entries())
        {
            var mapping = mappings.Resolve(entry);
            if (mapping is not null)
            {
                if (entry.State == EntityState.Added)
                    additions.Add(mapping.Add(entry.Entity));
                else if (entry.State == EntityState.Deleted)
                    removals.Add(mapping.Remove(entry.Entity));
            }
        }

        var propertyChanges = ReadModifiedProperties(changeTracker, mappings);
        var mutations = additions
            .Concat(propertyChanges)
            .Concat(navigationChanges)
            .Concat(removals)
            .ToArray();
        return new ConsistencyUnitOfWork(mutations.Length == 0 ? null : MutationSet.Create(mutations));
    }

    internal static ConsistencyUnitOfWork CaptureUnitOfWork(
        ChangeTracker changeTracker,
        ConsistencyUnitOfWorkMappings mappings,
        Func<EntityEntry, IProperty, bool> includeProperty,
        EfFingerprintDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(includeProperty);
        var navigationStart = Stopwatch.GetTimestamp();
        var navigationChanges = CaptureNavigationChanges(changeTracker, mappings, diagnostics);
        if (diagnostics is not null)
            diagnostics.NavigationCaptureTicks += Stopwatch.GetTimestamp() - navigationStart;
        var additions = new List<RuntimeMutation>();
        var removals = new List<RuntimeMutation>();
        foreach (var entry in changeTracker.Entries())
        {
            var mapping = mappings.Resolve(entry);
            if (mapping is not null)
            {
                if (entry.State == EntityState.Added)
                    additions.Add(mapping.Add(entry.Entity));
                else if (entry.State == EntityState.Deleted)
                    removals.Add(mapping.Remove(entry.Entity));
            }
        }

        var scalarStart = Stopwatch.GetTimestamp();
        var propertyChanges = ReadModifiedProperties(changeTracker, mappings, includeProperty);
        if (diagnostics is not null)
        {
            diagnostics.ScalarCaptureTicks += Stopwatch.GetTimestamp() - scalarStart;
            diagnostics.CapturedPropertyMutations += propertyChanges.Count;
            diagnostics.CapturedNavigationMutations += navigationChanges.Count;
        }
        var mutations = additions
            .Concat(propertyChanges)
            .Concat(navigationChanges)
            .Concat(removals)
            .ToArray();
        return new ConsistencyUnitOfWork(mutations.Length == 0 ? null : MutationSet.Create(mutations));
    }

    public static ChangeImpact? ApplyTrackedChanges(this ConsistencyRuntime runtime, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(context);
        var changes = CreateChangeSet(context.ChangeTracker);
        return changes is null ? null : runtime.Apply(changes);
    }

    public static int SaveChangesAndApply(
        this DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyUnitOfWorkMappings mappings)
    {
        context.ChangeTracker.DetectChanges();
        ConsistencyStoreSideEffectGuard.ThrowIfUnsafe(context, runtime);
        ConsistencyGeneratedValueGuard.RejectForConvenienceSave(context, runtime, mappings);
        var unitOfWork = CaptureUnitOfWork(context.ChangeTracker, mappings);
        unitOfWork.Prepare(runtime);
        var result = context.SaveChanges();
        try
        {
            unitOfWork.Commit(runtime);
        }
        catch (Exception exception)
        {
            throw new ConsistencyRuntimeSynchronizationException(runtime.Version, exception);
        }
        unitOfWork.Dispatch(runtime);
        return result;
    }

    public static async Task<int> SaveChangesAndApplyAsync(
        this DbContext context,
        ConsistencyRuntime runtime,
        ConsistencyUnitOfWorkMappings mappings,
        CancellationToken cancellationToken = default)
    {
        context.ChangeTracker.DetectChanges();
        ConsistencyStoreSideEffectGuard.ThrowIfUnsafe(context, runtime);
        ConsistencyGeneratedValueGuard.RejectForConvenienceSave(context, runtime, mappings);
        var unitOfWork = CaptureUnitOfWork(context.ChangeTracker, mappings);
        unitOfWork.Prepare(runtime);
        var result = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            unitOfWork.Commit(runtime);
        }
        catch (Exception exception)
        {
            throw new ConsistencyRuntimeSynchronizationException(runtime.Version, exception);
        }
        unitOfWork.Dispatch(runtime);
        return result;
    }

    private static List<PropertyChange> ReadModifiedProperties(
        ChangeTracker changeTracker,
        ConsistencyUnitOfWorkMappings? mappings,
        Func<EntityEntry, IProperty, bool>? includeProperty = null)
    {
        var changes = new List<PropertyChange>();
        foreach (var entry in changeTracker.Entries().Where(entry => entry.State == EntityState.Modified))
        {
            var mapping = mappings?.Resolve(entry);
            foreach (var property in entry.Properties.Where(property => property.IsModified))
            {
                if (includeProperty is not null && !includeProperty(entry, property.Metadata))
                    continue;
                var member = GetMember(property.Metadata);
                if (member is null)
                    continue;
                changes.Add(mapping is null
                    ? Change.Property(entry.Entity, member, property.OriginalValue, property.CurrentValue)
                    : mapping.Property(entry.Entity, member, property.OriginalValue, property.CurrentValue));
            }
        }
        return changes;
    }

    private static IReadOnlyList<RuntimeMutation> CaptureNavigationChanges(
        ChangeTracker changeTracker,
        ConsistencyUnitOfWorkMappings? mappings,
        EfFingerprintDiagnostics? diagnostics = null) =>
        CaptureStabilizedNavigationSnapshot(changeTracker, mappings, diagnostics).Changes;

    private static StabilizedNavigationCapture CaptureStabilizedNavigationSnapshot(
        ChangeTracker changeTracker,
        ConsistencyUnitOfWorkMappings? mappings,
        EfFingerprintDiagnostics? diagnostics)
    {
        TrackedGraphSnapshot snapshot;
        List<RuntimeMutation> changes;
        var principalReferences = new List<PrincipalReferenceEvidence>();
        var autoDetectChanges = changeTracker.AutoDetectChangesEnabled;
        changeTracker.AutoDetectChangesEnabled = false;
        try
        {
            snapshot = TrackedGraphSnapshot.Create(changeTracker, diagnostics);
            changes = CaptureNavigationChangesCore(
                snapshot, mappings, diagnostics, principalReferences).ToList();
        }
        finally
        {
            changeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }

        changeTracker.DetectChanges();
        foreach (var evidence in principalReferences)
        {
            changes.RemoveAll(change => change is PropertyChange property &&
                ReferenceEquals(property.Instance, evidence.Owner.Entity) &&
                property.Member == evidence.Member);
            var currentValue = evidence.Owner.Reference(evidence.Navigation.Name).CurrentValue;
            if (!ReferenceEquals(evidence.OriginalValue, currentValue))
                changes.Add(evidence.Mapping is null
                    ? Change.Property(
                        evidence.Owner.Entity, evidence.Member, evidence.OriginalValue, currentValue)
                    : evidence.Mapping.Property(
                        evidence.Owner.Entity, evidence.Member, evidence.OriginalValue, currentValue));
        }
        return new StabilizedNavigationCapture(snapshot, changes);
    }

    private static IReadOnlyList<RuntimeMutation> CaptureNavigationChangesCore(
        TrackedGraphSnapshot snapshot,
        ConsistencyUnitOfWorkMappings? mappings,
        EfFingerprintDiagnostics? diagnostics,
        List<PrincipalReferenceEvidence> principalReferences)
    {
        var changes = new List<RuntimeMutation>();
        var resets = new Dictionary<(object Owner, MemberInfo Member),
            ConsistencyUnitOfWorkMappings.IEntitySetMapping?>(ReferenceMemberPairComparer.Instance);
        foreach (var owner in snapshot.Entries)
        {
            var mapping = mappings?.Resolve(owner);
            foreach (var reference in owner.References)
            {
                if (reference.Metadata is not INavigation navigation) continue;
                if (diagnostics is not null)
                    diagnostics.ReferenceNavigationsVisited++;
                var member = GetMember(navigation);
                if (member is null) continue;
                var oldValue = ResolveReference(snapshot, owner, navigation, original: true);
                if (!navigation.IsOnDependent)
                    principalReferences.Add(new PrincipalReferenceEvidence(
                        owner, navigation, member, mapping, oldValue));
                var newValue = ResolveReference(snapshot, owner, navigation, original: false);
                if (!ReferenceEquals(oldValue, newValue))
                    changes.Add(mapping is null
                        ? Change.Property(owner.Entity, member, oldValue, newValue)
                        : mapping.Property(owner.Entity, member, oldValue, newValue));
            }
            foreach (var collection in owner.Collections)
            {
                if (diagnostics is not null)
                    diagnostics.CollectionNavigationsVisited++;
                var member = GetMember(collection.Metadata);
                if (member is null) continue;
                if (collection.IsModified || CollectionRelationshipChanged(
                        snapshot, owner, collection.Metadata, diagnostics))
                    resets.TryAdd((owner.Entity, member), mapping);
            }
        }
        changes.AddRange(resets.Select(reset => reset.Value is null
            ? Change.CollectionReset(reset.Key.Owner, reset.Key.Member)
            : reset.Value.CollectionReset(reset.Key.Owner, reset.Key.Member)));
        return changes;
    }

    private sealed record StabilizedNavigationCapture(
        TrackedGraphSnapshot Snapshot,
        IReadOnlyList<RuntimeMutation> Changes);

    private sealed record PrincipalReferenceEvidence(
        EntityEntry Owner,
        INavigation Navigation,
        MemberInfo Member,
        ConsistencyUnitOfWorkMappings.IEntitySetMapping? Mapping,
        object? OriginalValue);

    private static object? ResolveReference(
        TrackedGraphSnapshot snapshot,
        EntityEntry owner,
        INavigation navigation,
        bool original)
    {
        var foreignKey = navigation.ForeignKey;
        if (navigation.IsOnDependent)
        {
            var values = foreignKey.Properties.Select(p => Value(owner, p, original)).ToArray();
            if (EfRelationshipKey.IsNull(foreignKey, values)) return null;
            var principals = snapshot.FindPrincipals(
                navigation.TargetEntityType, foreignKey.PrincipalKey, values, original);
            return ResolveUnique(principals, original, navigation.Name);
        }

        if (!original)
            return owner.Reference(navigation.Name).CurrentValue;

        var ownerValues = foreignKey.PrincipalKey.Properties.Select(p => Value(owner, p, original)).ToArray();
        var matches = snapshot.FindDependents(
            navigation.TargetEntityType, foreignKey, ownerValues, original);
        if (matches.Count <= 1) return matches.SingleOrDefault()?.Entity;
        throw new InvalidOperationException(
            $"The {(original ? "original" : "current")} value for reference '{navigation.Name}' is not tracked unambiguously.");
    }

    private static object ResolveUnique(
        IReadOnlyList<EntityEntry> matches,
        bool original,
        string navigationName)
    {
        return matches.Count == 1 ? matches[0].Entity : throw new InvalidOperationException(
            $"The {(original ? "original" : "current")} value for reference '{navigationName}' is not tracked unambiguously.");
    }

    private static object? Value(EntityEntry entry, IProperty property, bool original) =>
        original ? entry.Property(property.Name).OriginalValue : entry.Property(property.Name).CurrentValue;

    private static GeneratedForeignKeyFixupEvidence? CaptureGeneratedFixupEvidence(
        TrackedGraphSnapshot snapshot,
        EntityEntry dependent,
        IProperty property,
        EfFingerprintDiagnostics? diagnostics)
    {
        var foreignKeys = dependent.Metadata.GetForeignKeys()
            .Where(foreignKey => foreignKey.Properties.Contains(property))
            .ToArray();
        if (foreignKeys.Length != 1) return null;
        var foreignKey = foreignKeys[0];
        var foreignKeyValues = foreignKey.Properties
            .Select(component => Value(dependent, component, original: false)).ToArray();
        if (EfRelationshipKey.IsNull(foreignKey, foreignKeyValues)) return null;
        var navigation = foreignKey.DependentToPrincipal;
        var principal = navigation is null ? null : dependent.Reference(navigation.Name).CurrentValue;
        if (principal is null) return null;
        if (diagnostics is not null)
            diagnostics.GeneratedFixupPrincipalLookups++;
        var principalEntry = snapshot.FindEntry(principal);
        if (principalEntry is null) return null;
        var component = Enumerable.Range(0, foreignKey.Properties.Count)
            .Single(index => foreignKey.Properties[index] == property);
        var principalProperty = foreignKey.PrincipalKey.Properties[component];
        var principalValue = principalEntry.Property(principalProperty.Name);
        var generatedAtCapture = principalValue.IsTemporary ||
            principalEntry.State == EntityState.Added && principalProperty.ValueGenerated != ValueGenerated.Never;
        return generatedAtCapture
            ? new GeneratedForeignKeyFixupEvidence(foreignKey, navigation, principal, principalEntry)
            : null;
    }

    private static bool CollectionRelationshipChanged(TrackedGraphSnapshot snapshot, EntityEntry owner,
        Microsoft.EntityFrameworkCore.Metadata.IPropertyBase propertyBase,
        EfFingerprintDiagnostics? diagnostics)
    {
        if (propertyBase is not INavigation navigation) return false;
        var foreignKey = navigation.ForeignKey;
        var ownerCurrent = foreignKey.PrincipalKey.Properties.Select(p => Value(owner, p, false)).ToArray();
        var ownerOriginal = foreignKey.PrincipalKey.Properties.Select(p => Value(owner, p, true)).ToArray();
        var candidates = snapshot.FindDependents(
                navigation.TargetEntityType, foreignKey, ownerCurrent, original: false, collectionLookup: true)
            .Concat(snapshot.FindDependents(
                navigation.TargetEntityType, foreignKey, ownerOriginal, original: true, collectionLookup: true))
            .DistinctBy(entry => entry.Entity, ReferenceEqualityComparer.Instance);
        return candidates.Any(e =>
        {
            if (diagnostics is not null)
                diagnostics.CollectionCandidateChecks++;
            var current = foreignKey.Properties.Select(p => Value(e, p, false)).ToArray();
            var original = foreignKey.Properties.Select(p => Value(e, p, true)).ToArray();
            var currentMatches = !EfRelationshipKey.IsNull(foreignKey, current) &&
                EfRelationshipKey.ValuesEqual(foreignKey, current, ownerCurrent);
            var originalMatches = !EfRelationshipKey.IsNull(foreignKey, original) &&
                EfRelationshipKey.ValuesEqual(foreignKey, original, ownerOriginal);
            return (e.State == EntityState.Added && currentMatches) ||
                   (e.State == EntityState.Deleted && originalMatches) ||
                   (!EfRelationshipKey.RelationshipsEqual(foreignKey, current, original) &&
                    (currentMatches || originalMatches));
        });
    }

    private sealed class ReferenceMemberPairComparer : IEqualityComparer<(object Owner, MemberInfo Member)>
    {
        public static ReferenceMemberPairComparer Instance { get; } = new();
        public bool Equals((object Owner, MemberInfo Member) x, (object Owner, MemberInfo Member) y) =>
            ReferenceEquals(x.Owner, y.Owner) && x.Member == y.Member;
        public int GetHashCode((object Owner, MemberInfo Member) value) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.Owner), value.Member);
    }

    private static MemberInfo? GetMember(Microsoft.EntityFrameworkCore.Metadata.IPropertyBase property) =>
        property.PropertyInfo ?? (MemberInfo?)property.FieldInfo;
}

internal sealed class CapturedEfMutationSnapshot(
    IReadOnlyList<RuntimeMutation> additions,
    IReadOnlyList<CapturedEfPropertyMutation> properties,
    IReadOnlyList<RuntimeMutation> navigationChanges,
    IReadOnlyList<RuntimeMutation> removals)
{
    public bool HasChanges => additions.Count + properties.Count + navigationChanges.Count + removals.Count > 0;

    public ConsistencyUnitOfWork FinalizeForPlanning(
        DbContext context,
        IReadOnlyList<GeneratedValueCandidate> generatedValues)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(generatedValues);
        var generatedUpdates = generatedValues
            .Select(candidate => (Candidate: candidate, Mutation: candidate.FinalizeUpdate(context)))
            .Where(result => result.Mutation is not null)
            .ToArray();
        var mutations = additions
            .Concat(properties
                .Where(property => !generatedUpdates.Any(generated =>
                    ReferenceEquals(generated.Candidate.Entry.EntityEntry.Entity, property.Entry.Entity) &&
                    generated.Candidate.Property == property.Property))
                .Select(property => property.FinalizeForPlanning(context)))
            .Concat(generatedUpdates.Select(result => (RuntimeMutation)result.Mutation!))
            .Concat(navigationChanges)
            .Concat(removals)
            .ToArray();
        return new ConsistencyUnitOfWork(
            mutations.Length == 0 ? null : MutationSet.Create(mutations));
    }
}

internal sealed record CapturedEfPropertyMutation(
    PropertyChange Mutation,
    ConsistencyUnitOfWorkMappings.IEntitySetMapping? Mapping,
    EntityEntry Entry,
    IProperty Property,
    GeneratedForeignKeyFixupEvidence? Fixup)
{
    public RuntimeMutation FinalizeForPlanning(DbContext context)
    {
        var current = Entry.Property(Property.Name).CurrentValue;
        if (Equals(current, Mutation.NewValue) || Fixup is null || !Fixup.ProvesFinalValue(context, Entry))
            return Mutation;
        return Mapping is null
            ? Change.Property(Mutation.Instance, Mutation.Member, Mutation.OldValue, current)
            : Mapping.Property(Mutation.Instance, Mutation.Member, Mutation.OldValue, current);
    }
}

internal sealed record GeneratedForeignKeyFixupEvidence(
    IForeignKey ForeignKey,
    INavigation? Navigation,
    object IntendedPrincipal,
    EntityEntry PrincipalEntry)
{
    public bool ProvesFinalValue(DbContext context, EntityEntry dependent)
    {
        if (!ReferenceEquals(dependent.Context, context) ||
            !ReferenceEquals(PrincipalEntry.Context, context) ||
            Navigation is null ||
            !ReferenceEquals(dependent.Reference(Navigation.Name).CurrentValue, IntendedPrincipal))
            return false;
        for (var index = 0; index < ForeignKey.Properties.Count; index++)
        {
            var principal = PrincipalEntry.Property(ForeignKey.PrincipalKey.Properties[index].Name);
            var dependentValue = dependent.Property(ForeignKey.Properties[index].Name).CurrentValue;
            var comparer = ForeignKey.PrincipalKey.Properties[index].GetKeyValueComparer();
            if (principal.IsTemporary || !comparer.Equals(dependentValue, principal.CurrentValue))
                return false;
        }
        return true;
    }
}
