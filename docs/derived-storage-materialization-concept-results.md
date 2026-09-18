# Derived storage and materialization concept results

Status: design evidence and maintainer decision gate. No production changes were
made and no storage model is selected.

The isolated executable dogfooded Models A, B, and C against identical D1-D12
scenarios using the existing compiled DAG/runtime for dependency semantics. The
policy facade simulated only synchronization timing and failure state.

## One-sentence PriceRate answers

- **A:** `runtime.Get` returns 5.5 and makes the runtime cache Fresh, while
  `link.PriceRate` remains 6 until explicit/EF materialization.
- **B:** `runtime.Get` returns 5.5, makes the runtime cache Fresh, and assigns
  5.5 to `link.PriceRate` before returning.
- **C-Hybrid:** `runtime.Get` recomputes 5.5, atomically assigns it to
  `link.PriceRate`, marks the node Fresh, and returns the property-backed value;
  runtime-only definitions still use cache storage.

## Results matrix

| Question | A Runtime authoritative | B Get syncs property | C Property-backed |
|---|---|---|---|
| `Get` always returns current value | Yes; existing runtime semantics | Yes, if synchronization succeeds | Yes, if recomputation/target assignment succeeds |
| property current immediately after `Get` | No | Yes | Yes |
| property current before persistence without prior read | Yes only because persistence adapter explicitly evaluates/materializes | Same boundary guarantee is still required | Same boundary guarantee is required for stale nodes never read |
| direct property read can silently be stale | Yes | Yes before first Get/boundary | Yes while node is stale unless property access is encapsulated/intercepted |
| duplicate value storage | Property plus runtime cache | Property plus runtime cache | Intended no duplicate for property-backed nodes; simulator retains an engine cache only to reuse existing DAG |
| supports runtime-only derived | Yes | Yes | Yes through C-Hybrid runtime-cache policy |
| supports relation incremental aggregate | Existing cache updates in place | Cache updates; property waits for Get/boundary | Viable only if incremental update also writes property atomically; simulator proved this timing |
| supports derived -> derived | Yes | Yes; upstream synchronization needs an internal hook, not only outer facade interception | Yes; downstream reads property-backed upstream through its definition |
| supports projected dependency | Yes | Yes | Yes; storage does not replace projection edges |
| setter failure atomicity simple | N/A during Get | No; cache may already be Fresh, so failure overlay/rollback is required | No; target write and state transition must be one transaction |
| rollback integration simple | Current runtime + separate EF rollback already exist | Get-time property snapshots must join runtime rollback | Runtime snapshots must capture prior property value or delay assignment |
| EF tracking behavior unsurprising | Get does not mark mirror modified | Get marks mirror modified even though named as a read | Get marks storage property modified; coherent but physically mutating |
| feedback-loop risk | Low because EF mirrors are sink-only | High if Get assignment is reported as a source mutation | High unless target is write-protected and compiler treats handle as the only dependency edge |
| external write protection required | Desirable; rogue mirror writes persist until boundary | Desirable; next Get/boundary overwrites rogue value | Required; simulator rejects a value differing from the last runtime-owned value |
| requires invasive Core changes | No | Yes for atomic assignment, upstream interception, reentrancy, and rollback | Yes for property-backed runtime states, snapshots, incremental writes, lifecycle cleanup, and write protection |
| requires invasive EF changes | No | Moderate: distinguish Get-caused writes and coordinate rollback | Moderate: EF must understand property-backed storage and planned writes |
| works for plain objects without EF | Yes | Yes | Yes if setter/access contract is available without EF |

## D1-D12 evidence

