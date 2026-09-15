# Codex Mechanical Rename Plan — `Raffinert.Relations` → `Raffinert.Consistency`

Baseline: `a072685fc087fc52382a04ae804cf3000f6619f3`

Tasks: **116–122**

This plan is written for a weaker coding agent. Follow it mechanically. This is a product/package/namespace rename plus a small set of explicitly listed product-level type renames. It is **not** permission to rename every symbol containing the word `Relation`.

The current baseline is green: CI #193 / run `34988798798` succeeded for `a072685...`.

The desired public product identity is:

```text
Product:      Raffinert.Consistency
Core package: Raffinert.Consistency
EF package:   Raffinert.Consistency.EntityFrameworkCore
Repository:   https://github.com/Raffinert/Consistency
Tagline:      Incremental consistency for .NET object models.
```

The reason for the rename is that binary relations are now only one mechanism inside the library. The public value proposition is maintaining incremental consistency across relations, derived state, invariants, impact planning, repair policy, and EF Core persistence boundaries.

---

# 0. Non-negotiable semantic rule: rename the product, not the `Relation` concept

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

The following names use `Relation` as an accidental old product prefix or as the name of the whole model/runtime. Rename them exactly as specified below.

## Exact product-level symbol map

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

Do not invent additional public renames without a failing naming/compile issue. In particular, keep generic names such as `RuntimeApplyResult`, `RuntimeDiagnostics`, `PreparedImpactPlan`, `ChangeImpact`, `Derived`, `Invariant`, and `ObjectSet`.

When replacing the exact symbol `RelationRuntime`, use symbol-aware rename or a word-boundary replacement. **Do not** accidentally turn `RelationRuntimeState` into `ConsistencyRuntimeState`; it is the runtime state of one binary relation and should remain named `RelationRuntimeState`.

---

# 1. Target filesystem/project/package layout

After the rename, the active repository tree must contain these paths:

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

The existing package-consumer folder names remain:

```text
tests/package-consumers/CoreNet8
tests/package-consumers/CoreNet10
tests/package-consumers/EfNet10
```

Only their package references/source code are updated.

The EF sample intentionally becomes:

```text
Raffinert.Consistency.EntityFrameworkCore.Sample
```

not:

```text
Raffinert.Consistency.EntityFrameworkCore.ConsistencySample
```

because the latter repeats the product word and reads poorly.

---

# 2. Namespace and package rules

## Namespace replacement

All active source/test/sample/benchmark code moves from:

```csharp
namespace Raffinert.Relations;
namespace Raffinert.Relations.EntityFrameworkCore;
namespace Raffinert.Relations.Tests;
...
```

to the corresponding `Raffinert.Consistency...` namespace.

The broad namespace replacement is allowed:

```text
Raffinert.Relations -> Raffinert.Consistency
```

because that token is the old root namespace/product identity.

This does **not** imply renaming the standalone type word `Relation`.

## NuGet IDs

Core csproj:

```xml
<PackageId>Raffinert.Consistency</PackageId>
```

EF csproj:

```xml
<PackageId>Raffinert.Consistency.EntityFrameworkCore</PackageId>
```

Do not create compatibility packages in Tasks 116–122 unless Task 116 discovers that the old NuGet IDs were actually published.

## Assembly names

The project files are renamed, so the default assembly names must become:

```text
Raffinert.Consistency.dll
Raffinert.Consistency.EntityFrameworkCore.dll
Raffinert.Consistency.Tests.dll
Raffinert.Consistency.EntityFrameworkCore.Tests.dll
Raffinert.Consistency.Benchmarks.dll
```

Do not add `<AssemblyName>` aliases preserving `Raffinert.Relations`. The point of this wave is a clean pre-stable rename.

## Version

Do not bump `Directory.Build.props` merely because of the rename. Keep the current version unless the publication preflight below proves a package migration is required.

---

# 3. Historical files: what NOT to rewrite

