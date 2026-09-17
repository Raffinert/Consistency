# Raffinert.Consistency 0.1.0-rc.1 release preparation

Status: **ACTIVE RELEASE-PREP PLAN — RESTARTED AFTER DAG HARDENING**

Current release-preparation baseline: `dddb1d819b7136b16061153715b69db63b68bf83`

The earlier RC proof was completed before Tasks 256–263 changed dependency-DAG runtime internals. Its package
artifacts, smoke tests, and CI evidence must not be reused. Restart every release verification gate from the
post-DAG implementation SHA above. The DAG wave made no public API change and exact-head CI #313 / run
`35245686551` succeeded, but this does not replace the release-candidate workflow and package-consumer proof
required by this plan.

Target release: **`0.1.0-rc.1`**

This plan is intentionally mechanical. It is written for a weak coding agent that must not improvise release policy, architecture, package versioning, or publication order.

The implementation/runtime is considered feature-frozen for this release-preparation wave. The purpose of this plan is to turn the already-green implementation into a truthful, reproducible, externally consumable RC artifact.

---

## 0. Non-negotiable scope

### Allowed work

Only change release-facing metadata, documentation, release verification, and the minimum workflow text required to verify and publish `0.1.0-rc.1`.

Expected files:

```text
Directory.Build.props
CHANGELOG.md
README.md
RELEASING.md
eng/VerifyReleaseCandidate.ps1
.github/workflows/release-candidate.yml
src/Raffinert.Consistency/Raffinert.Consistency.csproj
src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Consistency.EntityFrameworkCore.csproj
docs/release-candidate-verification.md
docs/roadmaps/README.md
```

### Forbidden work

Do NOT change:

```text
src/**/*.cs
tests/**/*.cs
samples/**/*.cs
public API shape
runtime semantics
EF consistency semantics
external-consumer-discovery semantics
architecture
benchmark logic
package IDs
namespaces
TFMs
EF Core dependency major/minor line
```

Do NOT add features while preparing the RC.

Do NOT “clean up” production code.

Do NOT rewrite `PublicAPI.Shipped.txt` unless the release gate proves it is actually inconsistent with the built public surface. Both `PublicAPI.Unshipped.txt` files are currently expected to remain empty.

If any production/test change becomes necessary, STOP this plan and create a separate implementation/fix plan. After that fix, this RC plan must restart from a new reviewed baseline.

---

# Task 247 — Freeze and record the RC baseline

Goal: make the release candidate work against one known repository state.

## 247.1 Confirm the starting head

Before editing anything:

```bash
git status --short
git rev-parse HEAD
```

Expected starting SHA for this plan:

```text
e0e23bda398871bfd46c6e920917c31506d27504
```

If `HEAD` differs:

1. inspect every intervening commit;
2. confirm whether any `src/**`, `tests/**`, `samples/**`, package project, or workflow behavior changed;
3. update this plan's baseline note before proceeding;
4. if runtime/test behavior changed, rerun the complete RC verification from scratch later.

Do not silently proceed from a different code baseline.

## 247.2 Record current facts

Confirm:

```text
main CI at starting head: green
core PublicAPI.Unshipped.txt: empty except #nullable enable
EF PublicAPI.Unshipped.txt: empty except #nullable enable
repository visibility: currently private
current Version: 0.1.0-alpha.1
```

No release tag or NuGet publication should be created in this task.

Commit only if documentation is changed.

---

# Task 248 — Change the package version to 0.1.0-rc.1

Goal: ensure every produced package and dependency uses the intended RC version.

## 248.1 Edit `Directory.Build.props`

Replace exactly:

```xml
<Version>0.1.0-alpha.1</Version>
```

with:

```xml
<Version>0.1.0-rc.1</Version>
```

Do not add per-project versions.

Both packages must inherit the same version from `Directory.Build.props`.

## 248.2 Verify no hardcoded package version remains

Search repository text for:

```text
0.1.0-alpha.1
alpha.1
```

Classify every match as one of:

```text
historical documentation — keep if clearly historical
current release metadata — update
current release verification text — update
obsolete release-plan wording — update if it can mislead the active RC process
```

