# Final API v2 PriceRate dogfood

This disposable executable tests one focused declaration vocabulary over the current engine. It does not add or change a production API.

The central declaration and its consumption are intentionally adjacent:

```csharp
var priceRate = model
    .Derived(links)
    .DependsOn(
        x => x.InvoiceLine.Price,
        x => x.PurchaseOrderLine.Price)
    .Select(PurchaseOrderInvoiceLine.CalculatePriceRate)
    .MaterializeTo(x => x.PriceRate)
    .Named("price-rate");

var unitRate = model
    .Derived(links)
    .From(priceRate)
    .DependsOn(
        x => x.InvoiceLine.Quantity,
        x => x.PurchaseOrderLine.OrderedQuantity)
    .Select((link, rate) => PurchaseOrderInvoiceLine.CalculateUnitRate(link, rate))
    .MaterializeTo(x => x.UnitRate)
    .Named("unit-rate");

var logicalRate = runtime.Evaluate(priceRate, link);
runtime.Materialize(link);
Use(link.PriceRate, link.UnitRate);
```

`From` is logical value flow, `DependsOn` tracks source reads hidden in ordinary calculator code, and `MaterializeTo` declares a physical output mirror. The executable also covers cross-object flow, recognized aggregates, impact policy, invariant repair, invalid declarations, object and targeted materialization, and EF mapping interop.

Run it from the repository root:

```powershell
dotnet run --project experiments/Raffinert.Consistency.FinalApiDogfood/Raffinert.Consistency.FinalApiDogfood.csproj
```

The evidence and production recommendation are in `docs/final-api-v2-price-rate-dogfood-results.md`.
