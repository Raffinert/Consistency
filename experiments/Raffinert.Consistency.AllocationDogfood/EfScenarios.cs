using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.AllocationDogfood;

internal static class EfScenarios
{
    public static async Task EfSave_EnforcesConfiguredInvariant()
    {
        await using var fixture = await EfFixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var (db, runtime) = await LoadCompleteGraphAsync(scope.ServiceProvider);
        var supply = await db.Supplies.SingleAsync(value => value.Id == 1);
        supply.Capacity = 6m;
        var version = runtime.Version;

        var error = await ScenarioAssert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => db.SaveChangesAsync(),
            "Ordinary SaveChangesAsync must reject an over-allocated supply.");

        ScenarioAssert.True(error.Violations.Any(value =>
                value.DefinitionKey == "supply-capacity-valid" && ReferenceEquals(value.Source, supply)),
            "The rejected plan must identify the affected capacity invariant and supply.");
        ScenarioAssert.Equal(version, runtime.Version,
            "A rejected save must not advance committed runtime state.");
        await using var verification = fixture.CreateContext();
        ScenarioAssert.Equal(10m, (await verification.Supplies.AsNoTracking()
            .SingleAsync(value => value.Id == 1)).Capacity,
            "A rejected save must not persist the unsafe capacity.");
    }

    public static async Task EfMaterializeBeforeRead_UsesInjectedRuntimeAndOrdinarySave()
    {
        await using var fixture = await EfFixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var (db, runtime) = await LoadCompleteGraphAsync(scope.ServiceProvider);
        var applicationRuntime = scope.ServiceProvider.GetRequiredService<IConsistencyRuntime>();
        var supply = await db.Supplies.SingleAsync(value => value.Id == 1);
        supply.Capacity = 15m;

        applicationRuntime.Materialize(supply);

        ScenarioAssert.Same(runtime, applicationRuntime,
            "Application code and the EF session must share one scoped runtime.");
        ScenarioAssert.Equal(3m, supply.FulfilledQuantity,
            "Object materialization must synchronize fulfillment total before save.");
        ScenarioAssert.Equal(5m, supply.AllocatedQuantity,
            "Object materialization must synchronize allocation total before save.");
        ScenarioAssert.Equal(7m, supply.RemainingCapacity,
            "Object materialization must make the read model current before it is read.");

        await db.SaveChangesAsync();

        await using var verification = fixture.CreateContext();
        var persisted = await verification.Supplies.AsNoTracking().SingleAsync(value => value.Id == 1);
        ScenarioAssert.Equal(15m, persisted.Capacity, "The business mutation must persist.");
        ScenarioAssert.Equal(7m, persisted.RemainingCapacity,
            "The explicitly materialized mirror must persist through ordinary SaveChangesAsync.");
    }

    public static async Task EfSaveWithoutPreRead_MaterializesAtBoundary()
    {
        await using var fixture = await EfFixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var (db, _) = await LoadCompleteGraphAsync(scope.ServiceProvider);
        var supply = await db.Supplies.SingleAsync(value => value.Id == 1);
        supply.Capacity = 12m;

        await db.SaveChangesAsync();

        ScenarioAssert.Equal(4m, supply.RemainingCapacity,
            "The save boundary must synchronize mirrors even when application code never reads them.");
        await using var verification = fixture.CreateContext();
        var persisted = await verification.Supplies.AsNoTracking().SingleAsync(value => value.Id == 1);
        ScenarioAssert.Equal(3m, persisted.FulfilledQuantity, "Fulfillment mirror must remain current.");
        ScenarioAssert.Equal(5m, persisted.AllocatedQuantity, "Allocation mirror must remain current.");
        ScenarioAssert.Equal(4m, persisted.RemainingCapacity, "Remaining capacity must persist at save.");
    }

    public static async Task EfSqlFailure_DoesNotCommitRuntime_AndRetrySucceeds()
    {
        await using var fixture = await EfFixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var (db, runtime) = await LoadCompleteGraphAsync(scope.ServiceProvider);
        var consistency = scope.ServiceProvider.GetRequiredService<IConsistencyRuntime>();
        var supply = await db.Supplies.SingleAsync(value => value.Id == 1);
        supply.Capacity = 12m;
        consistency.Materialize(supply);
        supply.FailureMarker = -1;
        var version = runtime.Version;

        await ScenarioAssert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(),
            "The database check constraint must force a post-planning SQL failure.");

        ScenarioAssert.Equal(version, runtime.Version,
            "A SQL failure must roll back the pending runtime commit.");
        supply.FailureMarker = 0;
        await db.SaveChangesAsync();
        ScenarioAssert.True(runtime.Version > version,
            "A successful retry must commit runtime state exactly at the durable boundary.");

        await using var verification = fixture.CreateContext();
        var persisted = await verification.Supplies.AsNoTracking().SingleAsync(value => value.Id == 1);
        ScenarioAssert.Equal(12m, persisted.Capacity, "The retry must persist the intended capacity.");
        ScenarioAssert.Equal(4m, persisted.RemainingCapacity,
            "The retry must persist the materialized value from the rebuilt plan.");
    }

    public static async Task EfLateTracking_InvalidatesPendingPlanBeforeSave()
    {
        await using var fixture = await EfFixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var runtime = services.GetRequiredService<ConsistencyRuntime>();
        var consistency = services.GetRequiredService<IConsistencyRuntime>();
        var db = services.GetRequiredService<AllocationDbContext>();
        await db.Demands.Where(value => value.Id == 1).LoadAsync();
        await db.Supplies.LoadAsync();
        await db.Allocations.Include(value => value.Demand).Include(value => value.Supply).LoadAsync();
        await db.Fulfillments.Include(value => value.Supply).LoadAsync();
        var supply = await db.Supplies.SingleAsync(value => value.Id == 1);
        supply.Capacity = 14m;
        consistency.Materialize(supply);

        await db.Demands.SingleAsync(value => value.Id == 2);
        await db.SaveChangesAsync();

        ScenarioAssert.True(runtime.Version > 0,
            "The save must re-plan and commit after late baseline admission.");
        await using var verification = fixture.CreateContext();
        ScenarioAssert.Equal(6m, (await verification.Supplies.AsNoTracking()
            .SingleAsync(value => value.Id == 1)).RemainingCapacity,
            "Late tracking must not let a stale pending materialization plan commit.");
    }

    public static async Task PartialTrackedGraph_IsNotTreatedAsAuthoritativelyComplete()
    {
        await using var fixture = await EfFixture.CreateAsync(completeScope: false);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var (db, runtime) = await LoadCompleteGraphAsync(scope.ServiceProvider);
        var supply = await db.Supplies.SingleAsync(value => value.Id == 1);
        supply.Capacity = 12m;

        await ScenarioAssert.ThrowsAsync<IncompleteConsistencyScopeException>(
            () => db.SaveChangesAsync(),
            "Tracking all currently known rows must not masquerade as a closed-world assertion.");
        ScenarioAssert.Equal(0L, runtime.Version,
            "Scope rejection must happen before runtime commit.");
    }

    public static async Task EfRejectedSave_CanBeRepairedAndRetried()
    {
        await using var fixture = await EfFixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var (db, runtime) = await LoadCompleteGraphAsync(scope.ServiceProvider);
        var supply = await db.Supplies.SingleAsync(value => value.Id == 1);
        var allocation = await db.Allocations
            .Include(value => value.Demand).Include(value => value.Supply)
            .SingleAsync(value => value.Id == 1);
        var session = scope.ServiceProvider
            .GetRequiredService<ConsistencyEfCoreSession<AllocationDbContext>>();
        supply.ChangeCapacity(6m);
        var version = runtime.Version;
        ConsistencyInvariantViolationException? error = null;
        RepairProcessingResult? repair = null;
        var saved = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await db.SaveChangesAsync();
                saved = true;
                break;
            }
            catch (ConsistencyInvariantViolationException rejected)
            {
                error = rejected;
                var request = rejected.RepairRequests.Single();
                ScenarioAssert.True(request.DefinitionKey == "supply-capacity-valid",
                    "The rejected save must expose the enforced repair-enabled invariant request.");
                ScenarioAssert.Same(supply, request.Source,
                    "The repair request must retain the tracked affected supply.");
                ScenarioAssert.Equal(version, runtime.Version,
                    "Inspecting repair data must not install the rejected runtime plan.");
                using var preview = session.CreateRejectedPreview(rejected);
                repair = new ReallocateDemand(fixture.Model).ProcessProposedState(
                    preview, rejected.RepairRequests);
                if (repair.Reallocated.Count == 0)
                    break;
            }
        }
        ScenarioAssert.True(error is not null,
            "The initial rich-domain mutation must be rejected before persistence.");
        ScenarioAssert.True(repair is not null,
            "The rejected proposed state must be available to application repair.");
        var completedRepair = repair!;
        ScenarioAssert.True(saved,
            "The repaired tracked graph must succeed on a subsequent save attempt.");
        ScenarioAssert.Same(allocation, completedRepair.Reallocated.Single(),
            "Application-owned repair must move the allocation identified from structured repair data.");
        ScenarioAssert.Equal(0, completedRepair.Unresolved.Count,
            "The tracked current graph has a deterministic replacement supply.");

        await using var verification = fixture.CreateContext();
        var persistedAllocation = await verification.Allocations.AsNoTracking()
            .SingleAsync(value => value.Id == 1);
        var supplies = await verification.Supplies.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync();
        ScenarioAssert.Equal(2, persistedAllocation.SupplyId,
            "Application-owned repair must persist the deterministic replacement.");
        ScenarioAssert.Equal(3m, supplies[0].RemainingCapacity,
            "The repaired original supply must retain only its fulfillment load.");
        ScenarioAssert.Equal(15m, supplies[1].RemainingCapacity,
            "The replacement supply must include the moved allocation.");
    }

    public static async Task EfRejectedPreview_InvalidatesAfterTrackedChange()
    {
        await using var fixture = await EfFixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var (db, runtime) = await LoadCompleteGraphAsync(scope.ServiceProvider);
        var session = scope.ServiceProvider
            .GetRequiredService<ConsistencyEfCoreSession<AllocationDbContext>>();
        var supply = await db.Supplies.SingleAsync(value => value.Id == 1);
        supply.ChangeCapacity(6m);
        var error = await ScenarioAssert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => db.SaveChangesAsync(),
            "The proposed-state preview test requires an enforced rejected save.");
        using var preview = session.CreateRejectedPreview(error);
        supply.Capacity = 5m;

        var stale = ScenarioAssert.Throws<InvalidOperationException>(
            () => preview.Evaluate(fixture.Model.RemainingCapacity, supply),
            "A proposed-state view must reject relevant tracked changes after rejection.");
        ScenarioAssert.True(stale.Message.Contains("stale", StringComparison.OrdinalIgnoreCase) ||
            stale.Message.Contains("drifted", StringComparison.OrdinalIgnoreCase),
            "The rejected proposed-state view must report deterministic staleness.");
        ScenarioAssert.Equal(0L, runtime.Version,
            "Invalidating a rejected preview must not install its runtime plan.");
    }

    private static async Task<(AllocationDbContext Db, ConsistencyRuntime Runtime)> LoadCompleteGraphAsync(
        IServiceProvider services)
    {
        var runtime = services.GetRequiredService<ConsistencyRuntime>();
        var db = services.GetRequiredService<AllocationDbContext>();
        await db.Demands.LoadAsync();
        await db.Supplies.LoadAsync();
        await db.Allocations.Include(value => value.Demand).Include(value => value.Supply).LoadAsync();
        await db.Fulfillments.Include(value => value.Supply).LoadAsync();
        return (db, runtime);
    }

    private sealed class EfFixture : IAsyncDisposable
    {
        private EfFixture(
            ServiceProvider provider,
            SqliteConnection connection,
            AllocationConsistencyModel model)
        {
            Provider = provider;
            Connection = connection;
            Model = model;
        }

        public ServiceProvider Provider { get; }
        public SqliteConnection Connection { get; }
        public AllocationConsistencyModel Model { get; }

        public static async Task<EfFixture> CreateAsync(bool completeScope = true)
        {
            var model = new AllocationConsistencyModel();
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var contextOptions = new DbContextOptionsBuilder<AllocationDbContext>()
                .UseSqlite(connection).Options;
            await using (var seed = new AllocationDbContext(contextOptions))
            {
                await seed.Database.EnsureCreatedAsync();
                var day = new DateOnly(2026, 1, 15);
                var demand1 = new Demand
                {
                    Id = 1,
                    ResourceCode = "A",
                    Date = day,
                    RequestedQuantity = 5m
                };
                var demand2 = new Demand
                {
                    Id = 2,
                    ResourceCode = "A",
                    Date = day,
                    RequestedQuantity = 2m
                };
                var supply1 = new Supply
                {
                    Id = 1,
                    ResourceCode = "A",
                    Date = day,
                    Capacity = 10m,
                    FulfilledQuantity = 3m,
                    AllocatedQuantity = 5m,
                    RemainingCapacity = 2m
                };
                var supply2 = new Supply
                {
                    Id = 2,
                    ResourceCode = "A",
                    Date = day,
                    Capacity = 20m,
                    RemainingCapacity = 20m
                };
                var unrelated = new Supply
                {
                    Id = 3,
                    ResourceCode = "B",
                    Date = day,
                    Capacity = 20m,
                    RemainingCapacity = 20m
                };
                seed.AddRange(demand1, demand2, supply1, supply2, unrelated,
                    new Allocation
                    {
                        Id = 1,
                        Demand = demand1,
                        Supply = supply1,
                        Quantity = 5m
                    },
                    new Fulfillment
                    {
                        Id = 1,
                        Supply = supply1,
                        Quantity = 3m
                    });
                await seed.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddDbContext<AllocationDbContext>(options => options.UseSqlite(connection));
            services.AddRaffinertConsistency<AllocationDbContext>(
                model.Compiled,
                model.Mappings,
                new ConsistencySaveOptions
                {
                    Scope = completeScope
                        ? model.CompleteScope()
                        : new ConsistencyScope().Complete(model.Supplies)
                });
            var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });
            return new EfFixture(provider, connection, model);
        }

        public AllocationDbContext CreateContext() => new(
            new DbContextOptionsBuilder<AllocationDbContext>().UseSqlite(Connection).Options);

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
