# Codex plan — `0.2.0-rc.1` final release-candidate closeout

Status: **FINAL RC PREPARATION — NO NEW FEATURES, NO ARCHITECTURE REDESIGN**

Audience: an extremely weak coding agent. Follow this document literally and in order. Do not improvise. Do not broaden scope. Do not tag or publish anything unless every release gate below is green.

Baseline implementation commit:

```text
ed6088bd94e5d8278c782f3cf25d7b5c009a3db1
```

The runtime/API/EF work is considered functionally complete for the release candidate. This plan is release closeout only.

---

# 0. Goal

Prepare one exact release commit for:

```text
0.2.0-rc.1
```

The final reviewed commit must satisfy all of these simultaneously:

```text
- current public API has no historical V2 naming
- no removed compatibility API returns
- both PublicAPI.Unshipped.txt files are empty except #nullable enable
- all approved current APIs are in PublicAPI.Shipped.txt
- Directory.Build.props version is 0.2.0-rc.1
- CHANGELOG current entry is 0.2.0-rc.1 and describes the current API without v1/v2 migration vocabulary
- README/docs/skill describe one current API, not an API generation migration
- Release build is clean
- all tests pass
- samples run
- format verification is clean
- packages build
- VerifyReleaseCandidate.ps1 -RequireEmptyUnshipped passes
- packed-package consumer smoke tests pass
- exact-head GitHub release-candidate workflow is green
```

Only after that exact commit is green may a human decide to create tag:

```text
v0.2.0-rc.1
```

This plan does **not** authorize automatic NuGet publication or repository visibility changes.

---

# 1. Frozen product/application behavior

Do not change runtime behavior unless a release gate exposes a concrete bug.

Do not redesign:

```text
From(...)
DependsOn(...)
Select(...)
recognized Sum / Count / LongCount / Any
MaterializeTo(...)
Evaluate(...)
Materialize(definition, source)
Materialize(source)
Invariant(...)
ScheduleRepairWith(...)
AddRaffinertConsistency<TDbContext>(...)
```

Do not change the intended injected EF application UX:

```csharp
public sealed class LinkService(
    AppDbContext db,
    ConsistencyRuntime consistency)
{
    public async Task ChangeAsync(
        long id,
        decimal newValue,
        CancellationToken cancellationToken)
    {
        var link = await db.Links
            .Include(x => x.Left)
            .Include(x => x.Right)
            .SingleAsync(x => x.Id == id, cancellationToken);

        link.Left.Value = newValue;

        consistency.Materialize(link); // only when mirrors are needed before save

        Use(link.Ratio);
        Use(link.NormalizedRatio);

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

Do not reintroduce application-level:

```text
runtime.Add(...)
runtime.Apply(...)
manual old values
ObjectSet<T> plumbing
ConsistencyEfCoreMappings in application services
custom SaveChanges wrapper requirements
manual consistency UoW for ordinary stable-key EF saves
```

---

# 2. First task: eliminate the last public `V2` name before API freeze

Current public code contains:

```csharp
public static class DerivedV2Extensions
```

This name must not be shipped in `0.2.0-rc.1`.

## 2.1 Rename the public extension container

Rename:

```text
DerivedV2Extensions
```

to:

```text
DerivedBuilderExtensions
```

Preferred file rename:

```text
src/Raffinert.Consistency/Derived/DerivedV2Extensions.cs
    ->
src/Raffinert.Consistency/Derived/DerivedBuilderExtensions.cs
```

Expected shape:

```csharp
namespace Raffinert.Consistency;

public static class DerivedBuilderExtensions
{
    public static Derived<TSource, TValue> Select<TSource, TValue>(
        this DerivedBuilder<TSource> builder,
        Func<TSource, TValue> computation)
        where TSource : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.SelectOpaque(computation);
    }
}
```

Preserve method behavior exactly.

Do not:

```text
- rename Select
- change overload resolution
- change the Func<TSource,TValue> opaque-calculator semantics
- add another compatibility class called DerivedV2Extensions
- add an obsolete alias for DerivedV2Extensions
```

There is no reason to carry the historical class name into the RC surface.

## 2.2 Search for current-facing `v2` / `V2` vocabulary

Search at minimum:

```bash
git grep -n -E '\b(v2|V2)\b' -- \
  README.md \
  CHANGELOG.md \
  RELEASING.md \
  src \
  docs/ef-core-consistency.md \
  .agents/skills