| Scenario | Observed result |
|---|---|
| D1 PriceRate | All returned 5.5/Fresh. A property stayed 6; B/C property became 5.5. |
| D2 no read before persistence | All required an explicit boundary; it evaluated and wrote 5.5. No model can rely on prior reads. |
| D3 repeated Fresh read | No second computation or redundant assignment. C read its validated property value. |
| D4 derived chain | `Get(unitRate)` used current upstream PriceRate. B/C synchronized Fresh configured upstreams; A did not. Runtime-only PriceRate successfully fed a materialized UnitRate. |
| D5 direct property read | Every model allowed a silent read of stale 6 before synchronization. C rejected an external write; B overwrote it on Get; A left it until boundary. |
| D6 relation aggregate | Membership add used the existing incremental plan and remained Fresh. C immediately wrote the updated property; A/B deferred it. Invalid item/removal impacts recomputed correctly. |
| D7 projected dependency | Allocation became stale through `Allocation.OrderLine`; Get used current RemainingQuantity. Storage policy did not hide the projection edge. |
| D8 pending repair | PriceRate could become Fresh while UnitRate remained Invalid and the repair request remained pending. Reading UnitRate did not erase the request; dispatch still invoked it once. |
| D9 computation failure | Property stayed 6 and node stayed stale. A failed affected-derived plan restored runtime cache/state/version. |
| D10 setter failure | B/C preserved property 6 and exposed concept state Invalid even though the underlying cache had computed 5.5; retry synchronized successfully. This requires Core integration to be truly atomic. |
| D11 EF tracking | A Get did not mark PriceRate modified. B/C Get did. An explicit no-read boundary marked it modified for every model. |
| D12 plain object | All policies worked without EF; explicit synchronization updated detached objects. |

## Materialization timing

### A — runtime authoritative

| Event | Should derived recompute? | Should target property assign? | Should repair dispatch? |
|---|---:|---:|---:|
| input mutation | Lazy; incremental operators may update | No | Existing policy-dependent behavior |
| `runtime.Get` on Fresh | No | No | No |
| `runtime.Get` on Dirty | Yes | No | No |
| `runtime.Get` on Invalid | Yes | No | No |
| downstream `runtime.Get` requires upstream | As needed | No | No |
| explicit materialize | As needed | Yes | No |
| EF `Materialize` | As needed during affected planning | Yes | No |
| Complete / persistence boundary | As required | Yes for active mappings | Existing policy |

### B — Get synchronizes mirror

| Event | Should derived recompute? | Should target property assign? | Should repair dispatch? |
|---|---:|---:|---:|
| input mutation | Lazy; incremental cache may update | No | Existing policy-dependent behavior |
| `runtime.Get` on Fresh | No | Only if mirror differs; no redundant write | No |
| `runtime.Get` on Dirty | Yes | Yes | No |
| `runtime.Get` on Invalid | Yes | Yes | No |
| downstream `runtime.Get` requires upstream | As needed | Yes for configured upstreams made Fresh | No |
| explicit materialize | As needed | Yes | No |
| EF `Materialize` | As needed | Yes | No |
| Complete / persistence boundary | As required | Yes for active mappings | Existing policy |

### C-Hybrid — property-backed when configured

| Event | Should derived recompute? | Should target property assign? | Should repair dispatch? |
|---|---:|---:|---:|
| input mutation | Lazy; incremental operator may update | Immediately only for successful Fresh incremental updates | Existing policy-dependent behavior |
| `runtime.Get` on Fresh | No | No; read validated property storage | No |
| `runtime.Get` on Dirty | Yes | Yes, atomically with Fresh transition | No |
| `runtime.Get` on Invalid | Yes | Yes, atomically with Fresh transition | No |
| downstream `runtime.Get` requires upstream | As needed | Yes for property-backed upstream recomputation | No |
| explicit materialize | As needed | Yes | No |
| EF `Materialize` | As needed | Usually already current; assign if stale | No |
| Complete / persistence boundary | As required | Yes for unread stale nodes | Existing policy |

No candidate needs read-time repair dispatch. Value freshness and business
consequences remain separate.

## D8 state timeline

```text
initial: PriceRate Fresh=6, UnitRate Fresh=12, validity evaluated
InvoiceLine.Price 60 -> 70
commit: PriceRate Invalid, UnitRate Invalid, LinkValidity Invalid, one repair request pending
Get(PriceRate): PriceRate Fresh=7; UnitRate/LinkValidity remain Invalid; request remains pending
Get(UnitRate): PriceRate Fresh=7, UnitRate Fresh=14; invariant consequence/request remains
Dispatch: the original request invokes rematch once
```

Materialization under B/C changes property values during this timeline but not
policy-request ownership.

## Misuse analysis

The easiest incorrect code under A is `Use(link.PriceRate)` after an input
mutation; it silently consumes a normal-looking stale mirror. Avoiding that
requires encapsulation, a naming convention, or an analyzer.

The easiest incorrect code under B is assuming `Get` is side-effect free. It can
mark an EF property modified, invoke user setter logic, and throw after the cache
has recomputed. Calling only a downstream Get also requires the runtime—not an
outer extension method—to synchronize materialized upstreams.

