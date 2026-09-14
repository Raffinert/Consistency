using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Raffinert.Relations.EntityFrameworkCore;

/// <summary>
/// Indicates that the database operation succeeded but synchronizing the committed domain state into
/// the relation runtime failed. The runtime retains its pre-commit state and must be reconciled or rebuilt.
/// </summary>
public sealed class RelationRuntimeSynchronizationException : Exception
{
    internal RelationRuntimeSynchronizationException(long runtimeVersion, Exception innerException)
        : base("The database operation succeeded, but relation runtime synchronization failed. " +
               "Do not retry the database command; reconcile or rebuild the runtime from authoritative state.",
            innerException) => RuntimeVersion = runtimeVersion;

    public bool DatabaseOperationSucceeded => true;
    public long RuntimeVersion { get; }
}

/// <summary>Maps EF entity entries to specific Raffinert object sets.</summary>
public sealed class RelationUnitOfWorkMappings
{
    private readonly List<IEntitySetMapping> _mappings = [];

    public RelationUnitOfWorkMappings Map<TEntity>(
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
        bool Matches(EntityEntry entry);
        ObjectAdded Add(object entity);
        ObjectRemoved Remove(object entity);
        PropertyChange Property(object entity, MemberInfo member, object? oldValue, object? newValue);
    }

    private sealed class EntitySetMapping<TEntity>(
        ObjectSet<TEntity> set,
        Func<EntityEntry<TEntity>, bool>? selector) : IEntitySetMapping where TEntity : class
    {
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
/// succeeds; a failed database operation therefore never advances Raffinert runtime state.
/// </summary>
public sealed class RelationUnitOfWork
{
    private readonly MutationSet? _mutations;
    private PreparedMutation? _prepared;
    private bool _isPrepared;
    private bool _emptyCommitted;
    private bool _emptyDispatched;

    internal RelationUnitOfWork(MutationSet? mutations) => _mutations = mutations;

    public bool HasChanges => _mutations is not null;

    /// <summary>Validates all captured mutations before the database operation begins.</summary>
    public void Prepare(RelationRuntime runtime)
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
    public ChangeImpact? Commit(RelationRuntime runtime)
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
        return runtime.Commit(_prepared!);
    }

    /// <summary>
    /// Commits prepared runtime state and returns detailed data before policy dispatch. Empty units
    /// return <see langword="null"/>.
    /// </summary>
    public RuntimeApplyResult? CommitDetailed(
        RelationRuntime runtime,
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
        return runtime.CommitDetailed(_prepared!, detailLevel);
    }

    /// <summary>
    /// Predicts detailed runtime impact for this prepared unit without committing or dispatching it.
    /// Empty units return <see langword="null"/>.
    /// </summary>
    public RuntimeApplyResult? PreviewDetailed(
        RelationRuntime runtime,
        RuntimeImpactDetailLevel detailLevel = RuntimeImpactDetailLevel.Summary)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!_isPrepared)
            throw new InvalidOperationException("This unit of work must be prepared before it is previewed.");
        return _mutations is null ? null : runtime.PreviewDetailed(_prepared!, detailLevel);
    }

    /// <summary>Dispatches post-commit policy callbacks.</summary>
    public void Dispatch(RelationRuntime runtime)
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
    public void Apply(RelationRuntime runtime)
    {
        Prepare(runtime);
        Commit(runtime);
        Dispatch(runtime);
    }
}

/// <summary>Translates EF Core change tracking into a post-database-commit Raffinert unit of work.</summary>
public static class ChangeTrackerAdapter
{
    public static ChangeSet? CreateChangeSet(ChangeTracker changeTracker)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        changeTracker.DetectChanges();
        var changes = ReadModifiedProperties(changeTracker, null);
        return changes.Count == 0 ? null : ChangeSet.Create(changes.ToArray());
    }

    public static RelationUnitOfWork CaptureUnitOfWork(
        ChangeTracker changeTracker,
        RelationUnitOfWorkMappings mappings)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        ArgumentNullException.ThrowIfNull(mappings);
        changeTracker.DetectChanges();
        var additions = new List<RuntimeMutation>();
        var removals = new List<RuntimeMutation>();
        var collectionChanges = new List<RuntimeMutation>();
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

            if (entry.State is not EntityState.Added and not EntityState.Deleted)
            {
                foreach (var collection in entry.Collections.Where(value => value.IsModified))
                {
                    var member = GetMember(collection.Metadata);
                    if (member is not null)
                        collectionChanges.Add(Change.CollectionReset(entry.Entity, member));
                }
            }
        }

        var propertyChanges = ReadModifiedProperties(changeTracker, mappings);
        var mutations = additions
            .Concat(propertyChanges)
            .Concat(collectionChanges)
            .Concat(removals)
            .ToArray();
        return new RelationUnitOfWork(mutations.Length == 0 ? null : MutationSet.Create(mutations));
    }

    public static ChangeImpact? ApplyTrackedChanges(this RelationRuntime runtime, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(context);
        var changes = CreateChangeSet(context.ChangeTracker);
        return changes is null ? null : runtime.Apply(changes);
    }

    public static int SaveChangesAndApply(
        this DbContext context,
        RelationRuntime runtime,
        RelationUnitOfWorkMappings mappings)
    {
        var unitOfWork = CaptureUnitOfWork(context.ChangeTracker, mappings);
        unitOfWork.Prepare(runtime);
        var result = context.SaveChanges();
        try
        {
            unitOfWork.Commit(runtime);
        }
        catch (Exception exception)
        {
            throw new RelationRuntimeSynchronizationException(runtime.Version, exception);
        }
        unitOfWork.Dispatch(runtime);
        return result;
    }

    public static async Task<int> SaveChangesAndApplyAsync(
        this DbContext context,
        RelationRuntime runtime,
        RelationUnitOfWorkMappings mappings,
        CancellationToken cancellationToken = default)
    {
        var unitOfWork = CaptureUnitOfWork(context.ChangeTracker, mappings);
        unitOfWork.Prepare(runtime);
        var result = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            unitOfWork.Commit(runtime);
        }
        catch (Exception exception)
        {
            throw new RelationRuntimeSynchronizationException(runtime.Version, exception);
        }
        unitOfWork.Dispatch(runtime);
        return result;
    }

    private static List<PropertyChange> ReadModifiedProperties(
        ChangeTracker changeTracker,
        RelationUnitOfWorkMappings? mappings)
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
            foreach (var reference in entry.References.Where(value => value.IsModified))
            {
                var member = GetMember(reference.Metadata);
                if (member is null)
                    continue;
                changes.Add(mapping is null
                    ? Change.Property(entry.Entity, member, null, reference.CurrentValue)
                    : mapping.Property(entry.Entity, member, null, reference.CurrentValue));
            }
        }
        return changes;
    }

    private static MemberInfo? GetMember(Microsoft.EntityFrameworkCore.Metadata.IPropertyBase property) =>
        property.PropertyInfo ?? (MemberInfo?)property.FieldInfo;
}
