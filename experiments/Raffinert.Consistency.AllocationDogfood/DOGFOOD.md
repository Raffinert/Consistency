# Allocation consistency dogfood ninth-pass findings

## One-to-one capture linearity closeout

The eighth-pass FK-only one-to-one correctness fix initially emitted provisional principal-side changes and then
called `RemoveAll` once per principal reference before emitting post-fixup truth. Deterministic diagnostics proved
that cleanup was quadratic: 10,000 retargets produced 20,000 principal evidences but 399,990,000 change-list
inspections.

Principal-side capture is now explicitly two-phase. The pre-fixup snapshot resolves and retains original evidence
without emitting a provisional principal mutation. After one `DetectChanges()`, capture reads the stabilized current
navigation and emits each real principal change once. Dependent references and collection resets retain their
indexed pre-fixup behavior.

| retargets | old capture | old cleanup inspections | new capture | allocated bytes | evidences | cleanup scans | emitted | lookups/candidates |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 18.233 ms | 39,900 | 16.569 ms | 1,268,392 | 200 | 0 | 200 | 400 / 0 |
| 1,000 | 64.238 ms | 3,999,000 | 34.502 ms | 12,194,920 | 2,000 | 0 | 2,000 | 4,000 / 0 |
| 10,000 | 1,043.365 ms | 399,990,000 | 231.397 ms | 121,376,400 | 20,000 | 0 | 20,000 | 40,000 / 0 |

The deterministic regression also proves exactly three navigation changes per retarget, no duplicate owner/member
mutation, proportional evidence/emission counts, zero cleanup scans, and zero reference candidate checks. Focused
tests cover FK-only retarget, removal, addition, no-op, explicit replacement, and ambiguity failure.

## EF capture correctness closeout

The eighth pass makes snapshot index membership follow EF metadata identity. Indexes now use
`IEntityType.IsAssignableFrom(entry.Metadata)`: unrelated shared entity types backed by the same CLR type are
excluded even when their key values match, while base-target relationships continue to include valid EF-derived
entries.

FK-only one-to-one retargeting exposed a real ordering gap. Before the fix, dependent-side lookup observed the new
foreign key while principal-side CLR navigations could still describe the old graph. Capture now records original
relationship evidence before fixup, invokes change detection once, and refreshes principal-side current references
from the stabilized graph. Both public adapter surfaces report the dependent retarget, removal from the old
principal, and addition to the new principal. Existing owned-reference replacement, ambiguity, generated-key,
partial-null, structural-key, and custom-comparer regressions remain green.

The current one-fingerprint measurements remain indexed, with zero reference candidate checks:

| tracked entries | capture | allocated bytes | references | reference lookups | candidate checks |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 2.219 ms | 690,992 | 200 | 400 | 0 |
| 1,000 | 18.894 ms | 6,132,616 | 2,000 | 4,000 | 0 |
| 10,000 | 46.860 ms | 58,067,688 | 20,000 | 40,000 | 0 |

The unchanged rejected-preview workload measured 0.968/1.554 ms and 4,726,736 bytes at 100;
5.769/10.950 ms and 41,307,208 bytes at 1,000; and 39.898/89.455 ms and 406,749,576 bytes at 10,000.
Every size retained five reads and six fingerprint validations.

## EF capture hardening

The seventh pass preserves the sixth-pass indexed navigation design and closes three narrower gaps. Optional
composite foreign keys now follow EF relationship semantics: any null component means there is no relationship,
so partial tuples are neither principal lookup keys nor dependent-index entries. Transitions between complete and
partial-null tuples emit exact old/new navigation evidence and reset only real principal collections.

Policy-aware capture now creates one `TrackedGraphSnapshot` for navigation and scalar/generated-fixup evidence.
Generated-FK principal lookup uses the snapshot's reference-identity entry map instead of scanning all tracked
entries per modified FK property. Regression cases at 100, 1,000, and 10,000 modified dependents observe exactly
one principal lookup per modified FK and zero full-tracker principal scans.

EF's `IProperty.GetKeyValueComparer()` is the key equality authority for tracked relationship indexes. Ordinary
`object.Equals` is insufficient for supported values such as distinct `byte[]` instances with equal contents and
value-converted keys with configured comparers. Snapshot dictionaries cache one component-comparer set per index
and use those comparers for both equality and hashing; ordinary scalar and composite keys retain their behavior.

