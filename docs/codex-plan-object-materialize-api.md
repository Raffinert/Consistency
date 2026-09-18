# Codex plan — object-level `runtime.Materialize(source)`

Status: **FOLLOW-UP DOGFOOD / DESIGN PLAN — DO NOT CHANGE PRODUCTION PUBLIC API YET**

Baseline evidence: commit `95662979d7b158f98e951b0c1cdb59476f3c86ba` (`experiments: dogfood Evaluate and Materialize API`).

Purpose: evaluate object-level materialization as the normal way to make every configured materialized derived representation on one source object current:

```csharp
runtime.Materialize(link);

Use(link.PriceRate);
Use(link.UnitRate);
```

The existing Model D experiment already proved the targeted primitive:

```csharp
var rate = runtime.Evaluate(priceRate, link);          // logical value only
var rate = runtime.Materialize(priceRate, link);       // one target
```

This plan adds and dogfoods:

```csharp
runtime.Materialize(link);                             // all applicable targets on this object
```

The central semantic proposal is:

> `Materialize(source)` synchronizes every materialized derived target whose target/source object is `source`, evaluating only what is necessary and preserving all unrelated consistency/repair semantics.

It does **not** mean “repair the entire graph around this object”.

---

# 1. Read the completed Model D evidence first

Before coding, read:

```text
docs/derived-storage-materialization-concept-results.md
docs/codex-plan-explicit-get-materialize-api.md
experiments/Raffinert.Consistency.DerivedStorageConcept/**
src/Raffinert.Consistency/Derived/**
src/Raffinert.Consistency/Runtime/**
src/Raffinert.Consistency.EntityFrameworkCore/**
```

Verify and preserve these completed findings:

```text
- Evaluate is cache-aware/lazy and does not write mirrors.
- targeted Materialize evaluates as needed, writes exactly the requested target, and returns TValue.
- targeted Materialize can reuse an already-Fresh runtime value without recomputation.
- setter failure does not require logical-cache rollback; retry can reuse the Fresh value.
- Evaluate does not affect EF property tracking; Materialize naturally can.
- runtime-only derived definitions are not materializable.
- targeted Materialize selected T1: it writes only the requested target, not the upstream materialized closure.
- persistence can invoke materialization without requiring application code to call it manually.
- direct property reads can still be stale before materialization.
```

Do not reopen those questions unless object-level materialization produces contradictory evidence.

---

# 2. Proposed public shape

Dogfood this conceptual surface:

```csharp
// Need one logical current value, no object-property synchronization:
var rate = runtime.Evaluate(priceRate, link);

// Need one known materialized representation synchronized:
var rate = runtime.Materialize(priceRate, link);

// Need the object's configured materialized derived state synchronized:
runtime.Materialize(link);
```

Do not add aliases such as:

```text
MaterializeAll
Refresh
Sync
Apply
EnsureMaterialized
```

unless the experiment proves `Materialize(source)` is materially ambiguous.

Do not add a collection overload in this wave.

---

# 3. Canonical PriceRate + UnitRate dogfood

Use one `PurchaseOrderInvoiceLine link` with at least two materialized derived definitions:

```text
InvoiceLine.Price ─────┐
                       ├──> PriceRate ─────> link.PriceRate
POL.Price ─────────────┘
                              │
                              ▼
                           UnitRate ────────> link.UnitRate
```

Optionally include a third independent materialized definition:

```text
Other input ──> Display/AlternateRate ──> link.AlternateUnitRate
```

Initial state:

```text
runtime PriceRate = 6 / Fresh
runtime UnitRate  = 6 / Fresh
link.PriceRate    = 6
link.UnitRate     = 6
```

Mutate inputs so both become stale and logical values become different.

Then:

```csharp
runtime.Materialize(link);
```

Required outcome:

```text
all applicable materialized definitions for `link` are current
link.PriceRate == runtime.Evaluate(priceRate, link)
link.UnitRate  == runtime.Evaluate(unitRate, link)
all required runtime nodes are Fresh
no unrelated source object's mirror was written
no runtime-only derived definition was "materialized"
no repair action was silently consumed
```