The easiest incorrect code under C is assigning `link.PriceRate = 999`. That
creates a second authority unless writes are prohibited. A public setter makes C
unsafe by construction; private/internal setters plus runtime-owned access (or a
future analyzer/generated interceptor) are required. Even C cannot prevent a
stale direct read while the node is Dirty without encapsulating/intercepting the
getter.

No model makes an ordinary public auto-property self-describe freshness.

## MaterializeTo versus Derive(target, compute)

The dogfood supports a semantic distinction:

- `Select(...).MaterializeTo(...)` means the derived value exists independently
  and the property is a synchronized output mirror. This matches A/B and current
  EF behavior.
- `Derive(target, compute)` means the property participates in value storage,
  write ownership, rollback, incremental updates, and definition validation.
  This is the clearer declaration for C-Hybrid.

They should not be syntax aliases unless the maintainer first decides that
property storage and mirror output are equivalent—which the failure and EF
tracking evidence contradicts.

## Derived target as dependency and feedback

The executable model declared UnitRate both ways:

```csharp
model.Derived(links).Using(priceRate)       // graph edge
model.Derived(links).Compute(x => x.PriceRate * 2m) // property edge
```

After B/C synchronized PriceRate, the derived-handle consumer was current but
the direct-property consumer retained its old Fresh cache because materializer
assignment was not reported. Explicitly reporting the assignment invalidated it
but created the second propagation path the plan warned about.

For current output mirrors, the existing EF sink-only validation is correct. For
property-backed C, the safest candidate rule is: reject ordinary dependencies on
the target property and require the derived handle. Canonicalization could be a
future compiler feature, but duplicate-safe propagation should not be the
default contract.

## External target mutation

- A ignores external writes until a boundary overwrites the mirror.
- B accepts temporary divergence and overwrites it on Get/boundary.
- C cannot accept an uncoordinated write as an override. The simulator detects
  it and marks synchronization failed.

No override API was added because no dogfood case required one. Private/internal
setters provide the smallest future protection. An analyzer can improve anemic
models but cannot provide runtime atomicity. Generated/intercepted setters are a
larger, optional future mechanism.

## Storage and memory

For a materialized decimal, A/B hold the domain's 16-byte decimal plus another
decimal in a runtime `CacheEntry`, a state enum, object/header/alignment, and one
dictionary entry with reference/hash/bucket overhead. Exact CLR allocation is
runtime-dependent, but duplicate value/cache-entry payload is roughly tens of
bytes per evaluated source, in addition to dictionary capacity.

A true property-backed C state can remove the cached `TValue` for materialized
definitions, but still needs per-source freshness state, identity/dictionary
indexing, and rollback metadata. It saves approximately the value field and some
entry padding—not the dictionary itself. Runtime-only definitions in C-Hybrid
retain the full current cache. This saving is not large enough to decide the
architecture.

## Snapshot, planning, and rollback

Current plans already snapshot affected cache entries, evaluate reversibly, and
restore before returning a forward patch. EF separately snapshots materialized
property values and modified flags. D9 confirmed failed planned computation
restores runtime state/version and does not touch the property.

B/C Get-time assignment falls outside both existing transactions. A production
implementation must either:

1. include property values in affected-source snapshots and restore cache,
   property, state, and counters together; or
2. compute provisionally, assign first, and publish Fresh cache/state only after
   successful assignment; or
3. defer property writes to an explicit commit/materialization boundary, which
   collapses read semantics back toward A.

Assignments during a reversible Plan must not escape into domain objects unless
the plan also carries and restores property writes. Delaying adapter writes until
the existing materialization phase is currently the least invasive behavior.

## Bulk synchronization and optional APIs

The persistence adapter already evaluates affected derived sources in one plan
and materializes only selected mappings, avoiding a public N-call loop. That is
sufficient for EF bulk persistence today. A generic non-EF adapter may need a
bulk materialization operation, but this spike does not justify public `Get`,
`Refresh`, and `Materialize` simultaneously.

If A remains the model, an explicit adapter-owned `Materialize` concept is clear.
If B/C is chosen, `Get` already synchronizes and `Refresh` would mostly duplicate
it. No production synchronization API is proposed by this spike.

## Invasiveness summary