Do not globally replace historical evidence in old roadmap/verification sections.

## 248.3 Expected package relationship

The produced EF package must depend on:

```text
Raffinert.Consistency = 0.1.0-rc.1
```

Do not use a range and do not leave the old alpha dependency.

Acceptance:

```text
Directory.Build.props has Version 0.1.0-rc.1
no active package metadata says 0.1.0-alpha.1
core and EF packages will resolve the same version
```

Suggested commit:

```text
release: set version to 0.1.0-rc.1
```

---

# Task 249 — Write a truthful RC changelog

Goal: make `CHANGELOG.md` describe the actual product being released, not the old pre-hardening alpha snapshot.

## 249.1 Preserve historical alpha section

Do NOT delete or rewrite the existing:

```markdown
## 0.1.0-alpha.1
```

Treat it as historical release-development context.

## 249.2 Add a new section above it

Add:

```markdown
## 0.1.0-rc.1
```

Use consumer-facing language. Do not dump internal task numbers or implementation details.

The section must cover at least these shipped capabilities that landed after the early alpha wording:

### Added

- explicit hidden source-member dependency declaration with `DependsOn(...)` for opaque source-derived calculations;
- authoritative EF consistency scope validation for cross-object enforcement/materialization;
- targeted, batched `DiscoverConsumers(...)` support for eligible direct EF reference-navigation consumers that may be unloaded;
- policy-aware manual consistency unit-of-work flow for caller-owned transactions/outbox/generated semantic values;
- generated INSERT/UPDATE semantic-value and FK-fixup handling described by the public EF workflow;
- store-side referential-action preflight for dangerous database cascade/set-null paths into consistency-managed state.

### Correctness / hardening

Summarize:

- exact binding-plan install after database durability;
- structural coverage admission separated from domain `ObjectAdded` semantics;
- ChangeTracker overlay for added/deleted/retargeted consumers;
- evaluation-closure validation for discovery resolvers;
- SQL failure/cancellation/retry/manual-UoW hardening;
- sink-only materialization contract;
- public API approval baseline and package-consumer smoke verification.

### Known limitations

State important RC limitations without marketing spin:

```text
- external consumer discovery currently supports eligible direct reference navigations only;
- multi-hop, collection-navigation, relation-source/target, and projected-consumer discovery are not substituted by DiscoverConsumers;
- ConsistencyRuntime is not thread-safe;
- raw SQL, ExecuteUpdate/ExecuteDelete, triggers mutating other rows, external writers, and other mutations invisible to EF/Raffinert require exact mutation publication or runtime reconciliation/rebuild;
- host query completeness and database concurrency/isolation remain application responsibilities.
```

## 249.3 Remove misleading terminal statement

The changelog currently ends with wording equivalent to:

```text
This alpha is not yet published automatically.
```

Move historical wording into the alpha section if needed, but the document as a whole must not end by describing the current release as alpha.

Acceptance:

```text
0.1.0-rc.1 is the first/top changelog section
alpha.1 history is preserved below
RC section mentions DiscoverConsumers and DependsOn
RC limitations are explicit
no current-release paragraph calls rc.1 alpha
```

Suggested commit:

```text
release: document 0.1.0-rc.1
```

---

# Task 250 — Align README and package metadata with RC status

Goal: make the README and `.nupkg` metadata tell the same story as the version number.

## 250.1 README status wording

Find the current wording:

```text
The project is currently experimental and alpha-oriented.
```

Replace it with conservative RC wording, for example:

```text
The project is pre-1.0 and currently available as a release candidate. APIs may still change before 1.0.
```

Do not claim production stability or semantic-version compatibility stronger than the repository's documented pre-1.0 policy.

## 250.2 Package release-note metadata

In both package projects:

```text
src/Raffinert.Consistency/Raffinert.Consistency.csproj
src/Raffinert.Consistency.EntityFrameworkCore/Raffinert.Consistency.EntityFrameworkCore.csproj
```

replace alpha-specific wording:

```xml
<PackageReleaseNotes>See CHANGELOG.md for alpha release notes.</PackageReleaseNotes>
```