---

# 4. Define "all applicable targets on this object" precisely

Do not implement object-level Materialize by CLR type alone.

A materialization target is applicable only when all of these are true:

```text
1. the compiled definition has a registered materialization target;
2. the target is defined for the object set/definition identity that owns this source;
3. this exact source instance is registered/alive in that set;
4. the target member belongs to this source object;
5. the definition belongs to this runtime/compiled model.
```

Mandatory same-CLR-type test:

```text
ObjectSet<A> linksA
ObjectSet<A> linksB
```

with different materialized definitions. Calling:

```csharp
runtime.Materialize(aFromLinksA);
```

must not apply mappings belonging to `linksB` merely because the CLR type matches.

If one source instance can legitimately belong to multiple object sets, document and dogfood the intended behavior rather than deduplicating by CLR type.

---

# 5. Ordering: use the compiled dependency DAG

Object-level Materialize must have deterministic dependency-aware ordering.

Example:

```text
PriceRate -> UnitRate
```

Both target the same `link`.

Dogfood two possible algorithms:

### O1 — evaluate/materialize each target in compiled topological order

```text
PriceRate: Evaluate -> write PriceRate
UnitRate:  Evaluate -> write UnitRate
```

### O2 — evaluate all required logical nodes first, then write all mirrors

```text
Evaluate closure
    PriceRate Fresh
    UnitRate Fresh
then write
    PriceRate target
    UnitRate target
```

Prefer O2 if it prevents mirror writes from becoming accidental graph inputs/notifications and gives cleaner separation:

```text
Phase 1: logical evaluation
Phase 2: representation synchronization
```

But measure whether existing engine architecture makes O1 simpler and equally safe.

Do not use arbitrary registration/dictionary iteration order.

---

# 6. Targeted Materialize semantics must remain targeted

Do not change the already-dogfooded contract:

```csharp
runtime.Materialize(unitRate, link);
```

should still synchronize only UnitRate's target under T1.

Object-level:

```csharp
runtime.Materialize(link);
```

is the explicit operation that synchronizes all materialized targets applicable to `link`.

This distinction is valuable:

```text
Materialize(definition, source) = precise one-target synchronization
Materialize(source)             = source-wide synchronization
```

Test both in the same scenario.

---

# 7. Return type of object-level Materialize

Do **not** return an arbitrary derived value because multiple targets may be synchronized.

Dogfood these options:

```csharp
void runtime.Materialize(link);
```

versus a diagnostic result:

```csharp
MaterializationResult runtime.Materialize(link);
```

Default hypothesis: public business-facing API should be `void` (or an async equivalent only if future semantics require it). Callers can read the now-current properties directly:

```csharp
runtime.Materialize(link);
Use(link.PriceRate, link.UnitRate);
```

A result object is justified only if a concrete application use case needs information such as changed targets. Diagnostics can be emitted separately.

Do not introduce `MaterializationResult` just for test assertions.

---

# 8. Already-Fresh logical values, stale mirrors

This is a primary use case.

Sequence:

```csharp
var rate = runtime.Evaluate(priceRate, link);
var unit = runtime.Evaluate(unitRate, link);

// runtime values Fresh; mirrors may still be stale
runtime.Materialize(link);
```

Required:

```text
no derived recomputation
all stale configured mirrors on link synchronized
already-equal mirrors not redundantly assigned where equality permits
```

This proves object Materialize is representation synchronization, not “recompute everything”.

---

# 9. Only some definitions stale

Set:

```text
PriceRate runtime Fresh, mirror Fresh
UnitRate runtime Dirty, mirror stale
AlternateRate runtime Fresh, mirror stale due to external write
```

Call:

```csharp
runtime.Materialize(link);
```

Expected:

```text
PriceRate: no recompute, no write
UnitRate: recompute as needed, write
AlternateRate: no recompute, overwrite rogue/stale mirror
```

Use computation and assignment counters.

---

# 10. Runtime-only derived definitions must not participate as targets

Have on the same source:

```text
PriceRate      -> materialized
UnitRate       -> materialized
RiskScore      -> runtime-only
```

`runtime.Materialize(link)` may evaluate RiskScore **only if it is a logical dependency required to evaluate a materialized target**.

It must not evaluate RiskScore merely because RiskScore exists for the source.

This is important for cost control.

Test an expensive runtime-only derived definition with a counter and prove it stays untouched when unrelated.

---

# 11. Cross-object dependencies must not expand physical scope

Example:

```text
PurchaseOrderLine.Price
        ↓ projected
link.PriceRate -> link.PriceRate target
```

Calling:

```csharp
runtime.Materialize(link);
```

may evaluate dependencies on `PurchaseOrderLine`, but it must not automatically materialize properties on that `PurchaseOrderLine` object.

Physical synchronization scope is the requested target object, not the entire dependency closure.

Likewise:

```text
link.UnitRate -> Allocation.Validity
```

Materializing `link` must not materialize `Allocation` objects downstream.

This boundary is mandatory.

---

# 12. Relation-backed aggregate on the same object

Add:

```text
Fulfillments -> FulfilledQuantity -> line.FulfilledQuantity
```

with another materialized derived property on the same `line`.

After incremental propagation leaves aggregate runtime state Fresh but mirror stale:

```csharp
runtime.Materialize(line);
```

must write the Fresh aggregate without full recomputation and also synchronize other applicable materialized targets.

No relation scan should occur solely because object-level materialization was requested.

---

# 13. Failure semantics with multiple targets

This is the most important new question introduced by `Materialize(source)`.

Suppose `link` has:

```text
PriceRate target
UnitRate target
AlternateRate target
```

and UnitRate setter throws.

Compare two policies.

### F1 — best-effort / partial materialization

```text
PriceRate may already be written
UnitRate throws
AlternateRate not written
runtime logical values remain Fresh where successfully evaluated
caller can retry
```

### F2 — source-level atomic materialization

```text
all target values captured
all writes attempted transactionally
on any setter failure, restore every target property to pre-call values
runtime logical cache remains independently Fresh
```

Dogfood both.

Default design pressure: **F2 is safer for a method whose contract says the object is materialized/current after successful return**, but it requires target snapshots and rollback of physical writes.

Important: do not roll back logical derived cache merely because physical synchronization failed. Separate logical evaluation atomicity from mirror-write atomicity.

If F2 is prohibitively invasive, document that explicitly and reconsider whether object-level Materialize should exist as a public primitive.

---

# 14. Evaluation failure before writes

If using O2 (evaluate all first, then write), inject failure while evaluating UnitRate.

Desired benefit:

```text
no target property has been written yet
Materialize(link) throws
logical nodes evaluated before failure may remain Fresh according to existing runtime semantics
mirrors remain as before call
```

This is a strong argument for two-phase O2. Verify it experimentally.

If O1 writes PriceRate before UnitRate evaluation fails, record the partial physical update as a design cost.

---

# 15. Repair/invariant consequences remain separate

Scenario:

```text
PriceRate stale
UnitRate stale
LinkValidity Invalid
Rematch repair pending
```

Call:

```csharp
runtime.Materialize(link);
```

Required:

```text
PriceRate/UnitRate materialized representations current
LinkValidity remains whatever its own logical processing dictates
pending rematch remains pending unless existing repair policy dispatches it
no repair is consumed merely because mirrors became current
```

Then execute normal completion/dispatch and prove exactly-once repair behavior.

---

# 16. Materialization target writes must not create feedback loops

When object Materialize writes:

```csharp
link.PriceRate = current;
link.UnitRate = current;
```

those writes must not accidentally be treated as independent authoritative source mutations that invalidate the definitions being materialized or double-propagate downstream.

Dogfood:

```text
A. target writes are suppressed/recognized as materialization writes
B. graph compiler rejects direct source dependencies on registered target members
C. writes are reported normally but deduplicated safely
```

Use the previous Model D finding as baseline: derived-handle dependencies are preferred; direct dependencies on registered mirror properties create a split graph.

