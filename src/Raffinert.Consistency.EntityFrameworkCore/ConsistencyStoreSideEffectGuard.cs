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

    public Type DeletedEntityType { get; }
    public Type AffectedEntityType { get; }
    public DeleteBehavior DeleteBehavior { get; }
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
            Traverse(runtime, entry.Metadata, origin, [origin], new HashSet<IForeignKey>());
        }
    }

    private static void Traverse(
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
            if (relevant)
                throw new ConsistencyStoreSideReferentialActionNotSupportedException(
                    origin, dependentType, behavior, nextPath);
            if (behavior == DeleteBehavior.Cascade)
                Traverse(runtime, dependent, origin, nextPath, visited);
        }
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