## Fixed by framework changes

- Relation aggregate policies now support strongly typed directional item-member classifiers. Fulfillment and
  allocation quantity increases classify as `Invalid`; decreases classify as `Dirty`; membership add/remove
  policies remain independent.
- Repairability is immutable model metadata configured with `RepairWhenViolated()`. A repair request is emitted
  only after an affected repairable invariant is evaluated and its predicate returns false. Safe capacity
  increases and safe quantity decreases therefore produce no repair work.
- Core exposes structured repair data through `ApplyDetailed(...).Result.RepairRequests`. An ordinary EF save
  rejected by an enforced invariant exposes the relevant requests through
  `ConsistencyInvariantViolationException.RepairRequests`. The compiled model contains no application callback
  or mutable repair queue; the allocation application translates requests into local repair requirements and
  owns replacement selection.
- `FromMembership(candidateSupplies, allocation => allocation.Demand, allocation => allocation.Supply)` reuses
  the existing candidate relation and its indexes. Allocation compatibility has one semantic authority: the
  candidate relation predicate.
- Projected membership tracks both selected references and changes that alter the underlying relation. Its
  derived value participates in invariant propagation; causal relation causes identify the underlying relation
  plus both projection selector paths.
- The rich `Supply.ChangeCapacity` method remains Raffinert-free. EF continues to observe ordinary tracked
  mutations at the `SaveChangesAsync` boundary.

## Eager repair proof

`RepairWhenViolated()` is not merely a label on a future handler. To prove whether repair is needed, Raffinert
evaluates every affected source of a repair-enabled invariant during mutation application or planning. Upstream
derived values needed by the predicate therefore become `Fresh` even when the incoming dependency impact was
`Dirty` or `Invalid`. A request is emitted only when that evaluated predicate is false, and at most once per
invariant/source in one operation result.

An invariant without `RepairWhenViolated()` retains its configured lazy reaction semantics unless another
explicit policy requests evaluation, such as EF enforcement or `EvaluateImmediately`.

## Still awkward

- Structured requests identify a framework invariant and source; the application still translates that generic
  identity into a domain-specific repair requirement before choosing a replacement.
- Core POCO callers must report mutations explicitly, while EF callers rely on tracked changes and save-time
  translation. The distinction is honest but requires two integration habits.
- The full-runtime reseed remains as a correctness/performance baseline, but the rejected-save scenario now uses
  the experimental proposed-state view. The application must obtain a fresh view after each repair mutation.
- The compact projected-membership declaration is clear for this case, but larger models still need explicit
  names and diagnostics to make cross-object ownership easy to follow.
- Causal traces identify definitions, members, relation pairs, and sources. They do not yet include the evaluated
  business operands that explain a numeric failure in domain terms.

## Repair reason semantics

`RepairRequestInfo.Reason` is the severity propagated by the current operation, not a projection of the
invariant's previous cached state. An unevaluated invariant therefore reports `Invalid` for an invalid incoming
impact and `Dirty` for a dirty incoming impact. Multiple causes for one invariant/source pair merge with
`Invalid` dominating `Dirty`; request deduplication remains scoped to one operation.

## Proposed-state repair gap

The EF tracked graph already contains proposed CLR values when Raffinert plans a save. If an enforced invariant
rejects that plan, the committed runtime intentionally remains at the durable baseline, but application repair
still needs relation, derived-value, and invariant queries against the proposed tracked state. The current
baseline solution creates a second runtime and seeds it from every object in `DbSet.Local` before running the
repair query. That is correct for this complete tracked graph, but it repeats O(graph) object admission and index
construction. The fourth-pass experiment compares that baseline with a read-only proposed-state view backed by
the rejected prepared plan. The full-runtime reseed remains in place as the baseline comparison path.

## Proposed-state experiment result

The internal preview reuses the prepared plan's final object-set overlay and current proposed CLR values. It builds
isolated derived/invariant query caches and evaluates relations against the final proposed object membership and
relation predicates. It does not install the forward patch into the committed runtime, rebuild all relation indexes,
dispatch policy work, materialize mirrors, or own a repair workflow. Runtime version, committed relation/index
state, and committed derived/invariant state remain unchanged.

Preview detects runtime-plan drift and prepared-mutation drift. The EF rejected-preview integration also detects
changes visible to the adapter's relevant tracked-state fingerprint. The dependency-free core cannot detect
arbitrary POCO mutations that were never reported as changes. A core preview may therefore read a current CLR value
that changed outside its prepared mutation; that behavior is not a validated snapshot guarantee.

