# Codex plan — `0.2.0-rc.2` release-candidate closeout

Status: **RELEASE CLOSEOUT ONLY — NO NEW FEATURES, NO ARCHITECTURE REDESIGN**

Audience: an extremely weak coding agent. Follow this document literally and in order. Do not improvise. Do not broaden scope. Do not tag, publish, or change repository visibility. Stop and report a blocker if any required gate is red.

Baseline implementation commit:

```text
f05377cf83326680b72af6f46e4e29575692d5b7
```

The `IConsistencyRuntime` feature is already implemented and tested on `main`. This plan does **not** implement that feature. This plan freezes it into the next release candidate and proves that the packed `0.2.0-rc.2` packages contain and exercise it correctly.

---

# 0. Goal

Prepare one exact release commit for:

```text
0.2.0-rc.2
```

The final reviewed commit must satisfy all of these simultaneously:

```text
- package version is exactly 0.2.0-rc.2
- CHANGELOG has a 0.2.0-rc.2 section and no leftover Unreleased content for this feature
- IConsistencyRuntime remains the narrow 3-method application-facing abstraction
- ConsistencyRuntime remains sealed
- no virtual runtime members are introduced
- EF DI resolves IConsistencyRuntime to the exact same scoped ConsistencyRuntime instance
- Core PublicAPI.Unshipped.txt is empty except #nullable enable
- EF PublicAPI.Unshipped.txt is empty except #nullable enable
- the 4 reviewed Core API entries for IConsistencyRuntime are promoted to PublicAPI.Shipped.txt
- no unrelated shipped API changes are introduced
- all packed-consumer projects reference 0.2.0-rc.2, not 0.2.0-rc.1
- packed consumers prove they are using the newly packed rc.2 packages
- packed package smoke coverage exercises IConsistencyRuntime
- Release build is clean
- Core tests pass on net8.0 and net10.0
- EF tests pass on net10.0
- all release workflow samples run successfully
- dotnet format verification is clean
- pack produces exactly two .nupkg and two .snupkg files for 0.2.0-rc.2
- VerifyReleaseCandidate.ps1 -RequireEmptyUnshipped passes
- exact-head GitHub release-candidate workflow is green
```

Only after the exact release commit is green may a human separately decide to create:

```text
v0.2.0-rc.2
```

and manually publish the already-verified packages.

This plan does **not** authorize:

```text
- creating a Git tag
- creating or publishing a GitHub Release
- publishing to NuGet.org
- changing repository visibility
- changing package ownership
- changing NuGet credentials
```

---

# 1. Frozen product behavior

Do not redesign or broaden the runtime API during this task.

The following application-facing interface is already implemented and is the intended surface:

```csharp
public interface IConsistencyRuntime
{
    TValue Evaluate<TSource, TValue>(
        Derived<TSource, TValue> derived,
        TSource source)
        where TSource : class;

    TValue Materialize<TSource, TValue>(
        Derived<TSource, TValue> derived,
        TSource source)
        where TSource : class;

    void Materialize<TSource>(TSource source)
        where TSource : class;
}
```

The concrete runtime must remain:

```csharp
public sealed partial class ConsistencyRuntime : IConsistencyRuntime
```

Do not:

```text
- unseal ConsistencyRuntime
- make runtime methods virtual
- add mutation/planning/diagnostic APIs to IConsistencyRuntime
- add Add / Remove / Apply / Prepare / Commit / PlanDetailed / Diagnostics to IConsistencyRuntime
- introduce IConsistencyMaterializer, IConsistencyEvaluator, or another overlapping abstraction
- create a wrapper object that owns a second ConsistencyRuntime
- change Evaluate semantics
- change Materialize semantics
- change runtime transaction/baseline semantics
```

The narrow interface exists for ordinary application-service injection and mocking. The concrete runtime remains the advanced engine API.

---

# 2. Frozen EF dependency-injection identity contract

The EF registration must continue to create exactly one concrete scoped runtime and expose the interface as an alias to that same object.

Required registration shape:

```csharp
services.AddScoped<ConsistencyRuntime>(serviceProvider =>
{
    var runtime = serviceProvider
        .GetRequiredService<CompiledConsistencyModel>()
        .CreateRuntime();

    serviceProvider
        .GetRequiredService<ConsistencyEfCoreSession<TDbContext>>()
        .BindRuntime(runtime);

    return runtime;
});

services.AddScoped<IConsistencyRuntime>(serviceProvider =>
    serviceProvider.GetRequiredService<ConsistencyRuntime>());
```

Do **not** replace the alias with:

```csharp
services.AddScoped<IConsistencyRuntime, ConsistencyRuntime>();
```

That form is forbidden because it can create a second scoped service instance instead of aliasing the already bound runtime.

The following must remain true in one service scope:

```csharp
var concrete = provider.GetRequiredService<ConsistencyRuntime>();
var application = provider.GetRequiredService<IConsistencyRuntime>();

Assert.Same(concrete, application);
```

Repeated interface resolution in the same scope must also return the same reference.

Different scopes must receive different runtime instances.

Do not modify EF session/interceptor transaction semantics as part of this release closeout.

---

# 3. Inspect current release state before editing

Before changing anything, verify the repository still matches the expected baseline intent.

## 3.1 Version

File:

```text
Directory.Build.props
```

Expected current value before this task:

```xml
<Version>0.2.0-rc.1</Version>
```

If it is already another version, stop and report the mismatch before proceeding.

## 3.2 Changelog

File:

```text
CHANGELOG.md
```

Expected current top section:

```markdown
## Unreleased

### Added

- Added `IConsistencyRuntime`, a narrow application-facing abstraction for logical evaluation and
  materialization, with EF Core DI resolving it to the same scoped `ConsistencyRuntime` instance.
```

Do not invent additional release notes unrelated to changes actually present since `v0.2.0-rc.1`.

## 3.3 Core unshipped API

File:

```text
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
```

Expected non-comment entries are exactly these four lines:

```text
Raffinert.Consistency.IConsistencyRuntime
Raffinert.Consistency.IConsistencyRuntime.Evaluate<TSource, TValue>(Raffinert.Consistency.Derived<TSource!, TValue>! derived, TSource! source) -> TValue
Raffinert.Consistency.IConsistencyRuntime.Materialize<TSource, TValue>(Raffinert.Consistency.Derived<TSource!, TValue>! derived, TSource! source) -> TValue
Raffinert.Consistency.IConsistencyRuntime.Materialize<TSource>(TSource! source) -> void
```

If there are additional unexpected unshipped Core APIs, stop and review them. Do not blindly promote them.

## 3.4 EF unshipped API

File:

```text
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
```

Expected content before this task:

```text
#nullable enable
```

No EF public API addition is required for the alias registration because `IConsistencyRuntime` belongs to the Core package and the DI extension method already shipped in `rc.1`.

If EF Unshipped contains new API entries, stop and review them separately.

---

# 4. Review and freeze the `IConsistencyRuntime` source before promotion

Inspect:

```text
src/Raffinert.Consistency/Runtime/IConsistencyRuntime.cs
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.Materialization.cs
src/Raffinert.Consistency.EntityFrameworkCore/ConsistencyEfCoreDependencyInjection.cs
```

Verify all of the following before touching PublicAPI baselines:

```text
[ ] IConsistencyRuntime is public.
[ ] It contains exactly 3 methods.
[ ] Evaluate has the same generic constraints and return type as ConsistencyRuntime.Evaluate.
[ ] targeted Materialize has the same generic constraints and return type as ConsistencyRuntime.Materialize.
[ ] object-level Materialize returns void and has the same generic constraint as the concrete method.
[ ] ConsistencyRuntime implements IConsistencyRuntime directly.
[ ] ConsistencyRuntime is still sealed.
[ ] No relevant runtime method is virtual merely for mocking.
[ ] EF DI interface registration resolves GetRequiredService<ConsistencyRuntime>().
[ ] The interface does not expose engine mutation/planning/diagnostic surface.
```

If any item fails, stop. This release task is not permission to redesign the feature silently.

---

# 5. Verify the implementation tests that protect the abstraction

Before release metadata changes, inspect tests and confirm there is explicit coverage for these contracts:

```text
Core:
- ConsistencyRuntime can be referenced as IConsistencyRuntime.
- Evaluate preserves logical-only semantics.
- targeted Materialize preserves targeted physical synchronization semantics.
- object-level Materialize preserves object-level synchronization semantics.

EF:
- concrete ConsistencyRuntime and IConsistencyRuntime are ReferenceEquals/Same within one scope.
- repeated IConsistencyRuntime resolution in one scope returns the same object.
- a second scope receives a different runtime.
- application-style service injection through IConsistencyRuntime works.
- Materialize + ordinary SaveChangesAsync persists the expected mirror.
- persisted state is re-read/freshly verified rather than asserted only from the tracked object.
```

Do not weaken or remove these tests during release closeout.

---

# 6. Promote the reviewed Core public API

This is an additive public API freeze for `0.2.0-rc.2`.

Files:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
```

Move/append exactly the four reviewed entries from Core Unshipped to Core Shipped:

```text
Raffinert.Consistency.IConsistencyRuntime
Raffinert.Consistency.IConsistencyRuntime.Evaluate<TSource, TValue>(Raffinert.Consistency.Derived<TSource!, TValue>! derived, TSource! source) -> TValue
Raffinert.Consistency.IConsistencyRuntime.Materialize<TSource, TValue>(Raffinert.Consistency.Derived<TSource!, TValue>! derived, TSource! source) -> TValue
Raffinert.Consistency.IConsistencyRuntime.Materialize<TSource>(TSource! source) -> void
```

Preserve the repository's existing PublicAPI formatting/order conventions.

After promotion, Core Unshipped must contain only:

```text
#nullable enable
```

EF Unshipped must also remain only:

```text
#nullable enable
```

Do not modify/remove existing shipped API entries.

Do not promote any unexpected entry without explicit review.

---

# 7. Change package version to exactly `0.2.0-rc.2`

Edit:

```text
Directory.Build.props
```

Change exactly:

```xml
<Version>0.2.0-rc.1</Version>
```

to:

```xml
<Version>0.2.0-rc.2</Version>
```

Do not use:

```text
0.2.0
0.2.0-rc2
0.2.0-rc.02
0.2.1-rc.1
0.3.0-rc.1
```

The intended release is exactly:

```text
0.2.0-rc.2
```

Do not set per-project package versions. `Directory.Build.props` remains authoritative.

---

# 8. Convert the changelog `Unreleased` entry into `0.2.0-rc.2`

Edit:

```text
CHANGELOG.md
```

Convert the current feature entry into a release section.

Preferred shape:

```markdown
## 0.2.0-rc.2

### Added

- Added `IConsistencyRuntime`, a narrow application-facing abstraction for logical evaluation and
  materialization, with EF Core DI resolving it to the same scoped `ConsistencyRuntime` instance.