Do not destroy historical roadmap provenance.

The following may intentionally retain `Raffinert.Relations` references/names:

```text
docs/codex-plan-*.md that predate this rename
docs/roadmaps/archive/**
historical benchmark result prose tied to old commit SHAs, when changing the name would misrepresent what was measured
CHANGELOG historical entries describing releases/commits before the rename
```

Current user-facing documentation must use the new name:

```text
README.md
RELEASING.md
docs/architecture.md
docs/ef-core-consistency.md
docs/optimizer-policy.md
docs/purchase-order-example.md
docs/release-candidate-verification.md
current sample README/comments/output if any
.github/workflows/**
eng/VerifyReleaseCandidate.ps1
all current csproj/sln/source/test/sample/benchmark files
```

When running final residue searches, exclude historical roadmap/archive documents explicitly. Do not satisfy a grep gate by rewriting old architecture history.

---

# Task 116 — Publication preflight and frozen rename inventory

## Goal

Before changing package IDs, prove whether `Raffinert.Relations` packages have ever been published. A package rename is cheap before publication and requires migration policy after publication.

## 116.1 Verify old NuGet package IDs

From a machine with Internet access run:

```bash
curl -fsS https://api.nuget.org/v3-flatcontainer/raffinert.relations/index.json
curl -fsS https://api.nuget.org/v3-flatcontainer/raffinert.relations.entityframeworkcore/index.json
```

Interpretation:

```text
HTTP 404 for both -> continue with clean rename.
HTTP 200 for either -> STOP. Do not delete/replace the old package identity blindly.
```

If either old package exists, record its versions and create a separate migration decision before continuing. Do not invent a metapackage/deprecation strategy inside this task.

At plan-authoring time there were no GitHub Releases and public web search did not surface either NuGet package, but the implementing agent must still run the direct NuGet check because package publication can change after this document is written.

## 116.2 Record baseline

Confirm:

```text
git rev-parse HEAD == a072685fc087fc52382a04ae804cf3000f6619f3
```

If the repository has advanced, inspect all commits after the baseline and update the rename inventory before editing. Do not discard newer work.

Confirm current CI is green or run the local equivalent:

```bash
dotnet restore Raffinert.Relations.sln
dotnet build Raffinert.Relations.sln -c Release --no-restore
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Relations.Tests/Raffinert.Relations.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Relations.EntityFrameworkCore.Tests/Raffinert.Relations.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
```

Do not begin the rename from a red baseline.

## 116.3 Create inventory using git, not memory

Run before editing:

```bash
git grep -n "Raffinert\.Relations" -- ':!docs/roadmaps/archive/**'
git ls-files '*Raffinert.Relations*'
git grep -n -E '\b(RelationModelBuilder|CompiledRelationModel|RelationRuntime|RelationUnitOfWork|RelationUnitOfWorkMappings|RelationRuntimeSynchronizationException|RelationEfCoreMappings|RelationEfCoreSaveBehavior|RelationEfCoreConsistencyOptions|RelationConsistencySaveChangesInterceptor|RelationConsistencyDbContextExtensions|RelationInvariantViolationException|RelationMaterializationSourceNotTrackedException|RelationUnsupportedTransactionException|RelationStoreGeneratedKeyRequiresManualWorkflowException)\b'
```

Save the command output temporarily or inspect it carefully. Every hit in active code must be accounted for by Tasks 117–120.

## Acceptance

- old NuGet IDs confirmed unpublished, or work stopped for migration design;
- baseline green;
- actual current-file inventory collected;
- no rename edits yet.

No commit is required for Task 116 unless the baseline/inventory needs a note.

---

# Task 117 — Rename solution, project directories, project files, namespaces, package IDs, and references

## Goal

Perform the mechanical identity rename while intentionally leaving public product-level type names for Task 118.

## 117.1 Filesystem moves

Use `git mv`, not copy/delete, so Git history remains easy to follow.

Run equivalent commands for the current shell:

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

