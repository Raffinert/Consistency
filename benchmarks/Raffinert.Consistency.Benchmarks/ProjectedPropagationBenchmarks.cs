using BenchmarkDotNet.Attributes;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser]
public class ProjectedPropagationBenchmarks
{
    private ConsistencyRuntime _runtime = null!;
    private ObjectSet<Owner> _owners = null!;
    private Owner _changed = null!;
    private int _value;

    [Params(10_000, 100_000)]
    public int DownstreamCount { get; set; }

    [Params(1, 10, 100)]
    public int FanOut { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var model = new ConsistencyModelBuilder();
        _owners = model.Objects<Owner>().Key(value => value.Id);
        var links = model.Objects<Link>().Key(value => value.Id);
        var amount = model.Derived(_owners).Select(value => value.Amount);
        _ = model.Derived(links).From(value => value.Owner, amount)
            .Select((_, current) => current);
        _changed = new Owner(0);
        var other = new Owner(1);
        var downstream = Enumerable.Range(0, DownstreamCount)
            .Select(index => new Link(index, index < FanOut ? _changed : other)).ToArray();
        _runtime = model.Build().CreateRuntime(seed =>
        {
            seed.Add(_owners, [_changed, other]);
            seed.Add(links, downstream);
        });
    }

    [Benchmark]
    public ChangeImpact PropagateToIndexedFanOut()
    {
        var oldValue = _value;
        _changed.Amount = ++_value;
        return _runtime.Apply(Change.Property(
            _owners, _changed, value => value.Amount, oldValue, _value));
    }

    public sealed record Owner(int Id)
    {
        public int Amount { get; set; }
    }

    public sealed record Link(int Id, Owner Owner);
}
