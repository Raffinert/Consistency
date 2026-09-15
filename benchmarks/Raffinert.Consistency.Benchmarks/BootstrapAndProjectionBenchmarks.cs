using BenchmarkDotNet.Attributes;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser]
public class BootstrapAndProjectionBenchmarks
{
    private CompiledConsistencyModel _compiled = null!;
    private ObjectSet<Owner> _owners = null!;
    private Owner[] _data = null!;

    [Params(10_000, 100_000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var model = new ConsistencyModelBuilder();
        _owners = model.Objects<Owner>().Key(value => value.Id);
        model.Derived(_owners).Compute(value => value.Amount);
        _compiled = model.Build();
        _data = Enumerable.Range(0, Count).Select(index => new Owner(index)).ToArray();
    }

    [Benchmark]
    public ConsistencyRuntime Bootstrap() => _compiled.CreateRuntime(seed => seed.Add(_owners, _data));

    public sealed record Owner(int Id)
    {
        public int Amount { get; init; } = Id;
    }
}
