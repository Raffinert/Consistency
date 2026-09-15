# Codex Mechanical Rename Plan v2 — `Raffinert.Relations` → `Raffinert.Consistency`

Baseline: `ccea9fde5a5e003decd1b82af9e8c9db93294020`

Tasks: **116–122**

This plan supersedes `docs/codex-plan-rename-to-raffinert-consistency.md`.

It is written for a weaker coding agent. Follow it mechanically. This is a product/package/namespace rename plus a small set of explicitly listed product-level type renames. It is **not** permission to rename every symbol containing the word `Relation`.

## Known publication fact — do not re-check and do not stop

The user has explicitly confirmed:

```text
No Raffinert.Relations package has ever been published.
No Raffinert.Relations.EntityFrameworkCore package has ever been published.
There are no external NuGet consumers that require compatibility aliases.
```

Therefore:

```text
DO NOT query NuGet to decide whether the rename may proceed.
DO NOT stop because of package-publication uncertainty.
DO NOT create compatibility packages.
DO NOT create type-forwarding packages.
DO NOT keep old package IDs as metapackages.
DO NOT add obsolete wrappers under the old namespace.
```

Perform a **clean pre-publication rename**.

The desired public identity is:

```text
Product:      Raffinert.Consistency
Core package: Raffinert.Consistency
EF package:   Raffinert.Consistency.EntityFrameworkCore
Repository:   https://github.com/Raffinert/Consistency
Tagline:      Incremental consistency for .NET object models.
```

The current repository may advance after this plan is committed. Before editing code, inspect commits after the baseline and preserve all newer work.

---

# 0. Semantic rule: rename the product, not the `Relation` concept

Do **not** perform a blind global replacement of `Relation` with `Consistency`.

These are real domain concepts and keep their current names:

```text
Relation<TLeft, TRight>
RelationBuilder<TLeft, TRight>
RelationDefinition<...>
IRelationDefinition
RelationImpact
RelationImpactCauseKind
RelationDependencyCause
RelationModelDiagnostics
RelationAccessPlan / RelationAccessPlanKind
RelationPropagationPlan / RelationPropagationPlanKind
RelationMaterializationMode
RelationRuntimeState<TLeft, TRight>
IRelationRuntimeState
RelationDelta
all methods named Relation(...) that define/query a binary relation
```

The following names use `Relation` as the old product/model/runtime prefix. Rename them exactly:

| Old | New |
| --- | --- |
| `RelationModelBuilder` | `ConsistencyModelBuilder` |
| `CompiledRelationModel` | `CompiledConsistencyModel` |
| `RelationRuntime` | `ConsistencyRuntime` |
| `RelationUnitOfWork` | `ConsistencyUnitOfWork` |
| `RelationUnitOfWorkMappings` | `ConsistencyUnitOfWorkMappings` |
| `RelationRuntimeSynchronizationException` | `ConsistencyRuntimeSynchronizationException` |
| `RelationEfCoreMappings` | `ConsistencyEfCoreMappings` |
| `RelationEfCoreSaveBehavior` | `ConsistencySaveBehavior` |
| `RelationEfCoreConsistencyOptions` | `ConsistencySaveOptions` |
| `RelationConsistencySaveChangesInterceptor` | `ConsistencySaveChangesInterceptor` |
| `RelationConsistencyDbContextExtensions` | `ConsistencyDbContextExtensions` |
| `RelationInvariantViolationException` | `ConsistencyInvariantViolationException` |
| `RelationMaterializationSourceNotTrackedException` | `ConsistencyMaterializationSourceNotTrackedException` |
| `RelationUnsupportedTransactionException` | `ConsistencyUnsupportedTransactionException` |
| `RelationStoreGeneratedKeyRequiresManualWorkflowException` | `ConsistencyStoreGeneratedKeyRequiresManualWorkflowException` |

Do not invent more public renames unless compilation proves a directly dependent product-level name must change.

