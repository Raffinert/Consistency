# Codex implementation plan — compiled DAG runtime authority and traversal hardening

Status: **COMPLETE**

Verified starting `main` SHA: `b9a5a0e46fad9f8ad42d3b543fc79febfd186172`

Tasks: **256–263**

## Completion record — 2026-09-17

Starting remote SHA: `5308069f4a614e660472b4d44ef54a1bc1875443`

| Task | SHA |
|---|---|
| 256 | `4685a1358a3c2eef3554f23261d16c664327d6b4` |
| 257 | `eda76241695361f1f9d760ecd6f17c15cc791d3b` |
| 258 | `ed6afb32f15c8f2675639544471c5e04ec4c7070` |
| 259 | `733ac4c575a19601ecb18843938a0775150994e4` |
| 260 | `de7f50dadd4918374e61510f993ee41cd8496437` |
| 261 | `12294c06a7124e999c28a47b7bd38cbf2ec98cec` |
| 262 | `d28f7537f5678f11b47ad143e37d21b97b378e9e` |
| 263 / final implementation | `dddb1d819b7136b16061153715b69db63b68bf83` |

Topology reconstruction removed:

```text
_derivedByUpstream: yes
_upstreamsByDerived: yes
_invariantsByDerived: yes
```

Direct seed indexes preserved:

```text
_derivedByRelation: yes
_derivedByMember: yes
_invariantsByMember: yes
```

The `CaptureState` fixed-point loop is removed. Compiled derived-to-derived edges are represented by
`CompiledDependencyEdge(FromNodeId, ToNodeId, Kind, DerivedInput)`, where `DerivedInput` is the exact
`UpstreamDerivedInput` instance owned by the downstream definition. Derived-to-invariant edges have an
explicit kind and a null `DerivedInput`. Incoming and outgoing adjacency are compiled once as read-only,
deterministically ordered lists.

Cycle diagnostics now report a deterministic real witness, for example:

```text
Dependency cycle detected: A -> B -> C -> A.
```

Public API diff: none. Both `PublicAPI.Unshipped.txt` files remain empty.

Benchmark source: `benchmarks/Raffinert.Consistency.Benchmarks/DependencyDagBenchmarks.cs`

Benchmark results: `benchmarks/DependencyDag-Results.md`. On the recorded machine, increasing a deep chain
from 32 to 128 nodes measured 4.02x apply time and 3.82x allocation, consistent with node/edge-proportional
traversal rather than repeated fixed-point scans.

Required behavior maps to these exact tests:

```text
Compiled_graph_assigns_upstream_before_downstream_topological_order
Compiled_graph_places_invariant_after_all_of_its_upstream_derived_nodes
Compiled_graph_deduplicates_duplicate_logical_edges
Compiled_graph_topological_order_is_deterministic_for_same_model
Compiled_graph_preserves_diamond_structure_without_duplicate_downstream_edges
Compiled_graph_outgoing_adjacency_matches_edges
Compiled_graph_incoming_adjacency_matches_edges
Derived_to_derived_edge_retains_exact_upstream_input_metadata
Derived_to_invariant_edge_has_correct_kind
Compiled_graph_adjacency_is_duplicate_free
Runtime_DAG_topology_matches_compiled_graph_for_chain_diamond_and_invariant
Projected_runtime_edge_uses_compiled_input_metadata
Deep_derived_chain_propagates_in_topological_order_without_registration_order_dependency
Projected_upstream_chain_maps_sources_across_sets_through_multiple_DAG_levels
Diamond_downstream_source_is_propagated_once_semantically
Capture_state_propagates_deep_chain_in_one_topological_pass
Capture_state_propagates_diamond_sources_without_duplicate_semantics
Capture_state_propagates_projected_sources_across_sets
Capture_state_merges_direct_and_inherited_sources_for_same_downstream_node
Capture_state_preserves_previous_snapshot_sources_through_downstream_chain
Causal_evidence_is_identical_for_compiled_DAG_chain_and_diamond_after_topology_refactor
Compiled_graph_rejects_self_cycle_with_actual_cycle_path
Compiled_graph_rejects_two_node_cycle_with_actual_cycle_path
Compiled_graph_reports_one_real_cycle_when_tail_nodes_depend_on_cycle
Compiled_graph_cycle_message_is_deterministic
Compiled_graph_cycle_message_prefers_definition_keys
DebugView_preserves_compiled_dependency_DAG_nodes_order_and_edges
```

Remaining production uses of `UpstreamDerivedInput` outside `CompiledDependencyGraph` are semantic rather
than propagation-topology reconstruction:

- `DependencyGraphRuntime` consumes the exact descriptor supplied by compiled incoming edges to perform
  source mapping and causal propagation.
- `ProjectionIndexRegistry` builds and queries the reverse projection indexes encoded by projected inputs.
- `ConsistencyRuntime` validates projected targets and ownership.
- scope-requirement and external-consumer analysis inspect definition semantics to calculate authoritative
  data coverage; they do not drive dependency-state propagation or construct runtime adjacency.
- model diagnostics, DebugView, and causal result rendering describe model/evidence semantics.
- derived/invariant definitions and runtime states retain their computation/evaluation descriptors.

Local verification:

```text
restore: PASS
build: PASS (0 warnings, 0 errors)
Core net8: PASS (346)
Core net10: PASS (346)
EF net10: PASS (176)
DependencyMaintenanceSample: PASS
OrderFulfillmentSample: PASS
EntityFrameworkCore.Sample: PASS
format: PASS
package creation and non-publication verifier: PASS (0.1.0-rc.1)
```

The build used isolated output under `C:\Windows\Temp\Raffinert-dag-dddb1d8` because three stale,
privileged sample processes held the ordinary DependencyMaintenanceSample output DLLs. The isolated build
preserved Release configuration and the required `--no-build` test/sample flow.

Remote final SHA: `dddb1d819b7136b16061153715b69db63b68bf83`

Exact GitHub Actions run: [CI #313 / run 35245686551](https://github.com/Raffinert/Consistency/actions/runs/35245686551)

```text
CI head_sha: dddb1d819b7136b16061153715b69db63b68bf83
CI status: completed
CI conclusion: success
git status --short: empty
Release-prep baseline reset to final DAG SHA: yes
```

This plan intentionally re-opens runtime/architecture work **before the first public release**.

The `0.1.0-rc.1` release-preparation wave (Tasks 247–255) was paused during this DAG-hardening work.
It is now reactivated from the final implementation SHA above; pre-DAG release evidence is obsolete.

The purpose of this wave is not to add a graph framework. Raffinert.Consistency already has a real dependency DAG. The purpose is to make the compiled DAG the authoritative topology used by runtime propagation, remove redundant topology reconstruction, replace an unnecessary fixed-point scan with one topological pass, strengthen cycle diagnostics, and prove that all existing source-scoped, projected, relation-derived, invariant, planning, rollback, and causal behavior remains unchanged.

---

# 0. Verified current state

The following facts were verified directly on `main` before this plan was created.

## 0.1 A real compiled dependency DAG already exists

Target:

```text
src/Raffinert.Consistency/Model/CompiledDependencyGraph.cs
```

`CompiledDependencyGraph.Compile(...)` currently:

```text
1. assigns IDs to derived + invariant definitions;
2. creates edges:
   upstream derived -> downstream derived
   upstream derived -> invariant
3. computes indegrees;
4. performs Kahn topological sort;
5. rejects cycles when not all nodes can be ordered;
6. stores TopologicalOrder on CompiledDependencyNode;
7. stores distinct CompiledDependencyEdge values.
```

This behavior is correct in principle and must be preserved.

## 0.2 Runtime construction already depends on topological order

Target:

```text
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
```

Derived runtime state is created by iterating compiled derived nodes in topological order so an upstream runtime state already exists before a downstream definition asks for it.

Do not regress this guarantee.

## 0.3 DependencyGraphRuntime reconstructs topology from definitions

Target:

```text
src/Raffinert.Consistency/Dependencies/DependencyGraphRuntime.cs
```

Current topology-like structures include:

```text
_invariantsByDerived
_derivedByUpstream
_upstreamsByDerived
```

These are reconstructed by reading `definition.Inputs` / `invariant.UpstreamDerived` again even though `CompiledDependencyGraph` already contains the DAG.

Other maps in the same class are **not** redundant DAG topology and must not be blindly removed:

```text
_derivedByRelation
_derivedByMember
_invariantsByMember
```

Those are seed/index structures used to find directly affected nodes from relation or member changes.

A weak agent must not confuse these categories.

## 0.4 Runtime propagation already uses graph traversal

`ExpandDownstream(...)` currently performs a queue-based traversal over `_derivedByUpstream` and deduplicates nodes through `HashSet<DerivedNode>`.

This is appropriate conceptually. The problem is not BFS itself; the problem is that it traverses a runtime-reconstructed topology instead of the compiled graph.

## 0.5 CaptureState contains an unnecessary fixed-point loop

Current shape:

```csharp
var changed = true;
while (changed)
{
    changed = false;
    foreach (var node in _derivedNodes)
    {
        ... propagate upstream-derived source sets into downstream source sets ...
        changed |= derivedSources[node].Add(source);
    }
}
```

`_derivedNodes` is already topologically ordered.

For an acyclic graph, after direct seeds have been collected for every node, one forward pass in topological order is sufficient to propagate every upstream source set to every downstream node. Repeating until no changes is unnecessary and obscures the actual DAG execution contract.

The replacement must be proven, not assumed.

## 0.6 Existing DAG proof is already valuable and must be preserved

Current tests include at least:

```text
tests/Raffinert.Consistency.Tests/DerivedDagTests.cs
tests/Raffinert.Consistency.Tests/RandomizedDagPropagationTests.cs
```

These cover chains, relation-derived composition, diamonds, multi-input values, invariants, ordering independence, and randomized comparison against a plain oracle.

Do not weaken, delete, rename away, or bypass these tests.

---

# 1. Architectural boundary — do not misunderstand the graph

There are two different graph concepts in Raffinert.Consistency.

## 1.1 Dependency definition graph — MUST remain a DAG

Example:

```text
Source member / relation inputs
        |
        v
Derived A
   |      \
   v       v
Derived B  Derived C
    \       /
     v     v
     Derived D
         |
         v
     Invariant E
```

This graph is compiled, finite, immutable after `Build()`, and must be acyclic.

Graph algorithms belong here:

```text
cycle detection
topological ordering
downstream reachability
source-set propagation along upstream edges
```

## 1.2 Runtime object/navigation/relation graph — MUST NOT be assumed acyclic

Domain objects and navigation paths may contain cycles.

Examples:

```text
Employee.Manager.Employee...
Customer.PreferredOrder.Customer...
A.Parent = B; B.Parent = A
```

Do not introduce a global assumption that domain/object/navigation graphs are DAGs.

Navigation/relation lookup must continue to use the existing indexes and exact access plans.

## 1.3 No third-party graph package

Do not add GraphX, QuikGraph, QuickGraph, or any generic graph dependency.

The required algorithms are small, deterministic, internal, and already mostly implemented.

---

# 2. Non-negotiable semantic invariants

The refactor is internal. Unless a task explicitly states otherwise, public behavior must not change.

Preserve all of the following:

```text
- exact DerivedValueState semantics;
- exact InvariantEvaluationState semantics;
- source-scoped Dirty / Invalid severity;
- direct evidence and causal evidence semantics;
- projected upstream source mapping;
- relation-derived membership/item/source impact behavior;
- policy reaction behavior;
- planning atomicity;
- rollback snapshot correctness;
- CoverageAdmission structural semantics;
- runtime.Version semantics;
- mutation-order independence;
- object-set identity boundaries;
- public API surface;
- DebugView semantics unless explicitly improved by Task 261;
- current EF behavior;
```

No public API additions are expected for Tasks 256–263.

If a production change appears to require public API, stop and find an internal design instead unless there is a demonstrated correctness reason.

---

# 3. Anti-dumb execution discipline

A task is not complete because code compiles.

For **every task**:

```text
1. git fetch origin
2. git checkout main
3. git pull --ff-only
4. git status --short
5. record git rev-parse HEAD
6. create a focused test or proof first where the task changes behavior/algorithm
7. run the focused test before production changes when possible
8. make the smallest coherent production change
9. run the focused tests
10. run the full Core test project on net10.0
11. commit
12. push
13. verify the commit exists remotely
14. record the SHA in the execution log
```

Do not accumulate all tasks into one giant commit.

Suggested branch if the agent does not commit directly to `main`:

```text
codex/compiled-dag-runtime-hardening
```

If using a branch, push it immediately before doing implementation work.

## Forbidden shortcuts

Do not:

```text
- delete the topological sort and rely on declaration order;
- keep two independent topology models after claiming the compiled graph is authoritative;
- turn every runtime index into the compiled graph;
- remove _derivedByMember, _derivedByRelation, or _invariantsByMember merely because they are adjacency-like;
- replace projected UpstreamDerivedInput metadata with only integer from/to IDs;
- add repeated scans under a different method name;
- replace HashSet dedupe with repeated List.Contains scans;
- materialize transitive closure for every node unless a benchmark proves it is needed;
- expose internal graph nodes/edges publicly just to simplify tests;
- use reflection in production to recover upstream definitions;
- weaken source-scoped precision to whole-node invalidation;
- change semantic relation/object lifecycle behavior;
- add a third-party graph package;
- claim cycle diagnostics are improved when the message still lists arbitrary remaining nodes instead of a real cycle witness;
- claim performance improvement without preserving correctness tests and adding a focused benchmark;
- continue release-prep 247–255 from the old baseline after this wave lands.
```

---

# Task 256 — freeze DAG execution contracts before refactoring

Purpose: create tests that describe the exact behavior the refactor must preserve.

## 256.1 Add a dedicated low-level compiled graph test file

Create:

```text
tests/Raffinert.Consistency.Tests/CompiledDependencyGraphTests.cs
```

Minimum tests:

```text
Compiled_graph_assigns_upstream_before_downstream_topological_order
Compiled_graph_places_invariant_after_all_of_its_upstream_derived_nodes
Compiled_graph_deduplicates_duplicate_logical_edges
Compiled_graph_topological_order_is_deterministic_for_same_model
Compiled_graph_preserves_diamond_structure_without_duplicate_downstream_edges
```

Do not assert exact numeric IDs unless the ID contract is intentionally stable.

Assert structural relationships instead:

```text
position(A) < position(B)
position(A) < position(C)
position(B) < position(D)
position(C) < position(D)
position(D) < position(Invariant)
```

## 256.2 Add runtime execution-order proof with a deep chain

Add a deterministic chain deeper than current small examples:

```text
A0 -> A1 -> A2 -> ... -> A11 -> invariant
```

Required test:

```text
Deep_derived_chain_propagates_in_topological_order_without_registration_order_dependency
```

Use a single source object and make the final value sensitive to every upstream stage.

Change the root input and assert:

```text
all expected downstream states become non-Fresh;
reading final value recomputes the exact expected result;
all upstream values needed by final evaluation become Fresh in dependency order;
invariant evaluates exact expected result.
```

Do not use sleeps or logging-order assumptions.

## 256.3 Add projected cross-set DAG proof

Because projected upstream edges carry mapping metadata, add:

```text
Projected_upstream_chain_maps_sources_across_sets_through_multiple_DAG_levels
```

Minimum shape:

```text
Set A: Derived A0
Set B: Derived B0 uses selector B -> A and A0
Set C: Derived C0 uses selector C -> B and B0
```

Change A source state and assert only correct B/C consumers become affected.

This test is mandatory before changing `_upstreamsByDerived` because an implementation that preserves only node IDs can easily break projected source mapping.

## 256.4 Add diamond source-dedup proof

Required:

```text
Diamond_downstream_source_is_propagated_once_semantically
```

Do not merely assert the final value.

Also assert no duplicate repair request / causal entry / impact source for the diamond join node.

Use the strongest existing externally observable result that proves dedupe without creating a public test hook.

Suggested commit:

```text
test: freeze compiled DAG execution contracts
```

Required commands:

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --filter "FullyQualifiedName~CompiledDependencyGraphTests|FullyQualifiedName~DerivedDagTests"
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0
```

---

# Task 257 — make CompiledDependencyGraph carry authoritative executable topology

Purpose: the compiled model must own the dependency DAG topology so runtime does not infer it again from definitions.

Targets:

```text
src/Raffinert.Consistency/Model/CompiledDependencyGraph.cs
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
src/Raffinert.Consistency/Dependencies/DependencyGraphRuntime.cs
```

## 257.1 Keep stable node identity internal

`CompiledDependencyNode` should continue to carry at least:

```text
Id
Kind
Definition
TopologicalOrder
```

You may add internal immutable adjacency data either directly on nodes or on `CompiledDependencyGraph`.

Preferred direction:

```csharp
internal sealed class CompiledDependencyGraph
{
    IReadOnlyList<CompiledDependencyNode> Nodes { get; }
    IReadOnlyList<CompiledDependencyEdge> Edges { get; }

    IReadOnlyList<CompiledDependencyEdge> GetIncoming(int nodeId);
    IReadOnlyList<CompiledDependencyEdge> GetOutgoing(int nodeId);
}
```

Equivalent immutable arrays/dictionaries are acceptable.

Do not expose them publicly.

## 257.2 Preserve semantic edge metadata

A simple edge:

```text
FromNodeId
ToNodeId
```

is **not sufficient** for all runtime work.

For derived -> derived edges, runtime currently needs the exact `UpstreamDerivedInput` because that input may encode projected source mapping.

Therefore compiled edge representation must distinguish at least:

```text
DerivedToDerived
DerivedToInvariant
```

and for derived-to-derived edges retain the exact semantic input descriptor used by downstream mapping.

Acceptable internal shape:

```csharp
internal enum CompiledDependencyEdgeKind
{
    DerivedToDerived,
    DerivedToInvariant
}

internal sealed record CompiledDependencyEdge(
    int FromNodeId,
    int ToNodeId,
    CompiledDependencyEdgeKind Kind,
    UpstreamDerivedInput? DerivedInput);
```

The exact type can differ, but the information must not be lost.

For `DerivedToInvariant`, `DerivedInput` must be null.

For `DerivedToDerived`, it must be the exact input belonging to the downstream definition.

## 257.3 Build adjacency once during compilation

After edge dedupe, construct immutable incoming/outgoing adjacency once.

Requirements:

```text
- deterministic order;
- no duplicate edges;
- no runtime GroupBy reconstruction needed for DAG topology;
- adjacency lists are read-only after Build();
- all node IDs referenced by edges exist;
- every DerivedToDerived edge points from a derived node to a derived node;
- every DerivedToInvariant edge points from a derived node to an invariant node.
```

Fail during model compilation if an internal graph invariant is violated.

Do not postpone malformed graph discovery to runtime.

## 257.4 Preserve DebugView

Current DebugView lists dependency nodes and edges.

Keep this information.

It is acceptable to enhance edge text with kind, but do not remove node order or edge information.

## 257.5 Tests

Extend `CompiledDependencyGraphTests`:

```text
Compiled_graph_outgoing_adjacency_matches_edges
Compiled_graph_incoming_adjacency_matches_edges
Derived_to_derived_edge_retains_exact_upstream_input_metadata
Derived_to_invariant_edge_has_correct_kind
Compiled_graph_adjacency_is_duplicate_free
```

For the projected input metadata test, assert reference identity where appropriate:

```text
ReferenceEquals(compiledEdge.DerivedInput, downstreamDefinition.Inputs.Single(...))
```

Suggested commit:

```text
refactor: compile executable dependency adjacency
```

---

# Task 258 — stop reconstructing DAG topology in DependencyGraphRuntime

Purpose: use compiled adjacency as the single source of truth for upstream/downstream dependency topology.

Target:

```text
src/Raffinert.Consistency/Dependencies/DependencyGraphRuntime.cs
```

## 258.1 Build runtime-node lookup by compiled node ID

When constructing runtime nodes, retain an internal mapping equivalent to:

```text
compiled node id -> DerivedNode
compiled node id -> InvariantNode
```

A single array indexed by compiled node ID is preferred if it stays type-safe enough internally.

Example conceptual layout:

```text
RuntimeNode[0] = DerivedNode(A)
RuntimeNode[1] = DerivedNode(B)
RuntimeNode[2] = InvariantNode(C)
```

Do not use definition rescans to rebuild graph adjacency.

## 258.2 Remove redundant topology maps only

After equivalent behavior is implemented through compiled adjacency, remove:

```text
_derivedByUpstream
_upstreamsByDerived
_invariantsByDerived
```

Do **not** remove:

```text
_derivedByRelation
_derivedByMember
_invariantsByMember
```

Those remain direct-impact indexes.

If the implementation keeps a runtime wrapper adjacency for speed, it must be a direct one-time translation of compiled edges by node ID, not a second inference from definitions.

Allowed:

```text
compiled edge ids -> runtime node references
```

Forbidden:

```text
definition.Inputs -> GroupBy -> inferred runtime DAG again
```

## 258.3 Replace `_upstreamsByDerived` behavior without losing input metadata

Current downstream inheritance requires:

```text
(UpstreamDerivedInput Input, DerivedNode Node)
```

Create it from compiled incoming `DerivedToDerived` edges.

Do not recover `Input` by searching downstream definitions after construction.

The compiled edge already carries it after Task 257.

## 258.4 Replace invariant lookup

Current:

```text
_invariantsByDerived
```

should be derived from compiled outgoing `DerivedToInvariant` edges.

When a derived node becomes affected, outgoing compiled invariant edges determine which invariants enter `currentInvariants`.

## 258.5 Replace downstream expansion lookup

`ExpandDownstream(...)` should traverse compiled outgoing `DerivedToDerived` edges.

Keep queue + HashSet semantics unless a focused benchmark proves a better structure.

Pseudo-shape:

```csharp
private void ExpandDownstream(HashSet<int> affectedDerivedNodeIds)
{
    var pending = new Queue<int>(...topological seed ids...);

    while (pending.TryDequeue(out var nodeId))
    {
        foreach (var edge in _compiledGraph.GetOutgoing(nodeId))
        {
            if (edge.Kind != DerivedToDerived)
                continue;

            if (affectedDerivedNodeIds.Add(edge.ToNodeId))
                pending.Enqueue(edge.ToNodeId);
        }
    }
}
```

Exact runtime representation may still use `DerivedNode`, but topology must come from compiled graph.

## 258.6 Constructor audit

After this task, search for these patterns in `DependencyGraphRuntime`:

```text
Definition.Inputs.OfType<UpstreamDerivedInput>()
Invariant.UpstreamDerived
```

They must not be used to reconstruct DAG adjacency.

They may still appear where definition semantics themselves are needed for evaluation, but every occurrence must be justified in the execution log.

## 258.7 Tests

All Task 256 tests must remain green.

Also add:

```text
Runtime_DAG_topology_matches_compiled_graph_for_chain_diamond_and_invariant
Projected_runtime_edge_uses_compiled_input_metadata
```

These may use InternalsVisibleTo; do not create public APIs.

Suggested commit:

```text
refactor: use compiled DAG topology at runtime
```

---

# Task 259 — replace CaptureState fixed-point propagation with one topological pass

Purpose: exploit the already-proven DAG property instead of scanning until convergence.

Target:

```text
DependencyGraphRuntime.CaptureState(...)
```

## 259.1 Preserve two-phase structure

The correct algorithm is:

```text
Phase A — seed direct source sets for every derived node
    lifecycle/structural roots
    existing invalid/dirty/conservative roots
    direct property roots
    relation membership/item roots
    previous snapshot roots

Phase B — one topological propagation pass
    for each derived node in topological order:
        for each incoming derived edge:
            map completed upstream source set through the exact UpstreamDerivedInput
            union mapped sources into this node
```

Do not interleave direct seeding in a way where a downstream node is processed before all of its own direct seeds have been collected.

## 259.2 Remove fixed-point loop completely

Delete the `while (changed)` convergence loop.

There must be no equivalent repeated-whole-DAG scan under another name.

One topological pass is sufficient because:

```text
- the graph is acyclic;
- every upstream node precedes every downstream node;
- when node N is visited, every upstream propagated source set is already complete;
- HashSet union handles diamond dedupe.
```

Document this reasoning in a concise code comment directly above the pass.

## 259.3 Use compiled incoming edges

Do not replace the loop with `_upstreamsByDerived`; Task 258 should have removed that reconstructed structure.

Use compiled incoming `DerivedToDerived` edges.

## 259.4 Mandatory focused tests

Add/strengthen:

```text
Capture_state_propagates_deep_chain_in_one_topological_pass
Capture_state_propagates_diamond_sources_without_duplicate_semantics
Capture_state_propagates_projected_sources_across_sets
Capture_state_merges_direct_and_inherited_sources_for_same_downstream_node
Capture_state_preserves_previous_snapshot_sources_through_downstream_chain
```

The tests must cover depth > 2 so an accidental single non-topological scan would fail.

For the projected case, use at least two projection levels.

## 259.5 No test-only public instrumentation

Do not add a public “pass count”.

If you need to prove the implementation no longer performs repeated scans, use:

```text
- internal test-visible diagnostics only, OR
- direct low-level algorithm tests, OR
- benchmark + code structure proof.
```

Prefer not to add permanent diagnostics unless useful beyond tests.

Suggested commit:

```text
perf: propagate snapshot sources in one DAG pass
```

---

# Task 260 — unify all runtime DAG traversals on compiled adjacency

Purpose: prevent future divergence where one runtime path uses compiled topology and another silently reconstructs or traverses a different graph.

Audit at least:

```text
ApplyChangeImpacts(...)
CaptureState(...)
RebaseCoverageAdmissions(...)
ExpandDownstream(...)
runtime derived state construction
invariant activation from derived changes
```

## 260.1 ApplyChangeImpacts

Required flow:

```text
1. seed directly affected derived node IDs using member/relation indexes;
2. expand downstream via compiled DerivedToDerived outgoing edges;
3. evaluate/apply nodes in compiled topological order;
4. use compiled incoming derived edges to apply inherited impact;
5. collect directly reachable invariants through compiled DerivedToInvariant edges;
6. process invariants only after derived propagation.
```

Do not change semantic severity merging.

## 260.2 RebaseCoverageAdmissions

Required flow:

```text
1. seed relation-affected derived nodes;
2. rebase directly affected sources;
3. expand downstream via compiled topology;
4. apply inherited state in compiled topological order;
5. activate invariants through compiled invariant edges;
6. update previous-derived / previous-invariant sets exactly once.
```

Preserve structural-vs-domain semantics from Tasks 237–243.

## 260.3 Previous-impact clearing

Current logic clears nodes that were previously affected but are no longer in the current affected set.

Preserve exact behavior:

```text
_previousDerived.Except(currentDerived) -> ClearImpact()
_previousInvariants.Except(currentInvariants) -> ClearImpact()
```

Refactoring IDs instead of node references must not leave stale impact state.

## 260.4 Causal evidence

Preserve:

```text
UpstreamPropagationEvidence
InvariantUpstreamEvidence
ImpactCausePrecision
```

A compiled-edge refactor is not allowed to reduce causal evidence precision.

Add test:

```text
Causal_evidence_is_identical_for_compiled_DAG_chain_and_diamond_after_topology_refactor
```

Compare relevant public/application result fields, not internal implementation details.

## 260.5 No transitive closure cache yet

Do not precompute `all descendants` for every node in this task.

BFS/queue traversal is adequate and avoids O(N^2) memory for large sparse DAGs.

Only add transitive closure in a later performance wave if benchmarks show a real need.

Suggested commit:

```text
refactor: unify runtime DAG traversal on compiled adjacency
```

---

# Task 261 — strengthen deterministic cycle detection and diagnostics

Purpose: cycle rejection is already present, but the current failure message may list all nodes left with non-zero indegree rather than an actual cycle path.

The compiled dependency graph is an architectural invariant. Diagnostics should identify a concrete cycle witness.

## 261.1 Keep Kahn topological sort

Do not replace the normal compile path with DFS-only sorting.

Kahn remains simple and deterministic.

## 261.2 Find an actual cycle only on failure

If Kahn produces fewer nodes than total definitions:

```text
1. restrict to unresolved nodes (remaining indegree > 0);
2. run deterministic cycle-witness search over unresolved outgoing edges;
3. return one actual closed cycle path:
   A -> B -> C -> A
```

A self-cycle should render:

```text
A -> A
```

Do not report an arbitrary list like:

```text
A -> B -> C -> D
```

unless every consecutive edge and the closing edge actually exist.

## 261.3 Determinism

For multiple possible cycles, choose deterministically according to compiled node ID / existing deterministic ordering.

The same model must produce the same exception message across runs.

## 261.4 Definition naming

Use existing `Describe(...)` rules:

```text
DefinitionKey when available
otherwise useful expression body
otherwise type/name fallback
```

If duplicate human-readable descriptions make a cycle ambiguous, include an internal stable node ID in the message only as a suffix, not instead of the useful description.

## 261.5 Tests

Because the fluent API naturally creates upstream values before downstream values and therefore makes cycles difficult to express, test cycle detection at the internal compiled-graph layer using test-only fake definitions or another internal construction method.

Do not weaken the public builder just to make a cycle test possible.

Required tests:

```text
Compiled_graph_rejects_self_cycle_with_actual_cycle_path
Compiled_graph_rejects_two_node_cycle_with_actual_cycle_path
Compiled_graph_reports_one_real_cycle_when_tail_nodes_depend_on_cycle
Compiled_graph_cycle_message_is_deterministic
Compiled_graph_cycle_message_prefers_definition_keys
```

Important tail case:

```text
A -> B -> C -> A
C -> D -> E
```

The message must identify the actual cycle:

```text
A -> B -> C -> A
```

and must not pretend `D -> E -> A` is part of it.

Suggested commit:

```text
feat: report deterministic dependency cycle paths
```

This is an internal diagnostic improvement, not a public API feature.

---

# Task 262 — benchmark the topology refactor and guard against regressions

Purpose: prove that removing fixed-point scans and topology reconstruction is useful and does not introduce a hidden regression.

## 262.1 Add a focused benchmark

Create:

```text
benchmarks/Raffinert.Consistency.Benchmarks/DependencyDagBenchmarks.cs
```

Minimum benchmark shapes:

```text
DeepChain_32
DeepChain_128
DiamondLayers_32
SparseDag_128
```

At least one benchmark should exercise `CaptureState`/prepared planning, not only lazy `Get(...)`.

Use deterministic data.

Do not benchmark model compilation and runtime propagation in the same method unless separately named.

Useful categories:

```text
CompileDeepDag
ApplyRootChangeDeepDag
PreparePlanDeepDag
ApplyDiamondDag
```

## 262.2 Add benchmark result document

Create/update:

```text
benchmarks/DependencyDag-Results.md
```

Record:

```text
commit SHA
runtime (.NET version)
machine summary if known
benchmark parameters
mean
allocations
interpretation
```

Do not invent numbers.

If the agent cannot run BenchmarkDotNet reliably in its environment, record that fact and still add the benchmark source; do not fabricate results.

## 262.3 Expected performance direction

No hard percentage target is required because CI hardware varies.

However, the new implementation must not obviously scale like repeated full-DAG fixed-point scans.

Reasonable expectation:

```text
snapshot propagation ~= O(V_affected + E_affected + mapped source work)
```

rather than:

```text
O(number_of_convergence_passes * V)
```

## 262.4 Allocation discipline

Do not introduce large per-Apply dictionaries of compiled edges that could have been precomputed during model build.

Compiled adjacency should be immutable and reused.

Temporary affected sets/queues are acceptable.

## 262.5 Keep randomized oracle tests

Run all seeds in:

```text
RandomizedDagPropagationTests
RandomizedFullGraphTests
```

These are correctness gates, not optional stress tests.

Suggested commit:

```text
perf: benchmark compiled DAG traversal
```

---

# Task 263 — architecture cleanup, exhaustive proof, and release-baseline reset

This is the only task allowed to declare this wave complete.

## 263.1 Production-code audit

Search Core production code for duplicate DAG reconstruction patterns.

At minimum search:

```text
UpstreamDerivedInput
UpstreamDerived
_derivedByUpstream
_upstreamsByDerived
_invariantsByDerived
while (changed)
TopologicalOrder
```

Required outcome:

```text
- no runtime DAG topology is inferred independently from definitions;
- compiled graph is the single topology authority;
- direct-impact/member/relation indexes remain where appropriate;
- projected mapping metadata comes from compiled semantic edges;
- fixed-point source propagation is gone;
- traversal order comes from compiled topological order;
- cycle detection still rejects malformed graphs.
```

Document every remaining production occurrence of `UpstreamDerivedInput` outside `CompiledDependencyGraph` and explain why it is semantic evaluation rather than topology reconstruction.

## 263.2 Public API audit

Compare:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
```

This wave should not require new public graph APIs.

Any public API diff must be explicitly justified in the completion log.

Expected default: **no public API change**.

## 263.3 DebugView / diagnostics proof

Assert DebugView still includes:

```text
Dependency DAG:
[topological order] node entries
edge entries
```

If edge kind is added to text, update tests intentionally.

Do not expose internal object references or unstable hash codes.

## 263.4 Full local verification matrix

Run from a clean tree:

```bash
dotnet restore Raffinert.Consistency.sln

dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build

dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

If release-prep scripts already exist and remain compatible, also run the non-publication verification portions, but do not publish packages in this task.

## 263.5 Mandatory focused test inventory

The pushed tree must contain exact or clearly equivalent tests for:

```text
Compiled_graph_assigns_upstream_before_downstream_topological_order
Compiled_graph_places_invariant_after_all_of_its_upstream_derived_nodes
Compiled_graph_deduplicates_duplicate_logical_edges
Compiled_graph_outgoing_adjacency_matches_edges
Compiled_graph_incoming_adjacency_matches_edges
Derived_to_derived_edge_retains_exact_upstream_input_metadata
Deep_derived_chain_propagates_in_topological_order_without_registration_order_dependency
Projected_upstream_chain_maps_sources_across_sets_through_multiple_DAG_levels
Diamond_downstream_source_is_propagated_once_semantically
Capture_state_propagates_deep_chain_in_one_topological_pass
Capture_state_propagates_diamond_sources_without_duplicate_semantics
Capture_state_propagates_projected_sources_across_sets
Capture_state_merges_direct_and_inherited_sources_for_same_downstream_node
Causal_evidence_is_identical_for_compiled_DAG_chain_and_diamond_after_topology_refactor
Compiled_graph_rejects_self_cycle_with_actual_cycle_path
Compiled_graph_rejects_two_node_cycle_with_actual_cycle_path
Compiled_graph_reports_one_real_cycle_when_tail_nodes_depend_on_cycle
Compiled_graph_cycle_message_is_deterministic
```

Equivalent names are acceptable only if the completion report maps every required behavior to an exact test method.

## 263.6 Exact remote SHA proof

Before final push:

```bash
git status --short
git rev-parse HEAD
```

`git status --short` must be empty.

Push the final implementation SHA.

Verify remotely:

```text
remote branch/main contains FINAL_SHA
GitHub Actions workflow head_sha == FINAL_SHA
status == completed
conclusion == success
```

A green run for an earlier SHA does not count.

## 263.7 Reset release-preparation baseline

After exact-head CI is green:

```text
Tasks 256–263 = complete
Tasks 247–255 = no longer valid against their old baseline
```

Update the release-preparation plan/roadmap so the release candidate is prepared from the **new DAG-hardening final SHA**.

Do not reuse an old pack/smoke/CI proof generated before this wave.

Suggested final commit before exact-head CI:

```text
chore: close compiled DAG runtime hardening
```

---

# 4. Expected end-state architecture

The intended structure after this wave is:

```text
ConsistencyModelBuilder.Build()
        |
        v
CompiledDependencyGraph
        |
        |-- immutable nodes
        |-- semantic edges
        |-- incoming adjacency
        |-- outgoing adjacency
        |-- deterministic topological order
        |-- deterministic cycle rejection
        |
        v
ConsistencyRuntime
        |
        |-- create derived states in compiled order
        |
        v
DependencyGraphRuntime
        |
        |-- direct seed indexes:
        |      member -> derived
        |      relation -> derived
        |      member -> invariant
        |
        |-- topology from CompiledDependencyGraph ONLY
        |
        |-- BFS/queue for affected downstream node discovery
        |-- one topological pass for inherited propagation
        |-- compiled edges for invariant activation
```

The runtime object graph remains independent and may contain cycles:

```text
ObjectSet / relation / navigation indexes
!=
Dependency definition DAG
```

---

# 5. Complexity expectations

Do not optimize blindly, but preserve these intended complexity characteristics.

## Model compilation

For V dependency nodes and E dependency edges:

```text
Kahn sort: O(V + E) apart from deterministic ready-set ordering
adjacency build: O(V + E)
cycle witness on failure only: O(V + E)
```

Current `SortedSet` ready queue can remain unless benchmarking proves it relevant.

Do not replace it solely for theoretical micro-optimization.

## Runtime downstream reachability

For affected subgraph:

```text
O(V_affected + E_affected)
```

plus HashSet operations.

## CaptureState source propagation

One topological pass:

```text
O(V + E + source-mapping work)
```

for nodes in snapshot scope.

Do not perform repeated global convergence scans.

---

# 6. Correctness traps for a weak agent

## Trap A — “Edges are only integer pairs”

Wrong because projected upstream mapping needs `UpstreamDerivedInput` metadata.

## Trap B — “All adjacency maps are duplicate graph structures”

Wrong because member/relation indexes are direct-impact lookup indexes, not DAG topology.

## Trap C — “One pass means seed and propagate simultaneously in any order”

Wrong. Direct seeds must be available correctly, and propagation must follow compiled topological order.

## Trap D — “Invariant is just another derived node”

Wrong semantically. It participates in topological ordering but has different runtime state, policy, evaluation, and repair behavior.

## Trap E — “No semantic duplicate means no duplicate traversal”

Diamond graphs can reach the same downstream node through multiple paths. Keep HashSet node/source dedupe.

## Trap F — “Projected upstream uses same source instance”

Wrong. It may map source ownership across ObjectSets through projection selectors/indexes.

## Trap G — “Cycle detection is unnecessary because fluent API makes cycles hard”

Wrong. Compiled graph acyclicity is an internal invariant and future APIs may allow different declaration mechanisms.

## Trap H — “Object graph must also be acyclic”

Wrong. Only the dependency-definition graph is a DAG.

## Trap I — “Green old CI proves refactor”

Wrong. Only exact final SHA CI counts.

## Trap J — “Release prep can continue from the old SHA”

Wrong. This wave changes runtime internals before first release, so release verification must restart from the new baseline.

---

# 7. Required completion report

The implementing agent must report exactly this information:

```text
Starting remote SHA
Task 256 SHA
Task 257 SHA
Task 258 SHA
Task 259 SHA
Task 260 SHA
Task 261 SHA
Task 262 SHA
Task 263/final SHA

Topology reconstruction removed:
  _derivedByUpstream: yes/no
  _upstreamsByDerived: yes/no
  _invariantsByDerived: yes/no

Direct seed indexes preserved:
  _derivedByRelation: yes/no
  _derivedByMember: yes/no
  _invariantsByMember: yes/no

Fixed-point CaptureState loop removed: yes/no
Compiled semantic edge metadata for projected inputs: describe exact representation
Cycle diagnostics: example real cycle message
Public API diff: none / exact justification
Benchmark source added: path
Benchmark results: path or explicit environment limitation

Required behavior -> exact test method mapping

Local verification:
  restore PASS/FAIL
  build PASS/FAIL
  Core net8 PASS/FAIL
  Core net10 PASS/FAIL
  EF net10 PASS/FAIL
  DependencyMaintenanceSample PASS/FAIL
  OrderFulfillmentSample PASS/FAIL
  EntityFrameworkCore.Sample PASS/FAIL
  format PASS/FAIL

Remote final SHA
Exact GitHub Actions run id/url
CI head_sha
CI conclusion

git status --short: <must be empty>

Release-prep baseline reset to final DAG SHA: yes/no
```

Do not use the phrase “fully implemented” if any field above is missing.

---

# 8. Definition of done

This wave is complete only when all are true:

```text
[ ] compiled DAG remains deterministic and cycle-safe
[ ] compiled DAG carries executable semantic adjacency
[ ] projected UpstreamDerivedInput metadata is preserved on compiled edges
[ ] runtime no longer re-infers DAG topology from definitions
[ ] direct member/relation seed indexes remain intact
[ ] ExpandDownstream uses compiled outgoing adjacency
[ ] inherited derived propagation uses compiled incoming adjacency
[ ] invariant activation uses compiled derived->invariant edges
[ ] CaptureState fixed-point loop is replaced by one topological pass
[ ] deep-chain, diamond, projected, invariant, causal, planning tests are green
[ ] randomized DAG/full-graph oracle tests are green
[ ] cycle error contains a real deterministic cycle path
[ ] no unintended public API change
[ ] benchmark source exists and no obvious scaling regression is introduced
[ ] full local matrix is green
[ ] final pushed SHA has exact-head green CI
[ ] release-prep baseline is reset to the new final SHA
```

Until every checkbox is satisfied, Tasks 256–263 remain active.
