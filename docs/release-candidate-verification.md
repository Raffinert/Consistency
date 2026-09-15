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