A is current behavior and preserves established rollback/EF boundaries, but has
the plan's highest-priority failure mode: silently stale normal property reads.
B reduces divergence after Get but adds physical mutation, duplicate storage,
setter atomicity, upstream interception, EF surprise, and feedback risks.
C-Hybrid gives one representation after successful Get and can eliminate value
duplication, but only with a new property-backed runtime-state kind, property
write ownership, snapshot integration, incremental assignment, and strong access
protection. The simulator's synchronization-failure overlay demonstrates why a
facade alone is insufficient.

The evidence does not clearly select a winner. C-Hybrid is the only model that
can make the configured property the single business representation, but its
correct implementation is substantially more invasive and still needs a policy
for stale direct reads. Production `runtime.Get` semantics should remain gated.

## Maintainer decisions required

1. **Q1:** May a configured materialized property remain stale after
   `runtime.Get`, as in A?
2. **Q2:** May `Get` physically mutate a source object and EF tracking state, as
   in B/C?
3. **Q3:** Is a materialized property itself storage (C-Hybrid), or only an
   output mirror (A/B)?
4. **Q4:** Must direct external writes to derived target properties be
   prohibited, and by what mechanism?
5. **Q5:** Must downstream definitions use the derived handle, with direct
   target-property dependencies rejected?
6. **Q6:** Should `MaterializeTo` retain output semantics while
   `Derive(target, compute)` declares property-backed state?
7. **Q7:** Is any explicit runtime `Refresh`/`Materialize` API needed beyond
   persistence-adapter bulk materialization?

Do not update the production runtime-read plan or implement a production API
until Q1-Q6 are answered. Q7 can remain deferred.

## Model D — explicit Evaluate / Materialize

Model D keeps the runtime-authoritative value and optional output mirror from A,
but gives the two operations distinct names and return contracts:

```csharp
var rate = runtime.Evaluate(priceRate, link);       // current logical value; no property write
var stored = runtime.Materialize(priceRate, link); // current value + link.PriceRate synchronized
```

The A/B/C evidence above uses the provisional name `Get`. In this comparison it
occupies the logical-read role now named `Evaluate`; no A/B/C result was rerun
with changed semantics. The Model D adapter delegates `Evaluate` to the existing
runtime evaluator and implements `Materialize` as exactly `Evaluate`, compare,
write the requested target, read back, and return the evaluated value. It does
not contain a second evaluator.

The canonical lifecycle was observed as follows:

```text
InvoiceLine.Price 60 -> 55
PriceRate runtime 6 / Invalid; link.PriceRate 6
Evaluate: returns 5.5; runtime 5.5 / Fresh; link.PriceRate remains 6
Materialize: reuses Fresh 5.5; writes link.PriceRate 5.5; returns 5.5
```

Direct `Materialize` computed once when stale, assigned once, and returned 5.5.
Repeated `Evaluate` and repeated `Materialize` reused the Fresh cache; an
equality check avoided redundant target assignment.

For runtime-only definitions, M1 was clearer than M2: making Materialize
degenerate to Evaluate would report success without materializing anything and
would make the two verbs behaviorally indistinguishable for that call.

### EM1-EM12 evidence

| Scenario | Observed result |
|---|---|
| EM1 Evaluate then Materialize | Evaluate returned 5.5/Fresh without changing property 6. Materialize returned and stored 5.5 without another computation. |
| EM2 direct Materialize | A stale value computed exactly once and was assigned. A repeated call neither recomputed nor reassigned. Nullable values worked. `EqualityComparer<T>.Default` honored custom `RateToken` equality. |
| EM3 repeated Evaluate | Two calls returned 5.5 with one computation and no property assignment. Evaluate is lazy/cache-aware, not forced recomputation. |
| EM4 runtime-only derived | Evaluate returned the runtime-only risk score. M1 rejected Materialize with guidance to use Evaluate because no target was registered. |
| EM5 transitive derived | Evaluating UnitRate refreshed PriceRate and UnitRate runtime nodes but wrote neither mirror. T1 Materialize updated only UnitRate. A direct PriceRate-property dependency then produced stale 12 rather than logical 11, proving split-graph risk. |
| EM6 incremental aggregate | Relation-add propagation left FulfilledQuantity Fresh at 7. Evaluate returned it without a scan or write; Materialize wrote 7 without a full recomputation. |
| EM7 pending repair | Evaluate and Materialize could refresh/synchronize PriceRate while UnitRate remained Invalid. The existing repair request remained pending and later dispatched exactly once. |
| EM8 computation failure | Evaluate and Materialize propagated the calculator failure, did not publish Fresh state, did not touch the mirror, and preserved the pending repair request. |
| EM9 setter failure | Evaluation succeeded and stayed Fresh while the setter threw and property stayed 6. Retry reused the cache and wrote 5.5. Read-back detected a normalizing setter and restored 6 rather than accepting stored 5.56 for logical 5.555. |
| EM10 EF tracking | Evaluate left PriceRate unchanged and unmodified. Materialize changed it and EF naturally marked it modified. |
| EM11 persistence without app Materialize | Application flow called an adapter-owned save boundary, not runtime Materialize. The boundary reused the Model D primitive and persisted 5.5. |
| EM12 plain object | An external write of 999 did not affect logical 5.5; Materialize restored and returned 5.5 for a detached object without a DbContext. |