Object-level Materialize magnifies this risk because multiple targets are written together.

---

# 17. External rogue mirror writes

Sequence:

```csharp
runtime.Evaluate(priceRate, link); // logical 5.5 Fresh
runtime.Evaluate(unitRate, link);  // logical 11 Fresh

link.PriceRate = 999m;
link.UnitRate = 888m;

runtime.Materialize(link);
```

Required:

```text
link.PriceRate restored to 5.5
link.UnitRate restored to 11
no recomputation if runtime values remained Fresh
```

This demonstrates a useful property of source-wide materialization: it can re-establish all configured mirrors after uncontrolled/anemic-model writes.

It still does not prevent stale/rogue values from being read before the call. Do not claim otherwise.

---

# 18. No materialized targets on source

What should happen here?

```csharp
runtime.Materialize(sourceWithOnlyRuntimeDerivedValues);
```

Dogfood:

```text
N1. no-op
N2. throw because source has nothing materializable
```

Default hypothesis: **N1 no-op** is more natural for source-wide synchronization, especially for generic infrastructure code:

```csharp
foreach (var changed in sources)
    runtime.Materialize(changed);
```

This differs intentionally from targeted:

```csharp
runtime.Materialize(runtimeOnlyDefinition, source)
```

where rejection remains useful because the caller explicitly requested an impossible target.

Document the distinction.

---

# 19. Unknown/unregistered/removed source

Test:

```text
null source
source of a CLR type unknown to compiled model
correct CLR type but never registered
removed source
source belonging to a different runtime/model
```

Decide whether source-wide Materialize should reject all unregistered sources or no-op when no applicable mapping can be resolved.

Default pressure: reject sources that are expected to belong to a known object set but are not registered; do not silently succeed and leave stale properties.

Use existing runtime exception conventions.

---

# 20. EF tracking

Tracked entity with two materialized properties:

```csharp
runtime.Materialize(link);
```

Expected:

```text
only target properties whose values actually changed are marked modified
unrelated scalar properties untouched
already-equal target does not become modified merely due to redundant assignment, where setter/equality behavior permits avoiding it
```

Compare with existing EF materialization boundary.

The long-term goal should be one semantic primitive for:

```text
application: runtime.Materialize(link)
persistence: materialize affected entity before save
```

Do not create a second divergent implementation.

---

# 21. Persistence boundary reuse

Dogfood whether the persistence adapter can conceptually do:

```text
for each affected source that has mapped materialized targets:
    Materialize(source)
```

rather than per-definition calls.

But check over-materialization cost: SaveChanges may know only PriceRate is affected while the object has ten materialized definitions.

Compare:

```text
P1. persistence uses source-wide Materialize
P2. persistence keeps affected-definition targeted Materialize
```

Likely result may be:

```text
public application API: source-wide is ergonomic
persistence engine: targeted affected definitions are more efficient
```

They can share the same lower-level target primitive without forcing the same orchestration API.

Do not optimize persistence around the public overload if it causes unnecessary evaluation.

---

# 22. Complexity/performance requirements

`Materialize(source)` must not scan every derived definition in the compiled model on every call.

Prototype an index conceptually like:

```text
ObjectSet/source identity
    -> materialized definitions targeting that source set
```

or equivalent compiled metadata.

Desired complexity:

```text
lookup applicable materializers: O(1) + O(k)
k = number of materialized definitions for that source set
```

Evaluation itself follows only required stale dependency paths.

Do not use reflection to discover target properties per call.

Measure with a concept case containing many unrelated definitions/source sets and prove only the relevant `k` targets are visited.

---

# 23. Concurrency remains out of scope

The runtime is currently not thread-safe. Do not add locks or concurrent collections in this plan.

However document that:

```csharp
runtime.Materialize(link);
```

is physically mutating and may evaluate/cache multiple definitions, so it requires the same external synchronization as other runtime mutation/query operations under the current contract.

Do not promise atomicity across threads.

---

# 24. Async remains out of scope

Current derived computation/materialization is synchronous. Do not add:

