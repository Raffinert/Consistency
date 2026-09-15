using System.Reflection;
using BenchmarkDotNet.Attributes;
using Raffinert.Consistency.Expressions;

namespace Raffinert.Consistency.Benchmarks;

[MemoryDiagnoser]
public class MemberPathBenchmarks
{
    private readonly BenchItem _item = new()
    {
        Order = new BenchOrder
        {
            Supplier = new BenchSupplier { Country = new BenchCountry { Code = "PO-100" } }
        }
    };
    private readonly PropertyInfo _order = typeof(BenchItem).GetProperty(nameof(BenchItem.Order))!;
    private readonly PropertyInfo _supplier = typeof(BenchOrder).GetProperty(nameof(BenchOrder.Supplier))!;
    private readonly PropertyInfo _country = typeof(BenchSupplier).GetProperty(nameof(BenchSupplier.Country))!;
    private readonly PropertyInfo _code = typeof(BenchCountry).GetProperty(nameof(BenchCountry.Code))!;
    private readonly MemberPath _path;

    public MemberPathBenchmarks() =>
        _path = new MemberPath(typeof(BenchItem), [_order, _supplier, _country, _code]);

    [Benchmark(Baseline = true)]
    public object? Reflection()
    {
        var order = _order.GetValue(_item);
        var supplier = order is null ? null : _supplier.GetValue(order);
        var country = supplier is null ? null : _country.GetValue(supplier);
        return country is null ? null : _code.GetValue(country);
    }

    [Benchmark]
    public object? CompiledNullSafePath() => _path.Read(_item);
}
