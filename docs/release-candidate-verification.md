# First-alpha release-candidate verification

Verification date: 2026-09-14  
Verified commit before documentation: `c355557f1865f7ab3de08487c38ac39be910e939`

Local release-style verification completed successfully:

- Release solution build: passed with zero warnings and errors.
- Core tests on .NET 8: 210 passed.
- Core tests on .NET 10: 210 passed.
- EF Core and SQLite tests on .NET 10: 26 passed.
- `dotnet format --verify-no-changes`: passed (workspace loader emitted its existing informational warning).
- Core and EF packages plus symbol packages: created successfully.
- Fresh-cache packed consumers for CoreNet8, CoreNet10, and EfNet10: passed.
- No package publication, release, or tag was performed.

The manual GitHub Actions release-candidate workflow was not dispatched from this environment because the
GitHub CLI is unavailable. Its `workflow_dispatch` definition remains the authoritative remote RC gate and
must be run before publication. This document does not claim that remote workflow has passed.

The current alpha contract distinguishes non-binding `PreviewDetailed` from binding `PlanDetailed`.
Durability-sensitive integrations persist data from `PreparedImpactPlan.Result`, commit the database, then
install the plan and dispatch callbacks. Database-success/runtime-failure recovery requires rebuilding or
reconciling runtime state from the authoritative database and durable outbox.