| Drift type | Detected? | Mechanism |
| --- | --- | --- |
| committed runtime version/baseline revision changes | yes | runtime plan validation |
| prepared mutation member changes | yes | prepared domain assumptions |
| relevant EF tracked mutation after rejected save | yes | EF mutation fingerprint / validation |
| EF add/remove/navigation changes represented in tracked capture | yes | EF fingerprint |
| arbitrary unreported core POCO mutation outside prepared mutation | not guaranteed | core has no arbitrary mutation observer |

The rejected EF flow is now:

```text
tracked mutation
  -> prepare binding plan
  -> enforced invariant rejection
  -> scoped session retains rejected plan + mutation fingerprint
  -> application obtains proposed-state view
  -> application selects and mutates one replacement
  -> fresh preview/re-plan on the next attempt
  -> SQL success
  -> committed runtime plan installation and dispatch
```

The application still owns candidate selection, ordering, unresolved behavior, and convergence. The baseline
`ProcessCurrentGraph(...)` full reseed path remains for comparison and future fallback testing.

### Core preview construction

Five measured iterations after one warm-up produced these representative medians on the local development
machine. Setup is the time to create a query context; query is the first remaining-capacity query.

| graph size | full reseed setup/query | preview setup/query | full allocated bytes | preview allocated bytes |
| ---: | ---: | ---: | ---: | ---: |
| 100 | 0.473 / 0.036 ms | 0.013 / 0.042 ms | 459,632 | 10,968 |
| 1,000 | 4.007 / 0.093 ms | 0.012 / 0.115 ms | 4,137,720 | 25,368 |
| 10,000 | 45.152 / 1.182 ms | 0.032 / 2.281 ms | 40,050,936 | 169,368 |

Preview creation and allocations are materially smaller and nearly independent of graph size. Its first relation
query is slightly slower at 10,000 because the prototype scans proposed candidates rather than maintaining a
second index. This is sufficient evidence to keep the experiment, but not to graduate a public API before query
workload and relation-overlay semantics are broadened. These are core-only figures and do not include EF change
detection, discovery, or mutation fingerprinting.

### Actual EF rejected-preview repair query

The EF benchmark uses an ordinary rejected `SaveChanges`, obtains the retained-plan preview from the scoped EF
session, and runs the same five-read repair-decision workload. Values below are medians from five Release iterations
after warm-up on this development machine; allocated bytes cover rejection/preview creation plus the query.

The fifth-pass implementation exposed a super-linear tracked-navigation capture path: reference resolution
re-enumerated every tracked entry for every tracked reference, and collection detection repeated another global
scan. A focused one-fingerprint benchmark made that cost explicit. The sixth pass snapshots tracked entries once
and lazily builds metadata-aware current/original principal-key and foreign-key indexes.

| tracked entries | old capture | indexed capture | old allocated bytes | indexed allocated bytes | old reference candidate checks | indexed reference lookups |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 4.238 ms | 2.254 ms | 3,531,104 | 658,392 | 41,200 | 400 |
| 1,000 | 54.220 ms | 20.307 ms | 264,984,704 | 5,812,016 | 4,012,000 | 4,000 |
| 10,000 | 4,238.213 ms | 26.890 ms | 25,686,713,664 | 54,860,888 | 400,120,000 | 40,000 |

The unchanged rejected-save workload now measures:

| allocations | rejection + preview | repair query | allocated bytes | preview reads | fingerprint validations |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 1.135 ms | 1.755 ms | 4,478,208 | 5 | 6 |
| 1,000 | 4.204 ms | 10.012 ms | 39,063,176 | 5 | 6 |
| 10,000 | 40.342 ms | 96.279 ms | 384,347,784 | 5 | 6 |

For comparison, the pre-optimization run on the same machine was 7.899/18.309 ms and 16,647,776 bytes at
100; 78.069/172.954 ms and 965,278,448 bytes at 1,000; and 8,952.954/22,400.720 ms and
90,287,397,064 bytes at 10,000. Those figures are historical evidence, not current performance.

Each decision validates once while creating the preview and once before each of its five reads. Repeated full EF
fingerprinting still provides the strongest stale-state guarantee, but indexed capture makes the six validations
practical at 10,000 allocations. Relation scanning remains visible in the core query numbers.