```

The next section must remain:

```markdown
## 0.2.0-rc.1
```

Do not rewrite the `rc.1` historical section.

Do not add claims about features that were not added after `rc.1`.

Do not describe `IConsistencyRuntime` as replacing the concrete runtime. It is the recommended narrow application dependency; `ConsistencyRuntime` remains the advanced engine API.

It is acceptable to omit a new empty `Unreleased` section during release closeout. Do not leave the `IConsistencyRuntime` item under both `Unreleased` and `0.2.0-rc.2`.

---

# 9. Update current release-status documentation only where necessary

Review at minimum:

```text
README.md
docs/roadmaps/README.md
docs/ef-core-consistency.md
.agents/skills/raffinert-consistency-consumer/SKILL.md
.agents/skills/raffinert-consistency-consumer/references/recipes.md
.agents/skills/raffinert-consistency-consumer/references/verification.md
```

The feature documentation was already updated by the implementation commit. Do not rewrite it again unless a release-version statement is stale.

Required current-state wording:

```text
- ordinary EF application services may inject IConsistencyRuntime for Evaluate/Materialize
- concrete ConsistencyRuntime remains available for advanced mutation/planning/diagnostics
- both resolve to the same scoped concrete runtime in the EF integration
- Materialize(entity) is optional before SaveChanges and needed only when a mirror must be current before save
- ordinary SaveChanges/SaveChangesAsync remains the persistence path
```

If `docs/roadmaps/README.md` has a current release-line status that says only `0.2.0-rc.1`, update the current status to `0.2.0-rc.2` while preserving historical entries.

Do not mass-edit archived historical plans.

Do not rewrite old release notes to pretend `IConsistencyRuntime` existed in `rc.1`.

---

# 10. CRITICAL: update all packed-consumer package references to `0.2.0-rc.2`

This step is mandatory and release-critical.

Current packed-consumer project files are version-pinned. If they remain on `0.2.0-rc.1`, the release workflow can restore the already-published old package from NuGet.org while the local package directory contains `0.2.0-rc.2`. That would create a false-positive green smoke test that does not test the package being released.

Update exactly these files:

```text
tests/package-consumers/CoreNet8/CoreNet8.csproj
tests/package-consumers/CoreNet10/CoreNet10.csproj
tests/package-consumers/EfNet10/EfNet10.csproj
```

Change Raffinert package references from:

```text
0.2.0-rc.1
```

to:

```text
0.2.0-rc.2
```

For example:

```xml
<PackageReference Include="Raffinert.Consistency" Version="0.2.0-rc.2" />
```

and:

```xml
<PackageReference Include="Raffinert.Consistency.EntityFrameworkCore" Version="0.2.0-rc.2" />
```

Do not change unrelated dependency versions such as `Microsoft.EntityFrameworkCore.Sqlite` unless a separate concrete failure requires it.

After editing, run a repository search and ensure no package-consumer project still references `0.2.0-rc.1`.

---

# 11. Make packed-consumer smoke tests exercise the new API

The packed-package tests must prove `IConsistencyRuntime` is actually in the `rc.2` package, not only that the repository source compiles.

## 11.1 Core packed consumers

At least one Core packed consumer must compile against the interface from the package.

Preferred minimal proof in either/both `CoreNet8` and `CoreNet10`:

```csharp
IConsistencyRuntime runtime = compiled.CreateRuntime(...);
```

Then call at least one interface method already supported by the sample model, preferably:

```csharp
runtime.Evaluate(...)
```

or:

```csharp
runtime.Materialize(...)
```

Do not add a mocking framework dependency to package consumers.

Do not change the consumer into a project reference. It must continue consuming the packed NuGet package.

## 11.2 EF packed consumer

Extend the EF packed consumer so it proves the DI alias contract from the packed `Raffinert.Consistency.EntityFrameworkCore` package.

The smoke test must use the public registration API:

```csharp
services.AddRaffinertConsistency<ConsumerContext>(compiledModel, mappings);
```

Within one service scope, resolve:

```csharp
var concrete = provider.GetRequiredService<ConsistencyRuntime>();
var application = provider.GetRequiredService<IConsistencyRuntime>();
```

and fail the program if:

```csharp
!ReferenceEquals(concrete, application)
```

Also resolve `IConsistencyRuntime` twice in the same scope and ensure the reference is stable.

If practical without making the smoke test brittle, preserve or add one application-style materialization/save path using the interface.

Important constraints:

```text
- do not resolve a second independently-created runtime and compare it
- do not use project references
- do not bypass AddRaffinertConsistency<TDbContext>
- do not use reflection merely to check the interface exists when normal compilation/resolution can prove it
- do not replace existing useful package-consumer coverage unless necessary; extend it carefully
```

If adapting the current manual-UoW EF consumer into DI would delete valuable manual-UoW package coverage, prefer adding a small DI verification section rather than removing the existing scenario.

---

# 12. Strengthen the smoke test against accidental NuGet.org fallback

The release workflow currently restores with both:

```text
https://api.nuget.org/v3/index.json
local artifacts/packages
```

That is needed for third-party dependencies, but it means a stale Raffinert version can silently come from NuGet.org.

For `rc.2`, the version pin update in section 10 is mandatory because `0.2.0-rc.2` must not already exist on NuGet.org before publication.

Additionally verify after restore that each consumer resolved exactly `0.2.0-rc.2`.

Use a deterministic check. Preferred options, in order:

1. Inspect generated `obj/project.assets.json` for the exact Raffinert package version.
2. Use a suitable `dotnet list package`/equivalent command and fail on any version other than `0.2.0-rc.2`.
3. Add a small release script check if needed.

Do not rely only on console text saying restore succeeded.

The final release proof must explicitly establish:

```text
CoreNet8 -> Raffinert.Consistency/0.2.0-rc.2
CoreNet10 -> Raffinert.Consistency/0.2.0-rc.2
EfNet10 -> Raffinert.Consistency.EntityFrameworkCore/0.2.0-rc.2
EfNet10 transitive Core -> Raffinert.Consistency/0.2.0-rc.2
```

If the current workflow does not enforce this, add a small explicit verification step to `.github/workflows/release-candidate.yml` or an `eng/` helper. Keep it simple and release-specific; do not redesign CI.

---

# 13. Search for stale release-version references

Before building, run repository searches.

At minimum:

```bash
git grep -n "0.2.0-rc.1"
git grep -n "0.2.0-rc.2"
```

Review every `rc.1` hit.

Allowed `rc.1` hits include:

```text
- historical changelog section
- historical release plans
- historical roadmap records
- references explicitly describing the prior release
```

Not allowed as current configuration:

```text
- Directory.Build.props
- package-consumer PackageReference versions
- current release-line status
- current RC verification instructions intended for the next package
```

Do not blindly replace historical references.

Also verify interface and concrete declarations:

```bash
git grep -n "IConsistencyRuntime"
git grep -n "public sealed partial class ConsistencyRuntime"
git grep -n "virtual.*Materialize\|virtual.*Evaluate" -- src
```

The last search should find no mocking-driven virtual API additions.

---

# 14. Run the local release proof in this exact order

Do not skip a command because implementation tests passed before release metadata changes.

From repository root:

```bash
dotnet restore Raffinert.Consistency.sln
```

Then:

```bash
dotnet build Raffinert.Consistency.sln -c Release --no-restore
```

Expected:

```text
0 warnings
0 errors
```

Then Core tests:

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net10.0 --no-build
```

