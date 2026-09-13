# Raffinert.Relations

Raffinert.Relations is an experimental declarative dependency engine for .NET object models.

The dependency-free core package supports .NET 8 and .NET 10. The EF adapter targets .NET 10 and EF
Core 10. Tests and benchmarks run on .NET 10; the normal build and pack gates compile both core targets.

Relationships are defined as expression trees. The library analyzes those expressions to derive dependencies, access paths, indexes, and change impact, allowing relationship queries to be maintained incrementally without repeating the business rule in index or refresh configuration.

## Example

```csharp
var model = new RelationModelBuilder();

var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
var poLines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);

var matches = model.Relation(invoices, poLines).Where((invoice, poLine) =>
    invoice.PurchaseOrderNumber == poLine.PurchaseOrderNumber &&
    invoice.ItemNumber == poLine.ItemNumber);

var runtime = model.Build().CreateRuntime();
runtime.Add(invoices, invoice);
runtime.Add(poLines, poLine);

var related = runtime.Related(matches, invoice);

var oldItem = poLine.ItemNumber;
poLine.ItemNumber = "ITEM-2";
runtime.Apply(Change.Property(poLines, poLine, x => x.ItemNumber, oldItem, poLine.ItemNumber));
```

`Change.Property` observes a mutation that has already happened; it never mutates the domain object.
The object-set argument can be omitted when the instance belongs to exactly one registered set.
An object's declared key is immutable while the object is registered. Reported key changes are rejected;
remove and re-add the object when its identity genuinely needs to change.

A `ChangeSet` is fully validated before runtime-maintained indexes and dependency state are updated.
Repeated changes to one instance/member must form a contiguous value chain (`A -> B`, `B -> C`) and
are normalized to their net effect; conflicting chains are rejected. Domain mutation remains external
and is not transactional. Pass `ChangeValidationMode.StrictNewValue` to `Apply` to additionally verify
that each member currently equals its reported final new value.

Use `MutationSet` when one domain operation includes object lifecycle, property, and collection changes:

```csharp
runtime.Apply(MutationSet.Create(
    Change.Add(poLines, addedLine),
    Change.Property(invoices, invoice, x => x.ItemNumber, oldItem, invoice.ItemNumber),
    Change.CollectionReset(order, x => x.Lines),
    Change.Remove(poLines, removedLine)));
```

The runtime validates the complete mutation set before updating runtime-owned state, commits it as one
logical operation, and dispatches dependency policy callbacks only after the final state is committed.
External unit-of-work integrations can split those phases explicitly:

```csharp
var prepared = runtime.Prepare(mutations, ChangeValidationMode.StrictNewValue);
await database.SaveChangesAsync();
runtime.Commit(prepared);
runtime.Dispatch(prepared);
```

Preparation performs ordinary mutation validation without changing runtime-owned state. A prepared
mutation records `runtime.Version`; commit rejects it as stale if another runtime mutation committed in
the meantime. Commit updates runtime state but never invokes application callbacks, which remain isolated
in the dispatch phase.

Collection navigation is explicit: mutate the domain collection first, then report it with
`Change.CollectionAdd`, `Change.CollectionRemove`, or `Change.CollectionReset`. The runtime maintains
owner/item reverse navigation so later item-property changes resolve affected owners incrementally.
Membership uses reference-identity set semantics: equal-but-distinct objects remain distinct, while
duplicate occurrences of the same reference count as one dependency membership. Report
`CollectionRemove` only when the final occurrence is absent; use `CollectionReset` after duplicate-count,
ordering, wholesale replacement, or other changes where an add/remove signal is insufficient. Ordering
and multiplicity are not independently indexed, but reset reevaluates expressions that depend on them.
Nested collections and one item shared by multiple registered roots are reverse-tracked; after removal,
later item mutations no longer affect the former owner.

Cached derived computations, invariant predicates, and relations materialized for derived propagation
must have complete dependency analysis. `Build()` rejects opaque code or mutable captured/static state by
default because the runtime cannot keep those caches reliably fresh. Direct-query-only relations may stay
opaque because their original predicate is evaluated on every query. Deliberate prototypes can call
`AllowIncompleteDependencies()` on the affected relation, derived value, or invariant; `DebugView` then
labels the weaker guarantee explicitly, and cached freshness must not be treated as fully tracked.

Derived definitions can express domain correctness severity independently of query optimization:

```csharp
var received = model.Derived(poLines)
    .Using(receipts)
    .Impact(policy => policy
        .MembershipAdded(DependencySeverity.Dirty)
        .MembershipRemoved(DependencySeverity.Invalid)
        .ItemChanged(DependencySeverity.Invalid))
    .Compute((line, matches) => matches.Sum(receipt => receipt.Quantity));
```