```csharp
MaterializeAsync(link)
```

unless the experiment uncovers an existing async target setter/materializer concept, which is unlikely.

Persistence can remain async independently because database I/O is async.

---

# 25. Naming check

Compare:

```csharp
runtime.Materialize(link);
```

with:

```csharp
runtime.MaterializeAll(link);
runtime.Sync(link);
runtime.Refresh(link);
```

Evaluate only semantic clarity.

Expected hypothesis: `Materialize(link)` is strongest because overload resolution naturally communicates scope:

```text
Materialize(definition, source) -> one target
Materialize(source)             -> all targets on source
```

`MaterializeAll` may falsely imply all graph objects, and `Sync`/`Refresh` do not say what representation is synchronized.

Do not add aliases.

---

# 26. Interaction with `Evaluate`

The developer story should become:

```csharp
// I need a current value but do not want to mutate the entity:
var rate = runtime.Evaluate(priceRate, link);

// I want this entity's derived materialized properties ready for ordinary direct reads:
runtime.Materialize(link);
Use(link.PriceRate);
Use(link.UnitRate);

// I need one specific mirror synchronized and want its value:
var rate = runtime.Materialize(priceRate, link);
```

This is the primary usability dogfood. Put it in the concept README.

Do not require `GetState` or cache inspection before any of these calls.

---

# 27. Does object Materialize reduce the stale-property problem enough?

The previous Model D weakness was:

```csharp
Use(link.PriceRate); // can be stale
```

Object-level Materialize gives application code a simple boundary:

```csharp
runtime.Materialize(link);
Use(link.PriceRate, link.UnitRate);
```

Evaluate whether this is sufficiently hard to forget in realistic workflows.

Dogfood at least these call-site shapes:

```text
service method before using several derived properties
controller/handler before mapping DTO
business operation after mutations
persistence boundary
```

Compare against alternatives:

```text
calling Evaluate separately for every derived value
calling targeted Materialize separately for every property
property-backed Model C
encapsulation/analyzer protection
```

Do not claim object Materialize fully solves direct-read safety. The results should state whether it meaningfully improves ergonomics while retaining explicitness.

---

# 28. Concept implementation location

Extend the existing experiment:

```text
experiments/Raffinert.Consistency.DerivedStorageConcept/
```

Suggested additions:

```text
ObjectMaterializeAdapter.cs
Scenarios/
    OM01PriceAndUnitRate.cs
    OM02FreshCacheStaleMirrors.cs
    OM03PartialStaleness.cs
    OM04RuntimeOnlyIgnored.cs
    OM05CrossObjectScope.cs
    OM06IncrementalAggregate.cs
    OM07EvaluationFailure.cs
    OM08SetterFailureAtomicity.cs
    OM09RepairSurvival.cs
    OM10RogueMirrorWrites.cs
    OM11NoTargets.cs
    OM12ObjectSetIdentity.cs
    OM13EfTracking.cs
    OM14PersistenceOrchestration.cs
    OM15LookupCost.cs
```

Do not modify production Core/EF/public API baseline during the experiment.

---

# 29. Update results document

Append to:

```text
docs/derived-storage-materialization-concept-results.md
```

with:

```text
## Model D2 — object-level Materialize(source)
```

Required results table:

| Question | Targeted `Materialize(def, source)` | Object `Materialize(source)` |
|---|---|---|
| synchronization scope | one target | all applicable targets on source |
| return value | TValue | void/result TBD |
| evaluates unrelated runtime-only nodes | no | must be no |
| writes dependency objects | no | must be no |
| writes downstream objects | no | must be no |
| reuses Fresh caches | yes | must be yes |
| handles rogue mirror writes | one target | all targets on source |
| no-target source behavior | explicit definition rejects | no-op/throw TBD |
| setter failure physical atomicity | one property | multi-property policy TBD |
| EF tracking side effect | explicit | explicit |
| persistence orchestration fit | precise/efficient | ergonomic but may over-materialize |
| lookup complexity | direct definition | indexed O(k) required |

Also include a timeline for the PriceRate + UnitRate example before/after `Materialize(link)`.