Important exact-token warning:

```text
RelationRuntime -> ConsistencyRuntime
```

must **not** turn:

```text
RelationRuntimeState -> ConsistencyRuntimeState
IRelationRuntimeState -> IConsistencyRuntimeState
```

Those types describe one binary relation and keep their names.

---

# 1. Target repository layout

Final active paths:

```text
Raffinert.Consistency.sln

src/Raffinert.Consistency/
    Raffinert.Consistency.csproj

src/Raffinert.Consistency.EntityFrameworkCore/
    Raffinert.Consistency.EntityFrameworkCore.csproj

tests/Raffinert.Consistency.Tests/
    Raffinert.Consistency.Tests.csproj

tests/Raffinert.Consistency.EntityFrameworkCore.Tests/
    Raffinert.Consistency.EntityFrameworkCore.Tests.csproj

benchmarks/Raffinert.Consistency.Benchmarks/
    Raffinert.Consistency.Benchmarks.csproj

samples/Raffinert.Consistency.PurchaseOrderSample/
    Raffinert.Consistency.PurchaseOrderSample.csproj

samples/Raffinert.Consistency.EntityFrameworkCore.Sample/
    Raffinert.Consistency.EntityFrameworkCore.Sample.csproj
```

Keep package-consumer folder names:

```text
tests/package-consumers/CoreNet8
tests/package-consumers/CoreNet10
tests/package-consumers/EfNet10
```

Only update package references/usings inside them.

The EF sample becomes:

```text
Raffinert.Consistency.EntityFrameworkCore.Sample
```

not `...ConsistencySample`.

---

# 2. Namespace/package rules

Broad root namespace replacement is allowed:

```text
Raffinert.Relations -> Raffinert.Consistency
```

because that token is product identity.

This does **not** authorize replacing standalone `Relation` words/types.

Package IDs after rename:

```xml
<PackageId>Raffinert.Consistency</PackageId>
<PackageId>Raffinert.Consistency.EntityFrameworkCore</PackageId>
```

Default assemblies must become:

```text
Raffinert.Consistency.dll
Raffinert.Consistency.EntityFrameworkCore.dll
Raffinert.Consistency.Tests.dll
Raffinert.Consistency.EntityFrameworkCore.Tests.dll
Raffinert.Consistency.Benchmarks.dll
```

Do not preserve old assembly aliases.

Keep current version in `Directory.Build.props`. The rename itself does not require a version bump.

---

# 3. Historical material

Do not rewrite historical roadmap provenance just to satisfy branding searches.

These may retain old names:

```text
docs/codex-plan-*.md that predate the rename
docs/roadmaps/archive/**
historical benchmark result prose tied to old commit SHAs
historical CHANGELOG entries
```

These current surfaces must use the new identity:

```text
README.md
RELEASING.md
docs/architecture.md
docs/ef-core-consistency.md
docs/optimizer-policy.md
docs/purchase-order-example.md
docs/release-candidate-verification.md
.github/workflows/**
eng/VerifyReleaseCandidate.ps1
all active csproj/sln/source/test/sample/benchmark files
package-consumer projects and source
```

---

# Task 116 — Baseline and rename inventory

## Goal

Collect the current rename surface before modifying code. There is **no publication preflight** in this task.

## 116.1 Inspect repository movement

Run:

```bash
git rev-parse HEAD
git log --oneline ccea9fde5a5e003decd1b82af9e8c9db93294020..HEAD
```

If newer commits exist, inspect them and incorporate any new files into the rename inventory. Do not reset or discard them.

## 116.2 Verify green baseline

