# Codex Implementation Plan — Alpha Proof, Decision-Time Causality, and Journaled Plans

Baseline: `main` at `8472d73ef435e03df7cec1035fbf4337b057aa8e`.

Baseline CI: run #88 completed successfully. Build, core tests on .NET 8/.NET 10, EF Core tests, formatting, packing, and packed-package smoke consumers are green.

The Tasks 48–54 wave is **partially implemented and superseded by this plan**. Do not recreate work that already landed:

- binding `PreparedImpactPlan`, `PlanDetailed(...)`, and `Commit(plan)` exist;
- binding plan commit does not rerun semantic classifiers/predicates/propagation;
- EF `RelationUnitOfWork.PlanDetailed(...)` exists;
- multiple store-generated additions can be captured before save and prepared/planned after the first transactional save;
- EF modified dependent references recover the tracked old principal when unambiguous instead of fabricating `null`;
- projection physical edges are canonicalized across consumers and final-state projection validation is incremental;
- source-scoped causal `ImpactId` / `UpstreamImpactId` links exist;
- main is green.

Verification also found that the repository is **not yet ready to declare the first alpha proven**:

1. causal relation/upstream causes are still reconstructed later in `MutationCommit.CreateCauses(...)`;
2. conservative relation origin attribution still uses post-hoc member/type heuristics;
3. summary detailed results still build `ImpactNodeIdentity` data even though causal mode was not requested;
4. `ObjectAdded` mutation origins cannot currently obtain a durable `SourceIdentity` because origins are captured before the object is registered;
5. preview/planning/rollback still snapshot whole touched object sets and whole navigation/projection registries for lifecycle/reference changes;
6. a binding plan currently stores pre/post `RuntimeStateSnapshot` objects rather than a compact forward patch;
7. the randomized parity test is still source-only and the semantic comparison helper mostly checks counts/severity;
8. `PreparedImpactPlanningBenchmarks.Preview*` still includes a normal commit after preview, so its name is not measuring preview alone;
9. there are no committed planning benchmark result documents;
10. the SQLite "outbox" test still does not create or persist an outbox row;
11. packed consumers still exercise `CommitDetailed(prepared)` rather than the new binding-plan surface;
12. `RuntimeApplyResult` XML still says it is produced by a committed mutation even though preview/planning returns it;
13. the manual release-candidate workflow has never been dispatched.

Implement this wave in order:

```text
55  Make causal evidence truly decision-time and causal-only
56  Replace snapshot plans/rollback with touched-state journals and compact forward patches
57  Prove full mixed-model Preview / Plan / Commit semantic equivalence and failure atomicity
58  Correct planning benchmarks and prove lifecycle/reference scaling
59  Prove a real SQLite transactional outbox, including generated keys and rollback/failure cases
60  Exercise the packed binding-plan API and close the first-alpha release-candidate gate
```

Do **not** add new feature families while completing this roadmap. Specifically do not add hypothetical pre-mutation simulation, distributed execution, nested/key-based/nullable projected selectors, generic `.Using(...)` arity 3..8, new relation planner families, runtime auto-tuning, or derived-valued relation predicates.

---

# Global rules

1. Correctness and recoverability outrank diagnostics and performance.
2. Original relation/computation/invariant expressions remain semantic authority.
3. `Invalid` always dominates `Dirty`; severity never weakens downstream.
4. Conservative precision is monotonic: once a path is Conservative it must never render as Exact later.
5. Public causal output may claim only evidence captured at the decision that actually routed the impact.
6. Summary/basic paths must not build causal graphs or source-scoped impact IDs.
7. Binding plan commit must never rerun semantic classification/routing after external durability.
8. Binding plan state must be scoped to touched runtime structures; do not keep whole-runtime clones for convenience.
9. Preview/Plan never dispatch callbacks and never advance `Version`.
10. Core remains persistence/EF/DI/logging/transport free.
11. Preserve Prepare/Commit version binding, domain-drift checks, key immutability, exception atomicity, and resumable dispatch.
12. Projected dependencies remain one direct non-null reference into the exact upstream object set.
13. Public API changes require deliberate `PublicAPI.Unshipped.txt` changes.
14. Core semantic changes pass net8.0 + net10.0; EF adapter changes pass net10.0.
15. Do not publish packages, create tags, or create a GitHub release in this roadmap.
16. Keep commits small, buildable, and bisectable.