Expected current baseline is at least:

```text
362 passed on net8.0
362 passed on net10.0
```

If test count increases because a legitimate release-smoke test was added, that is acceptable. Any failure is a blocker.

Then EF tests:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj \
  -c Release -f net10.0 --no-build
```

Expected current baseline is at least:

```text
214 passed
```

Then all release samples:

```bash
dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj \
  -c Release --no-build
```

All three must exit successfully.

Then formatting:

```bash
dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

Must exit successfully with no formatting changes required.

---

# 15. Pack fresh `0.2.0-rc.2` artifacts

Delete old package output first so stale `rc.1` artifacts cannot satisfy later checks.

PowerShell example:

```powershell
if (Test-Path artifacts/packages) {
    Remove-Item artifacts/packages -Recurse -Force
}
New-Item artifacts/packages -ItemType Directory | Out-Null
```

Then pack:

```bash
dotnet pack Raffinert.Consistency.sln \
  -c Release --no-build \
  -o artifacts/packages
```

Expected package set is exactly:

```text
Raffinert.Consistency.0.2.0-rc.2.nupkg
Raffinert.Consistency.0.2.0-rc.2.snupkg
Raffinert.Consistency.EntityFrameworkCore.0.2.0-rc.2.nupkg
Raffinert.Consistency.EntityFrameworkCore.0.2.0-rc.2.snupkg
```

There must be no `0.2.0-rc.1` package in `artifacts/packages`.

---

# 16. Run mandatory release-candidate package verification

Run:

```powershell
./eng/VerifyReleaseCandidate.ps1 \
  -PackageDirectory artifacts/packages \
  -RequireEmptyUnshipped
```

This must pass.

The script derives expected version from `Directory.Build.props`; do not hard-code `rc.2` into the verifier unnecessarily.

Required implications of success:

```text
- exactly two nupkg files
- exactly two snupkg files
- package versions match Directory.Build.props
- MIT license metadata is correct
- repository URL is correct
- README and CHANGELOG are packed
- Core package has net8.0 and net10.0 assets
- EF package has net10.0 asset
- EF package depends on Core at exact same version
- both Unshipped API baselines are empty when -RequireEmptyUnshipped is used
```

If it fails, fix the actual cause. Do not weaken the verifier.

---

# 17. Run packed-package consumers against the local `rc.2` packages

For each project:

```text
tests/package-consumers/CoreNet8
tests/package-consumers/CoreNet10
tests/package-consumers/EfNet10
```

restore with both NuGet.org and the local package directory, as the workflow does:

```bash
PACKAGE_SOURCE="$PWD/artifacts/packages"

dotnet restore tests/package-consumers/CoreNet8 \
  --source https://api.nuget.org/v3/index.json \
  --source "$PACKAGE_SOURCE"

dotnet restore tests/package-consumers/CoreNet10 \
  --source https://api.nuget.org/v3/index.json \
  --source "$PACKAGE_SOURCE"

dotnet restore tests/package-consumers/EfNet10 \
  --source https://api.nuget.org/v3/index.json \
  --source "$PACKAGE_SOURCE"
```

Immediately verify the resolved Raffinert versions are exactly `0.2.0-rc.2` as required by section 12.

Only after version verification run:

```bash
dotnet run --project tests/package-consumers/CoreNet8 -c Release --no-restore
dotnet run --project tests/package-consumers/CoreNet10 -c Release --no-restore
dotnet run --project tests/package-consumers/EfNet10 -c Release --no-restore
```

All must exit with code 0.

Do not consider this gate green if the consumers ran against `0.2.0-rc.1`.

---

# 18. Inspect packed public API behavior, not only repository source

After packed consumers pass, make sure the package smoke proves the new feature is usable from a consumer assembly.

Required proof:

```text
[ ] consumer code compiles with public IConsistencyRuntime
[ ] consumer can assign a real ConsistencyRuntime to IConsistencyRuntime
[ ] interface Evaluate/Materialize is callable from the packed Core package
[ ] EF consumer can resolve IConsistencyRuntime from AddRaffinertConsistency<TDbContext>
[ ] resolved IConsistencyRuntime and ConsistencyRuntime are the same reference in a scope
```

This protects against mistakes where source/tests compile but PublicAPI/package content is not what consumers receive.

---

# 19. Confirm API baselines are truly frozen

Before creating the final release commit, check both files manually.

Core:

```text
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
```

must be exactly:

```text
#nullable enable
```

EF:

```text
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
```

must be exactly:

```text
#nullable enable
```