Use the current old paths before the rename:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
```

Do not start the rename from a red baseline.

## 116.3 Inventory actual references

Run:

```bash
git grep -n "Raffinert\.Relations" -- ':!docs/roadmaps/archive/**'
git ls-files '*Raffinert.Relations*'
git grep -n -E '\b(RelationModelBuilder|CompiledRelationModel|RelationRuntime|RelationUnitOfWork|RelationUnitOfWorkMappings|RelationRuntimeSynchronizationException|RelationEfCoreMappings|RelationEfCoreSaveBehavior|RelationEfCoreConsistencyOptions|RelationConsistencySaveChangesInterceptor|RelationConsistencyDbContextExtensions|RelationInvariantViolationException|RelationMaterializationSourceNotTrackedException|RelationUnsupportedTransactionException|RelationStoreGeneratedKeyRequiresManualWorkflowException)\b'
```

Account for every active-code/build hit.

## Acceptance

```text
newer commits inspected
baseline green
rename inventory collected
no rename edits yet
```

There is no NuGet lookup and no publication-related stop condition.

---

# Task 117 — Rename solution, projects, namespaces, package IDs, and references

Use `git mv` so history follows the moves.

Equivalent commands:

```bash
git mv Raffinert.Relations.sln Raffinert.Consistency.sln

git mv src/Raffinert.Relations src/Raffinert.Consistency
git mv src/Raffinert.Consistency/Raffinert.Relations.csproj src/Raffinert.Consistency/Raffinert.Consistency.csproj

git mv src/Raffinert.Relations.EntityFrameworkCore src/Raffinert.Consistency.EntityFrameworkCore
git mv src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Relations.EntityFrameworkCore.csproj src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Consistency.EntityFrameworkCore.csproj

git mv tests/Raffinert.Relations.Tests tests/Raffinert.Consistency.Tests
git mv tests/Raffinert.Consistency.Tests/Raffinert.Relations.Tests.csproj tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj

git mv tests/Raffinert.Relations.EntityFrameworkCore.Tests tests/Raffinert.Consistency.EntityFrameworkCore.Tests
git mv tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj

git mv benchmarks/Raffinert.Relations.Benchmarks benchmarks/Raffinert.Consistency.Benchmarks
git mv benchmarks/Raffinert.Consistency.Benchmarks/Raffinert.Relations.Benchmarks.csproj benchmarks/Raffinert.Consistency.Benchmarks/Raffinert.Consistency.Benchmarks.csproj

git mv samples/Raffinert.Relations.PurchaseOrderSample samples/Raffinert.Consistency.PurchaseOrderSample
git mv samples/Raffinert.Consistency.PurchaseOrderSample/Raffinert.Relations.PurchaseOrderSample.csproj samples/Raffinert.Consistency.PurchaseOrderSample/Raffinert.Consistency.PurchaseOrderSample.csproj

git mv samples/Raffinert.Relations.EntityFrameworkCore.ConsistencySample samples/Raffinert.Consistency.EntityFrameworkCore.Sample
git mv samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Relations.EntityFrameworkCore.ConsistencySample.csproj samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj
```

Adapt paths only when newer commits actually changed the tree.

## 117.1 Solution

In `Raffinert.Consistency.sln`, change display names and paths but preserve existing project GUIDs.

Expected project names:

```text
Raffinert.Consistency
Raffinert.Consistency.Tests
Raffinert.Consistency.Benchmarks
Raffinert.Consistency.EntityFrameworkCore
Raffinert.Consistency.EntityFrameworkCore.Tests
Raffinert.Consistency.PurchaseOrderSample
Raffinert.Consistency.EntityFrameworkCore.Sample
```

## 117.2 Project references

Update every `<ProjectReference>` to the renamed project paths.

Run:

```bash
git grep -n "Raffinert.Relations.*csproj" -- '*.csproj' '*.sln' '*.props' '*.targets'
```

Expected: no active hits.

## 117.3 Package metadata

Core:

```xml
<PackageId>Raffinert.Consistency</PackageId>
<Description>Incremental consistency for .NET object models: relations, derived state, invariants, and impact planning.</Description>
<PackageTags>consistency;dependency-graph;incremental;invariants;relations;object-model</PackageTags>
```

EF:

```xml
<PackageId>Raffinert.Consistency.EntityFrameworkCore</PackageId>
<Description>EF Core consistency integration for Raffinert.Consistency.</Description>
<PackageTags>consistency;entity-framework-core;ef-core;invariants;derived-state;change-tracking</PackageTags>
```

Keep `RepositoryUrl=https://github.com/Raffinert/Relations` until the repository itself is renamed in Task 122.

