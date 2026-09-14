# Changelog

All notable package changes are recorded here. This project follows Semantic Versioning once a stable
`1.0.0` release exists; during `0.x`, minor versions may contain deliberate breaking API changes and
patch versions remain backward-compatible bug fixes where practical.

## 0.1.0-alpha.1

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
- Added a compiling purchase-order/goods-receipt/link-validity vertical slice.
- Added packed-package consumer smoke tests for .NET 8, .NET 10, and the EF Core adapter.
- Added transaction-safe detailed prepared commits in core and the EF unit of work.
- Added zero-version runtime bootstrap from authoritative object sets.
- Added projected cross-object-set derived dependencies for persisted reference models.
- Preserved normalized collection provenance and causal local severity, precision, and reaction escalation.

This alpha is not yet published automatically. Its public API is tracked by checked-in approval files.