If a path was added/changed after the baseline, adapt the command to the actual tree rather than deleting newer files.

## 117.2 Solution file

Edit `Raffinert.Consistency.sln`.

Change project display names and paths to the new project/folder names. Keep the existing project GUIDs unchanged.

Expected project display names:

```text
Raffinert.Consistency
Raffinert.Consistency.Tests
Raffinert.Consistency.Benchmarks
Raffinert.Consistency.EntityFrameworkCore
Raffinert.Consistency.EntityFrameworkCore.Tests
Raffinert.Consistency.PurchaseOrderSample
Raffinert.Consistency.EntityFrameworkCore.Sample
```

Do not regenerate the solution and accidentally churn project GUIDs/configuration sections unless necessary.

## 117.3 Project references

Update all `<ProjectReference>` paths to the renamed csproj paths.

Examples:

```xml
<ProjectReference Include="..\Raffinert.Consistency\Raffinert.Consistency.csproj" />
```

and corresponding relative paths from tests, benchmarks, and samples.

Run:

```bash
git grep -n "Raffinert.Relations.*csproj"
```

outside historical docs. There must be no active build reference to an old project path.

## 117.4 Package metadata

Core project must contain:

```xml
<PackageId>Raffinert.Consistency</PackageId>
<Description>Incremental consistency for .NET object models: relations, derived state, invariants, and impact planning.</Description>
<PackageTags>consistency;dependency-graph;incremental;invariants;relations;object-model</PackageTags>
```

EF project must contain:

```xml
<PackageId>Raffinert.Consistency.EntityFrameworkCore</PackageId>
<Description>EF Core consistency integration for Raffinert.Consistency.</Description>
<PackageTags>consistency;entity-framework-core;ef-core;invariants;derived-state;change-tracking</PackageTags>
```

Do not change `RepositoryUrl` to the new GitHub URL yet if the repository itself is still named `Raffinert/Relations`; Task 122 performs that final external cutover. Keeping the real URL during intermediate commits prevents broken package metadata in transient builds.

## 117.5 Root namespaces

Replace the root namespace token throughout all active `.cs` files:

```text
Raffinert.Relations -> Raffinert.Consistency
```

This includes:

```text
namespace declarations
using directives
fully qualified type references
test namespaces
sample namespaces
benchmark namespaces
```

Do not change ordinary prose words `relation`/`relations` when they describe the binary-relation concept.

## 117.6 InternalsVisibleTo

Update core `Properties/AssemblyInfo.cs` to:

```csharp
[assembly: InternalsVisibleTo("Raffinert.Consistency.Tests")]
[assembly: InternalsVisibleTo("Raffinert.Consistency.EntityFrameworkCore.Tests")]
[assembly: InternalsVisibleTo("Raffinert.Consistency.Benchmarks")]
[assembly: InternalsVisibleTo("Raffinert.Consistency.EntityFrameworkCore")]
```

## 117.7 Package consumers

Update:

```text
tests/package-consumers/CoreNet8/CoreNet8.csproj
tests/package-consumers/CoreNet10/CoreNet10.csproj
tests/package-consumers/EfNet10/EfNet10.csproj
```

Package IDs become:

```xml
<PackageReference Include="Raffinert.Consistency" Version="0.1.0-alpha.1" />
<PackageReference Include="Raffinert.Consistency.EntityFrameworkCore" Version="0.1.0-alpha.1" />
```

Also update their `Program.cs` namespaces/usings from `Raffinert.Relations` to `Raffinert.Consistency`.

Do not hardcode the package version somewhere new. Preserve the existing consumer-version strategy for this roadmap.

## 117 acceptance

At this point the solution may still fail because product-level public types retain old names. That is acceptable only until Task 118 immediately follows.

Commit:

```text
refactor: rename projects and namespaces to Raffinert.Consistency
```

Do not publish packages from this intermediate commit.

---

# Task 118 — Rename product-level core API types, but preserve relation-specific API names

