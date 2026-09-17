# Codex implementation plan — external consumer discovery final documentation closeout

Status: **ACTIVE DOCS-ONLY CLOSEOUT PLAN**

Baseline repository head reviewed: `197913a329f6de006798d89b4bfb3eb428de99e9`

Verified exact-head CI for that baseline:

- workflow: `CI`
- run number: **288**
- run id: **35216826855**
- head SHA: `197913a329f6de006798d89b4bfb3eb428de99e9`
- conclusion: **success**

Tasks: **244–246**

This plan exists only because the implementation/proof work is complete enough to close, while the repository documentation still says that Task 243 is active and still records the previous implementation head as the release-verification anchor.

This is **not another implementation wave**.

Do not change production code, test code, public API, samples, runtime behavior, or package behavior while executing this plan.

---

# 0. Why this closeout exists

The post-`ccfb24f` gap-closeout wave already landed the remaining implementation and proof work.

The important implementation/test commits are:

```text
cbe5387e1318be66548c50b99aaffbc4c23c91f1
    fix: capture relation-right admission dependency state

eba8ec9d43a9f26d04fabb77cd80659945219008
    test: complete discovery identity and evaluation closure proof

159e3d2ce13e96f106c13345aa21fd188b01008d
    test: complete discovery scope substitution boundaries

fcd5eb1960b4a5c46a955334405f980b3b12fe41
    test: complete discovery overlay batching and resolver guards

ed383a919ca0a41fe75a2c00a59de265de4cbf5a
    test: prove discovery durability and manual uow atomicity

197913a329f6de006798d89b4bfb3eb428de99e9
    docs: record external discovery closeout proof
```

The current code/test tree proves the supported direct-reference-navigation discovery contract through focused Core and EF tests.

The exact current baseline head `197913a329f6de006798d89b4bfb3eb428de99e9` also has a successful full repository CI run.

That CI run executes:

```text
restore
build
Core tests on .NET 8
Core tests on .NET 10
EF Core adapter tests on .NET 10
order-fulfillment dogfood scenarios
EF consistency sample
dependency-maintenance dogfood sample
format verification
pack
packed-package smoke test
artifact upload
```

Therefore the remaining work is documentation truthfulness and roadmap state only.

---

# 1. Frozen technical conclusion

The closeout must preserve the following conclusion exactly in substance:

> External consumer discovery for the supported direct-reference-navigation case is operation-scoped, structurally atomic, exact-ObjectSet-aware, ChangeTracker-overlay-aware, correctly batched, fail-closed outside its supported coverage boundary, retry-safe across persistence failure, compatible with manual persistence units of work, and does not confuse structural baseline admission with domain lifecycle addition.

Do not broaden this statement.

In particular, completion of this wave does **not** claim support for:

- multi-hop external discovery;
- collection-navigation external discovery;
- relation-predicate consumer discovery;
- projected-consumer discovery;
- automatic resolver query generation;
- automatic `Include(...)` generation;
- automatic `Reference.Load()`;
- lazy-loading based correctness;
- partition/key/tenant scoped `ConsistencyScope`;
- non-EF discovery providers;
- CDC/background reconciliation;
- cross-process serializability;
- automatic proof that a host resolver did not under-fetch;
- reconstruction of previously unknown existing Modified/Deleted roots;
- public structural-admission APIs.

These remain product boundaries, not unfinished items in this closeout.

---

# 2. Evidence that must be preserved

Do not remove or weaken references to the following proof.

## 2.1 Structural admission / Core planning proof

`CoverageAdmissionPlanningTests` contains focused proof for:

```text
coverage admission on relation right is invisible after planning
coverage admission on relation left is invisible after planning
relation baseline installs exactly once after exact plan commit
relation-right admission captures affected-left dependency state
relation-right planning restores primed derived state
relation-right exact install rebases derived state without semantic add
relation-right planning restores primed invariant state
forward-patch install failure restores relation state
forward-patch install failure restores relation + projection state
projection state is invisible after planning
projection state installs exactly once after exact plan commit
dependency patch scope includes coverage admission
duplicate-key coverage admissions fail closed
```

The critical semantic distinction remains:

```text
ObjectAdded
    = domain lifecycle mutation

CoverageAdmission
    = runtime learns an already-existing persisted root
```

Coverage admission must establish structural baseline state without manufacturing:

```text
MutationOriginKind.ObjectAdded
semantic relation AddedPairs
business lifecycle-add meaning
fake causal evidence claiming a domain insertion
```

## 2.2 EF discovery proof

`ExternalConsumerDiscoveryTests` contains focused proof for the supported EF path, including:

```text
unloaded consumers discovered and materialized
same root returned through two resolvers admitted once
tracked Added root remains a domain addition, not coverage admission
SQL failure restores runtime/framework materialization
same DbContext retry after SQL failure
resolver/planning failure atomicity
cancellation before durability
later open-world operation re-runs resolver
missing sibling reference fails before SQL
AsNoTracking/detached roots fail
loaded optional null accepted
non-null detached required reference fails
Validate ignores materialization-only discovery
RecalculateAndValidate activates materialization discovery
enforced invariant discovery in Validate
resolver identity is exact ObjectSet/navigation aware
different instances with the same runtime key fail closed
fully loaded evaluation closure passes
supported and unsupported obligations on the same set remain fail-closed
upstream requirement preservation
all active obligations require exact resolvers
same CLR type in different ObjectSets does not cross-satisfy coverage
relation-source/target/projected coverage are never substituted by DiscoverConsumers
retargeted-away consumer excluded
retargeted-in registered consumer included
tracked Deleted known consumer excluded
unknown Modified/Deleted existing consumer remains fail-closed
batching is one resolver call per navigation obligation
target references are deduplicated inside a batch
wrong ObjectSet mapping fails
missing required resolver fails before SQL
safe resolver superset is filtered by current state
manual UoW planning remains live-state neutral before durability
stale manual plans are rejected
```

Do not convert these exact contracts into broad prose such as “discovery works”.

---

# 3. The documentation problem to fix

At baseline `197913a...`, `docs/roadmaps/README.md` still says:

```text
Active completion plan: Tasks 237–243
...
Exact final-SHA release verification remains active in Task 243
```

That statement is now stale because the repository baseline itself has an exact-head successful CI run:

```text
head_sha = 197913a329f6de006798d89b4bfb3eb428de99e9
run      = 35216826855 / #288
result   = success
```

`docs/release-candidate-verification.md` and the post-`ccfb24f` closeout plan also currently emphasize the earlier implementation head `ed383a9...` and its CI run.

Those are valid historical implementation anchors, but they must no longer be presented as the final repository verification state.

---

# 4. Avoid the self-referential SHA trap

A documentation commit necessarily creates a new repository SHA.

Do **not** create an infinite sequence like:

```text
commit docs with final SHA A
    -> repository becomes SHA B
edit docs to say final SHA B
    -> repository becomes SHA C
edit docs to say final SHA C
    -> ...
```

Use two explicit concepts instead.

## 4.1 Verified feature baseline

Record the exact already-verified repository baseline:

```text
197913a329f6de006798d89b4bfb3eb428de99e9
CI run 35216826855 / #288
success
```

This baseline contains the complete implementation/test tree plus the prior closeout evidence and has full CI proof.

## 4.2 Documentation-only closeout commit

The commit produced by Tasks 244–246 is a **documentation-only closeout commit**.

Its SHA does not need to be written back into the repository documentation after it is created.

Instead:

- verify through Git history that the final commit changes docs only;
- verify GitHub Actions for that pushed docs-only head is green;
- report that SHA and CI run in the agent's final response / PR / execution log;
- do not make another commit merely to write that SHA into a Markdown file.

This is the finality rule for this plan.

---

# 5. Mandatory weak-agent discipline

Before editing:

```bash
git fetch origin
git checkout main
git pull --ff-only
git status --short
git rev-parse HEAD
```

Required precondition:

```text
working tree clean
current head is at or after 197913a329f6de006798d89b4bfb3eb428de99e9
```

If production or test code has changed after `197913a`, stop and re-audit before using this docs-only plan.

Allowed files for this closeout are limited to documentation, primarily:

```text
docs/roadmaps/README.md
docs/codex-plan-external-consumer-discovery-post-ccfb24f-gap-closeout.md
docs/release-candidate-verification.md
this closeout plan if a completion note is useful
```

Forbidden changes:

```text
src/**
tests/**
samples/**
*.csproj
Directory.*
PublicAPI.*
.github/workflows/**
package metadata
solution files
```

If any forbidden file changes, stop and revert it before continuing.

---

# Task 244 — correct release verification documentation

Primary target:

```text
docs/release-candidate-verification.md
```

Add a final external-consumer-discovery verification entry whose authoritative feature baseline is:

```text
Verified feature baseline:
197913a329f6de006798d89b4bfb3eb428de99e9

GitHub Actions:
run 35216826855
run number 288
conclusion success
```

State explicitly that this exact-head CI passed the complete repository pipeline, including:

