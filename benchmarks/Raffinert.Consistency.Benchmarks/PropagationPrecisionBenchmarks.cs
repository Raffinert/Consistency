using BenchmarkDotNet.Attributes;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser]
public class PropagationPrecisionBenchmarks
{
    private RelationRuntime _runtime = null!;
    private ObjectSet<PrecisionItem> _items = null!;
    private PrecisionItem[] _changedItems = null!;

    [Params(10_000, 100_000)]
    public int Size { get; set; }

    [Params(1, 10, 100)]
    public int ChangeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var model = new RelationModelBuilder();
        var sources = model.Objects<PrecisionSource>().Key(source => source.Id);
        _items = model.Objects<PrecisionItem>().Key(item => item.Id);
        var relation = model.Relation(sources, _items).Where((source, item) =>
            item.Order != null &&
            item.Order.Supplier != null &&
            item.Order.Supplier.Country != null &&
            source.Code == item.Order.Supplier.Country.Code &&
            item.Enabled);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Count);
        model.Derived(sources).Using(relation).Compute((_, matches) => matches.Sum(item => item.Quantity));
        _runtime = model.Build().CreateRuntime();
        var retainedSources = Enumerable.Range(0, Size)
            .Select(index => new PrecisionSource { Id = Guid.NewGuid(), Code = $"C-{index % 100}" })
            .ToArray();
        _changedItems = Enumerable.Range(0, 100)
            .Select(index => new PrecisionItem
            {
                Id = Guid.NewGuid(),
                Enabled = true,
                Quantity = index + 1,
                Order = CreateOrder("C-0")
            })
            .ToArray();
        _runtime.Apply(MutationSet.Create(retainedSources.Select(source => Change.Add(sources, source))
            .Concat(_changedItems.Select(item => Change.Add(_items, item))).ToArray()));
    }

    [Benchmark]
    public RuntimeDiagnostics DeepNestedRightMutationBatch()
    {
        _runtime.ResetDiagnostics();
        var changes = new PropertyChange[ChangeCount];
        for (var index = 0; index < ChangeCount; index++)
        {
            var country = _changedItems[index].Order!.Supplier!.Country!;
            var oldCode = country.Code;
            country.Code = oldCode == "C-0" ? "C-1" : "C-0";
            changes[index] = Change.Property(country, value => value.Code, oldCode, country.Code);
        }
        _runtime.Apply(ChangeSet.Create(changes), ChangeValidationMode.StrictNewValue);
        return _runtime.Diagnostics;
    }

    private static PrecisionOrder CreateOrder(string code) => new()
    {
        Supplier = new PrecisionSupplier { Country = new PrecisionCountry { Code = code } }
    };

    private sealed class PrecisionSource
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
    }

    private sealed class PrecisionItem
    {
        public Guid Id { get; init; }
        public bool Enabled { get; init; }
        public decimal Quantity { get; init; }
        public PrecisionOrder? Order { get; init; }
    }

    private sealed class PrecisionOrder
    {
        public PrecisionSupplier? Supplier { get; init; }
    }

    private sealed class PrecisionSupplier
    {
        public PrecisionCountry? Country { get; init; }
    }

    private sealed class PrecisionCountry
    {
        public string Code { get; set; } = "";
    }
}