---

# Task 55 — Make causal evidence decision-time and causal-only

## Goal

Finish the causal contract instead of adding more post-processing around `CreateCauses(...)`.

Current public `ImpactId` / `UpstreamImpactId` links are useful, but `CreateCauses(...)`, `ResolveRelationOriginIds(...)`, and `IsConservative(...)` still infer important facts after propagation. Remove that inference layer.

## 55A — causal capture context

When causal detail is requested, create an internal per-execution context that maps normalized prepared provenance to stable current-wave origin IDs.

Conceptual internal shape:

```text
CausalCaptureContext
  OriginByNormalizedMutation
  EvidenceNodes[(definition, source reference)]
  RelationRoutingEvidence
```

The context exists only for `RuntimeImpactDetailLevel.Causal` / binding causal planning. It must not be stored on the runtime after result creation.

The basic and summary paths should pass `null`/disabled capture and allocate none of these structures.

## 55B — evidence node identity

Use `(dependency definition, source reference)` as the internal identity of an impact node.

Each node needs, at minimum:

```text
Definition
Source
FinalSeverity
Precision
DirectCauses[]
UpstreamNodeRefs[]
ReactionCause?
```

Assign public `ImpactId` only when projecting a causal result. Summary results should leave `ImpactId == null` and `Causes` empty.

Do not call `CreateImpactIds(...)` for summary mode.

## 55C — direct member evidence

Keep source-member classification where it already happens in `DerivedNode.ClassifySourceRoots(...)`, but write directly into the causal node/context:

```text
OriginId
Member
local classified severity
policy kind
Exact precision
```

Never reconstruct local severity from final node severity.

## 55D — relation-routing evidence

Capture relation evidence while relation changes actually select affected left sources.

Required evidence:

```text
Relation definition
Left source
MembershipAdded / MembershipRemoved / RelatedItemChanged / ConservativeCandidate
Exact / Conservative
only origin IDs that actually participated in routing that left source
```

This likely requires carrying origin/provenance through `ResolvedChangeImpact`, relation delta construction, or a parallel internal routing-evidence structure. Choose the smallest design that keeps semantic and evidence data aligned.

Delete post-hoc heuristics that currently say, effectively, "same dependency member + right-side CLR type means relevant origin".

Rules:

- exact pair add/remove: associate lifecycle/property origin with the actual pair/source;
- exact item-property impact: associate the actual changed right item origin;
- conservative candidate routing: associate the right-side origin whose old/new candidate bucket selected that left source;
- if exact origin is unknowable, emit no origin rather than broad attribution.

After this task `ResolveRelationOriginIds(...)` should be gone.

## 55E — upstream evidence references

When `ApplyInherited(...)` maps an upstream impact into a downstream source, record the actual upstream source-scoped node reference at that moment.

Projected example:

```text
LinkValidity(link-42)
  <- AvailableQuantity(po-7)
```

Use `po-7`, not `link-42`, for upstream evidence/precision.

Precision is carried on the impact edge/node, not recursively rediscovered later:

```text
Exact + Exact          => Exact
Exact + Conservative   => Conservative
Conservative anywhere  => Conservative
```

After this task remove `IsConservative(...)` from result construction.

## 55F — invariant reaction evidence

Capture `InvariantReactionCause` at the exact point where a reaction changes effective severity or schedules repair.

Never add a fake escalation when inherited severity is already `Invalid`.

## 55G — durable identity for lifecycle origins

Fix `MutationOrigin.SourceIdentity` for `ObjectAdded`.

