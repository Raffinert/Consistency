# First-alpha release-candidate verification

Verification date: 2026-09-15

Verified implementation head before documentation: `de1f6ba6bfcb40fe3626354601d127f176414dd0`

Local release-style verification completed successfully:

- Release solution build: passed with zero warnings and errors.
- Core tests on .NET 8: 243 passed.
- Core tests on .NET 10: 243 passed.
- EF Core and SQLite tests on .NET 10: 29 passed.
- `dotnet format --verify-no-changes`: passed (workspace loader emitted its existing informational warning).
- Core and EF packages plus symbol packages: created successfully.
- Fresh-cache packed consumers for CoreNet8, CoreNet10, and EfNet10: passed.
- No package publication, release, or tag was performed.

The manual GitHub Actions release-candidate workflow completed successfully:

- Run: [34937013974](https://github.com/Raffinert/Consistency/actions/runs/34937013974)
- Result: success (2026-09-15 06:27:30–06:32:12 UTC).
- Verified head: `de1f6ba6bfcb40fe3626354601d127f176414dd0`.
- Artifact: `release-candidate-packages` (artifact ID `10383273956`).
- Remote restore, Release build, both core test targets, SQLite tests, formatting, package creation,
  package-payload validation, clean package-consumer smoke tests, and artifact upload all passed.
- The artifact was not published, and no release or tag was created.

The current alpha contract distinguishes non-binding `PreviewDetailed` from binding `PlanDetailed`.
Durability-sensitive integrations persist data from `PreparedImpactPlan.Result`, commit the database, then
install the plan and dispatch callbacks. Database-success/runtime-failure recovery requires rebuilding or
reconciling runtime state from the authoritative database and durable outbox.

## Post-alpha production integration gate

Verification date: 2026-09-15

Verified implementation head before documentation: `fa2153b0ccbea391fdc299d0b65b34debd65463a`

- Run: [34942535932](https://github.com/Raffinert/Consistency/actions/runs/34942535932)
- Result: success (2026-09-15 07:36:08–07:40:59 UTC).
- Artifact: `release-candidate-packages` (artifact ID `10385383621`).
- Validated package version: `0.1.0-alpha.1` for both core and EF packages.
- Core tests: 258 passed on .NET 8 and 258 passed on .NET 10.
- EF Core and SQLite tests: 30 passed on .NET 10.
- The packed CoreNet8, CoreNet10, and EfNet10 consumers passed.

This run verified the public `DurablePolicyWork` projection and the binding EF outbox consumer path. It also
verified non-empty shipped Public API baselines, empty first-release unshipped baselines, package-version
alignment, and the EF package's exact-version dependency on the produced core package. Package metadata,
target-framework assets, README/changelog payloads, symbol package count, and repository commit metadata were
validated before artifact upload.

The workflow retained read-only repository permissions. No package was published, and no tag or GitHub release
was created.

## Authoritative consistency scope safety gate

Verification date: 2026-09-15

Verified implementation head before closeout documentation: `009da18f01ab2e0eb4495d705628ea4708ab60fd`

- CI run: [35002380559](https://github.com/Raffinert/Consistency/actions/runs/35002380559) — success.
- Release-candidate run: [35002982418](https://github.com/Raffinert/Consistency/actions/runs/35002982418) — success
  (2026-09-15 17:42:53–17:47:47 UTC).
- Artifact: `release-candidate-packages` (artifact ID `10410702219`).
- Core tests: 294 passed on .NET 8 and 294 passed on .NET 10.
- EF Core and SQLite tests: 86 passed on .NET 10.
- Both samples, formatting verification, package/API validation, fresh package consumers, and artifact upload
  passed.

This run verifies that cross-object EF enforcement and materialization fail before SQL without an explicit
authoritative `ConsistencyScope`, while source-local policies remain scope-free. SQLite proofs cover both
false-valid aggregate prevention and partial-mirror prevention. No package was published, and no tag or
GitHub release was created.

## Scope completion and policy-aware manual persistence gate

Verification date: 2026-09-15

Verified implementation head before closeout documentation: `fdf8c010917c07c0ea57737a7dc2b0bbdbd96dee`

- CI run: [35021962335](https://github.com/Raffinert/Consistency/actions/runs/35021962335) — success.
- Release-candidate run: [35024349773](https://github.com/Raffinert/Consistency/actions/runs/35024349773) — success
  (2026-09-15 21:13:24–21:18:34 UTC).
- Artifact: `release-candidate-packages` (artifact ID `10418629562`).
- Core tests: 298 passed on .NET 8 and 298 passed on .NET 10.
- EF Core and SQLite tests: 97 passed on .NET 10.
- Both samples, formatting verification, package/API validation, fresh package consumers, and artifact upload
  passed.

This run verifies navigation-consumer scope requirements and the public policy-aware manual persistence
unit of work. SQLite proofs cover nested-navigation false authority, generated-key rollback and success,
materialization, immutable captured policy, post-durability synchronization failure, and resumable dispatch.
No package was published, and no tag or GitHub release was created.

## Generated-value fixup and set-scoped policy safety gate

Verification date: 2026-09-16

Verified implementation head before closeout documentation: `7140d712fe6fcb2c314ba8364ef29fe5a21b23e6`

- CI run: [35061215303](https://github.com/Raffinert/Consistency/actions/runs/35061215303) — success.
- Release-candidate run: [35061578764](https://github.com/Raffinert/Consistency/actions/runs/35061578764) — success
  (2026-09-16 05:57:04–06:02:44 UTC).
- Artifact: `release-candidate-packages` (artifact ID `10432444024`).
- Core tests: 298 passed on .NET 8 and 298 passed on .NET 10.
- EF Core and SQLite tests: 104 passed on .NET 10.
- Both samples, formatting verification, package/API validation, fresh package consumers, and artifact upload
  passed.

This run covers existing-dependent retargeting to a generated-key principal, selective finalization of
proven EF FK fixup, strict rejection of unrelated post-capture drift, generated semantic non-key readiness,
and exact object-set scoping when multiple sets share one CLR type. No package was published, and no tag or
GitHub release was created.

## Generated UPDATE values and unmapped dependency capture gate

Verification date: 2026-09-16

Verified implementation head before closeout documentation: `5623971a98d222c90cf388dc3c0eb23724fbc75e`

- CI run: [35118835450](https://github.com/Raffinert/Consistency/actions/runs/35118835450) — success.
- Release-candidate run: [35118849723](https://github.com/Raffinert/Consistency/actions/runs/35118849723) — success.
- Artifact: `release-candidate-packages` (artifact ID `10456377799`).
- Core tests: 298 passed on .NET 8 and 298 passed on .NET 10.
- EF Core and SQLite tests: 115 passed on .NET 10.
- Both samples, formatting verification, package/API validation, fresh package consumers, and artifact upload
  passed.

This run verifies that policy-aware capture preserves scalar mutations on tracked nested dependency targets
without requiring fake object-set mappings. It also verifies fail-closed convenience behavior for semantic
values generated by UPDATE, post-SQL manual reconciliation from immutable pre-save evidence, strict rejection
of unrelated drift, and explicit rejection of generated UPDATE changes to existing Raffinert identities. No
package was published, and no tag or GitHub release was created.

## Store-side referential actions and persistence authority gate

Verification date: 2026-09-16

Verified implementation head before closeout documentation: `d3458575c58f6f5c22594b2906526fb855aba3c7`

- CI run: [35122089225](https://github.com/Raffinert/Consistency/actions/runs/35122089225) — success.
- Release-candidate run: [35122105220](https://github.com/Raffinert/Consistency/actions/runs/35122105220) — success.
- Artifact: `release-candidate-packages` (artifact ID `10457238209`).
- Core tests: 300 passed on .NET 8 and 300 passed on .NET 10.
- EF Core and SQLite tests: 129 passed on .NET 10.
- Both samples, formatting verification, package/API validation, fresh package consumers, and artifact upload
  passed.

This run verifies pre-SQL rejection of direct and transitive store-side cascade or set-null paths into
consistency-managed state, zero-command rejection, six-path API parity, unrelated and owned cascade safety,
and supported fully tracked client-side cascade/set-null workflows. No package was published, and no tag or
GitHub release was created.