## 117.4 Namespace replacement

Across active `.cs` files replace the root token:

```text
Raffinert.Relations -> Raffinert.Consistency
```

This includes namespaces, usings, fully-qualified types, tests, samples and benchmarks.

Do not rename standalone `Relation*` concepts here.

## 117.5 InternalsVisibleTo

Core `AssemblyInfo.cs` must become:

```csharp
[assembly: InternalsVisibleTo("Raffinert.Consistency.Tests")]
[assembly: InternalsVisibleTo("Raffinert.Consistency.EntityFrameworkCore.Tests")]
[assembly: InternalsVisibleTo("Raffinert.Consistency.Benchmarks")]
[assembly: InternalsVisibleTo("Raffinert.Consistency.EntityFrameworkCore")]
```

## 117.6 Package consumers

Change references to:

```xml
<PackageReference Include="Raffinert.Consistency" Version="0.1.0-alpha.1" />
<PackageReference Include="Raffinert.Consistency.EntityFrameworkCore" Version="0.1.0-alpha.1" />
```

Update usings/namespaces in their `Program.cs` files.

## Commit

```text
refactor: rename projects and namespaces to Raffinert.Consistency
```

Do not publish anything.

---

# Task 118 — Rename product-level core API

## Exact renames

```text
RelationModelBuilder  -> ConsistencyModelBuilder
CompiledRelationModel -> CompiledConsistencyModel
RelationRuntime       -> ConsistencyRuntime
```

Rename files:

```bash
git mv src/Raffinert.Consistency/Model/RelationModelBuilder.cs src/Raffinert.Consistency/Model/ConsistencyModelBuilder.cs
git mv src/Raffinert.Consistency/Model/CompiledRelationModel.cs src/Raffinert.Consistency/Model/CompiledConsistencyModel.cs
git mv src/Raffinert.Consistency/Runtime/RelationRuntime.cs src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
```

Update partial declarations in other files such as `MutationCommit.cs`.

Expected API:

```csharp
var model = new ConsistencyModelBuilder();
var left = model.Objects<Left>().Key(x => x.Id);
var right = model.Objects<Right>().Key(x => x.Id);
var relation = model.Relation(left, right).Where((l, r) => l.Id == r.LeftId);
CompiledConsistencyModel compiled = model.Build();
ConsistencyRuntime runtime = compiled.CreateRuntime();
```

Keep:

```text
Relation<TLeft,TRight>
RelationBuilder<TLeft,TRight>
RelationRuntimeState<TLeft,TRight>
IRelationRuntimeState
RelationImpact
RelationModelDiagnostics
```

## Public API baselines

