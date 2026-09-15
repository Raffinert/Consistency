# Codex Implementation Plan — Post-Alpha Production Integration v1

Baseline: `main` at `611664b6d0d12d0ef544ff89e4f9cd8e39b82418`.

This plan is written for a weaker coding agent. Follow it mechanically. Do not redesign the runtime, do not add speculative features, do not publish packages, and do not collapse multiple tasks into one commit.

The previous Tasks 73–79 wave is complete. The remote release-candidate workflow passed successfully on `de1f6ba6bfcb40fe3626354601d127f176414dd0` and the repository recorded that verification on `611664b6d0d12d0ef544ff89e4f9cd8e39b82418`.

The goal of this wave is not more dependency-engine architecture. The core alpha contract is now proven. The goal is to make that contract safer for real production integration and ready for an intentional first package publication later.

---

# What is already complete — DO NOT redo it

The current repository already has all of the following. Preserve them unless a task below explicitly says otherwise:

- exact and conservative relation propagation;
- source-scoped derived/invariant DAG propagation;
- projected one/two-upstream dependencies with reverse fan-out indexes;
- touched object-set, relation, navigation, projection, derived, and invariant state journals/patches;
- explicit `RuntimeRollbackJournal` and `RuntimeForwardPatch` composition;
- non-binding `PreviewDetailed`;
- binding `PlanDetailed` + `Commit(plan)` without semantic re-execution;
- decision-time relation route evidence and upstream causal edges;
- complete prepared-result equivalence tests;
- prepared-execution failure atomicity tests;
- real SQLite transactional-outbox tests including generated identities and recovery boundaries;
- measured planning/fan-out scaling evidence;
- successful remote `release-candidate.yml` run;
- package version `0.1.0-alpha.1`;
- checked-in Public API approval files for both packages.

Do not create another alpha-hardening architecture wave.

---

# Verified remaining gaps

The agent must understand these exact gaps before editing code.

## Gap A — one architecture document shows the wrong durable-outbox API

`docs/architecture.md` currently says, in the same-database transactional-outbox section, to call `PreviewDetailed` after business `SaveChanges`, persist the returned impact plan, commit the database, and then commit runtime state.

That is incorrect.

`PreviewDetailed` returns `RuntimeApplyResult`. It is explicitly non-binding and does not retain a forward patch.

The binding same-transaction workflow is:

```text
capture unit
prepare when identities are stable
PlanDetailed
persist plan.Result-derived durable work
commit DB transaction
Commit(plan)
Dispatch
```

The README later describes this correctly. The architecture document must be brought into agreement with the public contract.

The early README paragraph that recommends `CommitDetailed(prepared, Causal)` for an outbox is also too easy to misread as the same-database atomic-outbox flow. That paragraph must distinguish:

- post-database detailed result / non-atomic external work; versus
- same-database atomic outbox, which requires `PlanDetailed`.

## Gap B — prebuilt-plan install timing is still not trustworthy

`benchmarks/PreparedImpactPlanning-Results.md` reports approximately:

```text
Commit(prebuilt plan), 10k   ~47 us
Commit(prebuilt plan), 100k ~136 us
```

but the benchmark uses `[InvocationCount(1)]` and recreates one complete runtime/plan in `IterationSetup`.

The document already says this timing is noisy. Allocation is flat, but the CPU result cannot be used as proof of population-independent installation.

Before changing runtime code, create a repeatable component benchmark that isolates:

- install-time validation;
- install rollback-journal capture;
- forward-patch application;
- rollback restoration used only by the benchmark harness.

Do not optimize because of one noisy number.

## Gap C — durable outbox consumers still have to manually project policy requests

`RuntimeApplyResult` contains public `RepairRequestInfo` and `ImmediateEvaluationRequestInfo` objects with both live `Source` references and optional durable identities.

A correct outbox consumer currently has to remember to:

1. reject non-durable requests;
2. extract `DefinitionKey`;
3. extract `DurableSourceIdentity`;
4. preserve the repair severity/reason;
5. avoid serializing runtime-only IDs, `Type`, or live object references.