Today origins are captured before execution and `TryCreateSourceIdentity(...)` requires registration, so added objects produce no durable source identity even when their final key is already known.

Add an identity path that can create a canonical `SourceIdentity` from:

```text
ObjectSet definition
final object key read from the added instance
```

without pretending the object is already registered.

Requirements:

- application-assigned added key -> durable identity in causal origin;
- generated key after first transactional save -> durable identity in causal origin;
- removed object -> preserve registered pre-removal key;
- property/collection origin -> preserve registered key;
- unsupported/noncanonical key -> identity remains explicitly non-durable, not fabricated.

Do not weaken registered-key immutability.

## 55H — renderer

`RuntimeImpactTraceRenderer` should render only the captured graph, with deterministic ordering and diamond protection.

It must not need runtime state or re-run expressions.

## Required tests

```text
summary result has ImpactId == null and no causal graph
basic Apply has no causal capture
relevant + unrelated same batch -> only actual relation origins
same member changed on two right objects -> each left gets only routed origin(s)
conservative old/new key routing -> exact candidate-origin subset
exact membership add/remove lifecycle provenance
projected upstream points to actual upstream source impact ID
projected conservative path remains Conservative
exact + conservative diamond remains Conservative
same derived definition + two source objects -> distinct impact nodes
invariant inherited Invalid -> no reaction escalation cause
ScheduleRepair Dirty -> Invalid -> one explicit reaction cause
ObjectAdded application key -> durable MutationOrigin identity
ObjectAdded generated key after first save -> durable MutationOrigin identity
mutation-order permutations render equivalent graph
```

### Acceptance gate

`CreateDetailedResult(...)` becomes a projection of captured causal evidence. `CreateCauses(...)`, `ResolveRelationOriginIds(...)`, and recursive precision inference are removed or reduced to trivial mapping with no semantic inference.

Suggested commits:

```text
test: expose decision-time causal provenance gaps
refactor: capture relation and upstream evidence during propagation
fix: make lifecycle origins durably identifiable
refactor: project causal results from captured evidence
```

---

# Task 56 — Journal touched state and make binding plans compact

## Goal

Remove broad snapshot copying from Preview/Plan/rollback and stop storing pre/post `RuntimeStateSnapshot` objects inside `PreparedImpactPlan`.

Current `CaptureState(...)` still does broad work:

```text
lifecycle set touched         -> whole ObjectSetRuntime snapshot
any lifecycle mutation        -> whole NavigationIndexRegistry snapshot
projection lifecycle/retarget -> whole ProjectionIndexRegistry snapshot
affected relation             -> whole relation runtime-state snapshot
```

That is acceptable as a safety prototype, not as the stabilized alpha architecture.

## 56A — common internal mutation journal

Introduce one internal reversible journal/patch abstraction used by:

```text
normal Commit rollback
PreviewDetailed rollback
PlanDetailed rollback
Commit(plan) installation rollback
```

Conceptually:

```text
RuntimeMutationJournal
  record old touched state
  execute mutation
  capture compact forward patch
  Rollback()

RuntimeForwardPatch
  Install(runtime)
```

Do not expose journal internals publicly.

## 56B — object-set entry journal

For lifecycle operations capture only touched entries:

```text
added instance + final key
removed instance + registered key
```

Rollback/forward install should update only the affected reference/key entries.

A one-object add/remove must not clone all entries in a 100k object set.

## 56C — projection entry journal

For each canonical physical projection edge capture only touched source/target buckets:

```text
downstream source
old target
new target
old/new reverse bucket membership
```

Retargeting one link must not clone the complete projection registry.

## 56D — navigation root journal

Add root-scoped capture/restore to `NavigationIndexRegistry`.

For a changed reference/collection root, journal only memberships/index buckets that can be modified by that root. Do not snapshot every registered navigation root.

## 56E — relation runtime journal

Review every mutating relation operation used by one batch:

```text
AddLeft / RemoveLeft
AddRight / RemoveRight
ReindexLeft / ReindexRight
RefreshMembership
exact pair changes
forward/reverse access buckets
```

Add relation-local touched-state patching so a lifecycle change does not copy an entire large relation state.

Keep original predicate evaluation semantics unchanged.

## 56F — dependency state journal

Derived/invariant rollback should be source-scoped.

Capture only impacted source cache entries and current-wave impact bookkeeping for DAG nodes that actually participate. Avoid cloning every cache entry in an affected derived definition.

Ensure `_previousDerived` / `_previousInvariants` and direct-evidence state are restored correctly.

## 56G — binding plan representation

Replace public plan internals:

```text
PreState  RuntimeStateSnapshot
PostState RuntimeStateSnapshot
```

with an internal compact forward patch plus only the minimal rollback metadata needed during installation.

`Commit(plan)` must still:

- validate runtime/prepared/version/domain/final structural state;
- install without rerunning classifiers/predicates/propagation;
- increment `Version` exactly once;
- attach the already planned policy actions;
- rollback atomically if patch installation throws.

The public `PreparedImpactPlan` API does not need to expose patch details.

## 56H — instrumentation for tests/benchmarks

Add internal test diagnostics describing captured scope, for example counts of:

```text
object-set entries journaled
navigation roots journaled
projection edges/sources journaled
relation entries/buckets journaled
derived/invariant source states journaled
```

Do not expose a production metrics subsystem solely for this test.

## Required tests

```text
single add/remove journals one object-set entry
one projection retarget journals one downstream source and old/new targets
one navigation root change does not journal unrelated roots
failed relation refresh restores exact indexes/pairs
failed derived/invariant propagation restores cache state
failed planned patch install restores runtime/version/diagnostics
PlanDetailed -> discard leaves identical runtime fingerprint
Commit(plan) installs only planned touched state
```

### Acceptance gate

A single lifecycle/reference mutation no longer copies state proportional to total object population, while all existing exception-atomicity tests remain green.

Suggested commits:

```text
refactor: journal object-set lifecycle state
refactor: journal navigation and projection roots
refactor: journal relation and dependency mutations
refactor: store compact forward patches in binding plans
```

---

# Task 57 — Prove full mixed-model semantic equivalence and failure atomicity

## Goal

Replace shallow parity assertions with a reusable semantic proof harness.

## 57A — canonical semantic result normalizer

Create test-only normalization/comparison for `RuntimeApplyResult` covering:

```text
ChangeImpact
relation definition key/id
added/removed pairs using canonical source identities where possible
AffectedLefts
source-scoped derived/invariant identity
severity
SourceIdentity + DurableSourceIdentity
repair definition/source/reason
immediate evaluation definition/source
MutationOrigins including kind/member/collection kind/item identity
ImpactId graph structure after remapping IDs canonically
cause kind
OriginIds
precision
upstream impact references
reaction escalation
```

Do not compare raw `ImpactId` numeric values across independent runtimes; normalize graph references by `(DefinitionKey, DurableSourceIdentity)`.

## 57B — mixed graph

Build one fixed test model containing all of:

```text
source-only derived value
exact relation-backed derived value
conservative relation-backed derived value
same-source chain + diamond
two projected upstream values
Dirty and Invalid fan-in
ScheduleRepair invariant
tracked reference retarget
tracked collection dependency
lifecycle add/remove
```

Use named object sets/relations/derived/invariants and canonical keys so durable comparison is possible.

## 57C — execution matrix

For each generated accepted mutation batch, run equivalent object graphs/runtimes through:

```text
PreviewDetailed(Summary)
PreviewDetailed(Causal)
PlanDetailed(Causal)
direct CommitDetailed(Causal) on an equivalent runtime
Commit(plan)
```

Verify non-mutating operations preserve a runtime fingerprint:

```text
Version
Diagnostics
registered set identities/keys
relation query results
materialized pair counts
projection diagnostics
selected derived/invariant states
LastRelationImpacts-equivalent observable state where accessible
```