---

# 30. Decision gates after dogfood

Stop and ask maintainer approval on:

```text
Q1. Should `runtime.Materialize(source)` be part of the public API?
Q2. Should its return type be void?
Q3. Should it materialize only targets physically located on the requested source? (default: yes)
Q4. Should logical evaluation happen for all targets first, then physical writes (O2)?
Q5. Should multi-target physical writes be atomic with rollback (F2) or allow partial materialization on exception (F1)?
Q6. Should a source with zero materialized targets be a no-op (N1)?
Q7. How should one source instance belonging to multiple object sets be resolved?
Q8. Should target-property dependencies be rejected/canonicalized to derived handles?
Q9. Should persistence use source-wide orchestration or retain targeted affected-definition orchestration?
Q10. Does object-level Materialize make Model D sufficiently ergonomic compared with property-backed Model C?
```

Do not implement production overload until Q1-Q7 are settled.

---

# 31. If approved: likely production architecture

Only after approval prepare/execute a production plan around this shape:

```text
CompiledModel
    └── Materialization index by object-set/source identity
            ├── PriceRate target descriptor
            ├── UnitRate target descriptor
            └── ...

ConsistencyRuntime.Materialize(source)
    ↓
resolve source identity
    ↓
lookup k target descriptors
    ↓
Evaluate required logical values
    ↓
write targets according to selected atomicity policy
```

Reuse the same lower-level primitive as:

```csharp
runtime.Materialize(definition, source);
```

Do not duplicate target setter/equality/read-back logic.

If O2/F2 are selected, structure it conceptually as:

```text
Prepare
    resolve targets
    evaluate values
    capture previous physical values

Apply
    write targets in deterministic order

Rollback on physical failure
    restore previously written target values

Commit
    return successfully; logical cache remains governed by normal runtime semantics
```

This is physical representation atomicity, not graph repair.

---

# 32. Completion checklist

```text
[ ] completed Model D evidence read
[ ] no production API changes made during dogfood
[ ] OM01 PriceRate + UnitRate source-wide synchronization proven
[ ] OM02 Fresh caches + stale mirrors synchronize without recompute
[ ] OM03 partial staleness only recomputes/writes what is necessary
[ ] OM04 unrelated runtime-only definitions not evaluated
[ ] OM05 cross-object logical dependencies do not expand physical write scope
[ ] OM06 incremental aggregate materializes without full scan
[ ] OM07 evaluation failure compared under O1/O2
[ ] OM08 multi-target setter failure compared under F1/F2
[ ] OM09 pending repair survives source materialization
[ ] OM10 rogue mirror writes restored without recompute
[ ] OM11 no-target source N1/N2 compared
[ ] OM12 object-set identity proven; no CLR-type-only lookup
[ ] OM13 EF tracking inspected
[ ] OM14 persistence source-wide vs targeted orchestration compared
[ ] OM15 applicable-target lookup shown to be O(k), not whole-model scan
[ ] targeted Materialize contract unchanged
[ ] ordering deterministic and DAG-aware
[ ] feedback-loop/split-graph risk tested
[ ] naming comparison completed
[ ] direct-read ergonomics assessed at realistic call sites
[ ] results appended to concept results document
[ ] agent stops for maintainer decision
```

---

# 33. Final instruction to the coding agent

Do not interpret:

```csharp
runtime.Materialize(link);
```

as:

```text
make everything connected to link consistent
```

Its proposed meaning is much narrower and testable:

```text
make every configured materialized derived representation ON THIS OBJECT current
```

Evaluation may traverse the dependency graph. Physical writes must remain scoped to the requested object.

The intended application story is:

```csharp
// mutations happened earlier
runtime.Materialize(link);

// all configured derived mirrors on link are now safe for ordinary direct reads
Use(link.PriceRate);
Use(link.UnitRate);
```

The experiment succeeds only if that guarantee remains understandable and reliable across multiple targets, transitive dependencies, incremental aggregates, exceptions, repair policy, EF tracking, object-set identity, and plain objects.