## Goal

Make the main API read as a consistency engine instead of a relation-only engine.

## 118.1 Rename `RelationModelBuilder`

Symbol-aware rename:

```text
RelationModelBuilder -> ConsistencyModelBuilder
```

Rename file:

```bash
git mv src/Raffinert.Consistency/Model/RelationModelBuilder.cs src/Raffinert.Consistency/Model/ConsistencyModelBuilder.cs
```

The public usage becomes:

```csharp
var model = new ConsistencyModelBuilder();
var invoices = model.Objects<InvoiceLine>().Key(x => x.Id);
var poLines = model.Objects<PurchaseOrderLine>().Key(x => x.Id);
var candidates = model.Relation(invoices, poLines).Where(...);
```

The method `.Relation(...)` stays `.Relation(...)` because it creates a real binary relation.

Internal constructors currently taking `RelationModelBuilder` must now take `ConsistencyModelBuilder`.

## 118.2 Rename `CompiledRelationModel`

Symbol-aware rename:

```text
CompiledRelationModel -> CompiledConsistencyModel
```

Rename file:

```bash
git mv src/Raffinert.Consistency/Model/CompiledRelationModel.cs src/Raffinert.Consistency/Model/CompiledConsistencyModel.cs
```

`ConsistencyModelBuilder.Build()` returns `CompiledConsistencyModel`.

## 118.3 Rename `RelationRuntime`

Symbol-aware exact rename:

```text
RelationRuntime -> ConsistencyRuntime
```

Rename only the product-level runtime file:

```bash
git mv src/Raffinert.Consistency/Runtime/RelationRuntime.cs src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
```

Also rename every partial declaration in files such as `MutationCommit.cs`.

Hard stop:

```text
RelationRuntimeState<TLeft,TRight> must remain RelationRuntimeState<TLeft,TRight>.
IRelationRuntimeState must remain IRelationRuntimeState.
RelationRuntimeDiagnostics should remain unchanged if it is diagnostics for one relation rather than the whole engine.
```

Use compiler errors plus semantic inspection; do not perform substring replacement that changes these names.

`CompiledConsistencyModel.CreateRuntime()` returns `ConsistencyRuntime`.

## 118.4 Core PublicAPI baselines

