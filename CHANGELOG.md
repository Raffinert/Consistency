# Changelog

All notable package changes are recorded here. This project follows Semantic Versioning once a stable
`1.0.0` release exists; during `0.x`, minor versions may contain deliberate breaking API changes and
patch versions remain backward-compatible bug fixes where practical.

## 0.2.0-rc.2

### Added

- Added `IConsistencyRuntime`, a narrow application-facing abstraction for logical evaluation and
  materialization, with EF Core DI resolving it to the same scoped `ConsistencyRuntime` instance.

## 0.2.0-rc.1

### Public API

- Standardized derived declarations around `From`, `DependsOn`, `Select`, and recognized
  `Sum`/`Count`/`LongCount`/`Any` aggregates.
- Added logical `Evaluate` plus targeted and object-level `Materialize` runtime operations.
- Added `MaterializeTo` as the single declaration for physical derived mirrors.
- Added typed local, relation, projected, and mixed derived composition.
- Froze the reviewed Core and EF Core public surfaces in their shipped API baselines.

### EF Core integration

- Added scoped dependency-injection integration with automatic tracked baseline admission and ordinary
  `SaveChanges`/`SaveChangesAsync` interception.
- Added EF-aware `Materialize(entity)` for applications that need synchronized mirrors before saving.
- Added member-aware first-binding guards that allow ordinary mapped properties outside the consistency
  model while rejecting relevant changes and mapped lifecycle mutations.
- Added pending-plan reuse and invalidation bound to runtime and baseline revisions, including bounded
  external-consumer discovery rebuilds.
- Added transactional rollback and retry protection for materialization, tracked baselines, and SQL failures.

### Correctness and diagnostics

- Preserved logical freshness, invalidation, repair dispatch, exact object-set ownership, and evaluate-first,
  write-second materialization semantics.
- Preserved incremental aggregate execution and typed dependency composition across local, relation, and
  projected inputs.
- Added compiled-model diagnostics for semantic dependencies and materialization targets.

### Known limitations

- `ConsistencyRuntime` is mutable and not thread-safe.
- External consumer discovery supports eligible direct reference navigations only; unsupported scope shapes
  require authoritative whole-set coverage or a redesigned consistency boundary.
- Raw SQL, `ExecuteUpdate`/`ExecuteDelete`, triggers, bulk operations, and external writers are invisible
  unless the host publishes exact mutations or reconciles/rebuilds the runtime.
- Authoritative query completeness and database concurrency/isolation remain host responsibilities.

## 0.1.0-rc.1

### Added

- Additive API-v2 value-flow declarations with `From` and `Select`, including typed local and projected
  derived composition and opaque method-group calculators backed by explicit `DependsOn` declarations.
- Recognized `Sum`, `Count`, `LongCount`, and `Any` operators that select existing incremental plans without an
  explicit `.Incrementally()` step.
- Core-owned `MaterializeTo` metadata plus logical `Evaluate`, targeted `Materialize`, and indexed object-level
  `Materialize` runtime operations.
- Logical dependency and materialization diagnostics covering direct, derived, projected, and relation edges.
- Explicit `DependsOn(...)` declarations for opaque source-derived calculations.
- Authoritative EF consistency-scope validation for cross-object enforcement and materialization.
- Targeted, batched `DiscoverConsumers(...)` support for eligible direct EF reference-navigation consumers,
  including consumers whose navigation is not currently loaded.
- Policy-aware manual consistency unit-of-work flow for caller-owned transactions, outboxes, and generated
  semantic values.
- Generated INSERT/UPDATE semantic-value and foreign-key fixup handling in the public EF workflow.
- Store-side referential-action preflight for dangerous cascade and set-null paths into managed state.

### Correctness and hardening

- Evaluate-first/write-second object materialization, skipped equal assignments, physical rollback on setter
  failure, exact object-set identity validation, and automatic EF consumption of Core materialization targets.
- Exact binding-plan installation after database durability, with structural coverage admission kept separate
  from domain `ObjectAdded` semantics.
- ChangeTracker overlay handling for added, deleted, and retargeted consumers, plus evaluation-closure
  validation for discovery resolver results.