### A/B/C/D comparison

| Question | A runtime-authoritative | B read syncs mirror | C property-backed | D Evaluate/Materialize |
|---|---|---|---|---|
| logical read returns current value | Yes | Yes if synchronization succeeds | Yes if recomputation/write succeeds | Yes |
| logical read name communicates possible computation | No; `Get` sounds passive | No; `Get` also hides a write | No; `Get` sounds passive | Yes; `Evaluate` signals graph work |
| repeated Fresh logical read avoids recompute | Yes | Yes | Yes | Yes |
| logical read has domain-object side effects | No | Yes | Yes | No |
| caller can explicitly synchronize property | Yes, at a separate boundary | Yes | Yes | Yes, with `Materialize` |
| synchronization returns TValue | Not in the experiment helper | Not in the experiment helper | Not in the experiment helper | Yes |
| direct property can be stale before synchronization | Yes | Yes | Yes while node is stale | Yes |
| Fresh runtime + stale mirror representable | Yes | Yes, before read synchronization | No by the property-backed contract | Yes, deliberately |
| setter failure requires logical-cache rollback | No | Yes to preserve read contract | Yes to preserve storage contract | No; Fresh cache and failed mirror write are separate |
| EF tracking side effect obvious from call | Logical read has none | No; hidden behind `Get` | No; hidden behind `Get` | Yes; only `Materialize` writes |
| runtime-only derived supported | Yes | Yes | Yes through hybrid storage | Yes; Evaluate only |
| incremental Fresh value materializes without recompute | Yes at boundary | Yes | Yes if assignment is integrated | Yes |
| transitive evaluation avoids unwanted mirror writes | Yes | No; configured upstreams synchronize | No; evaluation owns property storage | Yes |
| pending repair survives evaluation/materialization | Yes | Yes | Yes | Yes |
| plain-object materialization possible | Yes with an adapter | Yes | Yes with setter access | Yes with optional Core adapter metadata |
| persistence works without explicit app call | Yes | Yes | Yes | Yes; persistence boundary invokes primitive |
| invasive Core changes required | No | High | High | Moderate for typed API plus optional target abstraction |
| invasive EF changes required | No | Moderate | Moderate | Low to moderate to reuse one primitive |

No numeric score was assigned. Model D is clearer than B because logical reads
have no hidden physical mutation, and substantially less coupled than C because
a setter failure does not require rolling back logical freshness. Like A, it
still permits a stale direct property read and stores duplicate representations.
The experiment therefore supports Model D as the strongest API candidate if an
output mirror is accepted, not as a solution to stale public property access.

### Target metadata and mirror state

Two metadata placements are viable in Core terms:

- A compiled-model `MaterializeTo` mapping would make `runtime.Materialize`
  direct, but adds target access, equality, setter failure, and reflection or
  generated-access concerns to Core definitions.
- An optional Core adapter/extension can register those mappings while leaving
  derived identity and evaluation unchanged. The executable used this shape and
  proved the plain-object story.

Persistence-only metadata cannot power plain-object Materialize. Production EF
already has independent mapped materialization; a future implementation should
reuse one semantic primitive where possible, but this spike deliberately did
not move or alter it.

No persistent `Unknown/Synchronized/Stale` mirror state was needed. On each
Materialize, comparing the Fresh logical value with the target was enough to
skip equal writes or decide to assign. Nullable equality and custom value
equality worked with `EqualityComparer<T>.Default`; there was no evidence for a
configurable comparer. A normalizing setter is different: it cannot uphold
`returned value == stored value`, so read-back detected and rejected it and the
adapter restored the prior mirror. The runtime value remained Fresh, making
retry behavior explicit and independent. Mirror rollback is still local
materialization complexity; it does not require rolling back logical cache state.