Both files are current API contracts and must be updated:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
```

First mechanically change namespace prefix:

```text
Raffinert.Relations. -> Raffinert.Consistency.
```

Then apply the exact product-level symbol changes:

```text
RelationModelBuilder -> ConsistencyModelBuilder
CompiledRelationModel -> CompiledConsistencyModel
RelationRuntime -> ConsistencyRuntime
```

Again, exact token only: do not alter `RelationRuntimeState`, `RelationModelDiagnostics`, `RelationImpact`, etc.

The Public API analyzer must be clean after the rename. Do not suppress `RS0016`/`RS0017` or other API-baseline diagnostics.

## 118.5 Tests/samples/benchmarks

Update constructor/type references to the new names. Do not rename tests merely because their names contain `Relation` if they test a real relation behavior.

Examples that should remain valid names:

```text
RelationQueryRuntimeTests
RelationImpactTests
RelationTouchedStateTests
RelationBenchmarks
```

because those are specifically about binary relation behavior.

## Acceptance

Run:

```bash
dotnet build Raffinert.Consistency.sln -c Release
```

Build must succeed on all projects before proceeding.

Commit:

```text
refactor: rename consistency engine entry points
```

---

# Task 119 — Rename EF Core product-level API and source files

## Goal

Remove the old product prefix from EF integration while keeping the API concise and consistent with `Raffinert.Consistency`.

## 119.1 Exact public symbol renames

Apply only this map:

```text
RelationUnitOfWork                              -> ConsistencyUnitOfWork
RelationUnitOfWorkMappings                      -> ConsistencyUnitOfWorkMappings
RelationRuntimeSynchronizationException         -> ConsistencyRuntimeSynchronizationException
RelationEfCoreMappings                          -> ConsistencyEfCoreMappings
RelationEfCoreSaveBehavior                      -> ConsistencySaveBehavior
RelationEfCoreConsistencyOptions                -> ConsistencySaveOptions
RelationConsistencySaveChangesInterceptor       -> ConsistencySaveChangesInterceptor
RelationConsistencyDbContextExtensions           -> ConsistencyDbContextExtensions
RelationInvariantViolationException             -> ConsistencyInvariantViolationException
RelationMaterializationSourceNotTrackedException -> ConsistencyMaterializationSourceNotTrackedException
RelationUnsupportedTransactionException         -> ConsistencyUnsupportedTransactionException
RelationStoreGeneratedKeyRequiresManualWorkflowException -> ConsistencyStoreGeneratedKeyRequiresManualWorkflowException
```

All parameters formerly typed `RelationRuntime` must now use `ConsistencyRuntime` from Task 118.

## 119.2 Source filenames

Rename files that are now misleading:

```bash
git mv src/Raffinert.Consistency.EntityFrameworkCore/RelationEfCoreMappings.cs src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreMappings.cs
git mv src/Raffinert.Consistency.EntityFrameworkCore/RelationConsistencySaveChangesInterceptor.cs src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySaveChangesInterceptor.cs
git mv src/Raffinert.Consistency.EntityFrameworkCore/RelationConsistency.cs src/Raffinert.Consistency.EntityFrameworkCore/ConsistencySave.cs
```

`ChangeTrackerAdapter.cs` keeps its name.

If `RelationUnitOfWork` and `RelationUnitOfWorkMappings` still live in `ChangeTrackerAdapter.cs`, do not split the file just to match type names. Symbol rename is enough.

## 119.3 Target user-facing EF API

README/sample code should now look like:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(lines)
    .Materialize(availableQuantity, x => x.AvailableQuantity)
    .Enforce(availability);

await db.SaveChangesConsistentlyAsync(
    runtime,
    mappings,
    cancellationToken: cancellationToken);
```

Interceptor:

```csharp
new ConsistencySaveChangesInterceptor(runtime, mappings, new ConsistencySaveOptions());
```

Manual unit-of-work path:

```csharp
var mappings = new ConsistencyUnitOfWorkMappings().Map(lines);
var unit = ChangeTrackerAdapter.CaptureUnitOfWork(context.ChangeTracker, mappings);
```

Method names such as `SaveChangesConsistently`, `CaptureUnitOfWork`, `PlanDetailed`, `Prepare`, `Commit`, and `Dispatch` remain unchanged.

## 119.4 EF PublicAPI baselines

Update:

```text
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
```

Perform the namespace change first, then the exact symbol map above.

Do not leave aliases under old namespaces/types in this pre-stable rename unless Task 116 found real published consumers. Compatibility shims would double the public surface and undermine the clean rename.

## Acceptance

Run:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
```

Commit:

```text
refactor: rename ef consistency public surface
```

---

# Task 120 — Update active documentation, workflows, release verification, package smoke tests, and branding

## Goal

Make the repository operational under the new project/package identity and remove stale user-facing branding.

## 120.1 README

Update the opening to:

```md
# Raffinert.Consistency

**Incremental consistency for .NET object models.**