Update both:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
```

First namespace prefix:

```text
Raffinert.Relations. -> Raffinert.Consistency.
```

Then exact product-level names above.

Do not suppress PublicApiAnalyzer diagnostics.

## Gate

```bash
dotnet build Raffinert.Consistency.sln -c Release
```

## Commit

```text
refactor: rename consistency engine entry points
```

---

# Task 119 — Rename EF Core product-level API

Apply exactly:

```text
RelationUnitOfWork                              -> ConsistencyUnitOfWork
RelationUnitOfWorkMappings                      -> ConsistencyUnitOfWorkMappings
RelationRuntimeSynchronizationException         -> ConsistencyRuntimeSynchronizationException
RelationEfCoreMappings                          -> ConsistencyEfCoreMappings
RelationEfCoreSaveBehavior                      -> ConsistencySaveBehavior
RelationEfCoreConsistencyOptions                -> ConsistencySaveOptions
RelationConsistencySaveChangesInterceptor       -> ConsistencySaveChangesInterceptor
RelationConsistencyDbContextExtensions          -> ConsistencyDbContextExtensions
RelationInvariantViolationException             -> ConsistencyInvariantViolationException
RelationMaterializationSourceNotTrackedException -> ConsistencyMaterializationSourceNotTrackedException
RelationUnsupportedTransactionException         -> ConsistencyUnsupportedTransactionException
RelationStoreGeneratedKeyRequiresManualWorkflowException -> ConsistencyStoreGeneratedKeyRequiresManualWorkflowException
```

All runtime parameters use `ConsistencyRuntime`.

Rename misleading files:

```bash
git mv src/Raffinert.Consistency.EntityFrameworkCore/RelationEfCoreMappings.cs src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreMappings.cs
git mv src/Raffinert.Consistency.EntityFrameworkCore/RelationConsistencySaveChangesInterceptor.cs src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySaveChangesInterceptor.cs
git mv src/Raffinert.Consistency.EntityFrameworkCore/RelationConsistency.cs src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs
```

Do not split `ChangeTrackerAdapter.cs` merely to match renamed contained types.

Target API:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Materialize(availableQuantity, x => x.AvailableQuantity)
    .Enforce(availability);

await db.SaveChangesConsistentlyAsync(runtime, mappings, cancellationToken: cancellationToken);

new ConsistencySaveChangesInterceptor(runtime, mappings, new ConsistencySaveOptions());
```

Manual path:

```csharp
var mappings = new ConsistencyUnitOfWorkMappings().Map(lines);
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
```

Method names remain:

```text
SaveChangesConsistently
SaveChangesConsistentlyAsync
CaptureUnitOfWork
Prepare
PlanDetailed
Commit
Dispatch
```

Update EF `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` with new namespace and exact type names.

**Do not add old-name aliases or compatibility wrappers.** The old package/API was never published.

## Gate

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
```

## Commit

```text
refactor: rename ef consistency public surface
```

---

# Task 120 — Update branding, docs, workflows, release tooling, and smoke consumers

## README

Opening must become:

```md
# Raffinert.Consistency

**Incremental consistency for .NET object models.**