The library should provide one strict durable projection for policy work so consumers do not repeatedly implement this boundary themselves.

Do NOT serialize the full causal graph in this wave.

## Gap D — first-release Public API baselines are not frozen

Both packages still have an effectively empty `PublicAPI.Shipped.txt` (`#nullable enable` only), while all current surface remains in `PublicAPI.Unshipped.txt`.

That is correct during development, but the repository has now passed its first alpha RC. Before the first package is actually published, the approved alpha surface must be copied/moved into the shipped baseline.

Do this only after the durable-policy API in this plan is finalized.

## Gap E — release verification does not enforce version/API-baseline consistency

`release-candidate.yml` verifies build, tests, formatting, packing, package payloads, consumers, and artifacts. It currently does not fail when:

- package version and expected release version drift;
- `PublicAPI.Shipped.txt` is empty for a release candidate;
- `PublicAPI.Unshipped.txt` still contains the entire initial surface;
- the packed `.nuspec` version differs between the two packages.

The next RC gate should verify those conditions without publishing anything.

---

# Non-negotiable rules

1. Do not change relation/derived/invariant semantics in this roadmap.
2. Do not add concurrency/thread-safety behavior.
3. Do not add a durable serialization format for the entire `RuntimeApplyResult` or causal graph.
4. Do not add DI, hosting, messaging, database-provider, or JSON package dependencies to the core library.
5. Do not add async repair callbacks in this roadmap.
6. Do not create NuGet tags, GitHub releases, or publish packages.
7. Keep `PreviewDetailed` non-binding.
8. Keep `PlanDetailed` binding.
9. Keep `Commit(plan)` free of semantic re-execution.
10. Durable policy-work DTOs must contain no live domain object reference and no runtime `Type` value.
11. Durable policy projection must fail explicitly if any included policy request is not durable.
12. Do not silently drop a non-durable request.
13. Preserve existing request ordering unless this plan explicitly says otherwise.
14. Keep core package compatible with net8.0 and net10.0.
15. Keep EF adapter on net10.0.
16. Run the exact validation commands for each task before continuing.
17. Make the requested small commits. Do not squash the whole roadmap into one commit.

---

# Task 80 — Correct and executable-document the binding outbox contract

## Goal

Make all public documentation tell the same truthful story about `PreviewDetailed`, `PlanDetailed`, database durability, runtime commit, and dispatch.

## Files to edit

- `docs/architecture.md`
- `README.md`
- `docs/purchase-order-example.md` only if it contains the same incorrect sequence
- `tests/package-consumers/EfNet10/Program.cs`
- optionally one focused EF integration test if documentation behavior is not already executable there

Do not touch runtime implementation in this task.

## 80.1 — Fix the architecture document

Find the same-database transactional-outbox paragraph in `docs/architecture.md`.

Replace the incorrect `PreviewDetailed` sequence with the binding plan sequence.

The text must distinguish these APIs exactly:

### `PreviewDetailed`

- executes prediction against the already-mutated prepared domain state;
- restores runtime-owned state;
- returns `RuntimeApplyResult` only;
- is diagnostic/non-binding;
- a later normal commit may execute semantic code again;
- must not be used when external durable work requires exact parity with the later runtime installation.

### `PlanDetailed`

- executes semantic classification/propagation once;
- restores runtime-owned state;
- returns `PreparedImpactPlan`;
- `plan.Result` is the exact detailed result associated with the retained forward patch;
- later `Commit(plan)` installs that patch without rerunning semantic model code;
- is the required API for same-database atomic outbox work that depends on exact result parity.

### Correct application-assigned-key sequence

Document exactly:

```csharp
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
unit.Prepare(runtime);

await using var transaction = await context.Database.BeginTransactionAsync();
await context.SaveChangesAsync();

var plan = unit.PlanDetailed(runtime, RuntimeImpactDetailLevel.Causal);
PersistDurablePolicyWork(context, plan?.Result);
await context.SaveChangesAsync();

await transaction.CommitAsync();
unit.Commit(runtime);
unit.Dispatch(runtime);
```