Confirm the four `IConsistencyRuntime` entries exist in:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
```

Do not release with those entries still unshipped.

---

# 20. Final diff review before commit

Review the final diff against the baseline.

Expected categories of change:

```text
Directory.Build.props
CHANGELOG.md
Core PublicAPI.Shipped.txt
Core PublicAPI.Unshipped.txt
possibly docs/roadmaps/README.md current release-line wording
three package-consumer .csproj version pins
package-consumer Program.cs changes needed to exercise IConsistencyRuntime
possibly a small workflow/helper check that proves exact restored package versions
```

Possible but not automatically required:

```text
README/docs minor version wording cleanup
```

Unexpected changes that require investigation:

```text
runtime algorithm changes
EF transaction/baseline changes
new public APIs beyond IConsistencyRuntime
unsealing ConsistencyRuntime
virtual runtime members
new dependencies in production packages
new mocking-framework package dependency
changes to relation/derived/invariant semantics
large documentation rewrites
```

If unexpected product behavior changed, stop and report it instead of hiding it inside release closeout.

---

# 21. Create one final release-preparation commit

After all local gates are green, create a final release-preparation commit.

Preferred commit message:

```text
release: prepare 0.2.0-rc.2
```

Push that commit to `main`.

Record the exact resulting SHA as:

```text
RELEASE_SHA=<exact full SHA>
```

From this point, do not change code or docs before exact-head RC verification. Any new commit means the previous workflow run is no longer exact-head proof.

---

# 22. Run exact-head GitHub Release Candidate verification

Trigger:

```text
.github/workflows/release-candidate.yml
```

using GitHub Actions **Run workflow** against `main` after the final release commit is pushed.

The workflow must run against exactly:

```text
RELEASE_SHA
```

Verify the run/job metadata, not merely that a recent run is green.

Every workflow step must be green:

```text
checkout
setup-dotnet
restore
Release build
Core net8 tests
Core net10 tests
EF net10 tests
Order fulfillment sample
EF Core sample
Dependency maintenance sample
format verification
pack
VerifyReleaseCandidate -RequireEmptyUnshipped
packed consumer smoke tests
artifact upload
```

If section 12 added an explicit exact-package-version verification step, that step must also be green.

If the workflow runs against another SHA, it does not prove the release commit.

If any step fails, status is **NOT RC-READY**.

Fix, commit, push, and run the workflow again against the new exact head.

---

# 23. Verify workflow artifacts after exact-head CI

Download/inspect the artifact produced by the successful exact-head workflow.

Expected artifact name:

```text
release-candidate-packages
```

Expected package filenames:

```text
Raffinert.Consistency.0.2.0-rc.2.nupkg
Raffinert.Consistency.0.2.0-rc.2.snupkg
Raffinert.Consistency.EntityFrameworkCore.0.2.0-rc.2.nupkg
Raffinert.Consistency.EntityFrameworkCore.0.2.0-rc.2.snupkg
```

These exact CI artifacts are the packages a human should later publish if publication is authorized.

Do not rebuild packages after the successful exact-head workflow merely for publication. Publishing rebuilt local binaries would break the chain of verification.

---

# 24. Human handoff only — do not execute

When and only when all previous gates are green, report:

```text
RC-READY
```

with:

```text
- final release SHA
- package version 0.2.0-rc.2
- Core/EF test counts
- sample status
- format status
- API baseline status
- packed consumer resolved versions
- exact-head workflow run ID
- workflow head SHA
- artifact name
- artifact package filenames
```

Then stop.

A human may separately choose to:

```text
1. create annotated tag v0.2.0-rc.2 on RELEASE_SHA
2. push the tag
3. create a GitHub prerelease
4. publish the exact verified Core nupkg/snupkg
5. wait for Core indexing
6. publish the exact verified EF nupkg/snupkg
7. perform NuGet.org-only consumer smoke tests
```

The coding agent must not perform those actions under this plan.

---

# 25. Explicit forbidden shortcuts

Do not do any of the following:

```text
- do not leave package consumers pinned to 0.2.0-rc.1
- do not accept a smoke test that restored rc.1 from NuGet.org
- do not skip checking project.assets.json/resolved package versions
- do not merely rename Unreleased to rc.2 without promoting PublicAPI
- do not delete PublicAPI.Unshipped entries instead of promoting reviewed APIs
- do not modify PublicAPI.Shipped to hide analyzer errors
- do not unseal ConsistencyRuntime
- do not make Evaluate/Materialize virtual
- do not widen IConsistencyRuntime beyond the three approved methods
- do not register IConsistencyRuntime as a separate implementation instance
- do not add NSubstitute/Moq to production or package-consumer projects
- do not change runtime/EF transactional semantics
- do not publish packages from the agent
- do not create the v0.2.0-rc.2 tag from the agent
- do not claim RC-ready without exact-head GitHub Actions proof
- do not publish locally rebuilt packages after CI verified different binaries
```

---

# 26. Completion report template

Use this exact style at the end:

```text
0.2.0-rc.2 closeout completed.

1. Final release commit: <SHA>
2. Version: 0.2.0-rc.2
3. IConsistencyRuntime public surface: 3 methods, unchanged
4. ConsistencyRuntime sealed: YES
5. Runtime methods made virtual: NO
6. EF interface/concrete same-scope identity: PASS
7. Core PublicAPI.Unshipped non-comment entries: 0
8. EF PublicAPI.Unshipped non-comment entries: 0
9. IConsistencyRuntime shipped entries promoted: 4
10. Core net8: <passed>/<total>
11. Core net10: <passed>/<total>
12. EF net10: <passed>/<total>
13. Samples: PASS/PASS/PASS
14. dotnet format: PASS
15. VerifyReleaseCandidate -RequireEmptyUnshipped: PASS
16. CoreNet8 resolved package: Raffinert.Consistency 0.2.0-rc.2
17. CoreNet10 resolved package: Raffinert.Consistency 0.2.0-rc.2
18. EfNet10 resolved package: Raffinert.Consistency.EntityFrameworkCore 0.2.0-rc.2
19. EfNet10 transitive Core: Raffinert.Consistency 0.2.0-rc.2
20. Packed consumer IConsistencyRuntime smoke: PASS
21. Exact-head workflow run: <RUN_ID>
22. Exact-head workflow SHA: <SHA>
23. Exact-head workflow result: SUCCESS
24. Artifact: release-candidate-packages
25. Tag created: NO
26. NuGet publication performed: NO
27. Remaining RC blocker: NONE

RC-READY for separately authorized human tag/publication steps.
```

If any required item is not true, do **not** print `RC-READY`. State the blocker precisely.
