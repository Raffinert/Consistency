using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Raffinert.Relations.EntityFrameworkCore;

public sealed class RelationConsistencySaveChangesInterceptor(
    RelationRuntime runtime,
    RelationEfCoreMappings mappings,
    RelationEfCoreConsistencyOptions options) : SaveChangesInterceptor
{
    private readonly ConditionalWeakTable<DbContext, PendingConsistencySave> _pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Prepare(eventData.Context); return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Prepare(eventData.Context); return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Complete(eventData.Context); return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Complete(eventData.Context); return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Remove(eventData.Context);
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Remove(eventData.Context);
        return Task.CompletedTask;
    }

    private void Prepare(DbContext? context)
    {
        if (context is null) return;
        if (_pending.TryGetValue(context, out _)) throw new InvalidOperationException("A consistency save is already pending for this DbContext.");
        _pending.Add(context, ConsistencyCoordinator.Prepare(context, runtime, mappings, options));
    }

    private void Complete(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var pending)) return;
        _pending.Remove(context); ConsistencyCoordinator.Complete(runtime, pending);
    }

    private void Remove(DbContext? context) { if (context is not null) _pending.Remove(context); }
}
