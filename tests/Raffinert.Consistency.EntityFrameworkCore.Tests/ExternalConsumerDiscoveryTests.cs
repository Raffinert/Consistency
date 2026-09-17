using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class ExternalConsumerDiscoveryTests
{
    [Fact]
    public async Task Source_change_discovers_unloaded_consumers_and_materializes_all_rates_async()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var source = known.Source;
        var calls = new Counter();
        var (runtime, mappings, derived) = fixture.CreateModel(known, calls);

        source.UnitValue = 120m;
        await context.SaveChangesConsistentlyAsync(runtime, mappings);

        Assert.Equal(1, calls.Value);
        Assert.Equal(3, context.Associations.Count());
        Assert.Equal(1, runtime.Version);
        foreach (var association in context.Associations.OrderBy(x => x.Id))
        {
            var expected = association.Source.UnitValue / association.Target.UnitValue;
            Assert.Equal(expected, association.UnitRate);
            Assert.Equal(expected, runtime.Get(derived, association));
        }
    }

    [Fact]
    public void Complete_scope_skips_external_resolver()
    {
        using var fixture = DiscoveryFixture.Create();
        using var context = fixture.CreateContext();
        var known = context.Associations.Include(x => x.Source).Include(x => x.Target)
            .Single(x => x.Id == 1);
        var calls = new Counter();
        var (runtime, mappings, _) = fixture.CreateModel(known, calls);

        known.Source.UnitValue = 125m;
        context.SaveChangesConsistently(runtime, mappings,
            new ConsistencySaveOptions { Scope = new ConsistencyScope().Complete(fixture.Associations) });

        Assert.Equal(0, calls.Value);
    }

    private sealed class DiscoveryFixture : IDisposable
    {
        private readonly SqliteConnection _connection;
        internal ObjectSet<DiscoveryAssociation> Associations { get; private set; }

        private DiscoveryFixture(SqliteConnection connection, ObjectSet<DiscoveryAssociation> associations)
        {
            _connection = connection;
            Associations = associations;
        }

        internal static DiscoveryFixture Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            connection.Open();
            var options = new DbContextOptionsBuilder<DiscoveryContext>().UseSqlite(connection).Options;
            using (var context = new DiscoveryContext(options))
            {
                context.Database.EnsureCreated();
                var source = new DiscoverySource { Id = 1, UnitValue = 100m };
                var target1 = new DiscoveryTarget { Id = 1, UnitValue = 10m };
                var target2 = new DiscoveryTarget { Id = 2, UnitValue = 20m };
                var target3 = new DiscoveryTarget { Id = 3, UnitValue = 25m };
                context.AddRange(source, target1, target2, target3,
                    new DiscoveryAssociation { Id = 1, Source = source, Target = target1, UnitRate = 10m },
                    new DiscoveryAssociation { Id = 2, Source = source, Target = target2, UnitRate = 5m },
                    new DiscoveryAssociation { Id = 3, Source = source, Target = target3, UnitRate = 4m });
                context.SaveChanges();
            }
            var model = new ConsistencyModelBuilder();
            var associations = model.Objects<DiscoveryAssociation>().Key(x => x.Id);
            return new DiscoveryFixture(connection, associations);
        }

        internal DiscoveryContext CreateContext() => new(new DbContextOptionsBuilder<DiscoveryContext>()
            .UseSqlite(_connection).Options);

        internal (ConsistencyRuntime Runtime, ConsistencyEfCoreMappings Mappings, Derived<DiscoveryAssociation, decimal> Derived)
            CreateModel(DiscoveryAssociation known, Counter calls)
        {
            var builder = new ConsistencyModelBuilder();
            var associations = builder.Objects<DiscoveryAssociation>().Key(x => x.Id);
            Associations = associations;
            var derived = builder.Derived(associations)
                .DependsOn(x => x.Source.UnitValue).DependsOn(x => x.Target.UnitValue)
                .Compute(x => x.Source.UnitValue / x.Target.UnitValue);
            var runtime = builder.Build().CreateRuntime(seed => seed.Add(associations, [known]));
            var mappings = new ConsistencyEfCoreMappings().Map(associations).Materialize(derived, x => x.UnitRate)
                .DiscoverConsumers(associations, x => x.Source, (db, sources) =>
                {
                    calls.Value++;
                    var ids = sources.Select(x => x.Id).ToArray();
                    return db.Set<DiscoveryAssociation>().Where(x => ids.Contains(x.SourceId))
                        .Include(x => x.Source).Include(x => x.Target);
                })
                .DiscoverConsumers(associations, x => x.Target, (db, targets) =>
                {
                    var ids = targets.Select(x => x.Id).ToArray();
                    return db.Set<DiscoveryAssociation>().Where(x => ids.Contains(x.TargetId))
                        .Include(x => x.Source).Include(x => x.Target);
                });
            return (runtime, mappings, derived);
        }

        public void Dispose() => _connection.Dispose();
    }

    private sealed class DiscoveryContext(DbContextOptions<DiscoveryContext> options) : DbContext(options)
    {
        public DbSet<DiscoveryAssociation> Associations => Set<DiscoveryAssociation>();
        protected override void OnModelCreating(ModelBuilder model)
        {
            model.Entity<DiscoveryAssociation>().HasOne(x => x.Source).WithMany().HasForeignKey(x => x.SourceId);
            model.Entity<DiscoveryAssociation>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId);
        }
    }

    private sealed class DiscoverySource { public int Id { get; set; } public decimal UnitValue { get; set; } }
    private sealed class DiscoveryTarget { public int Id { get; set; } public decimal UnitValue { get; set; } }
    private sealed class DiscoveryAssociation
    {
        public int Id { get; set; }
        public int SourceId { get; set; }
        public DiscoverySource Source { get; set; } = null!;
        public int TargetId { get; set; }
        public DiscoveryTarget Target { get; set; } = null!;
        public decimal UnitRate { get; set; }
    }

    private sealed class Counter { public int Value { get; set; } }
}