with neutral/current wording such as:

```xml
<PackageReleaseNotes>See CHANGELOG.md for release notes.</PackageReleaseNotes>
```

Do not duplicate the entire changelog inside the csproj.

## 250.3 Verify package landing-page coherence

Because `README.md` and `CHANGELOG.md` are embedded in both packages, confirm there is no top-level text that tells an RC consumer they installed an alpha.

Acceptance:

```text
README calls current maturity release candidate / pre-1.0, not alpha-oriented
both package projects have neutral release-notes metadata
package README/changelog tell the same release story
```

Suggested commit:

```text
release: align package metadata with rc status
```

---

# Task 251 — Make release tooling version-neutral and RC-correct

Goal: ensure the release verifier and release documentation do not contain stale alpha-specific rules/messages.

## 251.1 `RELEASING.md`

Update current-process wording:

```text
Package publication is manual for the alpha series.
```

so it applies to prereleases/RC, for example:

```text
Package publication is manual for prerelease versions.
```

Preserve the documented version policy for `alpha`, `beta`, and `rc` labels.

Update the checklist sentence that says the verifier enforces the “current alpha.1 version”. It should say that the verifier enforces the version from `Directory.Build.props` and package/API consistency.

Do not weaken any release check.

## 251.2 `eng/VerifyReleaseCandidate.ps1`

The script already reads the expected version dynamically from `Directory.Build.props`. Keep that behavior.

Replace stale error text:

```text
The alpha.1 release gate requires an empty unshipped API baseline
```

with version-neutral wording, for example:

```text
The release gate requires an empty unshipped API baseline
```

Do NOT remove `-RequireEmptyUnshipped` for `rc.1`.

Do NOT remove any of these checks:

```text
2 nupkg files
2 snupkg files
non-empty shipped API baselines
empty unshipped API baselines when requested
package version equality
MIT license
repository URL
repository commit == GITHUB_SHA when available
README and CHANGELOG payloads
expected target-framework assets
no legacy package DLL/dependency
EF exact-version dependency on core
```

## 251.3 `.github/workflows/release-candidate.yml`

Change only stale comment wording such as:

```text
# Alpha publication remains an intentional manual action
```

into:

```text
# Prerelease publication remains an intentional manual action
```

The workflow must remain non-publishing and read-only.

Do not add NuGet secrets to this workflow.

Do not auto-publish on tag in this task.

Acceptance:

```text
release tooling contains no active alpha.1-only wording
verifier remains strict
automated RC workflow still cannot publish packages
```

Suggested commit:

```text
release: make rc verification version neutral
```

---

# Task 252 — Decide and enforce repository visibility before public publication

Goal: avoid publishing an OSS package whose repository/SourceLink destination is inaccessible to consumers.

Current reviewed repository state:

```text
visibility: private
RepositoryUrl: https://github.com/Raffinert/Consistency
SourceLink enabled
```

## 252.1 Decision gate

If `0.1.0-rc.1` is intended for public NuGet/OSS consumption, the repository must be made public before the package is publicly announced/published.

This is an account/repository administration action, not a code change.

The weak agent must NOT pretend it changed repository visibility by editing documentation.

If it lacks permission/API capability to change visibility, it must report:

```text
BLOCKED: repository remains private; public RC publication must not proceed.
```

If the intended destination is a private package feed for private consumers, record that explicitly and skip the public-visibility requirement.

## 252.2 After visibility change

Verify anonymously/publicly that these resolve:

```text
repository landing page
README
LICENSE
source files referenced by SourceLink/repository metadata
```

Do not publish first and “make it public later”.

Acceptance for public RC:

```text
Raffinert/Consistency is public
repository URL in package metadata is valid for unauthenticated consumers
MIT license visible
```

No commit is required for the visibility action itself unless release documentation needs a factual note.

---

# Task 253 — Run the exact release-candidate gate on the final release-prep commit

Goal: prove the exact code/docs/metadata that will be tagged and packed.

## 253.1 Freeze edits

After Tasks 248–252 repository-content edits are committed:

```bash
git status --short
```

must be clean.