After commit, results and observable state must converge.

Use multiple fixed random seeds and print seed + step + normalized batch on failure.

## 57D — failure injection

Inject deterministic failures from:

```text
conditional source classifier
relation predicate / incomplete relation gate
projection reference getter if possible
incremental/full derived computation during forced read where relevant
invariant reaction planning/callback boundary
forward-patch installation test hook
```

For Preview, Plan, direct Commit, and Commit(plan), verify failure leaves runtime state coherent according to that operation's contract.

Callbacks must remain undispatched until explicit dispatch.

## 57E — binding plan stale/drift matrix

Cover:

```text
foreign runtime
already committed plan
already committed prepared mutation
runtime version advance
changed prepared domain member
changed collection assumption
projection target retarget after planning
added object's key changed after planning
registered key mutation remains rejected
```

### Acceptance gate

The repository proves complete semantic parity of planned vs direct execution over a realistic mixed dependency graph, not merely one scalar derived value.

Suggested commits:

```text
test: normalize complete runtime impact semantics
test: randomize mixed plan and direct commit equivalence
test: inject preview and planned-install failures
```

---

# Task 58 — Correct and expand planning benchmarks

## Goal

Make benchmark names truthful and measure the safety machinery that actually matters.

## 58A — fix existing Preview benchmarks

Current `PreparedImpactPlanningBenchmarks.Preview*` calls preview and then commits in the same timed operation. Fix it.

Required categories:

```text
CommitSummary
CommitCausal
PreviewSummary        // preview only
PreviewCausal         // preview only
PlanSummary           // plan only; discard
PlanCausal            // plan only; discard
CommitPreparedPlan    // commit a plan prepared outside timed body where feasible
PlanAndCommitSummary  // explicit end-to-end measurement
PlanAndCommitCausal
```

Use BenchmarkDotNet setup/iteration setup so state remains valid without silently including unrelated work.

## 58B — lifecycle/reference matrices

Add 10k / 100k population benchmarks for:

```text
add one downstream object
remove one downstream object
retarget one projected reference
remove one upstream target with fan-out 1 / 10 / 100
tracked navigation reference change
relation membership change
```

Measure Preview, Plan, and planned commit where relevant.

## 58C — real rollback safety path

Benchmarks must use normal rollback/journal safety. Do not call `DisableRollbackSnapshotsForBenchmarking()` for the primary numbers.

Optional diagnostic comparison with safety disabled is fine only as a clearly named secondary benchmark.

## 58D — result docs

Commit reproducible result documents, for example:

```text
benchmarks/PreparedImpactPlanning-Results.md
benchmarks/JournaledLifecyclePlanning-Results.md
```

Document:

```text
machine
OS
SDK/runtime
BenchmarkDotNet job
population
fan-out
mean
allocation
```

No SLA claims.

## 58E — scaling gate

At fixed touched fan-out, one retarget/add/remove should not show population-linear allocation between 10k and 100k.

If it does, stop and fix the journal scope before declaring Task 58 complete.

### Acceptance gate

Benchmark labels correspond to exactly the operation being measured and committed results demonstrate touched-state scaling.

Suggested commits:

```text
bench: separate preview plan and commit measurements
bench: measure journaled lifecycle planning
bench: record prepared planning baselines
```

---

# Task 59 — Prove the real SQLite transactional outbox

## Goal

Replace documentation-only outbox guidance with an actual relational atomicity proof. Do not add an outbox abstraction to the production library.

## 59A — test schema

Extend the SQLite test model with a test-only outbox entity/table containing enough durable data to prove identity and reason, for example:

```text
Id
DefinitionKey
SourceSetKey
SourceKey / canonical durable key parts or serialized durable identity
Reason / Severity
Payload / OriginKind where useful
```

Keep serialization test-local.

## 59B — stable application-key flow

Automate:

```text
Capture
Prepare
PlanDetailed(Causal)
begin / use explicit DB transaction
SaveChanges business data
materialize outbox rows from plan.Result
SaveChanges outbox
commit database transaction
Commit(plan)
Dispatch
```

The order may place planning before or after the first business save for stable keys, but the documented sample and test must agree.

Verify:

- business row durable;
- outbox row durable;
- runtime still version N before DB commit;
- runtime advances after DB durability;
- committed result is exactly `plan.Result`;
- callback dispatch occurs only afterwards.

## 59C — store-generated-key flow

Use at least two new identity-key business rows that both start with the default key.

Required sequence:

```text
Capture before first SaveChanges
begin transaction
first SaveChanges -> final generated keys + relationship fixup
Prepare
PlanDetailed(Causal)
persist outbox rows using final durable identities
second SaveChanges
commit DB transaction
Commit(plan)
Dispatch
```

Assert each added `MutationOrigin` and any repair/outbox source identity uses the final generated key.

## 59D — rollback/failure matrix

Cover at least:

```text
business SaveChanges failure -> DB rollback, runtime unchanged
post-save Prepare/Plan failure -> transaction rollback, runtime unchanged
outbox SaveChanges failure -> transaction rollback, runtime unchanged
explicit DB rollback after successful plan -> runtime remains unchanged and plan is discarded
DB commit succeeds -> Commit(plan) succeeds
DB commit succeeds but forced Commit(plan) install fails -> outbox remains durable; runtime remains pre-commit/recoverable
Dispatch callback fails -> DB/runtime already committed and dispatch remains resumable
```

Core `PreparedImpactPlan` cannot know whether an external transaction committed or rolled back. Do not pretend otherwise.

For the first alpha, make the boundary explicit in docs/tests:

> A plan created for a database transaction is valid for runtime commit only after that transaction is durably committed. On rollback, discard the plan and reconcile/reload the domain objects before creating another plan.

If you add an EF-specific helper to bridge final DB commit -> runtime commit, keep the boundary explicit and small; do not hide the entire transaction in a convenience method.

## 59E — synchronization failure guidance

Verify the post-DB runtime-failure path keeps the DB/outbox durable and leaves runtime at its previous version. Documentation must say rebuild/reconcile from authoritative DB; never retry the already-successful DB command blindly.

### Acceptance gate

An automated SQLite test proves atomic business+outbox durability with runtime advancement strictly after DB commit for both stable and generated keys.

Suggested commits:

```text
test: persist binding impact results in sqlite outbox
test: cover generated-key transactional outbox
test: cover outbox rollback and runtime failure matrix
docs: prove the transactional durability boundary
```

---

# Task 60 — Exercise packaged plans and close the first-alpha RC gate

## Goal

Turn the currently green development build into a proven release candidate without publishing anything.

## 60A — package consumers use the new surface

Update packed consumers so they compile/run against actual `.nupkg` APIs for binding plans.

Core consumer should exercise:

```text
Prepare
PlanDetailed(Causal)
assert runtime unchanged
Commit(plan)
Dispatch
```

EF consumer should exercise:

```text
CaptureUnitOfWork
Prepare or post-save Prepare according to key strategy
PlanDetailed
Commit through RelationUnitOfWork
Dispatch
```

Keep at least one projected-derived usage in the core packed consumer.

## 60B — public API wording pass

Review unshipped API after Tasks 55–59:

```text
PreparedImpactPlan
PlanDetailed
Commit(plan)
RelationUnitOfWork.PlanDetailed
RuntimeApplyResult
ImpactId / UpstreamImpactId
causal cause records
projection diagnostics
```

Fix stale XML docs. In particular `RuntimeApplyResult` is stable impact result data and may represent preview, binding planning, or committed execution; it is not inherently "produced by a committed mutation".

Keep `ImpactId` explicitly in-process/result-local. Durable references are `DefinitionKey` + `DurableSourceIdentity`.

## 60C — documentation truth pass

