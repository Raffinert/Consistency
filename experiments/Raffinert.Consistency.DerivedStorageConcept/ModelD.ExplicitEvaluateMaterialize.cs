using Raffinert.Consistency;

namespace Raffinert.Consistency.DerivedStorageConcept;

internal sealed class ExplicitEvaluateMaterializeRuntime
{
    private readonly ConsistencyRuntime _runtime;
    private readonly Dictionary<object, object> _targetsByDerived = new(ReferenceEqualityComparer.Instance);

    public ExplicitEvaluateMaterializeRuntime(ConsistencyRuntime runtime) => _runtime = runtime;

    public ConsistencyRuntime InnerRuntime => _runtime;

    public void Register<TSource, TValue>(MaterializedValue<TSource, TValue> target)
        where TSource : class => _targetsByDerived.Add(target.Derived, target);

    /// <summary>
    /// Returns the current logical value of the derived definition, evaluating it only when its
    /// cached value is not Fresh.
    /// </summary>
    public TValue Evaluate<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class => _runtime.Get(derived, source);

    public TValue Materialize<TSource, TValue>(Derived<TSource, TValue> derived, TSource source)
        where TSource : class
    {
        if (!_targetsByDerived.TryGetValue(derived, out var candidate) ||
            candidate is not MaterializedValue<TSource, TValue> target)
            throw new InvalidOperationException(
                "This derived definition has no registered materialization target. Use Evaluate instead.");

        var value = Evaluate(derived, source);
        var previous = target.Read(source);
        if (EqualityComparer<TValue>.Default.Equals(previous, value))
            return value;

        try
        {
            target.Write(source, value);
            var stored = target.Read(source);
            if (!EqualityComparer<TValue>.Default.Equals(stored, value))
                throw new InvalidOperationException(
                    $"Materialization target '{target.Name}' did not retain the evaluated value.");
        }
        catch (Exception materializationError)
        {
            try
            {
                if (!EqualityComparer<TValue>.Default.Equals(target.Read(source), previous))
                    target.Write(source, previous);
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    $"Materialization target '{target.Name}' failed and its previous value could not be restored.",
                    materializationError,
                    rollbackError);
            }

            throw;
        }
        return value;
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
        runtime.Register(fixture.Model.PriceRateTarget);
        runtime.Register(fixture.Model.UnitRateTarget);
        runtime.Register(fixture.Model.AlternateUnitRateTarget);
        runtime.Register(fixture.Model.NullablePriceRateTarget);
        runtime.Register(fixture.Model.RateTokenTarget);
        runtime.Register(fixture.Model.FulfilledTarget);
        runtime.Register(fixture.Model.RemainingTarget);
        runtime.Register(fixture.Model.AllocationValidityTarget);
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
