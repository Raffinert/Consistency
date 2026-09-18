using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept;

internal sealed class ExplicitEvaluateMaterializeRuntime
{
    private readonly ConsistencyRuntime _runtime;
    private readonly Dictionary<object, IObjectMaterializationTarget> _targetsByDerived =
        new(ReferenceEqualityComparer.Instance);
    private int _targetedTargetsVisited;

    public ExplicitEvaluateMaterializeRuntime(ConsistencyRuntime runtime)
    {
        _runtime = runtime;
        Objects = new ObjectMaterializeAdapter(this);
    }

    public ConsistencyRuntime InnerRuntime => _runtime;
    internal ObjectMaterializeAdapter Objects { get; }
    public ObjectMaterializationDiagnostics MaterializationDiagnostics => new(
        Objects.ObjectLookups,
        Objects.ObjectTargetsVisited,
        _targetedTargetsVisited);

    public void RegisterSet<TSource>(ObjectSet<TSource> set) where TSource : class => Objects.RegisterSet(set);

    public void RegisterSource<TSource>(ObjectSet<TSource> set, TSource source) where TSource : class =>
        Objects.RegisterSource(set, source);

    public void Register<TSource, TValue>(
        ObjectSet<TSource> set,
        MaterializedValue<TSource, TValue> target,
        int topologicalOrder)
        where TSource : class =>
        _targetsByDerived.Add(target.Derived, Objects.RegisterTarget(set, target, topologicalOrder));

    /// <summary>
    /// Returns the current logical value of the derived definition, evaluating it only when its
    /// cached value is not Fresh.
    /// </summary>
    public TValue Evaluate<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class => _runtime.Get(derived, source);

    public TValue Materialize<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        if (!_targetsByDerived.TryGetValue(derived, out var target))
            throw new InvalidOperationException(
                "This derived definition has no registered materialization target. Use Evaluate instead.");
        _targetedTargetsVisited++;
        var prepared = target.Prepare(this, source);
        MaterializationTransaction.Apply([prepared]);
        return (TValue)prepared.Value!;
    }

    public void Materialize<TSource>(TSource source) where TSource : class => Objects.Materialize(source);

    public void ResetMaterializationDiagnostics()
    {
        _targetedTargetsVisited = 0;
        Objects.ResetDiagnostics();
    }
}

internal sealed class ModelDFixture
{
    private ModelDFixture()
    {
    }

    public required Fixture Base { get; init; }
    public required ExplicitEvaluateMaterializeRuntime Runtime { get; init; }
    public ConceptModel Model => Base.Model;
    public PurchaseOrderInvoiceLine Link => Base.Link;
    public OrderLine Line => Base.Line;
    public Fulfillment Fulfillment => Base.Fulfillment;
    public Allocation Allocation => Base.Allocation;

    public static ModelDFixture Create()
    {
        var fixture = Fixture.Create(new ModelARuntimeAuthoritative());
        var runtime = new ExplicitEvaluateMaterializeRuntime(fixture.Storage.Runtime);
        runtime.RegisterSet(fixture.Model.Fulfillments);
        runtime.RegisterSource(fixture.Model.Links, fixture.Link);
        runtime.RegisterSource(fixture.Model.Lines, fixture.Line);
        runtime.RegisterSource(fixture.Model.Fulfillments, fixture.Fulfillment);
        runtime.RegisterSource(fixture.Model.Allocations, fixture.Allocation);
        runtime.Register(fixture.Model.Links, fixture.Model.PriceRateTarget, 0);
        runtime.Register(fixture.Model.Links, fixture.Model.UnitRateTarget, 1);
        runtime.Register(fixture.Model.Links, fixture.Model.AlternateUnitRateTarget, 2);
        runtime.Register(fixture.Model.Links, fixture.Model.NullablePriceRateTarget, 3);
        runtime.Register(fixture.Model.Links, fixture.Model.RateTokenTarget, 4);
        runtime.Register(fixture.Model.Lines, fixture.Model.FulfilledTarget, 0);
        runtime.Register(fixture.Model.Lines, fixture.Model.RemainingTarget, 1);
        runtime.Register(fixture.Model.Allocations, fixture.Model.AllocationValidityTarget, 0);
        return new ModelDFixture { Base = fixture, Runtime = runtime };
    }

    public void Prime()
    {
        _ = Runtime.Materialize(Model.PriceRate, Link);
        _ = Runtime.Materialize(Model.UnitRate, Link);
        _ = Runtime.Materialize(Model.UnitFromRuntimePrice, Link);
        _ = Runtime.Materialize(Model.NullablePriceRate, Link);
        _ = Runtime.Materialize(Model.RateToken, Link);
        _ = Runtime.Materialize(Model.FulfilledQuantity, Line);
        _ = Runtime.Materialize(Model.RemainingQuantity, Line);
        _ = Runtime.Materialize(Model.AllocationValidity, Allocation);
        _ = Runtime.Evaluate(Model.RiskScore, Link);
        _ = Runtime.InnerRuntime.Evaluate(Model.LinkInvariant, Link);
    }

    public void ChangeInvoicePrice(decimal value) => Base.ChangeInvoicePrice(value);
}

internal static class ModelDAssertions
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Model D: {message}");
    }
}