## EF validation: CURRENT VALIDATION COUNT IS NOW ACCEPTABLE

The implementation keeps one validation at preview creation and one before every read. A tracked-state-only
prototype was rejected for preview validation: a new database consumer can appear while the tracked mutation
fingerprint remains unchanged, but authoritative discovery changes the admitted evaluation closure and the full
fingerprint. External consumer discovery therefore remains part of every preview validation, and final save still
revalidates persistence authority. No validation lease or weaker mutation-observation contract was introduced.
These repeated checks strengthen stale-state detection; they do not create an atomic or stable database snapshot.
Another transaction can change authoritative rows immediately after any preview validation completes. Final
`SaveChanges` planning and enforcement remains the durability boundary that revalidates authoritative consistency
requirements. The preview remains internal and experimental.

## Change-tracker enumeration audit

The seventh-pass audit found no nested full-tracker scan in mutation/fingerprint relationship capture.

- One-enumeration-per-operation cases: ordinary unit-of-work lifecycle/scalar capture, snapshot construction,
  generated-value candidate capture, runtime baseline admission/stabilization, and the initial deleted-entry guard.
- Per-result scans outside mutation capture remain in materialization write lookup and pending-plan mirror tracking;
  the store-side referential-action guard can also scan tracked entries for runtime instances. These are separate
  policy/session paths and remain visible future optimization candidates rather than being mechanically rewritten.
- Intentional scoped lookups remain for explicit object materialization eligibility and for merging tracked roots
  into each external-consumer authority request.

Generated-FK fixup was the only per-modified-property scan in policy-aware mutation capture. It is now replaced by
the snapshot reference map, with diagnostics proving zero full-tracker scans.

## Multi-step repair: PROVEN

The same-source scenario converges in three save attempts: two rejected saves, two preview instances, two repair
mutations, and one final successful save. Runtime version remains unchanged through both rejections and advances
once on success. The first allocation moves to S2 and the second to S3; no full runtime reseed is used.

The second-order scenario first rejects S1. Its application-owned first move places the allocation on S2, making S2
the source of the next independently planned repair request. A fresh preview then selects viable S3, and the third
save succeeds with final remaining capacities of 2, 2, and 6. This also uses zero full runtime reseeds.

## API decision: KEEP EXPERIMENTAL

The proposed-state view removes full graph reseeding from the rejected-save dogfood path, reuses prepared-plan
validation and lifecycle evidence, and has explicit isolation and staleness tests. It remains internal/experimental:
the prototype intentionally has only `Evaluate`, `Related`, and state reads; it has no materialization, dispatch,
persistence, transaction, or workflow behavior. A future public API should be designed only after measuring real
repair query workloads and deciding whether indexed relation overlays are needed.

## Fundamental limitations

- `ConsistencyScope.Complete(...)` is a host assertion about loaded coverage, not proof that a query fetched every
  database row. The runtime cannot detect an under-fetching host.
- The dependency-free core runtime cannot observe arbitrary POCO mutation without a reported `Change`; that is
  the responsibility of a mutation source or adapter.
- Replacement ranking, convergence policy, and unresolved-repair workflow remain application concerns. Core
  consistency does not become a workflow engine.
- Preview relation queries scan proposed candidates rather than maintaining a second index.
- Safe EF previews retain one authoritative fingerprint per read. Indexed tracked-graph capture makes the measured
  six-validation repair decision practical, but discovery and change detection still scale with the authoritative
  work required by the host configuration.
- Persistence can enforce the configured invariant and preserve runtime/SQL transaction boundaries, but it
  cannot infer untracked SQL-side effects outside the adapter's supported evidence model.

## Architectural result after second pass

The original four gaps are resolved in the framework and dogfood: directional relation-item impact is typed,
repair requests follow evaluated violations, callback/queue state is gone, and compatibility consumes the
candidate relation instead of duplicating its predicate. The resulting boundary is:

```text
domain model       local business meaning and admissibility
Consistency        dependency graph, propagation, freshness, evaluation, repair data
application        repair decisions, replacement ranking, convergence
EF adapter         tracked-change translation and persistence boundary
```

The dogfood now expresses the same domain with fewer semantic duplicates and with repair work visible in the
operation result. The remaining awkwardness is integration ceremony and diagnostics depth, not a reason to move
business reallocation into the framework.