Raffinert.Consistency lets you declare relations, derived values, and invariants as expression trees...
```

Use:

```text
Raffinert.Consistency
```

for product references.

Keep ordinary conceptual language such as:

```text
relation
relations
relation predicate
relation membership
```

where it describes actual relations.

Update code examples:

```text
RelationModelBuilder -> ConsistencyModelBuilder
RelationEfCoreMappings -> ConsistencyEfCoreMappings
RelationRuntime -> ConsistencyRuntime where explicit type names appear
```

Update sample link:

```text
samples/Raffinert.Consistency.EntityFrameworkCore.Sample
```

Update headings such as:

```text
What Raffinert.Consistency is not
```

## 120.2 Current docs

Update current documentation to the new namespace/product/API names:

```text
RELEASING.md
docs/architecture.md
docs/ef-core-consistency.md
docs/optimizer-policy.md
docs/purchase-order-example.md
docs/release-candidate-verification.md
```

Do not rewrite old roadmap plans as if they were authored under the new namespace. Historical plan commands may continue to show `Raffinert.Relations` because they describe their historical baseline.

For `CHANGELOG.md`, add a new top entry/note explaining:

```text
Renamed product/package/namespace from Raffinert.Relations to Raffinert.Consistency before stable release.
Binary relation concepts retain their Relation* terminology.
```

Do not retroactively rewrite old changelog entries.

## 120.3 GitHub Actions

Update `.github/workflows/ci.yml` and `.github/workflows/release-candidate.yml`.

Every command must use:

```text
Raffinert.Consistency.sln
tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj
samples/Raffinert.Consistency.PurchaseOrderSample/Raffinert.Consistency.PurchaseOrderSample.csproj
```

Add/run the EF consistency sample in CI if the current workflow already intends it as a release gate; use its new path:

```text
samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj
```

Do not leave old project paths in comments or scripts that someone may copy later.

## 120.4 `eng/VerifyReleaseCandidate.ps1`

Update all hardcoded paths and IDs.

Expected API files:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
```

Expected package IDs:

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

Keep the dependency check requiring the EF package to depend on the core package at the exact package version.

Repository URL validation stays on the actual old GitHub URL until Task 122 performs repository rename. Do not temporarily assert a URL that does not exist yet.

## 120.5 Package smoke consumers

After packing, the three package consumers must restore only the **new** package IDs from the local artifacts directory.

Explicitly inspect their `project.assets.json` or `dotnet list package` output if necessary to prove they did not silently resolve the old package from nuget.org.

## 120.6 Benchmark project

The benchmark project path/assembly/namespace is renamed to `Raffinert.Consistency.Benchmarks`.

Do not rename classes such as `RelationBenchmarks` because they benchmark relation-specific behavior.

Historical result files can remain named as they are if they do not include the old product token in their filename. If a result document contains the old project identity, add a small historical note rather than falsifying the original measured commit context.

## Acceptance

No active workflow/release script/csproj/package consumer may reference an old project/package path.

Commit:

```text
docs: update branding and release tooling for Raffinert.Consistency
```

---

# Task 121 — Full rename proof, package inspection, and residue gate

## Goal

Prove this is a complete rename rather than a build that succeeds while shipping mixed identities.

## 121.1 Full build/test/sample gate

Run exactly the renamed paths:

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

Then run package smoke consumers against `artifacts/packages` exactly as CI does.

## 121.2 Inspect produced NuGet package identities

There must be exactly two main `.nupkg` files and two `.snupkg` files.

Expected names start with:

```text
Raffinert.Consistency.
Raffinert.Consistency.EntityFrameworkCore.
```

Open/inspect nuspec metadata and prove:

```text
core id = Raffinert.Consistency
EF id   = Raffinert.Consistency.EntityFrameworkCore
EF dependency id = Raffinert.Consistency
core DLL names use Raffinert.Consistency.dll
EF DLL name uses Raffinert.Consistency.EntityFrameworkCore.dll
README.md and CHANGELOG.md are present
```

There must be no `Raffinert.Relations.dll` payload in either package.

## 121.3 Active-code residue search

Run:

```bash
git grep -n "Raffinert\.Relations" -- \
  '*.cs' '*.csproj' '*.sln' '*.props' '*.targets' '*.ps1' '*.yml' '*.yaml' \
  'README.md' 'RELEASING.md' 'docs/architecture.md' 'docs/ef-core-consistency.md' \
  'docs/optimizer-policy.md' 'docs/purchase-order-example.md' 'docs/release-candidate-verification.md' \
  ':!docs/roadmaps/archive/**' ':!docs/codex-plan-*.md'
```