Raffinert.Consistency lets you declare relations, derived values, and invariants as expression trees...
```

Use `Raffinert.Consistency` for the product. Keep conceptual words such as `relation`, `relation predicate`, and `relation membership` when they describe real relation semantics.

Update examples to `ConsistencyModelBuilder`, `ConsistencyEfCoreMappings`, and `ConsistencyRuntime` where explicit.

Update sample link to:

```text
samples/Raffinert.Consistency.EntityFrameworkCore.Sample
```

## Current docs

Update:

```text
RELEASING.md
docs/architecture.md
docs/ef-core-consistency.md
docs/optimizer-policy.md
docs/purchase-order-example.md
docs/release-candidate-verification.md
```

Add a new top CHANGELOG note:

```text
Renamed product/package/namespace from Raffinert.Relations to Raffinert.Consistency before first publication/stable release.
Binary relation concepts retain Relation* terminology.
```

Do not rewrite historical changelog entries.

## Workflows

Update `.github/workflows/ci.yml` and `release-candidate.yml` to renamed solution/project paths.

They must use:

```text
Raffinert.Consistency.sln
tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj
samples/Raffinert.Consistency.PurchaseOrderSample/Raffinert.Consistency.PurchaseOrderSample.csproj
samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj
```

## Release verification script

Update `eng/VerifyReleaseCandidate.ps1` paths and expected package IDs:

```powershell
$coreId = 'Raffinert.Consistency'
$efId = 'Raffinert.Consistency.EntityFrameworkCore'
```

Expected assets:

```text
lib/net8.0/Raffinert.Consistency.dll
lib/net10.0/Raffinert.Consistency.dll
lib/net10.0/Raffinert.Consistency.EntityFrameworkCore.dll
```

Keep exact EF->core package dependency/version validation.

Until Task 122, repository URL assertion remains the actual URL:

```text
https://github.com/Raffinert/Relations
```

## Package smoke consumers

After packing, consumer projects must restore only:

```text
Raffinert.Consistency
Raffinert.Consistency.EntityFrameworkCore
```

from local artifacts plus nuget.org.

There is no requirement to test old package compatibility because the old package was never published.

## Benchmark project

Rename project/namespace to `Raffinert.Consistency.Benchmarks` but keep relation-specific benchmark class names such as `RelationBenchmarks`.

## Commit

```text
docs: update branding and release tooling for Raffinert.Consistency
```

---

# Task 121 — Full rename proof and residue gate

Run:

```bash
dotnet restore Raffinert.Consistency.sln
dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.PurchaseOrderSample/Raffinert.Consistency.PurchaseOrderSample.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Consistency.sln -c Release --no-build -o artifacts/packages
```

Run all three package-consumer smoke tests exactly as CI does.

## Package inspection

Expected two `.nupkg` + two `.snupkg` packages.

Prove:

```text
core id = Raffinert.Consistency
EF id = Raffinert.Consistency.EntityFrameworkCore
EF dependency id = Raffinert.Consistency
core DLL = Raffinert.Consistency.dll
EF DLL = Raffinert.Consistency.EntityFrameworkCore.dll
README.md present
CHANGELOG.md present
no Raffinert.Relations.dll payload
no dependency on Raffinert.Relations package
```

## Active residue search

```bash
git grep -n "Raffinert\.Relations" -- \
  '*.cs' '*.csproj' '*.sln' '*.props' '*.targets' '*.ps1' '*.yml' '*.yaml' \
  'README.md' 'RELEASING.md' 'docs/architecture.md' 'docs/ef-core-consistency.md' \
  'docs/optimizer-policy.md' 'docs/purchase-order-example.md' 'docs/release-candidate-verification.md' \
  ':!docs/roadmaps/archive/**' ':!docs/codex-plan-*.md'
```

Before Task 122, only intentional current GitHub URL references to `https://github.com/Raffinert/Relations` are allowed.

Also:

```bash
git ls-files '*Raffinert.Relations*'
```

Only preserved historical roadmap/archive filenames may remain.

## Concept guard

Confirm these still exist:

```text
Relation<TLeft, TRight>
RelationBuilder<TLeft, TRight>
RelationImpact
RelationModelDiagnostics
RelationRuntimeState<TLeft, TRight>
RelationAccessPlanKind
RelationPropagationPlanKind
```

If they disappeared because of a global `Relation -> Consistency` replacement, restore them.

## Consumer naming spot-check

A package consumer must compile this shape:

```csharp
using Raffinert.Consistency;

var model = new ConsistencyModelBuilder();
var left = model.Objects<Left>().Key(x => x.Id);
var right = model.Objects<Right>().Key(x => x.Id);
Relation<Left, Right> relation = model.Relation(left, right).Where((l, r) => l.Id == r.LeftId);
CompiledConsistencyModel compiled = model.Build();
ConsistencyRuntime runtime = compiled.CreateRuntime();
```

## Commit

```text
test: prove Raffinert.Consistency rename integrity
```

---

# Task 122 — Rename GitHub repository and finalize URLs

Target repository:

```text
Raffinert/Consistency
```

This task may require repository administration permission.

If authenticated `gh` with administration rights is available:

```bash
gh api --method PATCH repos/Raffinert/Relations -f name=Consistency
```

If the coding agent lacks GitHub administration permission, **only the external repository rename may remain pending**. Report that exact limitation. This is unrelated to NuGet publication and must not block Tasks 116–121.

Do not create a second repository as a workaround.

After repository rename:

```bash
git remote set-url origin https://github.com/Raffinert/Consistency.git
git remote -v
```