### Correct store-generated-key sequence

Document exactly:

```text
capture unit before first SaveChanges
begin DB transaction
first SaveChanges -> final generated keys/fixup
Prepare(runtime)
PlanDetailed(runtime)
persist durable work from plan.Result
second SaveChanges
commit DB transaction
Commit(runtime) / Commit(plan)
Dispatch
```

Do not claim that `Prepare` must happen before the first save when store-generated keys are still temporary/default.

## 80.2 — Clarify the early README `CommitDetailed` paragraph

The README currently says to use `CommitDetailed(prepared, Causal)` when committed impact must be written to an outbox before callbacks.

Keep that API documented, but explicitly state:

- it is appropriate when the database is already durable or the external work does not need the same database transaction;
- it is not the same-database atomic-outbox pattern;
- use `PlanDetailed` for exact parity before the DB transaction commits.

The later detailed `PlanDetailed` example should remain the canonical atomic-outbox example.

## 80.3 — Make the packed EF consumer demonstrate the canonical sequence

Update `tests/package-consumers/EfNet10/Program.cs` so the packed package consumer visibly exercises:

```text
CaptureUnitOfWork
Prepare or generated-key-safe first SaveChanges sequence
PlanDetailed
read plan.Result
commit DB transaction
unit.Commit(runtime)
unit.Dispatch(runtime)
```

Do not add a fake in-memory helper that bypasses the EF adapter.

The consumer must still compile against the packed packages only.

## 80.4 — Documentation assertions

Add comments in the package consumer showing:

```text
PreviewDetailed = non-binding diagnostic
PlanDetailed = binding outbox contract
```

Do not add these comments to production runtime code.

## Required validation

```bash
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
dotnet pack Raffinert.Relations.sln -c Release -o artifacts/packages
PACKAGE_SOURCE="$PWD/artifacts/packages"
dotnet restore tests/package-consumers/EfNet10 --source https://api.nuget.org/v3/index.json --source "$PACKAGE_SOURCE"
dotnet run --project tests/package-consumers/EfNet10 -c Release --no-restore
dotnet format Raffinert.Relations.sln --verify-no-changes
```

Use the PowerShell-equivalent local package-source path on Windows if necessary.

## STOP condition

Do not continue if any documentation page still describes `PreviewDetailed` as returning or retaining a binding plan.

## Commit boundary

Commit 1:

```text
docs: correct binding outbox workflow
```

Commit 2:

```text
test: exercise binding outbox workflow from packed ef package
```

---

# Task 81 — Make prepared-plan installation performance measurable before optimizing it

## Goal

Replace the noisy one-shot interpretation of `Commit(prebuilt plan)` with repeatable component measurements. Optimize only if the new measurements prove population-dependent work for one touched source.

## Files to inspect first

- `benchmarks/Raffinert.Relations.Benchmarks/PreparedImpactPlanningBenchmarks.cs`
- `benchmarks/PreparedImpactPlanning-Results.md`
- `src/Raffinert.Relations/Runtime/RelationRuntime.cs`
- `src/Raffinert.Relations/Runtime/MutationCommit.cs`
- `src/Raffinert.Relations/Policies/PolicyActions.cs`
- dependency touched-state implementation

## 81.1 — Keep the existing public one-shot benchmark, but stop using it as the primary scaling proof

Do not delete `PreparedPatchInstallBenchmarks`.

Rename methods only if needed for clarity, for example:

```text
CommitPlannedSummaryOneShot
CommitPlannedCausalOneShot
```

The results document must continue to label this benchmark as one-shot/noisy.

## 81.2 — Add repeatable internal component benchmark hooks

Add internal benchmark-only helpers to `RelationRuntime`. Keep them internal; do not put them in PublicAPI files.

Suggested responsibilities:

```csharp
internal object CaptureInstallRollbackForBenchmark(PreparedMutation prepared)
internal void ApplyForwardPatchAndRestoreForBenchmark(PreparedImpactPlan plan)
```