```

Do **not** blindly rewrite historical archived plans merely because they contain `v2`.

Current-facing sources must read as though the current API is simply the Raffinert.Consistency API.

Historical plan filenames/content may keep historical wording if they are clearly archival/internal history.

---

# 3. Update public API approval files for the rename before promotion

Before moving unshipped APIs to shipped, make the rename visible to PublicAPI tooling.

The Core `PublicAPI.Unshipped.txt` currently contains the current public API additions, including the old class name.

Update approval entries so the surface contains:

```text
Raffinert.Consistency.DerivedBuilderExtensions
static Raffinert.Consistency.DerivedBuilderExtensions.Select<...>(...)
```

and does **not** contain:

```text
Raffinert.Consistency.DerivedV2Extensions
```

Run the relevant build/API analyzer before proceeding.

Do not manually delete arbitrary PublicAPI entries to silence analyzers.

If the compiler/API analyzer reports an unexpected public API, inspect the actual public source and decide whether it belongs in the RC surface.

---

# 4. Freeze the final current public surface

This is the compatibility freeze for `0.2.0-rc.1`.

Files:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
```

## 4.1 Review Core unshipped entries

Review every non-comment Core unshipped API.

Expected categories include current APIs such as:

```text
From(...)
DependsOn(...)
Select(...)
DerivedRelationBuilder aggregate operations
MaterializeTo(...)
ConsistencyRuntime.Evaluate(...)
ConsistencyRuntime.Materialize(...)
current derived dependency/materialization diagnostics
current builder types required by fluent chaining
```

Check that there is no historical compatibility API such as removed aliases.

Specifically verify absence of old compatibility vocabulary including, where applicable:

```text
Using
Compute
Incrementally
ConsistencyRuntime.Get alias
old *UsingBuilder names
explicit EF Materialize mapping API that was removed
DerivedV2Extensions
```

Do not reject a legitimate public builder type merely because users normally see it through fluent chaining. If it is public in compiled metadata, it is part of the public surface and must be intentionally approved.

## 4.2 Review EF unshipped entries

Review every EF unshipped API.

At minimum the current DI registration API is expected:

```csharp
AddRaffinertConsistency<TDbContext>(...)
```

Approve only APIs that are intentionally part of the RC surface.

## 4.3 Promote approved APIs

After review:

```text
append/move all approved non-comment Unshipped entries into the matching Shipped file
```

Preserve PublicAPI file formatting conventions.

After promotion, both Unshipped files must contain only:

```text
#nullable enable
```

No API lines.

Required verification:

```powershell
./eng/VerifyReleaseCandidate.ps1 -PackageDirectory artifacts/packages -RequireEmptyUnshipped
```

will later fail if this is not true.

---

# 5. Set the release version to `0.2.0-rc.1`

Update:

```text
Directory.Build.props
```

from:

```xml
<Version>0.2.0-alpha.1</Version>
```

exactly to:

```xml
<Version>0.2.0-rc.1</Version>
```

Do not set:

```text
0.2.0
0.2.0-rc
0.2.0-rc1
0.2.0-beta.*
```

The exact intended version is:

```text
0.2.0-rc.1
```

Do not set package project versions independently unless repository structure explicitly requires it. `Directory.Build.props` is the authoritative shared version today.

---

# 6. Rewrite the current CHANGELOG entry as the RC entry

Current `CHANGELOG.md` has a section:

```text
## 0.2.0-alpha.1
```

Rename it to:

```text
## 0.2.0-rc.1
```

The section must describe the current public product, not an internal migration story.

## 6.1 Remove current-facing v1/v2 migration vocabulary

Avoid wording such as:

```text
pre-v2
v2 fluent stages
v2 chaining
API v1/API v2
```

Prefer current-state wording, for example:

```text
### Public API
- Standardized derived declarations around From, DependsOn, Select, and recognized aggregates.
- Added logical Evaluate and targeted/object Materialize runtime operations.
- Added MaterializeTo as the single declaration for physical derived mirrors.
- Added typed local, relation, projected, and mixed derived composition.

### EF Core integration
- Added scoped DI integration with ordinary SaveChanges/SaveChangesAsync interception.
- Added automatic tracked baseline admission and EF-aware Materialize(entity).
- Added pending-plan reuse/invalidation bound to runtime and baseline revisions.
- Added transactional rollback/retry protections for materialization and tracked baseline changes.

### Correctness and diagnostics
...
```

It is acceptable for historical older release sections to describe what was true at the time, but the `0.2.0-rc.1` section should not teach consumers that there is a current API-generation split.