`Dirty` means the cached result must be recomputed when freshness matters; `Invalid` means it must not
be relied upon before recomputation. The same policy has identical semantics for scan and hash plans.

For outbox, queue, or background-work integrations, `ApplyDetailed` commits synchronously but returns
policy work as data before any application callback runs:

```csharp
RuntimeApplication application = runtime.ApplyDetailed(mutations);
RuntimeApplyResult result = application.Result;

foreach (var request in result.RepairRequests)
{
    DurablePolicyRequestIdentity durable = request.GetDurableIdentity();
    outbox.Add(durable.DefinitionKey, durable.Source, request.Reason);
}

application.Dispatch.Invoke(); // optional configured in-process callbacks
```

The result also includes relation pair deltas, derived and invariant impacts, immediate-evaluation
requests, and the original `ChangeImpact`. Numeric definition IDs and `Source` object references are
in-process conveniences only. Persist `DefinitionKey` plus canonical `DurableSourceIdentity`; the
`GetDurableIdentity()` helper fails explicitly when the invariant/object set is unnamed or the key cannot
be represented canonically.

Standalone aggregates can opt into conservative incremental maintenance:

```csharp
var received = model.Derived(poLines)
    .Using(receipts)
    .Incrementally()
    .Compute((line, matches) => matches.Sum(receipt => receipt.Quantity));
```

Exact `Count`, `LongCount`, parameterless `Any`, and direct numeric `Sum` expressions have incremental
plans. They update already-fresh cache entries from relation/item deltas without enumerating the full
match list. Unrecognized expressions retain the original compiled computation as the semantic fallback;
the selected plan is shown in `DebugView`.

Full-recompute relation consumers may instead call `Conservatively()` before `Compute(...)`.
This avoids retaining permanent matching pairs and invalidates a safe source superset; lazy reads
still execute the original predicate. Incremental computations and exact membership-severity policies
retain exact materialized propagation. Query access, reverse candidate access, and propagation plan are
reported independently in compiled diagnostics.

`DebugView` also identifies each relation's `None` or `ExactPropagation` materialization mode.
`runtime.Diagnostics.Relations` reports forward/reverse access-index entries, materialized pair count,
average fan-out, and a density-warning flag. Configure advisory thresholds with
`CreateRuntime(new RuntimeDiagnosticOptions { ... })`; they do not reject or limit runtime mutations.

`compiled.Diagnostics` is the machine-readable counterpart to `DebugView`. It provides immutable object
set, relation, derived-value, and invariant records with deterministic IDs, types/expressions,
relation and upstream-derived input IDs, access plans, completeness issues, materialization, LINQ semantics, computation plans, and
configured reactions/severities. Runtime counters additionally track reindexed roots, affected sources,
membership pairs added/removed, full and incremental derived computations, and policy requests since the
last `ResetDiagnostics()` call.

## Implemented

- Typed object sets with stable keys
- Separate mutable object-set builders and stable `ObjectSet<T>` runtime handles
- Binary relations whose original compiled predicate remains the semantic authority
- Dependency and nested member-path analysis
- Safe-by-default cached dependency completeness validation with explicit weaker-guarantee opt-ins
- Automatic single and composite hash indexes for safe equality joins
- Explicit scan and hash-join access planning
- Reverse hash access for exact derived-state propagation
- Ordinal and ordinal-ignore-case comparer-aware string joins
- Correct scan fallback for opaque or unsupported predicates
- Incremental add, remove, and scalar-property index maintenance
- Explicit collection add/remove/reset with incremental owner/item navigation
- Shared arbitrary-depth reverse navigation for nested paths
- Compiled null-safe member-path and cached single-member readers
- Access-impact and semantic-impact reporting
- Deterministic, atomically validated `ChangeSet` and unified lifecycle/property/collection `MutationSet` application
- Bidirectional relation queries
- Lazy derived state with distinct fresh, dirty, and invalid states
- Source-only derived values and one/two-upstream derived composition through a compiled DAG
- Opt-in incremental `Count`, `LongCount`, `Any`, and direct numeric `Sum` computation plans
- Documented monotonic state transitions with explicit recomputation and revalidation recovery
- Public per-derived dependency severity for membership additions/removals and item changes, independent of access planning
- Exact source-scoped derived invalidation backed by bidirectional relation membership
- Unified relation-impact snapshots for delta, semantic, and access propagation
- Focused member/relation/derived/invariant/policy dependency-graph propagation
- Source lifecycle cleanup for derived, invariant, and materialized relation state
- Role-aware dependency analysis for derived computations and invariant predicates
- LINQ dependency extraction for common aggregate, filter, and projection operators
- Explicit membership/item/ordering semantics for selection, cardinality, distinct, paging, and containment operators
- Single- and multi-input invariant evaluation with immediate, dirty, invalidation, and repair policies
- Post-commit immediate evaluation and deduplicated repair-request dispatch
- Data-only `RuntimeApplyResult` impacts and policy requests with a separate resumable `PolicyDispatchHandle`
- A separate EF Core change-tracker adapter package
- EF Core unit-of-work capture with prepare-before-save, versioned commit-after-success, relationship resets, and explicit set mapping
- A BenchmarkDotNet benchmark project
- Propagation precision diagnostics and 1/10/100-change benchmarks at 10k/100k scale
- Measured range-planning benchmark (current equality-prefix strategy retained)
- Human-readable compiled model diagnostics through `DebugView`
- Per-relation materialization, index-size, pair-count, fan-out, and configurable density diagnostics
- Structured compiled-model diagnostics and cumulative incremental-work runtime counters
- Deterministic full-graph randomized optimized-versus-scan correctness coverage
- CI restore/build/test/format/pack validation and NuGet-ready package metadata
- Deterministic SourceLink-enabled packages, repository commit metadata, package validation, and `.snupkg` symbols

