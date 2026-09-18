using Raffinert.Consistency;
using Raffinert.Consistency.DerivedStorageConcept.Scenarios;

namespace Raffinert.Consistency.DerivedStorageConcept;

internal interface IStorageSemantics
{
    string Name { get; }
    bool SynchronizeOnGet { get; }
    bool SynchronizeFreshIncrementalValues { get; }
    bool ReadFreshValueFromProperty { get; }
    string DeclarationMeaning { get; }
}

internal sealed record MaterializedValue<TSource, TValue>(
    string Name,
    Derived<TSource, TValue> Derived,
    Func<TSource, TValue> Read,
    Action<TSource, TValue> Write)
    where TSource : class;

internal sealed class ConceptModel
{
    private ConceptModel()
    {
    }

    public required CompiledConsistencyModel Compiled { get; init; }
    public required ObjectSet<PurchaseOrderInvoiceLine> Links { get; init; }
    public required ObjectSet<OrderLine> Lines { get; init; }
    public required ObjectSet<Fulfillment> Fulfillments { get; init; }
    public required ObjectSet<Allocation> Allocations { get; init; }
    public required Derived<PurchaseOrderInvoiceLine, decimal> PriceRate { get; init; }
    public required Derived<PurchaseOrderInvoiceLine, decimal> RuntimeOnlyPriceRate { get; init; }
    public required Derived<PurchaseOrderInvoiceLine, decimal> RiskScore { get; init; }
    public required Derived<PurchaseOrderInvoiceLine, decimal> UnitRate { get; init; }
    public required Derived<PurchaseOrderInvoiceLine, decimal> RuntimeOnlyUnitRate { get; init; }
    public required Derived<PurchaseOrderInvoiceLine, decimal> UnitFromRuntimePrice { get; init; }
    public required Derived<PurchaseOrderInvoiceLine, decimal> DirectPropertyUnitRate { get; init; }
    public required Derived<PurchaseOrderInvoiceLine, bool> LinkValidity { get; init; }
    public required Invariant<PurchaseOrderInvoiceLine> LinkInvariant { get; init; }
    public required Derived<OrderLine, decimal> FulfilledQuantity { get; init; }
    public required Derived<OrderLine, decimal> RemainingQuantity { get; init; }
    public required Derived<Allocation, bool> AllocationValidity { get; init; }
    public required MaterializedValue<PurchaseOrderInvoiceLine, decimal> PriceRateTarget { get; init; }
    public required MaterializedValue<PurchaseOrderInvoiceLine, decimal> UnitRateTarget { get; init; }
    public required MaterializedValue<PurchaseOrderInvoiceLine, decimal> AlternateUnitRateTarget { get; init; }
    public required MaterializedValue<OrderLine, decimal> FulfilledTarget { get; init; }
    public required MaterializedValue<OrderLine, decimal> RemainingTarget { get; init; }
    public required MaterializedValue<Allocation, bool> AllocationValidityTarget { get; init; }
    public required List<int> Repairs { get; init; }