Exact names may differ.

Rules:

- helpers must use the real production capture/apply code paths;
- they must restore runtime state before returning so the same prepared plan can be measured repeatedly;
- they must not mark the plan or prepared mutation committed;
- they must not increment `Version`;
- they must not dispatch policy actions;
- they must not rerun semantic propagation when measuring patch application;
- if a helper cannot safely reuse the same plan, construct a small internal harness object rather than weakening production validation.

If adding benchmark-only helpers would distort production code, put the repeatable harness in the benchmark assembly using `InternalsVisibleTo` already available to tests/benchmarks. Do not expose public API.

## 81.3 — Add component benchmarks

Add benchmarks for at least:

```text
Capture install rollback journal
Apply forward patch + restore rollback journal
Validate prepared mutation/domain assumptions (if independently measurable)
Full one-shot Commit(plan) as reference only
```

Run with populations:

```text
10_000
100_000
```

The fixture must touch exactly one source and keep the touched dependency/relation/navigation bucket size constant.

Do not create one giant shared-key bucket whose size itself grows with population.

## 81.4 — Interpretation rule

If repeatable component measurements are approximately population-independent while the one-shot benchmark remains noisy, do NOT change runtime implementation. Update the result document explaining that the previous 47us/136us comparison was not a reliable scaling signal.

If a component measurably grows with unrelated population, find the exact loop/dictionary copy causing the growth and fix only that code path.

Acceptable evidence for a real scaling issue:

- allocations grow materially with unrelated population; or
- repeatable component mean at 100k is greater than 2x the 10k mean across repeated runs while touched work is constant.

Do not use one BenchmarkDotNet iteration as proof.

## 81.5 — Likely code paths to inspect in order

Inspect in this order before changing anything:

1. `ValidatePreparedMutation`
2. `PreparedMutation.ValidateDomainState`
3. `ValidateProjectedFinalState`
4. `CaptureInstallRollbackJournal`
5. `ImpactResolver.Resolve`
6. `_dependencyGraph.ResolveNavigationRoots`
7. `CaptureRollbackJournal` / `CapturePatchParts`
8. `ApplyForwardPatch`
9. touched relation/navigation/dependency restore methods

Do not optimize another subsystem if the benchmark does not hit it.

## Required tests

Add or retain a functional test proving the benchmark helper leaves:

```text
Version unchanged
plan.IsCommitted == false
prepared.IsCommitted == false
runtime relation/query results unchanged
runtime derived/invariant states unchanged
diagnostics unchanged
```

## Required validation

```bash
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0
dotnet run --project benchmarks/Raffinert.Relations.Benchmarks -c Release -- --filter '*Prepared*Install*'
dotnet format Raffinert.Relations.sln --verify-no-changes
```

## Commit boundary

Commit 3:

```text
bench: isolate prepared patch installation components
```

If and only if a real scaling defect is proven, make a separate Commit 4:

```text
perf: remove population-dependent prepared patch install work
```

If no defect is proven, do not create Commit 4. Update benchmark documentation in Commit 3 instead.

---

# Task 82 — Add a strict durable policy-work projection

## Goal

Let outbox/queue consumers obtain a data-only, JSON-friendly policy-work batch without touching live domain references, runtime `Type`, numeric IDs, or optional durability fields manually.

This is the only new public feature in this roadmap.

## Files to edit

Primary:

- `src/Raffinert.Relations/Policies/PolicyActions.cs`
- `src/Raffinert.Relations/PublicAPI.Unshipped.txt`
- focused core tests (new file is preferred)

Suggested test file:

- `tests/Raffinert.Relations.Tests/DurablePolicyWorkTests.cs`

## 82.1 — Add these public DTOs

Use these shapes unless compilation/naming conflicts require a very small adjustment:

```csharp
public sealed record DurableRepairRequestInfo(
    string DefinitionKey,
    DurableSourceIdentity Source,
    DependencySeverity Reason);

public sealed record DurableImmediateEvaluationRequestInfo(
    string DefinitionKey,
    DurableSourceIdentity Source);

public sealed record DurablePolicyWork(
    IReadOnlyList<DurableRepairRequestInfo> RepairRequests,
    IReadOnlyList<DurableImmediateEvaluationRequestInfo> ImmediateEvaluationRequests);
```

Do not add runtime numeric IDs.

Do not add `Type`.

Do not add live `Source` references.

Do not add callback delegates.

Do not include relation/derived/invariant causal graphs in this DTO.

## 82.2 — Add one strict projection method

Add to `RuntimeApplyResult`:

```csharp
public DurablePolicyWork GetDurablePolicyWork()
```

Behavior:

1. Preserve existing `RepairRequests` ordering.
2. Preserve existing `ImmediateEvaluationRequests` ordering.
3. For each repair request, call/use the same strict durability contract as `GetDurableIdentity()`.
4. For each immediate-evaluation request, do the same.
5. If any request is not durable, throw `InvalidOperationException`.
6. Do not silently omit invalid requests.
7. Do not partially return a batch.
8. Construct the full batch only after all entries validate.
9. Return read-only collections (`Array.AsReadOnly` or equivalent), not publicly mutable arrays/lists.

Error text must clearly state that all policy requests require:

```text
named invariant
named object set
canonically supported source key
```

Do not add a `Try...` API in this wave.

## 82.3 — Reuse existing durability logic

Do not duplicate canonical key serialization.

Reuse:

```text
RepairRequestInfo.GetDurableIdentity()
ImmediateEvaluationRequestInfo.GetDurableIdentity()
DurableSourceIdentityFactory
```

If small factoring is needed to avoid duplicated exception text, keep it internal.

## 82.4 — Required tests

Add tests covering all of these:

```text
Empty_result_returns_empty_durable_policy_work
Repair_request_projects_without_live_source_reference
Immediate_evaluation_projects_without_live_source_reference
Repair_reason_is_preserved
Request_order_is_preserved
Composite_key_parts_are_preserved
Guid_key_is_preserved
Date_and_numeric_canonical_key_parts_are_preserved_if already supported
Unnamed_invariant_throws
Unnamed_object_set_throws
Unsupported_key_shape_throws
One_invalid_request_rejects_the_entire_batch
Returned_collections_are_not_mutable_through_the_public_contract
```

For the “no live reference” assertion, inspect only the durable DTO public properties. Do not use reflection against unrelated runtime internals.

## 82.5 — JSON-neutral contract test

In tests only, use `System.Text.Json` from the framework to:

1. serialize `DurablePolicyWork`;
2. deserialize it;
3. compare all logical values.

The core package must not add a `System.Text.Json` package reference and must not add serializer attributes unless absolutely necessary.

The JSON test proves that the DTO is serializer-friendly. It does not define a forever-stable wire format yet.

## STOP condition

Do not continue if the durable DTO contains `object`, `Type`, live runtime IDs, or callbacks.

## Required validation

Run both core TFMs and formatting.

## Commit boundary

Commit 5:

```text
feat: project runtime policy work to durable data
```

Commit 6:

```text
test: prove durable policy work serialization contract
```

---

# Task 83 — Use durable policy work in EF outbox and packed-package consumers

## Goal

Prove that the new durable projection is the normal persistence boundary for real database/outbox integrations.

## Files to edit

- `tests/Raffinert.Relations.EntityFrameworkCore.Tests/EntityFrameworkCoreSqliteTests.cs`
- `tests/package-consumers/CoreNet8/Program.cs`
- `tests/package-consumers/CoreNet10/Program.cs`
- `tests/package-consumers/EfNet10/Program.cs`
- `README.md`
- `docs/architecture.md`

## 83.1 — Update SQLite outbox tests

Find the successful binding-plan outbox tests.

Replace hand-written extraction similar to:

```text
plan.Result.RepairRequests
request.GetDurableIdentity()
```

with:

```csharp
var durableWork = plan.Result.GetDurablePolicyWork();
```

Persist outbox rows from `durableWork` only.