Expected result before Task 122:

```text
only intentional RepositoryUrl / GitHub URL references to https://github.com/Raffinert/Relations are allowed
```

No old namespace/package/project/assembly identity may remain.

Also run:

```bash
git ls-files '*Raffinert.Relations*'
```

Expected remaining hits may only be explicitly preserved historical roadmap/archive filenames. There must be no active source/test/sample/project path containing `Raffinert.Relations`.

## 121.4 Wrong symbol rename guard

Search for accidental conceptual renames that should not have happened.

Confirm these still exist where applicable:

```text
Relation<TLeft, TRight>
RelationBuilder<TLeft, TRight>
RelationImpact
RelationModelDiagnostics
RelationRuntimeState<TLeft, TRight>
RelationAccessPlanKind
RelationPropagationPlanKind
```

If they disappeared because of a global `Relation -> Consistency` replacement, revert those changes.

## 121.5 API surface spot-check

Compile a tiny consumer or inspect package-consumer source demonstrating:

```csharp
using Raffinert.Consistency;

var model = new ConsistencyModelBuilder();
var left = model.Objects<Left>().Key(x => x.Id);
var right = model.Objects<Right>().Key(x => x.Id);
Relation<Left, Right> relation = model.Relation(left, right).Where((l, r) => l.Id == r.LeftId);
CompiledConsistencyModel compiled = model.Build();
ConsistencyRuntime runtime = compiled.CreateRuntime();
```

This is the intended naming balance: **Consistency is the product/runtime; Relation is a domain concept inside it.**

## Acceptance

Do not proceed to repository rename unless all local gates pass.

Commit any final mechanical fixes as:

```text
test: prove Raffinert.Consistency rename integrity
```

---

# Task 122 — Rename GitHub repository and finalize repository metadata/URLs

## Goal

Complete the external identity cutover from:

```text
Raffinert/Relations
```

to:

```text
Raffinert/Consistency
```

This task requires GitHub repository administration permission. A coding agent may not have it.

## 122.1 Repository rename

If authenticated `gh` with repository administration is available, the equivalent GitHub API operation is:

```bash
gh api --method PATCH repos/Raffinert/Relations -f name=Consistency
```

If this fails because the agent lacks permission or `gh` is unavailable:

```text
STOP the external cutover.
Do not claim the repository was renamed.
Report exactly: "Code/package rename is complete; GitHub repository must still be renamed from Raffinert/Relations to Raffinert/Consistency by an administrator."
```

Do not create a second repository or copy history as a workaround.

## 122.2 Update git remote after repository rename

For a local checkout:

```bash
git remote set-url origin https://github.com/Raffinert/Consistency.git
```

Verify:

```bash
git remote -v
```

GitHub normally redirects old repository URLs after rename, but do not rely on redirects in package metadata/docs.

## 122.3 Final URL metadata

After the repository actually exists at the new location, replace current URL references:

```text
https://github.com/Raffinert/Relations
    ->
https://github.com/Raffinert/Consistency
```

Required locations include at least:

```text
src/Raffinert.Consistency/Raffinert.Consistency.csproj
src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Consistency.EntityFrameworkCore.csproj
eng/VerifyReleaseCandidate.ps1
README.md
RELEASING.md
docs/release-candidate-verification.md
```

Historical roadmap documents may retain old links if they are explicitly documenting historical repository identity, but current navigation links must use the new repository.

Update `VerifyReleaseCandidate.ps1` repository metadata assertion to:

```text
https://github.com/Raffinert/Consistency
```

## 122.4 Final GitHub CI proof

Push the URL-finalization commit to the renamed repository and wait for the normal push CI.

Required:

```text
CI workflow completes successfully on the renamed repository.
```

Then manually run `release-candidate.yml` through `workflow_dispatch` and require success. Do not substitute local success for the remote RC workflow because repository rename/path behavior is part of this proof.