    public static ConceptModel Build()
    {
        var repairs = new List<int>();
        var builder = new ConsistencyModelBuilder();
        var links = builder.Objects<PurchaseOrderInvoiceLine>().Named("links").Key(x => x.Id);
        var lines = builder.Objects<OrderLine>().Named("order-lines").Key(x => x.Id);
        var fulfillments = builder.Objects<Fulfillment>().Named("fulfillments").Key(x => x.Id);
        var allocations = builder.Objects<Allocation>().Named("allocations").Key(x => x.Id);

        var priceRate = builder.Derived(links)
            .DependsOn(x => x.InvoiceLine.Price)
            .DependsOn(x => x.PurchaseOrderLine.Price)
            .Impact(impact => impact.SourceChanged(DependencySeverity.Invalid))
            .Compute(x => PurchaseOrderInvoiceLine.CalculatePriceRate(x))
            .Named("price-rate");
        var runtimeOnlyPriceRate = builder.Derived(links)
            .Compute(x => x.InvoiceLine.Price / x.PurchaseOrderLine.Price)
            .Named("runtime-only-price-rate");
        var riskScore = builder.Derived(links)
            .Compute(x => x.InvoiceLine.Price - x.PurchaseOrderLine.Price)
            .Named("risk-score");
        var unitRate = builder.Derived(links)
            .Using(priceRate)
            .Impact(impact => impact.SourceChanged(DependencySeverity.Invalid))
            .Compute((_, rate) => rate * 2m)
            .Named("unit-rate");
        var runtimeOnlyUnitRate = builder.Derived(links)
            .Using(priceRate)
            .Compute((_, rate) => rate * 2m)
            .Named("runtime-only-unit-rate");
        var unitFromRuntimePrice = builder.Derived(links)
            .Using(runtimeOnlyPriceRate)
            .Compute((_, rate) => rate * 2m)
            .Named("unit-from-runtime-price");
        var directPropertyUnitRate = builder.Derived(links)
            .Compute(x => x.PriceRate * 2m)
            .Named("direct-property-unit-rate");
        var linkValidity = builder.Derived(links)
            .Using(unitRate)
            .Impact(impact => impact.SourceChanged(DependencySeverity.Invalid))
            .Compute((_, rate) => rate <= 12m)
            .Named("link-validity");
        var invariant = builder.Invariant(links)
            .Using(linkValidity)
            .Must((_, valid) => valid)
            .Named("link-validity-invariant")
            .ScheduleRepairWith(link => repairs.Add(link.Id));

        var matching = builder.Relation(lines, fulfillments)
            .Where((line, fulfillment) =>
                line.OrderNumber == fulfillment.OrderNumber &&
                line.ItemNumber == fulfillment.ItemNumber &&
                !fulfillment.Cancelled)
            .Named("matching-fulfillments");
        var fulfilledQuantity = builder.Derived(lines)
            .Using(matching)
            .Impact(impact => impact
                .MembershipAdded(DependencySeverity.Dirty)
                .MembershipRemoved(DependencySeverity.Invalid)
                .ItemChanged(DependencySeverity.Invalid))
            .Incrementally()
            .Compute((_, rows) => rows.Sum(x => x.Quantity))
            .Named("fulfilled-quantity");
        var remainingQuantity = builder.Derived(lines)
            .Using(fulfilledQuantity)
            .Compute((line, fulfilled) => line.OrderedQuantity - fulfilled)
            .Named("remaining-quantity");
        var allocationValidity = builder.Derived(allocations)
            .Using(x => x.OrderLine, remainingQuantity)
            .Compute((allocation, remaining) => allocation.ReservedQuantity <= remaining)
            .Named("allocation-validity");

        return new ConceptModel
        {
            Compiled = builder.Build(),
            Links = links,
            Lines = lines,
            Fulfillments = fulfillments,
            Allocations = allocations,
            PriceRate = priceRate,
            RuntimeOnlyPriceRate = runtimeOnlyPriceRate,
            RiskScore = riskScore,
            UnitRate = unitRate,
            RuntimeOnlyUnitRate = runtimeOnlyUnitRate,
            UnitFromRuntimePrice = unitFromRuntimePrice,
            DirectPropertyUnitRate = directPropertyUnitRate,
            LinkValidity = linkValidity,
            LinkInvariant = invariant,
            FulfilledQuantity = fulfilledQuantity,
            RemainingQuantity = remainingQuantity,
            AllocationValidity = allocationValidity,
            PriceRateTarget = new("price-rate", priceRate, x => x.PriceRate, (x, value) => x.PriceRate = value),
            UnitRateTarget = new("unit-rate", unitRate, x => x.UnitRate, (x, value) => x.UnitRate = value),
            AlternateUnitRateTarget = new("unit-from-runtime-price", unitFromRuntimePrice,
                x => x.AlternateUnitRate, (x, value) => x.AlternateUnitRate = value),
            FulfilledTarget = new("fulfilled-quantity", fulfilledQuantity,
                x => x.FulfilledQuantity, (x, value) => x.FulfilledQuantity = value),
            RemainingTarget = new("remaining-quantity", remainingQuantity,
                x => x.RemainingQuantity, (x, value) => x.RemainingQuantity = value),
            AllocationValidityTarget = new("allocation-validity", allocationValidity,
                x => x.IsValid, (x, value) => x.IsValid = value),
            Repairs = repairs
        };
    }
}

internal sealed class StorageRuntime
{
    private readonly IStorageSemantics _semantics;
    private readonly List<ITrackedMaterialization> _tracked = [];

    public StorageRuntime(IStorageSemantics semantics, ConsistencyRuntime runtime) =>
        (_semantics, Runtime) = (semantics, runtime);

    public ConsistencyRuntime Runtime { get; }
    public string Name => _semantics.Name;
    public int SuccessfulAssignments => _tracked.Sum(x => x.SuccessfulAssignments);

