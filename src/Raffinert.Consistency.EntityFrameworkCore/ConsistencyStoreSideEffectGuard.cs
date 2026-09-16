using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raffinert.Consistency.EntityFrameworkCore;

/// <summary>
/// Indicates that a database referential action can mutate consistency-relevant rows without tracked evidence.
/// </summary>
public sealed class ConsistencyStoreSideReferentialActionNotSupportedException : Exception
{
    internal ConsistencyStoreSideReferentialActionNotSupportedException(
        Type deletedEntityType,
        Type affectedEntityType,
        DeleteBehavior deleteBehavior,
        IReadOnlyList<Type> entityPath)
        : base($"Deleting '{deletedEntityType.Name}' can invoke database {deleteBehavior} and change or delete " +
            $"consistency-relevant '{affectedEntityType.Name}' rows without tracked mutation evidence. " +
            "Raffinert will not auto-load missing rows. Configure ClientCascade/ClientSetNull and explicitly " +
            "track dependents, or perform the operation outside authoritative save APIs and rebuild or " +
            "reconcile the runtime from authoritative state.")
    {
        DeletedEntityType = deletedEntityType;
        AffectedEntityType = affectedEntityType;
        DeleteBehavior = deleteBehavior;
        EntityPath = entityPath;
    }

    /// <summary>The tracked entity type whose deletion starts the store-side path.</summary>
    public Type DeletedEntityType { get; }
    /// <summary>The consistency-relevant entity type reached by the path.</summary>
    public Type AffectedEntityType { get; }
    /// <summary>The store-mutating behavior on the final relationship in the reported path.</summary>
    public DeleteBehavior DeleteBehavior { get; }
    /// <summary>The principal-to-dependent CLR type path from the deletion to the affected type.</summary>
    public IReadOnlyList<Type> EntityPath { get; }
}

internal static class ConsistencyStoreSideEffectGuard
{
    public static void ThrowIfUnsafe(DbContext context, ConsistencyRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runtime);
        foreach (var entry in context.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Deleted))
        {
            var origin = entry.Metadata.ClrType;
            Traverse(context, runtime, entry.Metadata, origin, [origin], new HashSet<IForeignKey>());
        }
    }

    private static void Traverse(
        DbContext context,
        ConsistencyRuntime runtime,
        IEntityType principal,
        Type origin,
        IReadOnlyList<Type> path,
        HashSet<IForeignKey> visited)
    {
        foreach (var foreignKey in principal.GetReferencingForeignKeys())
        {
            if (!visited.Add(foreignKey)) continue;
            var behavior = foreignKey.DeleteBehavior;
            if (behavior is not DeleteBehavior.Cascade and not DeleteBehavior.SetNull) continue;
            var dependent = foreignKey.DeclaringEntityType;
            var dependentType = dependent.ClrType;
            var nextPath = path.Append(dependentType).ToArray();
            var independentlyRepresented = runtime.HasObjectSetForClrType(dependentType);
            var relevant = behavior == DeleteBehavior.Cascade
                ? independentlyRepresented || !foreignKey.IsOwnership &&
                    runtime.HasNestedSemanticUsageForClrType(dependentType)
                : independentlyRepresented || SetNullTouchesSemantics(runtime, foreignKey);
            if (relevant && !AllRuntimeDependentsAreTracked(context, runtime, foreignKey, behavior))
                throw new ConsistencyStoreSideReferentialActionNotSupportedException(
                    origin, dependentType, behavior, nextPath);
            if (behavior == DeleteBehavior.Cascade)
                Traverse(context, runtime, dependent, origin, nextPath, visited);
        }
    }

    private static bool AllRuntimeDependentsAreTracked(
        DbContext context,
        ConsistencyRuntime runtime,
        IForeignKey foreignKey,
        DeleteBehavior behavior)
    {
        var instances = runtime.GetObjectSetInstancesForClrType(foreignKey.DeclaringEntityType.ClrType);
        if (instances.Count == 0) return false;
        foreach (var instance in instances)
        {
            var entry = context.ChangeTracker.Entries()
                .SingleOrDefault(candidate => ReferenceEquals(candidate.Entity, instance));
            if (entry is null) return false;
            if (behavior == DeleteBehavior.Cascade)
            {
                if (entry.State != EntityState.Deleted) return false;
                continue;
            }
            if (entry.State != EntityState.Modified || foreignKey.Properties.Any(property =>
                    entry.Property(property.Name).CurrentValue is not null))
                return false;
        }
        return true;
    }

    private static bool SetNullTouchesSemantics(ConsistencyRuntime runtime, IForeignKey foreignKey)
    {
        foreach (var property in foreignKey.Properties)
        {
            var member = property.PropertyInfo ?? (MemberInfo?)property.FieldInfo;
            if (member is not null && runtime.GetTrackedMemberUsage(null, member) !=
                ConsistencyRuntime.ModelMemberUsageKind.None)
                return true;
        }
        var navigation = foreignKey.DependentToPrincipal;
        var navigationMember = navigation?.PropertyInfo ?? (MemberInfo?)navigation?.FieldInfo;
        return navigationMember is not null && runtime.GetTrackedMemberUsage(null, navigationMember) !=
            ConsistencyRuntime.ModelMemberUsageKind.None;
    }
}
