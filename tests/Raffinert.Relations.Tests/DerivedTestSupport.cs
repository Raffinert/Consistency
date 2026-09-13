namespace Raffinert.Relations.Tests;

public sealed partial class DerivedStateTests
{
    private static RelationModelBuilder CreateQuantityModel(
        out ObjectSet<DerivedSourceRecord> sources,
        out ObjectSet<DerivedItemRecord> items,
        out Derived<DerivedSourceRecord, decimal> derived,
        System.Linq.Expressions.Expression<Func<DerivedSourceRecord, IReadOnlyList<DerivedItemRecord>, decimal>> computation,
        bool forceScan = false)
    {
        var model = new RelationModelBuilder();
        if (forceScan)
            model.UseScanPlansForTesting();
        sources = model.Objects<DerivedSourceRecord>().Key(x => x.Id);
        items = model.Objects<DerivedItemRecord>().Key(x => x.Id);
        var relation = model.Relation(sources, items).Where((source, item) => source.Code == item.Code);
        derived = model.Derived(sources).Using(relation).Compute(computation);
        return model;
    }

    private static QuantityScenario CreateQuantityScenario(bool forceScan)
    {
        var model = CreateQuantityModel(
            out var sources,
            out var items,
            out var derived,
            (source, matches) => matches.Sum(item => item.Quantity),
            forceScan);
        var runtime = model.Build().CreateRuntime();
        var source = Source("A");
        var item = Item("A", quantity: 2m);
        runtime.Add(sources, source);
        runtime.Add(items, item);
        Assert.Equal(2m, runtime.Get(derived, source));
        return new QuantityScenario(runtime, sources, items, derived, source, item);
    }

    private static DerivedSourceRecord Source(
        string code,
        decimal adjustment = 0m,
        ReceiptPolicy? policy = null) => new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Adjustment = adjustment,
            Policy = policy
        };

    private static DerivedItemRecord Item(
        string code,
        decimal quantity = 0m,
        DerivedItemDetails? details = null) => new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Quantity = quantity,
            Details = details
        };

    private static decimal OpaqueTotal(IReadOnlyList<DerivedItemRecord> items) =>
        items.Sum(item => item.Quantity);

    private sealed record QuantityScenario(
        RelationRuntime Runtime,
        ObjectSet<DerivedSourceRecord> Sources,
        ObjectSet<DerivedItemRecord> Items,
        Derived<DerivedSourceRecord, decimal> Derived,
        DerivedSourceRecord Source,
        DerivedItemRecord Item);

    private sealed record IncrementalScenario(
        RelationRuntime Runtime,
        ObjectSet<DerivedSourceRecord> Sources,
        ObjectSet<DerivedItemRecord> Items,
        Derived<DerivedSourceRecord, decimal> Total,
        DerivedSourceRecord Source);

    private sealed class InvalidMembershipImpactPolicy : IDependencyImpactPolicy
    {
        public DependencyImpactKind Classify(RelationMembershipDependencyImpact impact) =>
            DependencyImpactKind.Invalid;
    }

    private sealed class DeliberateDispatchException : Exception;

    private sealed class RepairCallbackException : Exception
    {
    }

    private static class PredicateProbe
    {
        public static int Evaluations { get; private set; }

        public static bool Observe()
        {
            Evaluations++;
            return true;
        }

        public static void Reset() => Evaluations = 0;
    }
}