    public void Track<TSource, TValue>(MaterializedValue<TSource, TValue> definition, TSource source)
        where TSource : class => _tracked.Add(new TrackedMaterialization<TSource, TValue>(definition, source));

    public TValue Get<TSource, TValue>(Derived<TSource, TValue> definition, TSource source)
        where TSource : class
    {
        var value = Runtime.Get(definition, source);
        if (_semantics.SynchronizeOnGet)
            SynchronizeFresh();
        return value;
    }

    public TValue Get<TSource, TValue>(MaterializedValue<TSource, TValue> definition, TSource source)
        where TSource : class
    {
        var tracked = Find(definition, source);
        if (_semantics.ReadFreshValueFromProperty && tracked.HasSynchronizedValue && !tracked.SynchronizationFailed)
        {
            var propertyValue = tracked.ReadPropertyBackedValue();
            if (Runtime.GetState(definition.Derived, source) == DerivedValueState.Fresh)
                return propertyValue;
        }

        var value = Runtime.Get(definition.Derived, source);
        if (_semantics.SynchronizeOnGet)
            SynchronizeFresh();
        return value;
    }

    public DerivedValueState GetState<TSource, TValue>(
        MaterializedValue<TSource, TValue> definition,
        TSource source)
        where TSource : class
    {
        var tracked = Find(definition, source);
        return tracked.SynchronizationFailed
            ? DerivedValueState.Invalid
            : Runtime.GetState(definition.Derived, source);
    }

    public ChangeImpact Apply(PropertyChange change)
    {
        var impact = Runtime.Apply(change);
        if (_semantics.SynchronizeFreshIncrementalValues)
            SynchronizeFresh();
        return impact;
    }

    public ChangeImpact Apply(CollectionChange change)
    {
        var impact = Runtime.Apply(change);
        if (_semantics.SynchronizeFreshIncrementalValues)
            SynchronizeFresh();
        return impact;
    }

    public ChangeImpact Apply(MutationSet mutations)
    {
        var impact = Runtime.Apply(mutations);
        if (_semantics.SynchronizeFreshIncrementalValues)
            SynchronizeFresh();
        return impact;
    }

    public void MaterializeAll()
    {
        foreach (var tracked in _tracked)
            tracked.Force(Runtime);
    }

    public void Materialize<TSource, TValue>(MaterializedValue<TSource, TValue> definition, TSource source)
        where TSource : class => Find(definition, source).Force(Runtime);

    private void SynchronizeFresh()
    {
        foreach (var tracked in _tracked)
            tracked.SynchronizeIfFresh(Runtime);
    }

    private TrackedMaterialization<TSource, TValue> Find<TSource, TValue>(
        MaterializedValue<TSource, TValue> definition,
        TSource source)
        where TSource : class => _tracked.OfType<TrackedMaterialization<TSource, TValue>>()
        .Single(x => ReferenceEquals(x.Definition, definition) && ReferenceEquals(x.Source, source));

    private interface ITrackedMaterialization
    {
        int SuccessfulAssignments { get; }
        void Force(ConsistencyRuntime runtime);
        void SynchronizeIfFresh(ConsistencyRuntime runtime);
    }

    private sealed class TrackedMaterialization<TSource, TValue>(
        MaterializedValue<TSource, TValue> definition,
        TSource source) : ITrackedMaterialization
        where TSource : class
    {
        private TValue? _expected;

        public MaterializedValue<TSource, TValue> Definition { get; } = definition;
        public TSource Source { get; } = source;
        public bool HasSynchronizedValue { get; private set; }
        public bool SynchronizationFailed { get; private set; }
        public int SuccessfulAssignments { get; private set; }

        public TValue ReadPropertyBackedValue()
        {
            var current = Definition.Read(Source);
            if (!EqualityComparer<TValue>.Default.Equals(current, _expected!))
            {
                SynchronizationFailed = true;
                throw new InvalidOperationException(
                    $"External write detected for property-backed derived value '{Definition.Name}'.");
            }
            return current;
        }

        public void Force(ConsistencyRuntime runtime) =>
            Synchronize(runtime.Get(Definition.Derived, Source));

        public void SynchronizeIfFresh(ConsistencyRuntime runtime)
        {
            if (runtime.GetState(Definition.Derived, Source) == DerivedValueState.Fresh)
                Synchronize(runtime.Get(Definition.Derived, Source));
        }

        private void Synchronize(TValue value)
        {
            var previous = Definition.Read(Source);
            if (EqualityComparer<TValue>.Default.Equals(previous, value))
            {
                _expected = value;
                HasSynchronizedValue = true;
                SynchronizationFailed = false;
                return;
            }
            try
            {
                Definition.Write(Source, value);
                _expected = value;
                HasSynchronizedValue = true;
                SynchronizationFailed = false;
                SuccessfulAssignments++;
            }
            catch
            {
                try
                {
                    if (!EqualityComparer<TValue>.Default.Equals(Definition.Read(Source), previous))
                        Definition.Write(Source, previous);
                }
                finally
                {
                    SynchronizationFailed = true;
                }
                throw;
            }
        }
    }
}

