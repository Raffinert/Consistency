using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Raffinert.Relations.EntityFrameworkCore;

/// <summary>Maps EF entity entries to specific Raffinert object sets.</summary>
public sealed class RelationUnitOfWorkMappings
{
    private readonly List<IEntitySetMapping> _mappings = [];

    public RelationUnitOfWorkMappings Map<TEntity>(
        ObjectSetBuilder<TEntity> set,
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
        void Add(RelationRuntime runtime, object entity);
        void Remove(RelationRuntime runtime, object entity);
        PropertyChange Property(object entity, MemberInfo member, object? oldValue, object? newValue);
    }

    private sealed class EntitySetMapping<TEntity>(
        ObjectSetBuilder<TEntity> set,
        Func<EntityEntry<TEntity>, bool>? selector) : IEntitySetMapping where TEntity : class
    {
        public bool Matches(EntityEntry entry) =>
            entry.Entity is TEntity entity &&
            (selector is null || selector(entry.Context.Entry(entity)));

        public void Add(RelationRuntime runtime, object entity) => runtime.Add(set, (TEntity)entity);

        public void Remove(RelationRuntime runtime, object entity) => runtime.Remove(set, (TEntity)entity);

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
    private readonly IReadOnlyList<Action<RelationRuntime>> _additions;
    private readonly ChangeSet? _changes;
    private readonly IReadOnlyList<CollectionChange> _collectionChanges;
    private readonly IReadOnlyList<Action<RelationRuntime>> _removals;
    private bool _applied;

    internal RelationUnitOfWork(
        IReadOnlyList<Action<RelationRuntime>> additions,
        ChangeSet? changes,
        IReadOnlyList<CollectionChange> collectionChanges,
        IReadOnlyList<Action<RelationRuntime>> removals)
    {
        _additions = additions;
        _changes = changes;
        _collectionChanges = collectionChanges;
        _removals = removals;
    }

    public bool HasChanges =>
        _additions.Count > 0 || _changes is not null || _collectionChanges.Count > 0 || _removals.Count > 0;

    public void Apply(RelationRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (_applied)
            throw new InvalidOperationException("This unit of work has already been applied.");
        _applied = true;
        foreach (var add in _additions)
            add(runtime);
        if (_changes is not null)
            runtime.Apply(_changes, ChangeValidationMode.StrictNewValue);
        foreach (var collectionChange in _collectionChanges)
            runtime.Apply(collectionChange);
        foreach (var remove in _removals)
            remove(runtime);
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
        var additions = new List<Action<RelationRuntime>>();
        var removals = new List<Action<RelationRuntime>>();
        var collectionChanges = new List<CollectionChange>();
        foreach (var entry in changeTracker.Entries())
        {
            var mapping = mappings.Resolve(entry);
            if (mapping is not null)
            {
                if (entry.State == EntityState.Added)
                    additions.Add(runtime => mapping.Add(runtime, entry.Entity));
                else if (entry.State == EntityState.Deleted)
                    removals.Add(runtime => mapping.Remove(runtime, entry.Entity));
            }

            if (entry.State == EntityState.Modified)
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
        return new RelationUnitOfWork(
            additions,
            propertyChanges.Count == 0 ? null : ChangeSet.Create(propertyChanges.ToArray()),
            collectionChanges,
            removals);
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
        var result = context.SaveChanges();
        unitOfWork.Apply(runtime);
        return result;
    }

    public static async Task<int> SaveChangesAndApplyAsync(
        this DbContext context,
        RelationRuntime runtime,
        RelationUnitOfWorkMappings mappings,
        CancellationToken cancellationToken = default)
    {
        var unitOfWork = CaptureUnitOfWork(context.ChangeTracker, mappings);
        var result = await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        unitOfWork.Apply(runtime);
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