Record:

```bash
git rev-parse HEAD
```

Call this SHA:

```text
RC_HEAD
```

No repository-content edit is allowed after RC_HEAD verification without invalidating the verification.

## 253.2 Ordinary CI

Wait for ordinary `CI` on `RC_HEAD`.

Require `success`.

It must cover at least:

```text
restore
Release build
Core tests net8.0
Core tests net10.0
EF tests net10.0
order-fulfillment sample
generic EF sample
dependency-maintenance sample
format verification
pack
packed-package consumer smoke tests
artifact upload
```

If ordinary CI fails, STOP. Do not tag or publish.

## 253.3 Manual Release Candidate Verification workflow

Run `.github/workflows/release-candidate.yml` against `RC_HEAD`.

Require `success`.

This workflow must execute:

```text
eng/VerifyReleaseCandidate.ps1 -RequireEmptyUnshipped
```

If the workflow was run on a different SHA, it does not count.

## 253.4 Inspect artifact metadata

Download the `release-candidate-packages` artifact from the exact RC workflow run.

Verify there are exactly:

```text
Raffinert.Consistency.0.1.0-rc.1.nupkg
Raffinert.Consistency.0.1.0-rc.1.snupkg
Raffinert.Consistency.EntityFrameworkCore.0.1.0-rc.1.nupkg
Raffinert.Consistency.EntityFrameworkCore.0.1.0-rc.1.snupkg
```

Inspect package metadata/payload and confirm:

```text
version == 0.1.0-rc.1
repository URL == https://github.com/Raffinert/Consistency
repository commit == RC_HEAD
license == MIT
README.md present
CHANGELOG.md present
core contains net8.0 and net10.0 assets
EF contains net10.0 asset
EF depends exactly on core 0.1.0-rc.1
no Raffinert.Relations payload/dependency
```

Do not substitute locally rebuilt packages for the verified workflow artifact when recording final evidence.

---

# Task 254 — Perform an external packed-consumer sanity test

Goal: prove the RC artifact behaves like a consumer package, not only like projects inside the source solution.

The workflow already runs repository package-consumer projects; keep that as the formal automated gate.

Additionally, before publication, manually or via a temporary external folder create at least one clean consumer that references the verified packages from an artifact/local package source and uses only public APIs.

Minimum useful scenario:

```text
new console/test project
add Raffinert.Consistency 0.1.0-rc.1
build a simple ObjectSet + Derived model
build runtime
mutate an object
report/apply change
read derived result
```

For EF package sanity:

```text
new net10.0 project
add Raffinert.Consistency.EntityFrameworkCore 0.1.0-rc.1
restore transitive core package at exact rc.1 version
compile a minimal ConsistencyEfCoreMappings setup
```

Do not copy source projects or use project references.

Acceptance:

```text
fresh public API consumer restores from package artifacts
no internal APIs required
core and EF dependency versions align
```

This task does not replace CI; it catches packaging/ergonomics mistakes before publication.

---

# Task 255 — Record final RC evidence, tag, and publish manually

Goal: make release evidence reproducible and prevent accidental publication from an unverified SHA.

## 255.1 Update `docs/release-candidate-verification.md`

Append a new top/current section for `0.1.0-rc.1` with:

```text
verification date
RC_HEAD exact SHA
ordinary CI run ID/number + success
Release candidate verification run ID/number + success
artifact ID/name
core test count net8.0
core test count net10.0
EF test count net10.0
samples passed
format passed
package validation passed
packed consumers passed
package version 0.1.0-rc.1
PublicAPI.Unshipped empty for both packages
repository visibility at publication time
publication status
```

Important: this documentation commit necessarily creates a SHA after the verified `RC_HEAD`.

Do NOT create an infinite self-referential verification loop.

Use this rule:

```text
RC_HEAD = exact source/package commit used to create and verify release artifacts.
Post-verification evidence docs may be committed afterward.
The release tag MUST point to RC_HEAD, not to a later evidence-only commit, unless you rerun all release verification for the later commit.
```

If README/CHANGELOG/package metadata changes after RC_HEAD, that is NOT “evidence-only”; verification is invalid and must be rerun.