internal sealed class Fixture
{
    private Fixture()
    {
    }

    public required IStorageSemantics Semantics { get; init; }
    public required ConceptModel Model { get; init; }
    public required StorageRuntime Storage { get; init; }
    public required PurchaseOrderInvoiceLine Link { get; init; }
    public required OrderLine Line { get; init; }
    public required Fulfillment Fulfillment { get; init; }
    public required Allocation Allocation { get; init; }

    public static Fixture Create(IStorageSemantics semantics)
    {
        var model = ConceptModel.Build();
        var invoice = new InvoiceLine { Id = 10, Price = 60m };
        var purchaseOrder = new PurchaseOrderLine { Id = 20, Price = 10m };
        var link = new PurchaseOrderInvoiceLine
        {
            Id = 1,
            InvoiceLineId = invoice.Id,
            InvoiceLine = invoice,
            PurchaseOrderLineId = purchaseOrder.Id,
            PurchaseOrderLine = purchaseOrder,
            PriceRate = 6m,
            UnitRate = 12m,
            AlternateUnitRate = 12m
        };
        var line = new OrderLine { Id = 1, OrderedQuantity = 10m, FulfilledQuantity = 4m, RemainingQuantity = 6m };
        var fulfillment = new Fulfillment { Id = 1, Quantity = 4m };
        var allocation = new Allocation { Id = 1, OrderLineId = line.Id, OrderLine = line, ReservedQuantity = 5m, IsValid = true };
        var runtime = model.Compiled.CreateRuntime(seed =>
        {
            seed.Add(model.Links, [link]);
            seed.Add(model.Lines, [line]);
            seed.Add(model.Fulfillments, [fulfillment]);
            seed.Add(model.Allocations, [allocation]);
        });
        var storage = new StorageRuntime(semantics, runtime);
        storage.Track(model.PriceRateTarget, link);
        storage.Track(model.UnitRateTarget, link);
        storage.Track(model.AlternateUnitRateTarget, link);
        storage.Track(model.FulfilledTarget, line);
        storage.Track(model.RemainingTarget, line);
        storage.Track(model.AllocationValidityTarget, allocation);
        return new Fixture
        {
            Semantics = semantics,
            Model = model,
            Storage = storage,
            Link = link,
            Line = line,
            Fulfillment = fulfillment,
            Allocation = allocation
        };
    }

    public void PrimeAll()
    {
        Storage.MaterializeAll();
        _ = Storage.Get(Model.RiskScore, Link);
        _ = Storage.Runtime.Evaluate(Model.LinkInvariant, Link);
    }

    public void ChangeInvoicePrice(decimal value)
    {
        var old = Link.InvoiceLine.Price;
        Link.InvoiceLine.Price = value;
        Storage.Apply(Change.Property(Link.InvoiceLine, x => x.Price, old, value));
    }
}

internal static class Harness
{
    public static IReadOnlyList<IStorageSemantics> Policies { get; } =
    [
        new ModelARuntimeAuthoritative(),
        new ModelBGetSynchronizesProperty(),
        new ModelCPropertyBacked()
    ];

    public static async Task RunAsync()
    {
        D01PriceRate.Run();
        D02PersistenceWithoutRead.Run();
        D03RepeatedRead.Run();
        D04DerivedChain.Run();
        D05DirectPropertyRead.Run();
        D06RelationAggregate.Run();
        D07ProjectedDependency.Run();
        D08RepairSurvival.Run();
        D09ComputationFailure.Run();
        D10SetterFailure.Run();
        await D11EfTracking.RunAsync();
        D12PlainObject.Run();
        Console.WriteLine("Derived storage/materialization concept D1-D12 passed for Models A, B, and C.");
    }

    public static void ForEachPolicy(Action<Fixture> scenario)
    {
        foreach (var policy in Policies)
            scenario(Fixture.Create(policy));
    }

    public static void Require(bool condition, Fixture fixture, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"{fixture.Semantics.Name}: {message}");
    }
}
