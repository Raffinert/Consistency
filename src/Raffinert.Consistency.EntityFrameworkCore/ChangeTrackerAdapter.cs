using System.Reflection;
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
        ConsistencyUnitOfWorkMappings mappings)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        ArgumentNullException.ThrowIfNull(mappings);
        var navigationChanges = CaptureNavigationChanges(changeTracker, mappings);
        changeTracker.DetectChanges();
        var additions = new List<RuntimeMutation>();
        var removals = new List<RuntimeMutation>();
        var properties = new List<CapturedEfPropertyMutation>();
        foreach (var entry in changeTracker.Entries())
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
                var mutation = mapping is null
                    ? Change.Property(entry.Entity, member, property.OriginalValue, property.CurrentValue)
                    : mapping.Property(entry.Entity, member, property.OriginalValue, property.CurrentValue);
                properties.Add(new CapturedEfPropertyMutation(
                    mutation,
                    mapping,
                    entry,
                    property.Metadata,
                    CaptureGeneratedFixupEvidence(changeTracker, entry, property.Metadata)));
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
        changeTracker.DetectChanges();
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
        changeTracker.DetectChanges();
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
        ConsistencyUnitOfWorkMappings? mappings)
    {
        var changes = new List<PropertyChange>();
        foreach (var entry in changeTracker.Entries().Where(entry => entry.State == EntityState.Modified))
        {
            var mapping = mappings?.Resolve(entry);
            foreach (var property in entry.Properties.Where(property => property.IsModified))
            {
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
        ChangeTracker changeTracker, ConsistencyUnitOfWorkMappings? mappings)
    {
        var autoDetectChanges = changeTracker.AutoDetectChangesEnabled;
        changeTracker.AutoDetectChangesEnabled = false;
        try
        {
            return CaptureNavigationChangesCore(changeTracker, mappings);
        }
        finally
        {
            changeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    private static IReadOnlyList<RuntimeMutation> CaptureNavigationChangesCore(
        ChangeTracker changeTracker, ConsistencyUnitOfWorkMappings? mappings)
    {
        var changes = new List<RuntimeMutation>();
        var resets = new HashSet<(object Owner, MemberInfo Member)>(ReferenceMemberPairComparer.Instance);
        foreach (var owner in changeTracker.Entries())
        {
            var mapping = mappings?.Resolve(owner);
            foreach (var reference in owner.References)
            {
                if (reference.Metadata is not INavigation navigation) continue;
                var member = GetMember(navigation);
                if (member is null) continue;
                var oldValue = ResolveReference(changeTracker, owner, navigation, original: true);
                var newValue = ResolveReference(changeTracker, owner, navigation, original: false);
                if (!ReferenceEquals(oldValue, newValue))
                    changes.Add(mapping is null
                        ? Change.Property(owner.Entity, member, oldValue, newValue)
                        : mapping.Property(owner.Entity, member, oldValue, newValue));
            }
            foreach (var collection in owner.Collections)
            {
                var member = GetMember(collection.Metadata);
                if (member is null) continue;
                if (collection.IsModified || CollectionRelationshipChanged(changeTracker, owner, collection.Metadata))
                    resets.Add((owner.Entity, member));
            }
        }
        changes.AddRange(resets.Select(x => Change.CollectionReset(x.Owner, x.Member)));
        return changes;
    }

    private static object? ResolveReference(ChangeTracker tracker, EntityEntry owner,
        INavigation navigation, bool original)
    {
        var foreignKey = navigation.ForeignKey;
        if (navigation.IsOnDependent)
        {
            var values = foreignKey.Properties.Select(p => Value(owner, p, original)).ToArray();
            if (values.All(x => x is null)) return null;
            return ResolveUnique(tracker, navigation.TargetEntityType, foreignKey.PrincipalKey.Properties, values,
                original, navigation.Name);
        }

        if (!original)
            return owner.Reference(navigation.Name).CurrentValue;

        var ownerValues = foreignKey.PrincipalKey.Properties.Select(p => Value(owner, p, original)).ToArray();
        var matches = ResolveMatches(tracker, navigation.TargetEntityType, foreignKey.Properties, ownerValues, original);
        if (matches.Length <= 1) return matches.SingleOrDefault();
        throw new InvalidOperationException(
            $"The {(original ? "original" : "current")} value for reference '{navigation.Name}' is not tracked unambiguously.");
    }

    private static object ResolveUnique(ChangeTracker tracker, IEntityType target,
        IReadOnlyList<IProperty> properties, object?[] values, bool original, string navigationName)
    {
        var matches = ResolveMatches(tracker, target, properties, values, original);
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException(
            $"The {(original ? "original" : "current")} value for reference '{navigationName}' is not tracked unambiguously.");
    }

    private static object[] ResolveMatches(ChangeTracker tracker, IEntityType target,
        IReadOnlyList<IProperty> properties, object?[] values, bool original) => tracker.Entries()
        .Where(candidate => target.ClrType.IsInstanceOfType(candidate.Entity))
        .Where(candidate => properties.Select(p => Value(candidate, p, original)).SequenceEqual(values))
        .Select(candidate => candidate.Entity).ToArray();

    private static object? Value(EntityEntry entry, IProperty property, bool original) =>
        original ? entry.Property(property.Name).OriginalValue : entry.Property(property.Name).CurrentValue;

    private static GeneratedForeignKeyFixupEvidence? CaptureGeneratedFixupEvidence(
        ChangeTracker tracker,
        EntityEntry dependent,
        IProperty property)
    {
        var foreignKeys = dependent.Metadata.GetForeignKeys()
            .Where(foreignKey => foreignKey.Properties.Contains(property))
            .ToArray();
        if (foreignKeys.Length != 1) return null;
        var foreignKey = foreignKeys[0];
        var navigation = foreignKey.DependentToPrincipal;
        var principal = navigation is null ? null : dependent.Reference(navigation.Name).CurrentValue;
        if (principal is null) return null;
        var principalEntry = tracker.Entries().SingleOrDefault(entry => ReferenceEquals(entry.Entity, principal));
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

    private static bool CollectionRelationshipChanged(ChangeTracker tracker, EntityEntry owner,
        Microsoft.EntityFrameworkCore.Metadata.IPropertyBase propertyBase)
    {
        if (propertyBase is not INavigation navigation) return false;
        var foreignKey = navigation.ForeignKey;
        var ownerCurrent = foreignKey.PrincipalKey.Properties.Select(p => Value(owner, p, false)).ToArray();
        var ownerOriginal = foreignKey.PrincipalKey.Properties.Select(p => Value(owner, p, true)).ToArray();
        return tracker.Entries().Where(e => navigation.TargetEntityType.ClrType.IsInstanceOfType(e.Entity)).Any(e =>
        {
            var current = foreignKey.Properties.Select(p => Value(e, p, false)).ToArray();
            var original = foreignKey.Properties.Select(p => Value(e, p, true)).ToArray();
            return (e.State == EntityState.Added && current.SequenceEqual(ownerCurrent)) ||
                   (e.State == EntityState.Deleted && original.SequenceEqual(ownerOriginal)) ||
                   (!current.SequenceEqual(original) &&
                    (current.SequenceEqual(ownerCurrent) || original.SequenceEqual(ownerOriginal)));
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
            if (principal.IsTemporary || !Equals(
                    dependent.Property(ForeignKey.Properties[index].Name).CurrentValue,
                    principal.CurrentValue))
                return false;
        }
        return true;
    }
}