README / architecture / CHANGELOG must consistently explain:

```text
runtime is not thread-safe
Prepare -> Plan -> DB durability -> Commit(plan) -> Dispatch
PreviewDetailed is non-binding and may rerun semantics later
PlanDetailed is binding and semantic code executes once
stable vs store-generated key timing
plan must be discarded on DB rollback
Dirty vs Invalid
Exact vs Conservative
projection direct/non-null/exact-set alpha restriction
bootstrap/recovery semantics
known alpha limitations
```

## 60D — run the manual release-candidate workflow

The repository currently has zero `workflow_dispatch` runs. Run `.github/workflows/release-candidate.yml` once if repository permissions/tooling allow.

Record the run ID/result in a short release-candidate verification document.

The run must prove:

```text
Release restore/build
core net8 tests
core net10 tests
EF net10 tests
format
pack
package payload README/CHANGELOG
packed CoreNet8/CoreNet10/EfNet10 consumers
nupkg + snupkg artifact upload
```

If dispatch is unavailable to the agent, explicitly leave this gate open. Do not claim alpha-ready.

## 60E — final repository gates

Before marking this roadmap complete:

```text
latest main CI green
Tasks 55–59 tests green
planning/lifecycle benchmark result docs committed
real transactional outbox tests green
packed consumers use binding plans
PublicAPI approval files intentional
manual RC workflow successful
no NuGet publish workflow/secrets added
no GitHub release/tag created
repository version remains centralized
```

Then update `docs/roadmaps/README.md`:

- Tasks 48–54 -> implemented partial wave superseded by Tasks 55–60;
- Tasks 55–60 -> completed only after every gate above passes;
- no active roadmap after completion unless a later plan exists.

Suggested commits:

```text
build: smoke test packed binding plan consumers
docs: align binding plan and result contracts
build: verify first alpha release candidate
docs: close alpha proof roadmap
```

---

# Recommended commit sequence

Keep the wave reviewable:

```text
1  test: expose decision-time causal provenance gaps
2  refactor: capture relation evidence during routing
3  refactor: capture upstream and invariant causal edges
4  fix: make lifecycle origins durably identifiable
5  refactor: remove post-hoc causal inference
6  refactor: journal object-set lifecycle state
7  refactor: journal navigation and projection roots
8  refactor: journal relation and dependency state
9  refactor: store compact forward patches in binding plans
10 test: normalize complete runtime impact semantics
11 test: randomize mixed plan and direct commit equivalence
12 test: inject planned execution and install failures
13 bench: separate preview plan and commit measurements
14 bench: measure journaled lifecycle planning
15 bench: record prepared planning baselines
16 test: persist binding impact results in sqlite outbox
17 test: cover generated-key outbox and failure matrix
18 build: smoke test packed binding plan consumers
19 docs: align first-alpha contracts
20 build: verify first alpha release candidate
21 docs: close alpha proof roadmap
```

Do not squash this into one feature commit.

---

# Definition of done

This roadmap is complete when Raffinert.Relations can truthfully demonstrate this workflow:

```text
ordinary domain mutation
        ↓
Capture / Prepare at the correct persistence boundary
        ↓
PlanDetailed once
        ↓
source-scoped causal graph captured at routing decisions
        ↓
persist business + outbox atomically
        ↓
database durable
        ↓
install compact precomputed runtime patch
        ↓
Version advances exactly once
        ↓
Dispatch resumably
```

and the repository proves all of the following rather than merely documenting them:

```text
causes have source-scoped decision-time provenance
Summary/basic paths do not pay causal-graph allocation
ObjectAdded origins can be durable
planned commit does not rerun semantics
plan/preview rollback touches state proportional to the mutation
mixed exact/conservative/projected graphs have complete semantic parity
benchmark names measure exactly what they claim
business + outbox rows commit atomically in SQLite
store-generated keys produce final durable identities
packed packages expose the binding-plan workflow
manual release-candidate verification is green
```
