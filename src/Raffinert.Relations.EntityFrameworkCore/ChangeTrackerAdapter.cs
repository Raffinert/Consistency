using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Raffinert.Relations.EntityFrameworkCore;

/// <summary>Translates EF Core scalar-property tracking into atomic Raffinert change sets.</summary>
public static class ChangeTrackerAdapter
{
    public static ChangeSet? CreateChangeSet(ChangeTracker changeTracker)
    {
        ArgumentNullException.ThrowIfNull(changeTracker);
        changeTracker.DetectChanges();
        var changes = new List<PropertyChange>();
        foreach (var entry in changeTracker.Entries().Where(entry => entry.State == EntityState.Modified))
        {
            foreach (var property in entry.Properties.Where(property => property.IsModified))
            {
                var member = property.Metadata.PropertyInfo ?? (System.Reflection.MemberInfo?)property.Metadata.FieldInfo;
                if (member is null)
                    continue;
                changes.Add(Change.Property(
                    entry.Entity,
                    member,
                    property.OriginalValue,
                    property.CurrentValue));
            }
        }
        return changes.Count == 0 ? null : ChangeSet.Create(changes.ToArray());
    }

    public static ChangeImpact? ApplyTrackedChanges(this RelationRuntime runtime, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(context);
        var changes = CreateChangeSet(context.ChangeTracker);
        return changes is null ? null : runtime.Apply(changes);
    }
}
