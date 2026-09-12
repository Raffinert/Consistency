# Raffinert.Relations — Remaining Implementation Plan

## Purpose

This plan covers the work remaining after the semantic-hardening milestone. Each phase has a completion gate and should be completed before proceeding to dependent phases.

## Implementation status

All twelve phases now have an initial working implementation. The completion gates are covered by the solution build, automated tests, randomized scan-equivalence testing, and a dry benchmark baseline. Items under “Further work” in the README remain intentional extensions rather than unfinished steps in this plan.

## Phase 1 — Introduce explicit access plans — Complete

1. Create a `RelationAccessPlan` abstraction.
2. Add `ScanAccessPlan`.
3. Add `HashJoinAccessPlan`.
4. Create `RelationPlanner` that selects a plan from `RelationAnalysis`.
5. Store the selected plan in each compiled relation.
6. Move all `JoinKeyParts.Count` access decisions out of `RelationRuntimeState`.
7. Update `DebugView` to report the selected plan.
8. Add tests confirming that existing relations select the expected plan.
9. Compare scan and hash results for semantic equivalence.
10. Run `dotnet build` and `dotnet test`.

Completion gate: no behavior change and all existing tests remain green.

## Phase 2 — Preserve complete dependency paths — Complete

1. Add `DependencyPath`.
2. Add `DependencyPathSegment`.
3. Make expression analysis retain complete root-to-leaf paths.
4. Derive segment-level dependency metadata from complete paths when needed.
5. Stop using independently expanded segments as the primary representation.
6. Update change routing and diagnostics to consume the new model.
7. Add depth-one, depth-two, and depth-three analysis tests.

Completion gate: diagnostics can display complete paths without losing existing routing behavior.

## Phase 3 — Introduce reusable navigation indexes — Complete

1. Define stable navigation-edge identity using root type and member.
2. Extract reverse-navigation logic from relation runtime state.
3. Introduce a model/runtime-level `NavigationIndex`.
4. Store both mappings:
   - forward: root to referenced object;
   - reverse: referenced object to roots.
5. Share an index when multiple relations use the same navigation edge.
6. Replace reverse-bucket scans during removal with direct forward lookup.
7. Update add, remove, and reference-change handling.
8. Add tests for:
   - `null -> object`;
   - `object -> null`;
   - `object A -> object B`;
   - many roots referencing one object;
   - multiple relations sharing one edge.

Completion gate: one reference update performs approximately O(1) dictionary work per affected edge.

## Phase 4 — Support deep navigation propagation — Complete

1. Connect navigation indexes according to complete dependency paths.
2. Resolve changes backward through two navigation levels.
3. Add two-level nested property-change tests.
4. Generalize traversal to arbitrary path depth.
5. Prevent cycles and duplicate affected-root results.
6. Add depth-three tests.
7. Test shared intermediate objects and multiple roots.

Completion gate: a leaf-object change reindexes every affected relation root at arbitrary supported depth.

## Phase 5 — Centralize change routing — Complete

1. Introduce `ImpactResolver`.
2. Move property-change interpretation out of individual relation runtimes.
3. Define `AccessImpact`.
4. Define `SemanticImpact`.
5. Resolve directly changed roots.
6. Resolve roots reached through navigation indexes.
7. Route access impact to access-plan maintenance.
8. Record semantic impact even when no reindex is necessary.
9. Update diagnostics to expose affected and reindexed relations.
10. Add tests where residual-predicate changes cause semantic impact only.

Completion gate: “no reindex required” no longer means “nothing was affected.”

## Phase 6 — Support unregistered nested objects — Complete

1. Index referenced objects independently of root object-set registration.
2. Allow `Change.Property(nestedObject, ...)` to enter impact resolution.
3. Find affected roots through navigation indexes.
4. Keep object sets limited to managed root populations.
5. Define behavior when a nested object matches no navigation index.
6. Add tests where only relation root types have object sets.

Completion gate: nested scalar changes propagate without registering every referenced CLR type as an object set.

## Phase 7 — Add atomic `ChangeSet` — Complete

1. Introduce `ChangeSet.Create(...)`.
2. Add `RelationRuntime.Apply(ChangeSet)`.
3. Validate every change before mutating runtime indexes.
4. Reject the whole batch if any change is invalid.
5. Resolve combined impact and deduplicate affected roots.
6. Update navigation indexes.
7. Update access-plan indexes.
8. Record semantic impact.
9. Return or internally produce `ChangeImpact`.
10. Keep single-change `Apply` as a wrapper around a one-item change set.
11. Add tests for:
    - two indexed fields changing together;
    - navigation plus nested scalar change;
    - invalid change rejecting the whole batch;
    - repeated impacts being deduplicated.

Completion gate: runtime queries cannot observe partially applied batches.

## Phase 8 — Strengthen correctness testing — Complete

1. Add a forced-scan testing path.
2. Run identical datasets through scan and optimized plans.
3. Compare results after additions, removals, and changes.
4. Add randomized operation sequences.
5. Include null navigation and shared-reference cases.
6. Include composite keys and residual predicates.
7. Add regression tests for every discovered mismatch.

Completion gate: optimized and forced-scan results remain identical across randomized mutation sequences.

## Phase 9 — Add benchmarks — Complete

1. Create `benchmarks/Raffinert.Relations.Benchmarks`.
2. Benchmark 10,000- and 100,000-object populations.
3. Cover single and composite hash joins.
4. Cover one-level and three-level navigation.
5. Measure add, lookup, removal, and change application.
6. Measure allocations.
7. Add performance baselines for reference changes.
8. Watch specifically for O(N) single-object operations.

Completion gate: benchmark results provide a baseline before higher-level features are added.

## Phase 10 — Expand expression coverage — Complete

1. Recognize ordinal `string.Equals`.
2. Add comparer-aware hash keys.
3. Recognize ordinal-ignore-case equality.
4. Normalize safe nullable and conversion expressions.
5. Recognize constant filters for diagnostics.
6. Recognize range conditions as metadata only.
7. Keep unsupported forms as residual predicates or scan plans.
8. Add scan-equivalence tests for each recognized form.

Completion gate: every optimization uses equality and hashing semantics identical to the predicate.

## Phase 11 — Consider bidirectional access — Complete

1. Add neutral relation-direction metadata.
2. Design `RelatedFromLeft` and `RelatedFromRight`.
3. Let planner policy decide which direction to index.
4. Preserve scan fallback for an unindexed direction.
5. Add directional equivalence tests.

Completion gate: reverse lookup is possible without requiring two indexes for every relation.

## Phase 12 — Implement higher-level features — Complete

Begin this phase only after Phases 1–8 are complete.

1. Add `DerivedState`.
2. Implement `Fresh` and `Dirty`.
3. Add distinct `Invalid` propagation.
4. Connect derived state to semantic impact.
5. Add invariants using the same dependency graph.
6. Add evaluation and repair policies.
7. Create a separate EF Core adapter package.
8. Translate EF Core change tracking into atomic `ChangeSet` instances.

## Suggested next task

Profile the full benchmark matrix under a statistically meaningful BenchmarkDotNet job, then use the results to prioritize selective derived-state invalidation, collection navigation, and any additional access plans.