## 6.2 Keep known limitations honest

Ensure current known limitations remain documented somewhere appropriate, especially:

```text
- ConsistencyRuntime is not thread-safe
- external consumer discovery scope limitations
- raw SQL / ExecuteUpdate / ExecuteDelete / triggers / external writers are invisible unless explicitly reconciled/published
- authoritative query completeness and DB concurrency/isolation remain host responsibilities
```

Do not remove limitations merely to make the RC look cleaner.

---

# 7. Align README current-state wording

README already describes the package as a release candidate.

Verify it is consistent with `0.2.0-rc.1` and current APIs.

Required checks:

```text
- no current-facing API v1/API v2 wording
- no DerivedV2Extensions naming
- injected EF example remains DbContext + ConsistencyRuntime + Materialize(entity) + ordinary SaveChanges
- logical Evaluate vs physical Materialize distinction remains clear
- current package TFMs remain .NET 8/.NET 10 Core and .NET 10 EF adapter
- release-candidate wording is truthful once version is 0.2.0-rc.1
```

Do not rewrite the README wholesale.

Do not reintroduce a migration guide into the top-level README.

---

# 8. Align current docs and coding skill

Review current-facing documentation only:

```text
README.md
docs/ef-core-consistency.md
docs/architecture.md
.agents/skills/raffinert-consistency-consumer/SKILL.md
.agents/skills/raffinert-consistency-consumer/references/recipes.md
.agents/skills/raffinert-consistency-consumer/references/verification.md
```

Required rule:

> They must describe the current API as one API, without instructing users about removed compatibility APIs.

Do not add negative migration examples such as:

```text
Do not use old Using(...)
Do not use API v1
Use API v2 instead
```

The removed API should simply not exist in current consumer guidance.

Historical internal plans are not consumer guidance and do not need mass rewriting.

---

# 9. Update roadmap current-status line

Update:

```text
docs/roadmaps/README.md
```

The first/current line must no longer say:

```text
CURRENT API LINE: 0.2.0-alpha.1 ... v2 ...
```

Replace it with a concise RC status, for example:

```text
- **CURRENT RELEASE LINE:** `0.2.0-rc.1` freezes the current public API and injected EF Core workflow; historical implementation plans below are retained only as design/verification history.
```

Do not erase historical roadmap entries.

Do not rewrite every old plan.

---

# 10. Search for accidental release blockers before building

Run repository searches.

## 10.1 Current-facing V2 names

```bash
git grep -n -E '\b(v2|V2)\b' -- \
  README.md \
  CHANGELOG.md \
  RELEASING.md \
  src \
  docs/ef-core-consistency.md \
  docs/architecture.md \
  .agents/skills
```

Review every hit.

A historical/internal filename may remain if not public/current-facing, but no public type/API or current consumer guidance should contain the API-generation naming.

## 10.2 Removed public compatibility vocabulary

Search relevant source/PublicAPI files for removed public API names.

Do not use a naive substring search that creates false positives from ordinary English prose; inspect each hit.

## 10.3 Release version consistency

Search:

```bash
git grep -n "0.2.0-alpha.1"
git grep -n "0.2.0-rc.1"
```

After updates, no current release metadata should still identify `0.2.0-alpha.1` as the current line.

Historical plan references may mention the old version if clearly historical.

---

# 11. Local release proof — run in this exact order

Do not skip commands because targeted tests passed.

From repository root:

```bash
dotnet restore Raffinert.Consistency.sln
```

Then:

```bash
dotnet build Raffinert.Consistency.sln -c Release --no-restore
```

Then Core tests on both TFMs:

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net10.0 --no-build
```

Then EF Core tests:

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj \
  -c Release -f net10.0 --no-build
```

Then all release workflow samples:

```bash
dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj \
  -c Release --no-build
```

Then formatting:

```bash
dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

Then pack:

```bash
rm -rf artifacts/packages
mkdir -p artifacts/packages

dotnet pack Raffinert.Consistency.sln \
  -c Release --no-build \
  -o artifacts/packages
```

On PowerShell use the platform-equivalent directory cleanup commands; do not change pack semantics.

---

# 12. Mandatory release-candidate verification script

Run:

```powershell
./eng/VerifyReleaseCandidate.ps1 \
  -PackageDirectory artifacts/packages \
  -RequireEmptyUnshipped