## Further work

- Additional access plans only for measured workloads that satisfy the
  [optimizer admission policy](docs/optimizer-policy.md)
- Optional asynchronous dispatch integrations built on structured policy requests

The core package has no EF Core or dependency-injection dependency.

Definitions that cross a process boundary can be assigned stable logical keys with `Named(...)` on
object-set builders, relations, derived values, and invariants. Names are ordinal-independent and must be
unique within the compiled model. Structured policy requests expose both the local numeric invariant ID
and its optional `DefinitionKey`, plus a `SourceIdentity` containing the object-set key, CLR type, and
registered source key. `SourceIdentity.IsDurable` requires both a named source object set and a
canonically representable key. A durable policy request additionally requires a stable invariant
`DefinitionKey`; unnamed definitions and numeric IDs remain intended for in-process diagnostics only.

## Documentation

- [Architecture and runtime contracts](docs/architecture.md)
- [End-to-end purchase order and goods receipt example](docs/purchase-order-example.md)
- [Measured-workload optimizer policy](docs/optimizer-policy.md)
- [Release and versioning process](RELEASING.md)

The EF Core adapter captures and prepares a `RelationUnitOfWork` before `SaveChanges`, then commits its
mutations as one atomic runtime batch and dispatches callbacks only after the database operation succeeds.
`SaveChangesAndApply`/`SaveChangesAndApplyAsync` provide this ordering. Manual integrations can call the
unit of work's `Prepare`, `Commit`, and `Dispatch` methods directly.
If the database succeeds but runtime commit fails, the convenience methods throw
`RelationRuntimeSynchronizationException`. The database must not be retried blindly: reconcile or rebuild
the runtime from authoritative state, then prepare new runtime work. Policy callback failures are distinct;
they occur after runtime commit and resumable dispatch can continue from the failed action.
Added and deleted entities require a `RelationUnitOfWorkMappings` entry; selectors disambiguate CLR types
used by multiple object sets. Modified scalars, references, owned entries, and collection resets are
translated through the same core change contracts. A stale prepared mutation is rejected if runtime state
advances between preparation and commit and requires application-level reconciliation; it cannot roll back
the database transaction.

For database-generated keys, additions are prepared while EF still owns the temporary/default value but
are committed only after `SaveChanges`, when the generated stable key is available. If `SaveChanges`
participates in an explicit or ambient transaction, success does not mean that transaction is durable:
capture and `Prepare` before saving, then call the unit's `Commit` and `Dispatch` only after the surrounding
transaction commits. Discard the prepared unit on rollback. Calling `SaveChangesAndApply` inside an
uncommitted external transaction advances runtime state too early, so use the manual three-phase API there.

When using `SaveChanges(acceptAllChangesOnSuccess: false)`, commit and dispatch the captured unit once, then
call `ChangeTracker.AcceptAllChanges()` separately; do not recapture the still-`Added`/`Modified` entries.

`RelationRuntime` is not thread-safe. Mutations and queries must be externally synchronized.

Both packages check their complete public surface with `Microsoft.CodeAnalysis.PublicApiAnalyzers` and
checked-in `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` baselines. Public signature additions,
removals, and nullability changes therefore fail the normal build until deliberately approved. The
public `ObjectSetBuilder<T>` remains intentional: it is the transient type-safe stage that requires a
stable key before yielding the runtime `ObjectSet<T>` handle.

Release builds are deterministic and SourceLink-enabled, embed repository URL/branch/commit metadata,
run package validation, and produce `.snupkg` symbol packages. See `CHANGELOG.md` for release notes and
`RELEASING.md` for the prerelease version policy and manual publication checklist. Main-branch CI only
uploads package artifacts; it never publishes them.

Immediate invariant evaluations and repair callbacks run only after runtime-owned state has committed.
If a callback throws, the operation surfaces that exception but does not roll back the committed runtime state.
