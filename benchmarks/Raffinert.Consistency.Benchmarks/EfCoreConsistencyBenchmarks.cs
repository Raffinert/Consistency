using BenchmarkDotNet.Attributes;
using Microsoft.EntityFrameworkCore;
using Raffinert.Consistency.EntityFrameworkCore;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser, InvocationCount(1)]
public class EfCoreConsistencyBenchmarks
{
    [Params(10_000, 100_000)]
    public int Population { get; set; }

    [Params(RuntimeImpactDetailLevel.Summary, RuntimeImpactDetailLevel.Causal)]
    public RuntimeImpactDetailLevel Detail { get; set; }

    private Scenario _capture = null!;
    private Scenario _planning = null!;
    private Scenario _materialization = null!;

    [IterationSetup]
    public void Setup()
    {
        _capture = Scenario.Create(Population);
        _planning = Scenario.Create(Population);
        _materialization = Scenario.Create(Population);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _capture.Dispose();
        _planning.Dispose();
        _materialization.Dispose();
    }

    [Benchmark]
    public ConsistencyUnitOfWork CaptureTrackedUnit() =>
        ChangeTrackerAdapter.CaptureUnitOfWork(_capture.Context.ChangeTracker, _capture.Mappings);

    [Benchmark]
    public PreparedImpactPlan PlanAffectedEvaluations()
    => _planning.Prepared.PlanDetailed(_planning.Runtime, Detail, PlannedInvariantEvaluationMode.Affected,
            PlannedDerivedEvaluationMode.Affected)!;

    [Benchmark]
    public void WriteMaterializedValue()
    {
        var property = _materialization.Context.Entry(_materialization.Touched).Property(x => x.Mirror);
        if (!Equals(property.CurrentValue, 2)) property.CurrentValue = 2;
        _materialization.Context.ChangeTracker.DetectChanges();
    }

    private sealed class Scenario : IDisposable
    {
        private Scenario(BenchmarkContext context, ConsistencyRuntime runtime,
            ConsistencyUnitOfWorkMappings mappings, Source touched, ConsistencyUnitOfWork prepared)
        {
            Context = context; Runtime = runtime; Mappings = mappings; Touched = touched; Prepared = prepared;
        }

        public BenchmarkContext Context { get; }
        public ConsistencyRuntime Runtime { get; }
        public ConsistencyUnitOfWorkMappings Mappings { get; }
        public Source Touched { get; }
        public ConsistencyUnitOfWork Prepared { get; }

        public static Scenario Create(int population)
        {
            var model = new ConsistencyModelBuilder();
            var sources = model.Objects<Source>().Key(x => x.Id);
            var doubled = model.Derived(sources).Select(x => x.Value * 2);
            model.Invariant(sources).From(doubled).Must((_, value) => value >= 0);
            var values = Enumerable.Range(0, population).Select(i => new Source { Value = i, Mirror = i * 2 }).ToArray();
            var runtime = model.Build().CreateRuntime(seed => seed.Add(sources, values));
            var options = new DbContextOptionsBuilder<BenchmarkContext>()
                .UseInMemoryDatabase($"ef-benchmark-{Guid.NewGuid()}").Options;
            var context = new BenchmarkContext(options);
            context.AttachRange(values);
            var touched = values[0];
            touched.Value = 1;
            var mappings = new ConsistencyUnitOfWorkMappings().Map(sources);
            var prepared = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
            prepared.Prepare(runtime);
            return new Scenario(context, runtime, mappings, touched, prepared);
        }

        public void Dispose() => Context.Dispose();
    }

    private sealed class BenchmarkContext(DbContextOptions<BenchmarkContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<Source>();
    }

    private sealed class Source
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int Value { get; set; }
        public int Mirror { get; set; }
    }
}