For generated-key scenarios, assert the outbox payload is built from:

```text
DurablePolicyWork
 -> DurableSourceIdentity
 -> KeyParts
```

not from the live entity's `Id` property.

It is fine to compare persisted values to the entity IDs afterwards for verification.

## 83.2 — Add one failure test at the integration boundary

Create a model with an intentionally unnamed invariant or object set and produce a policy request.

Inside the explicit DB transaction:

1. execute the business save;
2. create the binding plan;
3. call `GetDurablePolicyWork()`;
4. verify it throws before an outbox row is added;
5. rollback the DB transaction;
6. verify runtime version is unchanged;
7. verify business/outbox rows are absent.

This proves nondurable policy work fails before database durability.

## 83.3 — Packed core consumers

Update both CoreNet8 and CoreNet10 consumers to:

- configure a named object set;
- configure a named repair invariant;
- create one repair request;
- call `GetDurablePolicyWork()`;
- verify the expected definition key/key part/reason.

Do not duplicate a large sample. Keep package consumers small.

## 83.4 — Packed EF consumer

Extend the canonical binding-outbox flow from Task 80 so the persisted payload originates from `GetDurablePolicyWork()`.

This is important: the package consumer must compile against packed packages, so it proves the new API is actually included in the `.nupkg` public surface.

## 83.5 — Documentation

Use `GetDurablePolicyWork()` in the same-database outbox examples.

Clearly state:

- `RuntimeApplyResult` is rich in-process diagnostic/impact data;
- `DurablePolicyWork` is the strict data-only projection intended for durable policy scheduling;
- full causal results are not declared a stable wire format by this API.

## Required validation

```bash
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
dotnet pack Raffinert.Relations.sln -c Release -o artifacts/packages
```

Then fresh-cache restore/run all three packed consumers exactly as CI does.

## Commit boundary

Commit 7:

```text
test: persist durable policy work through sqlite outbox
```

Commit 8:

```text
test: exercise durable policy work from packed packages
```

Commit 9:

```text
docs: document durable policy work boundary
```

---

# Task 84 — Audit and freeze the first shipped Public API baseline

## Goal

Convert the approved first-alpha API from “all unshipped” to a real shipped baseline for both packages.

Do this only after Tasks 80–83 are green.

## Files to edit

Core:

- `src/Raffinert.Relations/PublicAPI.Shipped.txt`
- `src/Raffinert.Relations/PublicAPI.Unshipped.txt`

EF:

- `src/Raffinert.Relations.EntityFrameworkCore/PublicAPI.Shipped.txt`
- `src/Raffinert.Relations.EntityFrameworkCore/PublicAPI.Unshipped.txt`

Also inspect:

- `RELEASING.md`
- `CHANGELOG.md`

## 84.1 — Review before moving entries

Before changing approval files, build the solution once and inspect the current `Unshipped` surfaces.

Do not redesign APIs during this task.

The review is only to catch obvious accidental public members such as:

- testing-only helpers accidentally marked public;
- mutable setters that were intended internal;
- implementation types that should already have been internal;
- benchmark/testing APIs;
- duplicate APIs added accidentally in the latest wave.

If you find a suspicious API, STOP and document it in the commit/plan notes instead of silently breaking the public contract. Do not remove it automatically.

## 84.2 — Move the approved surface

For each package:

1. preserve the `#nullable enable` header;
2. copy/move every approved current API entry from `PublicAPI.Unshipped.txt` into `PublicAPI.Shipped.txt`;
3. leave `PublicAPI.Unshipped.txt` with only the required header unless the analyzer itself requires another generated marker;
4. sort/format according to the analyzer's existing output conventions;
5. build and ensure PublicApiAnalyzers reports no errors.

Do not hand-edit method signatures beyond moving the approval lines.

## 84.3 — Update release documentation

Update `RELEASING.md` to state:

- the first published version establishes the initial shipped baseline;
- after that, new APIs go into `Unshipped` until the next release;
- removed/changed shipped APIs require explicit compatibility/version review.

