using BenchmarkDotNet.Attributes;

namespace Raffinert.Relations.Benchmarks;

[MemoryDiagnoser]
public class RelationBenchmarks
{
    private RelationRuntime _singleRuntime = null!;
    private ObjectSetBuilder<BenchItem> _singleRight = null!;
    private Relation<BenchItem, BenchItem> _singleRelation = null!;
    private BenchItem _singleSource = null!;
    private BenchItem _scalarTarget = null!;

    private RelationRuntime _compositeRuntime = null!;
    private Relation<BenchItem, BenchItem> _compositeRelation = null!;
    private BenchItem _compositeSource = null!;

    private RelationRuntime _navigationRuntime = null!;
    private Relation<BenchItem, BenchItem> _navigationRelation = null!;
    private BenchItem _navigationSource = null!;
    private ObjectSetBuilder<BenchItem> _navigationRight = null!;
    private BenchItem _navigationTarget = null!;
    private BenchOrder _firstOrder = null!;
    private BenchOrder _secondOrder = null!;

    [Params(10_000, 100_000)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        SetupSingleKey();
        SetupCompositeKey();
        SetupNavigation();
    }

    [Benchmark(Baseline = true)]
    public IReadOnlyList<BenchItem> SingleKeyLookup() =>
        _singleRuntime.Related(_singleRelation, _singleSource);

    [Benchmark]
    public IReadOnlyList<BenchItem> CompositeKeyLookup() =>
        _compositeRuntime.Related(_compositeRelation, _compositeSource);

    [Benchmark]
    public IReadOnlyList<BenchItem> ThreeLevelNavigationLookup() =>
        _navigationRuntime.Related(_navigationRelation, _navigationSource);

    [Benchmark]
    public ChangeImpact ScalarPropertyChange()
    {
        var oldCode = _scalarTarget.Code;
        _scalarTarget.Code = oldCode == "0" ? "1" : "0";
        return _singleRuntime.Apply(Change.Property(
            _singleRight,
            _scalarTarget,
            item => item.Code,
            oldCode,
            _scalarTarget.Code));
    }

    [Benchmark]
    public ChangeImpact ReferenceNavigationChange()
    {
        var oldOrder = _navigationTarget.Order!;
        var newOrder = ReferenceEquals(oldOrder, _firstOrder) ? _secondOrder : _firstOrder;
        _navigationTarget.Order = newOrder;
        return _navigationRuntime.Apply(Change.Property(
            _navigationRight,
            _navigationTarget,
            item => item.Order,
            oldOrder,
            newOrder));
    }

    [Benchmark]
    public void AddAndRemove()
    {
        var item = new BenchItem { Id = Guid.NewGuid(), Code = "0", ItemNumber = 0 };
        _singleRuntime.Add(_singleRight, item);
        _singleRuntime.Remove(_singleRight, item);
    }

    private void SetupSingleKey()
    {
        var model = new RelationModelBuilder();
        var left = model.Objects<BenchItem>().Key(item => item.Id);
        _singleRight = model.Objects<BenchItem>().Key(item => item.Id);
        _singleRelation = model.Relation(left, _singleRight).Where((a, b) => a.Code == b.Code);
        _singleRuntime = model.Build().CreateRuntime();
        _singleSource = new BenchItem { Id = Guid.NewGuid(), Code = "0" };
        _singleRuntime.Add(left, _singleSource);
        for (var index = 0; index < Size; index++)
        {
            var item = new BenchItem { Id = Guid.NewGuid(), Code = (index % 100).ToString(), ItemNumber = index };
            _singleRuntime.Add(_singleRight, item);
            if (index == 0)
                _scalarTarget = item;
        }
    }

    private void SetupCompositeKey()
    {
        var model = new RelationModelBuilder();
        var left = model.Objects<BenchItem>().Key(item => item.Id);
        var right = model.Objects<BenchItem>().Key(item => item.Id);
        _compositeRelation = model.Relation(left, right).Where((a, b) =>
            a.Code == b.Code && a.ItemNumber == b.ItemNumber);
        _compositeRuntime = model.Build().CreateRuntime();
        _compositeSource = new BenchItem { Id = Guid.NewGuid(), Code = "0", ItemNumber = 0 };
        _compositeRuntime.Add(left, _compositeSource);
        for (var index = 0; index < Size; index++)
            _compositeRuntime.Add(right, new BenchItem
            {
                Id = Guid.NewGuid(),
                Code = (index % 100).ToString(),
                ItemNumber = index
            });
    }

    private void SetupNavigation()
    {
        var model = new RelationModelBuilder();
        var left = model.Objects<BenchItem>().Key(item => item.Id);
        _navigationRight = model.Objects<BenchItem>().Key(item => item.Id);
        _navigationRelation = model.Relation(left, _navigationRight).Where((a, b) =>
            a.Code == b.Order!.Supplier!.Country!.Code);
        _navigationRuntime = model.Build().CreateRuntime();
        _navigationSource = new BenchItem { Id = Guid.NewGuid(), Code = "0" };
        _navigationRuntime.Add(left, _navigationSource);
        _firstOrder = CreateOrder("0");
        _secondOrder = CreateOrder("1");
        for (var index = 0; index < Size; index++)
        {
            var item = new BenchItem
            {
                Id = Guid.NewGuid(),
                Order = index % 100 == 0 ? _firstOrder : CreateOrder((index % 100).ToString())
            };
            _navigationRuntime.Add(_navigationRight, item);
            if (index == 0)
                _navigationTarget = item;
        }
    }

    private static BenchOrder CreateOrder(string code) => new()
    {
        Supplier = new BenchSupplier { Country = new BenchCountry { Code = code } }
    };
}

public sealed class BenchItem
{
    public Guid Id { get; init; }
    public string Code { get; set; } = "";
    public int ItemNumber { get; init; }
    public BenchOrder? Order { get; set; }
}

public sealed class BenchOrder
{
    public BenchSupplier? Supplier { get; init; }
}

public sealed class BenchSupplier
{
    public BenchCountry? Country { get; init; }
}

public sealed class BenchCountry
{
    public string Code { get; init; } = "";
}