```

This is a hard gate.

Do not bypass or edit the script merely to make the release pass.

If it fails, fix the underlying release state.

Expected enforced conditions include:

```text
- package version matches Directory.Build.props
- exactly two nupkg packages
- exactly two snupkg packages
- shipped API baselines are non-empty
- unshipped API baselines contain no API entries
- README and CHANGELOG are packed
- repository URL is correct
- no legacy Raffinert.Relations DLL/dependency payload
- Core package contains net8.0 and net10.0 assets
- EF package contains net10.0 asset
- EF package depends on the exact same Raffinert.Consistency version
```

---

# 13. Mandatory packed-package consumer smoke tests

Run the same smoke flow as `.github/workflows/release-candidate.yml` against the packages just built.

Conceptually:

```bash
PACKAGE_SOURCE="$PWD/artifacts/packages"

for project in CoreNet8 CoreNet10 EfNet10; do
  dotnet restore "tests/package-consumers/$project" \
    --source https://api.nuget.org/v3/index.json \
    --source "$PACKAGE_SOURCE"

  dotnet run --project "tests/package-consumers/$project" \
    -c Release --no-restore
done
```

Use the correct shell syntax for the environment.

Do not let package-consumer projects resolve a previously installed/remote version instead of the local RC packages.

Confirm package version in restore output/assets if necessary.

---

# 14. Inspect package contents manually

Inspect both `.nupkg` and both `.snupkg` outputs.

For each package verify:

```text
package ID
version == 0.2.0-rc.1
README.md included
CHANGELOG.md included
MIT license expression
repository URL == https://github.com/Raffinert/Consistency
repository commit metadata present/correct in CI build
expected target frameworks only
symbol package exists
SourceLink/repository metadata present
no legacy Raffinert.Relations payload
```

Core expected library assets:

```text
lib/net8.0/Raffinert.Consistency.dll
lib/net10.0/Raffinert.Consistency.dll
```

EF expected library asset:

```text
lib/net10.0/Raffinert.Consistency.EntityFrameworkCore.dll
```

Do not publish if package structure differs unexpectedly.

---

# 15. Commit discipline

After all code/docs/API-baseline/version changes pass locally, create one release-preparation commit.

Recommended commit message:

```text
release: prepare 0.2.0-rc.1
```

The exact release commit SHA must be recorded in the completion report.

Do not create a release tag yet.

Do not publish NuGet packages yet.

Push the release-preparation commit to `origin/main` only after local proof is green.

---

# 16. Exact-head GitHub release-candidate workflow is a hard gate

The repository already contains:

```text
.github/workflows/release-candidate.yml
```

It is `workflow_dispatch` and runs the authoritative RC verification matrix.

After pushing the release-preparation commit:

```text
1. trigger "Release candidate verification"
2. ensure the workflow is running against the exact release commit SHA
3. wait for completion
4. inspect every job/step
5. require green success
6. record workflow run ID and URL
```

Do not accept:

```text
- green CI from an older commit
- local tests only
- normal CI from a different SHA
- a partially completed workflow
- a workflow where package verification was skipped
```

The exact release commit must have a green RC workflow.

If the workflow fails:

```text
fix issue
create a new commit
push
rerun RC workflow on the new exact SHA
```

The old failed SHA is not releaseable.

---

# 17. Tag gate

Only after the exact-head RC workflow is green may a human or explicitly authorized release step create:

```text
v0.2.0-rc.1
```

The tag must point exactly to the verified release commit SHA.

Do not tag an earlier/later commit.

Do not create the tag merely because local proof passed.

This plan does not authorize automatic tag creation unless the user explicitly asked the agent to create the tag.

---

# 18. NuGet publication gate

Package publication remains manual according to `RELEASING.md`.

Do not run `dotnet nuget push` unless explicitly authorized by the user after the verified RC commit/tag exists.

Do not change repository visibility as part of this plan.

The repository is currently private; that is a product/release decision separate from code correctness.

If public NuGet publication is later requested, report any implications of repository visibility/SourceLink/repository links before changing settings.

---

# 19. Required regression checks before declaring done

The following must still be true after release cleanup:

```text
[ ] Current derived declarations use From / DependsOn / Select.
[ ] Recognized Sum / Count / LongCount / Any still compile and pass tests.
[ ] Evaluate is logical-only.
[ ] Targeted Materialize still synchronizes only requested definition.
[ ] Object Materialize still synchronizes configured mirrors on the exact source object.
[ ] EF injected runtime remains scoped.
[ ] Ordinary SaveChanges/SaveChangesAsync still performs consistency synchronization.
[ ] Explicit Materialize before Save still reuses unchanged pending plan.
[ ] BaselineRevision semantics are unchanged.
[ ] Failed SQL still leaves committed runtime baseline unchanged.
[ ] External discovery rebuild behavior remains covered.
[ ] First-bind relevance filtering remains member-based.
[ ] Mapped irrelevant properties remain ordinary EF state.
[ ] No removed compatibility aliases reappear.
```

Do not weaken or delete tests simply because PublicAPI/version changes make them inconvenient.

---

# 20. Required final repository state

Before reporting work done, verify all of these literally.

## Version

```text
Directory.Build.props == 0.2.0-rc.1
```

## Public API

```text
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
    only #nullable enable