External mirror writes are silently permitted but never authoritative:
Evaluate continues to return the runtime value, and Materialize overwrites the
rogue value. Encapsulation/private setters are the strongest simple protection;
an analyzer could diagnose writes in anemic models. Source generation or
interception is a later, more invasive option.

### Dependency, scope, and persistence choices

The experiment selected T1: single-node Materialize writes only the requested
target. Materializing UnitRate did not implicitly write its PriceRate upstream
mirror. T2 (materialized upstream closure) is reasonable for an explicitly bulk
persistence operation, but would make a single-target call's physical scope
surprising.

A dependency on the derived handle observes the current logical graph. A direct
dependency on `link.PriceRate` observes a potentially stale mirror and creates a
split graph if mirror writes are also reported as mutations. The preferred
compiler policy is to reject a dependency on a registered target and require
the derived handle. Canonicalization could be considered only if it remains
unambiguous; silently allowing both is the unsafe choice.

The EM11 boundary proves that application code need not call Materialize before
SaveChanges. Persistence can invoke the same per-definition/per-source primitive
for mapped affected objects. No concrete non-EF bulk use case emerged, so this
spike does not justify `Materialize(source)` or collection overloads.

### Naming and diagnostics

`Evaluate` is preferred over `Get` for maintainer approval. `Get` understates
dependency traversal, stale recomputation, and runtime cache/state mutation.
`Evaluate` has the opposite risk—it may sound like forced execution—so its
contract must say:

> Returns the current logical value of the derived definition, evaluating it only when its cached value is not Fresh.

EM3 and EM6 demonstrate that lazy meaning. A future force operation should be
named separately, such as `Recompute`.

`Materialize` is clearer than `Sync`, whose direction and scope are ambiguous,
and `GetAndApply`, which does not identify what is applied. It already implies
evaluation as necessary and returns the value, so `EvaluateAndMaterialize` and
other aliases add no value. `GetState` was useful for diagnostics and executable
assertions only; normal code needed neither a state pre-read nor `TryGetCached`.

### Misuse analysis

- `Use(link.PriceRate)` is silently permitted and may read a stale mirror. The
  API makes the legitimate alternatives clearer but cannot intercept this read.
- `Materialize` when only a logical value is needed is permitted and visibly
  requests an unnecessary physical write; use Evaluate instead.
- `DependsOn(x => x.PriceRate)` is silently dangerous without compiler help;
  target registration should diagnose/reject or safely canonicalize it.
- `link.PriceRate = 999m` is permitted by a public setter and later overwritten;
  encapsulation or an analyzer should diagnose/prohibit rogue writes.
- Expecting Evaluate to force recomputation is a naming risk, diagnosed by the
  required cache-aware documentation and examples; repeated calls do not run
  the calculator.

### Decision against the plan criteria

Model D removes hidden physical mutation from logical reads, names possible
computation, makes EF tracking effects explicit, supports runtime-only and
incremental values, and decouples setter failure from cache rollback. It does
not solve stale direct reads, duplicate mirror/cache storage, or split-property
dependencies. It avoids an application persistence ritual when adapters own the
save boundary. Its likely implementation cost is below property-backed C but
above current A because Core needs a typed Evaluate surface and either Core or
an optional adapter needs materialization metadata.

No production API, EF behavior, sample, or public API baseline was changed.
Maintainer decisions are required before a production plan:

1. **Q1.** Is `Evaluate` preferable to `Get` for the logical read?
2. **Q2.** Is Evaluate explicitly cache-aware/lazy rather than force-recompute? (default: yes)
3. **Q3.** Must Evaluate never synchronize a domain-property mirror? (default: yes)
4. **Q4.** Should Materialize return TValue? (default: yes)
5. **Q5.** Should Materialize reject definitions without a target?
6. **Q6.** Should Materialize synchronize only requested target or upstream materialized closure?
7. **Q7.** Does materialization metadata belong in Core, optional Core adapter, or persistence adapter?
8. **Q8.** Should dependencies on registered mirror properties be rejected/canonicalized to derived handles?
9. **Q9.** Should external writes to mirror properties be protected by analyzer/encapsulation?
10. **Q10.** Is Model D preferable to property-backed Model C after considering stale direct reads?