Update `CHANGELOG.md` only to mention the durable policy projection and release-contract cleanup. Do not claim NuGet publication.

## Required validation

```bash
dotnet clean Raffinert.Relations.sln
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
```

## STOP condition

After this task:

```text
core PublicAPI.Shipped.txt must contain real API entries
EF PublicAPI.Shipped.txt must contain real API entries
both Unshipped files must be empty except analyzer header/required markers
```

## Commit boundary

Commit 10:

```text
build: freeze first alpha public api baseline
```

---

# Task 85 — Harden the release-candidate gate for version and API-baseline consistency

## Goal

Make the manual RC workflow reject an incorrectly prepared first release before artifact upload.

## Files to edit

- `.github/workflows/release-candidate.yml`
- optionally `eng/` script(s) if keeping validation readable requires them
- `RELEASING.md`
- package consumer projects only if version checks require small changes

Do not add NuGet publication credentials or a publish step.

## 85.1 — Verify expected package version

The repository currently uses:

```xml
<Version>0.1.0-alpha.1</Version>
```

in `Directory.Build.props`.

Add a workflow step that reads the repository version and asserts both generated `.nupkg` files use exactly that version.

Do not hardcode package versions in multiple scripts if the value can be read from `Directory.Build.props`.

Suggested implementation options:

- a small PowerShell script using XML parsing and `System.IO.Compression`; or
- an `eng/VerifyReleaseCandidate.ps1` script called by the workflow.

Prefer one reusable script over a large inline YAML block if validation grows beyond ~30 lines.

## 85.2 — Verify shipped Public API baselines

The RC script must assert:

```text
src/Raffinert.Relations/PublicAPI.Shipped.txt
src/Raffinert.Relations.EntityFrameworkCore/PublicAPI.Shipped.txt
```

contain at least one real API line beyond `#nullable enable`.

For this first-alpha gate, also assert the two `PublicAPI.Unshipped.txt` files contain no API entries after Task 84.

Do not make “Unshipped must always be empty” a permanent generic release rule without documenting that later prereleases may intentionally include new API before the next baseline move. Phrase the check as the current alpha.1 release-preparation rule or parameterize it.

## 85.3 — Verify core/EF package dependency version alignment

Inspect the EF package `.nuspec` and assert its dependency on `Raffinert.Relations` resolves to the same alpha package version being produced.

Do not allow the EF package to accidentally reference a previous published core version when both are produced together.

## 85.4 — Verify package metadata already expected by the project

Keep existing README/CHANGELOG checks and add lightweight assertions for:

```text
license expression
repository URL
repository commit metadata when CI supplies it
symbol package count
expected target-framework assets
```

Do not build a general NuGet validator from scratch. Use existing package-validation tooling plus simple archive/nuspec checks.

## 85.5 — Keep publication impossible

The workflow must retain:

```yaml
permissions:
  contents: read
```

and must contain no:

```text
dotnet nuget push
nuget push
GitHub release creation
tag push
contents: write
packages: write
```

Add a short YAML comment near the artifact upload saying publication is intentionally manual for alpha.

## Required local validation

Run the validation script directly against locally packed packages before relying on Actions.

Then run normal build/tests/format/pack consumers.

## Commit boundary

Commit 11:

```text
build: harden alpha release candidate verification
```

---

# Task 86 — Execute and record the post-alpha-production RC gate

## Goal

Prove the new durable integration surface, shipped API baseline, and hardened release checks on GitHub Actions.

## 86.1 — Local gate first

