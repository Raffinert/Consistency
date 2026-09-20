# Allocation consistency dogfood

This executable exercises Raffinert against a neutral allocation model: demands find candidate supplies by
resource and date, fulfillment and allocation rows feed incremental totals, remaining capacity is derived
transitively, and capacity and compatibility invariants protect persistence.

It is an experiment rather than a polished sample. It exercises directional impact classification, projects
allocation compatibility from the candidate-supply relation instead of repeating its predicate, and proves
repair-enabled invariants before emitting structured repair requests. Core callers report POCO mutations
explicitly; EF observes ordinary tracked changes at `SaveChanges`. The findings and remaining limitations are
documented in [DOGFOOD.md](DOGFOOD.md).

Run it from the repository root:

```powershell
dotnet run --project experiments/Raffinert.Consistency.AllocationDogfood/Raffinert.Consistency.AllocationDogfood.csproj
```

The executable covers the core runtime and SQLite-backed EF integration. EF scenarios resolve one scoped
`IConsistencyRuntime`, load the host-asserted complete consistency scope, mutate normal tracked entities, and
use ordinary `SaveChangesAsync`. `Materialize` is called only when a mirror is read before save.

The scope assertion is important: tracked does not mean complete. The host remains responsible for loading
the declared closed world (or configuring supported consumer discovery) before it claims
`ConsistencyScope.Complete(...)`.