## 255.2 Tag

Only after all gates pass, create the release tag on `RC_HEAD`:

```text
v0.1.0-rc.1
```

Prefer an annotated/signed tag according to the maintainer's release practice.

Before pushing the tag, verify:

```bash
git rev-parse v0.1.0-rc.1^{}
```

matches `RC_HEAD`.

Do not move/reuse an existing published version tag.

## 255.3 Manual NuGet publication

Publication remains manual.

Push only the two verified `.nupkg` files from the exact release-candidate artifact, not ad-hoc rebuilt packages.

Do not publish `.snupkg` separately if `dotnet nuget push` uploads symbols through the normal symbol package mechanism associated with the package/push configuration; follow NuGet's actual push behavior in the release environment.

Use a scoped API key.

Do not paste the key into logs, markdown, shell history committed to the repo, or GitHub comments.

## 255.4 Post-publication verification

After NuGet.org reports package availability:

Create a fresh external project using **NuGet.org only** and install:

```text
Raffinert.Consistency 0.1.0-rc.1
```

and separately:

```text
Raffinert.Consistency.EntityFrameworkCore 0.1.0-rc.1
```

Verify:

```text
restore succeeds
correct version selected
EF pulls exact matching core rc.1
README/repository/license links look correct
minimal consumer builds/runs
symbols/source navigation works where expected
```

Only then announce the RC.

---

# Final release checklist

The agent may say **“0.1.0-rc.1 is ready to publish”** only if every box is true:

```text
[ ] No production/runtime/test changes were mixed into RC prep.
[ ] Directory.Build.props = 0.1.0-rc.1.
[ ] CHANGELOG has a truthful top-level 0.1.0-rc.1 section.
[ ] README no longer calls the current release alpha-oriented.
[ ] Both package projects use neutral/current release-note metadata.
[ ] RELEASING.md describes prerelease/RC publication correctly.
[ ] VerifyReleaseCandidate.ps1 contains no alpha.1-specific active error wording.
[ ] Release candidate workflow remains non-publishing and strict.
[ ] Core PublicAPI.Unshipped is empty.
[ ] EF PublicAPI.Unshipped is empty.
[ ] Public repository visibility is confirmed if this is a public NuGet RC.
[ ] Ordinary CI is green on exact RC_HEAD.
[ ] Release Candidate Verification workflow is green on exact RC_HEAD.
[ ] The verified artifact contains exactly two nupkg + two snupkg files.
[ ] Package versions are exactly 0.1.0-rc.1.
[ ] EF package depends exactly on core 0.1.0-rc.1.
[ ] Repository commit metadata equals RC_HEAD.
[ ] Packed consumer tests pass.
[ ] Fresh external consumer sanity test passes.
[ ] release evidence is recorded.
[ ] v0.1.0-rc.1 tag points exactly to RC_HEAD.
[ ] Only verified artifacts are published.
[ ] NuGet.org restore test passes after publication.
```

If any box is false, do not publish and do not claim release readiness.

---

# Anti-shortcut rules for weak agents

Never do these:

```text
- publish while repository remains private when the intended package is public OSS;
- call a green ordinary CI run equivalent to the stricter Release Candidate Verification workflow;
- publish 0.1.0-alpha.1 and merely call it “RC” in release notes;
- change package version in only one project;
- remove -RequireEmptyUnshipped to make the verifier pass;
- rewrite PublicAPI.Shipped.txt just to silence an analyzer;
- add public API during release prep;
- change src/**/*.cs while calling the commit docs/release-only;
- tag a documentation commit that was not the verified package-producing SHA;
- rebuild packages locally after verification and publish those different bytes;
- globally replace historical alpha wording in old verification evidence;
- skip package artifact inspection because tests are green;
- assume SourceLink/repository URLs are useful to users while the repository is private;
- publish first and run the external NuGet.org consumer test later if a pre-publication blocker is already known.
```

The objective is not “make the release script green.”

The objective is:

> publish exactly the reviewed, tested, versioned, documented, externally inspectable `0.1.0-rc.1` package artifacts and nothing else.