## 122.5 Final current-surface residue check

After repository rename:

```bash
git grep -n "Raffinert\.Relations" -- \
  '*.cs' '*.csproj' '*.sln' '*.props' '*.targets' '*.ps1' '*.yml' '*.yaml' \
  'README.md' 'RELEASING.md' 'docs/architecture.md' 'docs/ef-core-consistency.md' \
  'docs/optimizer-policy.md' 'docs/purchase-order-example.md' 'docs/release-candidate-verification.md' \
  ':!docs/roadmaps/archive/**' ':!docs/codex-plan-*.md'
```

Expected: no hits.

It is acceptable for old historical roadmap files or changelog history to mention `Raffinert.Relations` intentionally.

## Commit

```text
docs: finalize Raffinert.Consistency repository identity
```

---

# Required commit boundaries

Prefer this sequence so failures are bisectable:

```text
1. refactor: rename projects and namespaces to Raffinert.Consistency
2. refactor: rename consistency engine entry points
3. refactor: rename ef consistency public surface
4. docs: update branding and release tooling for Raffinert.Consistency
5. test: prove Raffinert.Consistency rename integrity
6. docs: finalize Raffinert.Consistency repository identity
7. docs: close Raffinert.Consistency rename roadmap
```

Do not collapse the entire rename into one huge commit unless repository tooling makes directory moves impossible to review otherwise.

---

# Final completion checklist

Do not mark Tasks 116–122 complete unless every item below is true:

```text
Old NuGet IDs were verified unpublished OR an explicit migration plan was approved before continuing.
Solution is Raffinert.Consistency.sln.
Core project/package/assembly is Raffinert.Consistency.
EF project/package/assembly is Raffinert.Consistency.EntityFrameworkCore.
Active namespaces start with Raffinert.Consistency.
ConsistencyModelBuilder replaces RelationModelBuilder.
CompiledConsistencyModel replaces CompiledRelationModel.
ConsistencyRuntime replaces product-level RelationRuntime.
Binary Relation<TLeft,TRight> terminology remains intact.
RelationRuntimeState remains relation-specific and was not accidentally renamed.
EF product-level types use the Task 119 exact names.
InternalsVisibleTo strings use new assembly names.
PublicAPI shipped and unshipped baselines contain the new namespaces/types and analyzer is green.
All ProjectReference paths use renamed projects.
Package consumers reference only the new NuGet IDs.
CI and RC workflows use renamed solution/project/sample paths.
VerifyReleaseCandidate expects new package IDs and DLL assets.
README/current docs use Raffinert.Consistency branding and new API examples.
Core net8 + net10 tests pass.
EF net10 tests pass.
Both samples run.
format gate passes.
pack gate passes.
local package consumers run from new packages.
Produced nupkgs contain no Raffinert.Relations DLL/package dependency.
No active source/build/workflow/release file retains old namespace/package/project identity.
GitHub repository is actually Raffinert/Consistency, or the roadmap remains explicitly incomplete pending administrator action.
RepositoryUrl metadata points to https://github.com/Raffinert/Consistency only after that repository exists.
Normal CI passes after repository rename.
Manual release-candidate workflow_dispatch passes after repository rename.
No packages are published as part of this roadmap unless the user separately requests publication.
```

---

# Explicit non-goals

Do not use the rename as an excuse to redesign runtime behavior.

Do not change:

```text
relation semantics
impact propagation semantics
Dirty/Invalid behavior
invariant truth semantics
binding-plan lifecycle
generated-key workflow
EF consistency transaction boundaries
materialization sink-only restriction
repair scheduling semantics
benchmark algorithms
```

Do not add backward-compatibility wrappers under the old namespace unless Task 116 proves that published external consumers require them.

Do not publish NuGet packages, create tags, or create a GitHub Release in Tasks 116–122.

The only intended behavioral change from an application author's point of view is the compile-time identity/name change from the old product to `Raffinert.Consistency`.