```text
Core .NET 8 tests
Core .NET 10 tests
EF .NET 10 tests
samples/dogfood
format verification
pack
packed-package smoke tests
artifact upload
```

Keep the earlier `ed383a9...` / run `35216322587` entry as historical evidence if useful, but make clear that it is no longer the latest verification anchor.

Do not claim:

```text
NuGet publication
GitHub release creation
tag creation
unsupported discovery modes
```

unless independently true and already evidenced.

Suggested heading:

```text
## External consumer discovery final verified baseline
```

Suggested commit scope:

```text
docs: finalize external discovery verification evidence
```

---

# Task 245 — close roadmap state truthfully

Primary target:

```text
docs/roadmaps/README.md
```

Remove the stale active wording for Tasks 237–243.

Replace it with a completed entry equivalent in substance to:

```text
- Completed external consumer discovery structural-admission, scope, overlay, durability, and manual-UoW closeout through Tasks 237–243. The verified feature baseline is `197913a329f6de006798d89b4bfb3eb428de99e9`, with exact-head GitHub Actions CI run 288 (`35216826855`) successful. [Tasks 237–243](../codex-plan-external-consumer-discovery-post-ccfb24f-gap-closeout.md)
```

Update the Tasks 220–228 line so it no longer says Task 243 remains active.

Preferred meaning:

```text
Tasks 220–228 implemented the broader structural-admission/discovery wave.
Tasks 237–243 closed the remaining verified gaps and final proof.
Both are historical/completed context.
```

Do not create another “active external consumer discovery” roadmap unless a new independently verified defect exists.

If there is currently no other active project roadmap, it is acceptable for the roadmap index to have **no active plan**.

Do not invent the next feature just to keep an active roadmap entry.

---

# Task 246 — close the plan without changing implementation

## 246.1 Mark the post-`ccfb24f` plan complete

Target:

```text
docs/codex-plan-external-consumer-discovery-post-ccfb24f-gap-closeout.md
```

Change:

```text
Status: ACTIVE FOLLOW-UP PLAN
```

to a completed status, for example:

```text
Status: COMPLETED
```

Add a concise completion block that says:

```text
Tasks 237–241 implemented/proved the remaining contracts.
Task 242 recorded the requirement-to-test audit and release evidence.
Task 243 exact-head gate is satisfied by verified feature baseline `197913a...` and CI run #288 / 35216826855.
```

Clarify that any later commit created solely to clean documentation is not a new implementation baseline and does not require writing its own SHA back into the same document.

## 246.2 Verify diff scope before push

Run:

```bash
git status --short
git diff --stat
git diff -- src tests samples .github
```

Required:

```text
only intended docs files changed
no output from the production/test/sample/workflow diff command
```

## 246.3 Commit and push once

Commit the docs-only closeout.

Example:

```text
docs: close external consumer discovery roadmap
```

Push once.

Do not make another commit merely to write this new commit SHA into documentation.

## 246.4 Verify CI on the docs-only closeout head

After push, obtain the actual pushed SHA and verify its GitHub Actions run:

```text
head_sha == pushed docs-only closeout SHA
status   == completed
result   == success
```

Verify the job still passes the normal repository gates.

If CI fails because documentation formatting or links are wrong, fix that and push again.

If CI fails because implementation/tests fail without any code change, investigate before claiming completion; do not hide or bypass the failure.

## 246.5 Final report format

The executing agent's final response must contain these fields:

```text
Feature verification baseline
    197913a329f6de006798d89b4bfb3eb428de99e9

Feature baseline CI
    run 35216826855 / #288
    success

Documentation closeout SHA
    <actual pushed docs-only SHA>

Documentation closeout CI
    <actual run id / number>
    success

Changed files
    <docs only>

Production/test changes
    none

Roadmap state
    Tasks 237–243 completed
    no external-consumer-discovery completion plan remains active
```

Do not say “fully implemented” if the docs-only closeout CI is still running or failed.

---

# 6. Completion criteria

This docs-only plan is complete when all are true:

```text
[ ] release-candidate verification identifies 197913a / CI #288 as the verified feature baseline
[ ] Tasks 237–243 are marked completed
[ ] roadmap no longer says Task 243 is active
[ ] Tasks 220–228 no longer claim later verification is pending
[ ] no production/test/sample/workflow files changed
[ ] docs-only closeout commit is pushed
[ ] exact docs-only closeout head has green CI
[ ] final report records the docs-only SHA/CI externally without another self-referential docs commit
```

After that, stop.

Do not create another external-consumer-discovery hardening plan unless a new concrete correctness defect is independently demonstrated.
