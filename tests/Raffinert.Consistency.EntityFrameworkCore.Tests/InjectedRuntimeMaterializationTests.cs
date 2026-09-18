using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class InjectedRuntimeMaterializationTests
{
    [Fact]
    public async Task Injected_service_materializes_before_save_and_persists_with_ordinary_save()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<LinkService>();

        await service.ChangeLeftValueAsync(fixture.LinkId, 55m, CancellationToken.None);

        Assert.Equal(5.5m, service.ObservedRatio);
        Assert.Equal(5.5m, service.ObservedNormalizedRatio);
        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking()
            .Include(value => value.Left).Include(value => value.Right).SingleAsync();
        Assert.Equal(55m, persisted.Left.Value);
        Assert.Equal(5.5m, persisted.Ratio);
        Assert.Equal(5.5m, persisted.NormalizedRatio);
    }

    [Fact]
    public async Task Runtime_and_context_resolution_order_uses_one_scoped_runtime_and_session()
    {
        await using var fixture = await Fixture.CreateAsync();
        ConsistencyRuntime firstRuntime;
        await using (var firstScope = fixture.Provider.CreateAsyncScope())
        {
            _ = firstScope.ServiceProvider.GetRequiredService<LinkContext>();
            var runtime = firstRuntime = firstScope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
            var session = firstScope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
            Assert.Same(runtime, session.Runtime);
            Assert.Same(firstScope.ServiceProvider.GetRequiredService<LinkContext>(), session.Context);
        }

        await using (var secondScope = fixture.Provider.CreateAsyncScope())
        {
            var runtime = secondScope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
            var context = secondScope.ServiceProvider.GetRequiredService<LinkContext>();
            var session = secondScope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<LinkContext>>();
            Assert.Same(runtime, session.Runtime);
            Assert.Same(context, session.Context);
            Assert.NotSame(firstRuntime, runtime);
        }
    }

    [Fact]
    public async Task Runtime_created_before_query_admits_tracked_entities_automatically()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 50m;
        runtime.Materialize(link);

        Assert.Equal(5m, link.Ratio);
        Assert.Equal(5m, link.NormalizedRatio);
    }

    [Fact]
    public async Task Entities_tracked_before_runtime_resolution_are_admitted_when_runtime_binds()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();

        link.Left.Value = 55m;
        runtime.Materialize(link);

        Assert.Equal(5.5m, link.Ratio);
        Assert.Equal(5.5m, link.NormalizedRatio);
    }

    [Fact]
    public async Task Intervening_change_rebuilds_the_pending_plan()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 55m;
        runtime.Materialize(link);
        Assert.Equal(5.5m, link.Ratio);
        link.Left.Value = 50m;

        await context.SaveChangesAsync();

        Assert.Equal(5m, link.Ratio);
        Assert.Equal(5m, link.NormalizedRatio);
        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking()
            .Include(value => value.Left).Include(value => value.Right).SingleAsync();
        Assert.Equal(50m, persisted.Left.Value);
        Assert.Equal(5m, persisted.Ratio);
        Assert.Equal(5m, persisted.NormalizedRatio);
    }

    [Fact]
    public async Task Save_without_explicit_materialize_still_persists_mirrors()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 55m;
        await context.SaveChangesAsync();

        await using var verification = fixture.CreateContext();
        var persisted = await verification.Links.AsNoTracking()
            .Include(value => value.Left).Include(value => value.Right).SingleAsync();
        Assert.Equal(55m, persisted.Left.Value);
        Assert.Equal(5.5m, persisted.Ratio);
        Assert.Equal(5.5m, persisted.NormalizedRatio);
    }

    [Fact]
    public async Task Materialize_does_not_advance_runtime_before_save_and_failure_is_retryable()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();
        var version = runtime.Version;

        link.Left.Value = -1m;
        runtime.Materialize(link);
        Assert.Equal(version, runtime.Version);
        Assert.Equal(-0.1m, link.Ratio);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(version, runtime.Version);

        link.Left.Value = 55m;
        await context.SaveChangesAsync();
        Assert.True(runtime.Version > version);
        Assert.Equal(5.5m, link.Ratio);
    }

    [Fact]
    public async Task Repeated_materialize_reuses_the_pending_plan_without_recomputing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<LinkContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var link = await context.Links.Include(value => value.Left).Include(value => value.Right).SingleAsync();

        link.Left.Value = 55m;
        runtime.Materialize(link);
        runtime.Materialize(link);
        runtime.Materialize(link);
        await context.SaveChangesAsync();

        Assert.Equal(1, fixture.EvaluationCounter.RatioEvaluations);
    }

    [Fact]
    public async Task Baseline_admission_preserves_exact_object_set_identity_for_same_clr_type()
    {
        var modelBuilder = new ConsistencyModelBuilder();
        var firstSet = modelBuilder.Objects<DualItem>().Named("first").Key(value => value.Id);
        var secondSet = modelBuilder.Objects<DualItem>().Named("second").Key(value => value.Id);
        var model = modelBuilder.Build();
        var databaseName = $"dual-{Guid.NewGuid()}";
        await using (var seed = new DualContext(
                         new DbContextOptionsBuilder<DualContext>().UseInMemoryDatabase(databaseName).Options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AddRange(new DualItem { Id = 1, Kind = 1 }, new DualItem { Id = 2, Kind = 2 });
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<DualContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddRaffinertConsistency<DualContext>(model,
            new ConsistencyEfCoreMappings()
                .Map(firstSet, entry => entry.Entity.Kind == 1)
                .Map(secondSet, entry => entry.Entity.Kind == 2));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var context = scope.ServiceProvider.GetRequiredService<DualContext>();
        var values = await context.Items.OrderBy(value => value.Id).ToArrayAsync();
        var session = scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<DualContext>>();

        Assert.Same(runtime, session.Runtime);
        Assert.True(runtime.IsRegistered(firstSet.Definition, values[0]));
        Assert.False(runtime.IsRegistered(firstSet.Definition, values[1]));
        Assert.False(runtime.IsRegistered(secondSet.Definition, values[0]));
        Assert.True(runtime.IsRegistered(secondSet.Definition, values[1]));
        Assert.Throws<InvalidOperationException>(() =>
            runtime.AdmitBaseline(firstSet.Definition, new DualItem { Id = 1, Kind = 1 }));
    }

    private sealed class LinkService(LinkContext db, ConsistencyRuntime consistency)
    {
        public decimal? ObservedRatio { get; private set; }
        public decimal? ObservedNormalizedRatio { get; private set; }

        public async Task ChangeLeftValueAsync(
            long linkId,
            decimal newValue,
            CancellationToken cancellationToken)
        {
            var link = await db.Links
                .Include(value => value.Left)
                .Include(value => value.Right)
                .SingleAsync(value => value.Id == linkId, cancellationToken);
            link.Left.Value = newValue;
            consistency.Materialize(link);
            ObservedRatio = link.Ratio;
            ObservedNormalizedRatio = link.NormalizedRatio;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            ServiceProvider provider,
            SqliteConnection connection,
            long linkId,
            CompiledConsistencyModel model,
            ConsistencyEfCoreMappings mappings,
            EvaluationCounter evaluationCounter)
        {
            Provider = provider;
            Connection = connection;
            LinkId = linkId;
            Model = model;
            Mappings = mappings;
            EvaluationCounter = evaluationCounter;
        }

        public ServiceProvider Provider { get; }
        public SqliteConnection Connection { get; }
        public long LinkId { get; }
        public CompiledConsistencyModel Model { get; }
        public ConsistencyEfCoreMappings Mappings { get; }
        public EvaluationCounter EvaluationCounter { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var modelBuilder = new ConsistencyModelBuilder();
            var evaluationCounter = new EvaluationCounter();
            var items = modelBuilder.Objects<Item>().Named("items").Key(value => value.Id);
            var links = modelBuilder.Objects<Link>().Named("links").Key(value => value.Id);
            var ratio = modelBuilder.Derived(links)
                .DependsOn(value => value.Left.Value, value => value.Right.Value)
                .Select((Func<Link, decimal?>)(value =>
                {
                    evaluationCounter.RatioEvaluations++;
                    return value.Right.Value == 0m
                        ? null
                        : value.Left.Value / value.Right.Value;
                }))
                .MaterializeTo(value => value.Ratio)
                .Named("ratio");
            var normalizedRatio = modelBuilder.Derived(links).From(ratio)
                .Select((_, value) => value == null
                    ? (decimal?)null
                    : value == 0m ? 0m : value >= 1m ? value : 1m / value)
                .MaterializeTo(value => value.NormalizedRatio)
                .Named("normalized-ratio");
            var model = modelBuilder.Build();
            var mappings = new ConsistencyEfCoreMappings().Map(items).Map(links);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using (var seed = new LinkContext(
                             new DbContextOptionsBuilder<LinkContext>().UseSqlite(connection).Options))
            {
                await seed.Database.EnsureCreatedAsync();
                var left = new Item { Id = 1, Value = 60m };
                var right = new Item { Id = 2, Value = 10m };
                seed.AddRange(left, right, new Link
                {
                    Id = 1,
                    Left = left,
                    Right = right,
                    Ratio = 6m,
                    NormalizedRatio = 6m
                });
                await seed.SaveChangesAsync();
            }

            var services = new ServiceCollection();
            services.AddDbContext<LinkContext>(options => options.UseSqlite(connection));
            services.AddRaffinertConsistency<LinkContext>(model, mappings,
                new ConsistencySaveOptions
                {
                    Scope = new ConsistencyScope().Complete(items).Complete(links)
                });
            services.AddScoped<LinkService>();
            return new Fixture(services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            }), connection, 1, model, mappings, evaluationCounter);
        }

        public LinkContext CreateContext() => new(
            new DbContextOptionsBuilder<LinkContext>().UseSqlite(Connection).Options);

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class LinkContext(DbContextOptions<LinkContext> options) : DbContext(options)
    {
        public DbSet<Item> Items => Set<Item>();
        public DbSet<Link> Links => Set<Link>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Item>();
            modelBuilder.Entity<Link>(entity =>
            {
                entity.HasOne(value => value.Left).WithMany().HasForeignKey(value => value.LeftId);
                entity.HasOne(value => value.Right).WithMany().HasForeignKey(value => value.RightId);
                entity.ToTable(table => table.HasCheckConstraint(
                    "CK_Link_Ratio_NonNegative", "Ratio >= 0 OR Ratio IS NULL"));
            });
        }
    }

    private sealed class DualContext(DbContextOptions<DualContext> options) : DbContext(options)
    {
        public DbSet<DualItem> Items => Set<DualItem>();
    }

    private sealed class Item
    {
        public long Id { get; set; }
        public decimal Value { get; set; }
    }

    public sealed class EvaluationCounter
    {
        public int RatioEvaluations { get; set; }
    }

    private sealed class Link
    {
        public long Id { get; set; }
        public long LeftId { get; set; }
        public Item Left { get; set; } = null!;
        public long RightId { get; set; }
        public Item Right { get; set; } = null!;
        public decimal? Ratio { get; set; }
        public decimal? NormalizedRatio { get; set; }
    }

    private sealed class DualItem
    {
        public long Id { get; set; }
        public int Kind { get; set; }
    }
}