src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
    only #nullable enable
```

`PublicAPI.Shipped.txt` files include the approved current RC surface.

## Naming

```text
DerivedBuilderExtensions exists
DerivedV2Extensions does not exist as a public type
```

## Current documentation

```text
README describes current API only
CHANGELOG has ## 0.2.0-rc.1
roadmap current line says 0.2.0-rc.1
consumer skill describes current API only
```

## Proof

```text
Release build green
Core net8 tests green
Core net10 tests green
EF net10 tests green
all three release samples green
format verification green
pack green
VerifyReleaseCandidate -RequireEmptyUnshipped green
CoreNet8 packed consumer green
CoreNet10 packed consumer green
EfNet10 packed consumer green
exact-head GitHub RC workflow green
```

---

# 21. Anti-dumb forbidden shortcuts

Do not do any of the following:

```text
- do not leave version at alpha.1 and merely rename CHANGELOG
- do not claim RC-ready while PublicAPI.Unshipped contains APIs
- do not edit VerifyReleaseCandidate.ps1 to relax gates
- do not delete legitimate public APIs from approval files to make the gate pass
- do not retain DerivedV2Extensions as Obsolete compatibility API
- do not create another API-generation suffix such as V3
- do not mass-rewrite archived historical plans
- do not redesign runtime or EF semantics during release cleanup
- do not remove tests
- do not skip net8 Core tests
- do not skip package consumer tests
- do not use Debug builds as release proof
- do not accept CI from another commit SHA
- do not tag before exact-head RC workflow success
- do not publish NuGet without explicit authorization
- do not change repository visibility
```

---

# 22. If a release gate exposes a real bug

Stop the mechanical release sequence and fix only the concrete bug.

Then rerun from the appropriate earlier gate.

Any source-code fix after package/API/version proof means at minimum rerun:

```text
Release build
all tests
all samples
format
pack
VerifyReleaseCandidate
packed consumer smoke tests
exact-head RC workflow
```

Do not assume a small fix cannot affect package/API behavior.

---

# 23. Required completion report

Do not report only "done".

Return exactly the following information:

```text
1. Files changed.
2. Final release commit SHA.
3. Confirmed package version.
4. Final public extension-container name replacing DerivedV2Extensions.
5. Core PublicAPI.Unshipped non-comment entry count.
6. EF PublicAPI.Unshipped non-comment entry count.
7. Core PublicAPI.Shipped promotion summary.
8. EF PublicAPI.Shipped promotion summary.
9. Current-facing v2/V2 search result summary.
10. Removed-compatibility API search result summary.
11. Release build result and warning/error count.
12. Core net8 test count/result.
13. Core net10 test count/result.
14. EF net10 test count/result.
15. Three sample execution results.
16. dotnet format verification result.
17. Pack result and exact package filenames.
18. VerifyReleaseCandidate -RequireEmptyUnshipped result.
19. CoreNet8/CoreNet10/EfNet10 packed consumer results.
20. Package inspection summary.
21. Exact-head GitHub RC workflow run ID, URL, commit SHA, and result.
22. Whether a tag was created. Expected: NO unless separately authorized.
23. Whether NuGet packages were published. Expected: NO unless separately authorized.
24. Any remaining RC blocker. Expected: NONE before declaring RC-ready.
```

If item 21 is not green on the exact final release commit, the correct completion status is:

```text
NOT RC-READY
```

not "done".

---

# 24. Definition of Done

This plan is complete only when:

```text
- the public API is frozen without historical generation naming
- both Unshipped API files are empty of API entries
- repository release metadata says 0.2.0-rc.1 consistently
- all local release proof is green
- packages pass strict RC verification
- packed consumers pass
- the exact final commit has a green Release candidate verification workflow
```

At that point the codebase is ready for the human release action:

```text
tag v0.2.0-rc.1
manual NuGet publication if desired
```

Do not invent additional feature work as part of this release closeout.
