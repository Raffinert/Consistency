using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Tests;

public sealed class MutableFingerprintIntegrationTests
{
    [Fact]
    public async Task Rejected_preview_becomes_stale_after_in_place_mutable_value_change()
    {
        await using var fixture = await Fixture.CreateAsync(enforceInvariant: true);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MutableContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var session = scope.ServiceProvider.GetRequiredService<ConsistencyEfCoreSession<MutableContext>>();
        var record = await context.Records.SingleAsync();
        record.Token = new MutableToken(3);
        var current = record.Token;
        var version = runtime.Version;
        var baselineRevision = runtime.BaselineRevision;
        var error = await Assert.ThrowsAsync<ConsistencyInvariantViolationException>(
            () => context.SaveChangesAsync());
        using var preview = session.CreateRejectedPreview(error);
        Assert.Equal(3, preview.Evaluate(fixture.TokenValue, record));

        current.Value = 4;

        Assert.Same(current, record.Token);
        var stale = Assert.Throws<InvalidOperationException>(() =>
            preview.Evaluate(fixture.TokenValue, record));
        Assert.Contains("relevant tracked state changed", stale.Message);
        Assert.Equal(version, runtime.Version);
        Assert.Equal(baselineRevision, runtime.BaselineRevision);
    }

    [Fact]
    public async Task In_place_mutable_value_change_invalidates_pending_materialization_plan()
    {
        await using var fixture = await Fixture.CreateAsync(enforceInvariant: false);
        await using var scope = fixture.Provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MutableContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<ConsistencyRuntime>();
        var record = await context.Records.SingleAsync();
        record.Token = new MutableToken(1);
        var current = record.Token;
        var version = runtime.Version;

        runtime.Materialize(record);
        Assert.Equal(1, record.Mirror);
        Assert.Equal(1, fixture.Evaluations.Count);

        current.Value = 2;
        runtime.Materialize(record);

        Assert.Same(current, record.Token);
        Assert.Equal(2, record.Mirror);
        Assert.Equal(2, fixture.Evaluations.Count);
        Assert.Equal(version, runtime.Version);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            ServiceProvider provider,
            SqliteConnection connection,
            Derived<MutableRecord, int> tokenValue,
            EvaluationCounter evaluations)
        {
            Provider = provider;
            Connection = connection;
            TokenValue = tokenValue;
            Evaluations = evaluations;
        }

        internal ServiceProvider Provider { get; }
        private SqliteConnection Connection { get; }
        internal Derived<MutableRecord, int> TokenValue { get; }
        internal EvaluationCounter Evaluations { get; }

        internal static async Task<Fixture> CreateAsync(bool enforceInvariant)
        {
            var builder = new ConsistencyModelBuilder();
            var records = builder.Objects<MutableRecord>().Key(value => value.Id);
            var evaluations = new EvaluationCounter();
            var tokenValue = builder.Derived(records)
                .DependsOn(value => value.Token)
                .Select((Func<MutableRecord, int>)(value =>
                {
                    evaluations.Count++;
                    return value.Token.Value;
                }))
                .MaterializeTo(value => value.Mirror)
                .Named("token-value");
            var invariant = builder.Invariant(records).From(tokenValue)
                .Must((_, value) => value < 3)
                .Named("token-valid");
            var model = builder.Build();
            var mappings = new ConsistencyEfCoreMappings().Map(records);
            if (enforceInvariant)
                mappings.Enforce(invariant);
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using (var seed = new MutableContext(
                             new DbContextOptionsBuilder<MutableContext>().UseSqlite(connection).Options))
            {
                await seed.Database.EnsureCreatedAsync();
                seed.Add(new MutableRecord { Id = 1, Token = new MutableToken(0), Mirror = 0 });
                await seed.SaveChangesAsync();
            }
            var services = new ServiceCollection();
            services.AddDbContext<MutableContext>(options => options.UseSqlite(connection));
            services.AddRaffinertConsistency<MutableContext>(
                model,
                mappings,
                new ConsistencySaveOptions { Scope = new ConsistencyScope().Complete(records) });
            return new Fixture(
                services.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                }),
                connection,
                tokenValue,
                evaluations);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class MutableContext(DbContextOptions<MutableContext> options) : DbContext(options)
    {
        internal DbSet<MutableRecord> Records => Set<MutableRecord>();

        protected override void OnModelCreating(ModelBuilder model)
        {
            var comparer = new ValueComparer<MutableToken>(
                (left, right) => left!.Value == right!.Value,
                value => value.Value,
                value => new MutableToken(value.Value));
            model.Entity<MutableRecord>().Property(value => value.Token)
                .HasConversion(value => value.Value, value => new MutableToken(value))
                .Metadata.SetValueComparer(comparer);
        }
    }

    private sealed class MutableRecord
    {
        public int Id { get; set; }
        public MutableToken Token { get; set; } = new(0);
        public int Mirror { get; set; }
    }

    private sealed class MutableToken(int value)
    {
        internal int Value { get; set; } = value;
    }

    private sealed class EvaluationCounter
    {
        internal int Count { get; set; }
    }
}