Before dispatching remote RC, run:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
dotnet format Raffinert.Relations.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Relations.sln -c Release --no-build -o artifacts/packages
```

Run the release validation script and all packed-package consumers.

## 86.2 — Dispatch remote `release-candidate.yml`

Dispatch the workflow on the exact reviewed `main` head.

Do not dispatch against an earlier implementation commit.

Record:

```text
run ID
head SHA
start/end time
result
artifact ID/name
validated package version
core test counts
EF test count
```

## 86.3 — Failure handling

If remote RC fails:

1. do not mark the roadmap complete;
2. do not update docs claiming release readiness;
3. inspect the exact failed step;
4. fix the smallest issue;
5. rerun local relevant checks;
6. commit the fix separately;
7. dispatch RC again on the new exact head.

## 86.4 — Verification documentation

Update `docs/release-candidate-verification.md` with a new section for this post-alpha-production gate.

Preserve the previous successful RC record as history.

The new section must explicitly record that the run verified:

```text
DurablePolicyWork public API
binding EF outbox consumer path
shipped Public API baselines
empty first-release Unshipped baselines
package version alignment
core/EF package dependency alignment
packed consumers
no publication/tag/release
```

## 86.5 — Roadmap index

Only after the remote RC succeeds, update `docs/roadmaps/README.md` so this plan is marked completed.

If RC has not succeeded, keep this plan active.

## Commit boundary

Commit 12:

```text
docs: record post-alpha production rc verification
```

Commit 13:

```text
docs: complete post-alpha production integration roadmap
```

---

# Final definition of done

This roadmap is complete only when all of the following are true:

```text
[ ] architecture and README agree on PreviewDetailed vs PlanDetailed
[ ] packed EF consumer exercises the canonical binding outbox flow
[ ] prepared-plan install has repeatable component benchmark evidence
[ ] no runtime optimization was made solely from the old one-shot noisy timing
[ ] RuntimeApplyResult.GetDurablePolicyWork() exists
[ ] durable policy DTOs contain no object/Type/runtime numeric IDs/callbacks
[ ] nondurable request rejects the whole durable batch
[ ] JSON roundtrip test passes on net8 and net10 core tests
[ ] SQLite binding outbox persists from DurablePolicyWork
[ ] generated-key outbox payload uses durable result identity, not live entity access
[ ] CoreNet8 packed consumer uses DurablePolicyWork
[ ] CoreNet10 packed consumer uses DurablePolicyWork
[ ] EfNet10 packed consumer uses DurablePolicyWork in PlanDetailed flow
[ ] core PublicAPI.Shipped.txt contains the approved alpha surface
[ ] EF PublicAPI.Shipped.txt contains the approved alpha surface
[ ] both first-release PublicAPI.Unshipped.txt files contain no API entries
[ ] release-candidate workflow verifies package version alignment
[ ] release-candidate workflow verifies shipped API baseline
[ ] EF package dependency version matches produced core package version
[ ] remote release-candidate workflow succeeds on exact reviewed head
[ ] verification doc records run/artifact/head/version
[ ] no package was published
[ ] no tag was created
[ ] no GitHub release was created
```

---

# Recommended commit sequence

Do not squash these into one giant change.

```text
1.  docs: correct binding outbox workflow
2.  test: exercise binding outbox workflow from packed ef package
3.  bench: isolate prepared patch installation components
4.  perf: remove population-dependent prepared patch install work   # only if benchmark proves it
5.  feat: project runtime policy work to durable data
6.  test: prove durable policy work serialization contract
7.  test: persist durable policy work through sqlite outbox
8.  test: exercise durable policy work from packed packages
9.  docs: document durable policy work boundary
10. build: freeze first alpha public api baseline
11. build: harden alpha release candidate verification
12. docs: record post-alpha production rc verification
13. docs: complete post-alpha production integration roadmap
```

If Commit 4 is not justified by repeatable benchmark evidence, skip it. Do not invent a performance fix just to satisfy numbering.

---

# Explicit future work — NOT part of this roadmap

Do not implement these while executing Tasks 80–86:

- full durable causal-graph serialization;
- async repair callback/dispatch API;
- built-in message bus adapters;
- built-in outbox database schema;
- dependency injection integration;
- thread-safe/concurrent `RelationRuntime`;
- distributed runtime coordination;
- new access-plan families;
- hypothetical pre-mutation simulation;
- package publication;
- GitHub release creation;
- signing/tagging automation.

Those should be separate post-alpha decisions after the first public API baseline is intentionally frozen.
