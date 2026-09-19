using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Raffinert.Consistency.EntityFrameworkCore;

public static class ConsistencyEfCoreServiceCollectionExtensions
{
    public static IServiceCollection AddRaffinertConsistency<TDbContext>(
        this IServiceCollection services,
        CompiledConsistencyModel compiledModel,
        ConsistencyEfCoreMappings mappings,
        ConsistencySaveOptions? options = null)
        where TDbContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(compiledModel);
        ArgumentNullException.ThrowIfNull(mappings);
        if (services.Any(descriptor =>
                descriptor.ServiceType == typeof(ConsistencyEfCoreRegistrationMarker)))
            throw new InvalidOperationException(
                "Exactly one Raffinert EF Core integration registration is supported per IServiceCollection.");
        services.AddSingleton<ConsistencyEfCoreRegistrationMarker>();
        services.AddSingleton(compiledModel);
        services.AddSingleton(mappings);
        services.AddSingleton(options ?? new ConsistencySaveOptions());
        services.AddScoped<ConsistencyEfCoreSession<TDbContext>>(serviceProvider =>
        {
            var context = serviceProvider.GetRequiredService<TDbContext>();
            return new ConsistencyEfCoreSession<TDbContext>(
                context,
                serviceProvider.GetRequiredService<ConsistencyEfCoreMappings>(),
                serviceProvider.GetRequiredService<ConsistencySaveOptions>(),
                () => serviceProvider.GetRequiredService<ConsistencyRuntime>());
        });
        services.AddScoped<ConsistencyRuntime>(serviceProvider =>
        {
            var runtime = serviceProvider.GetRequiredService<CompiledConsistencyModel>().CreateRuntime();
            serviceProvider.GetRequiredService<ConsistencyEfCoreSession<TDbContext>>().BindRuntime(runtime);
            return runtime;
        });
        services.AddScoped<IConsistencyRuntime>(serviceProvider =>
            serviceProvider.GetRequiredService<ConsistencyRuntime>());
        services.AddScoped<ConsistencyScopedSaveChangesInterceptor<TDbContext>>();
        services.AddSingleton<IDbContextOptionsConfiguration<TDbContext>,
            ConsistencyDbContextOptionsConfiguration<TDbContext>>();
        return services;
    }
}

internal sealed class ConsistencyEfCoreRegistrationMarker;

internal sealed class ConsistencyDbContextOptionsConfiguration<TDbContext>
    : IDbContextOptionsConfiguration<TDbContext>
    where TDbContext : DbContext
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(
            serviceProvider.GetRequiredService<ConsistencyScopedSaveChangesInterceptor<TDbContext>>());
}

internal sealed class ConsistencyScopedSaveChangesInterceptor<TDbContext> : SaveChangesInterceptor
    where TDbContext : DbContext
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Session(eventData.Context).PrepareForSave();
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await Session(eventData.Context).PrepareForSaveAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Session(eventData.Context).CompleteSave();
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Session(eventData.Context).CompleteSave();
        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) =>
        Session(eventData.Context).FailSave();

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Session(eventData.Context).FailSave();
        return Task.CompletedTask;
    }

    private static ConsistencyEfCoreSession<TDbContext> Session(DbContext? context)
    {
        if (context is not TDbContext typedContext)
            throw new InvalidOperationException("The consistency interceptor received an unexpected DbContext type.");
        return typedContext.GetService<ConsistencyEfCoreSession<TDbContext>>();
    }
}
