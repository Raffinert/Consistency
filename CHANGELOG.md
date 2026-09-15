# Changelog

All notable package changes are recorded here. This project follows Semantic Versioning once a stable
`1.0.0` release exists; during `0.x`, minor versions may contain deliberate breaking API changes and
patch versions remain backward-compatible bug fixes where practical.

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