- SQL-failure, cancellation, retry, and manual-UoW atomicity protections.
- Sink-only materialization enforcement, approved public API baselines, and packed-package consumer smoke
  verification.

### Known limitations

- External consumer discovery currently supports eligible direct reference navigations only.
- Multi-hop, collection-navigation, relation-source/target, and projected-consumer discovery are not
  substituted by `DiscoverConsumers`.
- `ConsistencyRuntime` is not thread-safe.
- Raw SQL, `ExecuteUpdate`/`ExecuteDelete`, triggers mutating other rows, external writers, and other
  mutations invisible to EF/Raffinert require exact mutation publication or runtime reconciliation/rebuild.
- Host query completeness and database concurrency/isolation remain application responsibilities.

## 0.1.0-alpha.1

- Renamed product/package/namespace from `Raffinert.Relations` to `Raffinert.Consistency` before first
  publication/stable release. Binary relation concepts retain `Relation*` terminology.
- Added strict, data-only `DurablePolicyWork` projection for durable repair and immediate-evaluation scheduling.
- Corrected the binding transactional-outbox guidance, froze the initial shipped Public API baselines, and
  strengthened release-contract preparation without publishing packages.
- Added non-mutating prepared impact preview in core and EF Core, including an explicit transactional
  outbox workflow that can persist impact plans before runtime commit.
- Added binding prepared impact plans whose later commit installs the exact planned runtime state without
  rerunning user classifiers, predicates, or dependency propagation.
- Hardened EF planning for multiple store-generated identities and truthful reference-navigation old values.
- Made causal node IDs opt-in, preserved conservative precision through propagation, and added durable
  identities for added and store-generated lifecycle origins.
- Replaced whole object-set and projection-registry plan snapshots with touched-entry journals and added
  real SQLite business-row/outbox commit and rollback coverage.
- Corrected prepared-planning benchmarks so preview, planning, and patch installation measure distinct work,
  and verified the binding-plan surface through fresh packed-package consumers.
- Added decision-time relation route triggers so conservative multi-item waves preserve per-left provenance,
  retained only the forward patch in binding plans, and expanded generated-key outbox rollback coverage.
- Added canonical full-result parity comparison across preview, planning, and committed execution tests.
- Shared physical direct-reference projection indexes across semantic consumers and replaced whole-runtime
  projection validation snapshots with touched-edge final-state validation.
- Added typed keyed object sets and expression-defined binary relations.
- Added scan/hash planning, reverse access, nested reference and collection dependency tracking.
- Added derived values, invariants, configurable severity, policy requests, and structured diagnostics.
- Added atomic mutation batches with versioned prepare/commit/dispatch integration.
- Added opt-in incremental Count, LongCount, Any, and numeric Sum plans.
- Added EF Core 10 unit-of-work integration and SQLite transaction coverage.
- Added .NET 8 and .NET 10 core assets and .NET 10 EF Core adapter assets.
- Added deterministic source-scoped dependency DAG propagation and composed derived values/invariants.
- Separated relation query access from exact or selectively routed conservative propagation.
- Added typed value-sensitive source-member severity for asymmetric domain correctness rules.
- Added opt-in committed-impact causality with exact/conservative precision and trace rendering.
- Added a compiling order-fulfillment/allocation-validity vertical slice.
- Added packed-package consumer smoke tests for .NET 8, .NET 10, and the EF Core adapter.
- Added transaction-safe detailed prepared commits in core and the EF unit of work.
- Added zero-version runtime bootstrap from authoritative object sets.
- Added projected cross-object-set derived dependencies for persisted reference models.
- Preserved normalized collection provenance and causal local severity, precision, and reaction escalation.
- Enforced non-null, exact-object-set integrity for projected targets across bootstrap and lifecycle batches.
- Replaced projected all-source scans and dynamic invocation with maintained reverse ownership indexes.
- Added two-upstream projected composition and removed incomplete tracking from the order-fulfillment sample.

This alpha is not yet published automatically. Its public API is tracked by checked-in approval files.