Then update current URL metadata from:

```text
https://github.com/Raffinert/Relations
```

to:

```text
https://github.com/Raffinert/Consistency
```

Required locations include:

```text
src/Raffinert.Consistency/Raffinert.Consistency.csproj
src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Consistency.EntityFrameworkCore.csproj
eng/VerifyReleaseCandidate.ps1
README.md
RELEASING.md
docs/release-candidate-verification.md
```

Historical roadmaps may retain historical old URLs.

## Remote proof

Push URL-finalization to the renamed repository and require normal CI success.

Then run `release-candidate.yml` through `workflow_dispatch` and require success.

Do not substitute local success for this final remote check.

## Final residue gate

Run the same current-surface `Raffinert.Relations` grep from Task 121.

Expected after repository rename: no active current-surface hits.

## Commit

```text
docs: finalize Raffinert.Consistency repository identity
```

---

# Required commit sequence

Prefer:

```text
1. refactor: rename projects and namespaces to Raffinert.Consistency
2. refactor: rename consistency engine entry points
3. refactor: rename ef consistency public surface
4. docs: update branding and release tooling for Raffinert.Consistency
5. test: prove Raffinert.Consistency rename integrity
6. docs: finalize Raffinert.Consistency repository identity
7. docs: close Raffinert.Consistency rename roadmap
```

Do not collapse the whole rename into one commit unless unavoidable.

---

# Final completion checklist

Do not mark Tasks 116–122 complete unless all applicable items are true:

```text
No NuGet publication lookup was required; unpublished status is an explicit user-provided fact.
No compatibility package/type/namespace shim was added.
Solution is Raffinert.Consistency.sln.
Core project/package/assembly is Raffinert.Consistency.
EF project/package/assembly is Raffinert.Consistency.EntityFrameworkCore.
Active namespaces start with Raffinert.Consistency.
ConsistencyModelBuilder replaces RelationModelBuilder.
CompiledConsistencyModel replaces CompiledRelationModel.
ConsistencyRuntime replaces product-level RelationRuntime.
Binary Relation<TLeft,TRight> terminology remains intact.
RelationRuntimeState remains relation-specific and was not renamed.
EF product-level types use the exact Task 119 names.
InternalsVisibleTo strings use new assembly names.
PublicAPI shipped/unshipped baselines use new namespaces/types and analyzers are green.
All ProjectReference paths use renamed projects.
Package consumers reference only new NuGet IDs.
CI and RC workflows use renamed paths.
VerifyReleaseCandidate expects new package IDs and DLLs.
README/current docs use Raffinert.Consistency branding.
Core net8 + net10 tests pass.
EF net10 tests pass.
Both samples run.
format passes.
pack passes.
local package consumers run against new packages.
Produced nupkgs contain no Raffinert.Relations DLL/package dependency.
No active source/build/workflow/release file retains old namespace/package/project identity.
RepositoryUrl stays on the old real URL until GitHub repository rename actually occurs.
After repository rename, RepositoryUrl points to https://github.com/Raffinert/Consistency.
Normal CI passes after repository rename.
Manual release-candidate workflow_dispatch passes after repository rename.
No package is published as part of this roadmap unless the user separately requests publication.
```

---

# Explicit non-goals

Do not change runtime behavior while renaming.

Do not change:

```text
relation semantics
impact propagation semantics
Dirty/Invalid behavior
invariant truth semantics
binding-plan lifecycle
generated-key workflow
EF transaction boundaries
materialization sink-only restriction
repair scheduling semantics
benchmark algorithms
```

Do not add backward-compatibility wrappers, aliases, old-namespace facades, migration packages, metapackages, or type-forwarding packages. The old product was never published.

Do not publish NuGet packages, create tags, or create a GitHub Release in Tasks 116–122.

The intended application-visible change is the compile-time product identity/name change to `Raffinert.Consistency`; binary-relation concepts remain `Relation*`.
