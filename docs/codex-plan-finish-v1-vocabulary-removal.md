# Codex plan — finish API v1 vocabulary removal

Status: **NARROW FOLLOW-UP CLEANUP — NO SEMANTIC REDESIGN**

Audience: very weak coding agent. Follow this plan literally. Do not rename anything not listed here. Do not change runtime behavior. Do not add new features.

Baseline reviewed: `08f1e8bd5c6daa135f49635b1da5884553682c71` (`docs!: document v1 removal and bump 0.2 alpha`).

Purpose: finish the API-v1 removal by removing the last legacy word `Using` from **public type names**, then strengthen public-surface tests and exact-head verification.

The previous removal already deleted the legacy methods:

```text
Using(...)
Compute(...)
Incrementally()
Invariant.Using(...)
ConsistencyRuntime.Get(...)
ConsistencyEfCoreMappings.Materialize(...)
```

Do **not** re-add them.

The remaining problem is that public signatures still expose these legacy type names:

```text
DerivedUsingBuilder<TSource, TItem>
InvariantUsingBuilder<TSource, TValue>
InvariantUsingBuilder<TSource, TFirst, TSecond>
```

That means the old vocabulary still appears in IntelliSense, compiler diagnostics, reflection, documentation, and public API baselines.

This plan removes only that leftover vocabulary.

---

# 1. Fixed rename decisions

Use these exact public names unless a compile-time collision makes one impossible:

```text
DerivedUsingBuilder<TSource, TItem>
    -> DerivedRelationBuilder<TSource, TItem>

InvariantUsingBuilder<TSource, TValue>
    -> InvariantValueBuilder<TSource, TValue>

InvariantUsingBuilder<TSource, TFirst, TSecond>
    -> InvariantValueBuilder<TSource, TFirst, TSecond>
```

Do not invent alternatives such as:

```text
DerivedFromBuilder
DerivedInputBuilder
RelationUsingBuilder
InvariantFromBuilder
InvariantInputBuilder
InvariantDerivedBuilder
```

The goal is not another naming experiment.

Rationale:

```text
DerivedRelationBuilder
    = stage created by From(relation)

InvariantValueBuilder
    = stage created by From(derivedValue)
```

These names describe the stage without preserving v1 `Using` vocabulary.

---

# 2. Hard guardrails

Do not:

1. change any runtime semantics;
2. change Fresh/Dirty/Invalid behavior;
3. change propagation planning;
4. change aggregate recognition;
5. change Evaluate/Materialize behavior;
6. change EF materialization behavior;
7. change dependency analysis;
8. change repair scheduling;
9. change public method names already selected for v2;
10. introduce compatibility aliases for the renamed types;
11. keep the old public types as obsolete wrappers;
12. create implicit conversions between old/new types;
13. add source generators/analyzers;
14. change package version again unless an existing repository release rule forces it;
15. touch concurrency/YAML/other roadmaps.

This task is a **public vocabulary cleanup only**.

---

# 3. First task — inventory all remaining public legacy vocabulary

Before editing code, search the entire repository for:

```text
DerivedUsingBuilder
InvariantUsingBuilder
Using(
.Compute(
.Incrementally(
ConsistencyRuntime.Get(
ConsistencyEfCoreMappings.Materialize(
```

Classify every occurrence as one of:

```text
A. production public declaration/signature
B. production internal implementation
C. test code
D. benchmark/sample code
E. docs/history/changelog/roadmap
F. experiment/archive evidence
```

Rules:

```text
A must be eliminated if it is legacy public vocabulary.
B may remain only if it is an internal implementation name that cannot leak publicly; prefer renaming when trivial.
C/D should use v2 names.
E/F may mention old names historically when describing removed API.
```

Do not blindly replace historical changelog text that says the old API was removed.

In the completion report, include exact counts before/after for public `*UsingBuilder*` types.

---

# 4. Rename `DerivedUsingBuilder` safely

Locate:

```csharp
public sealed class DerivedUsingBuilder<TSource, TItem>
```

Rename the type itself to:

```csharp
public sealed class DerivedRelationBuilder<TSource, TItem>
```

Then update every constructor, return type, local type reference, test reflection reference, and public API baseline entry.

## 4.1 Required call-site behavior must stay identical

This code must remain source-compatible at the fluent call-site level:

```csharp
var receivedQuantity = model
    .Derived(lines)
    .From(matchingReceipts)
    .Impact(...)
    .Sum(x => x.Quantity);
```

The user should not need to write the builder type explicitly.

## 4.2 Public signature must change

Before:

```text
DerivedBuilder<TSource>.From<TItem>(Relation<TSource,TItem>)
    -> DerivedUsingBuilder<TSource,TItem>
```

After:

```text
DerivedBuilder<TSource>.From<TItem>(Relation<TSource,TItem>)
    -> DerivedRelationBuilder<TSource,TItem>
```

## 4.3 Preserve methods on the renamed type

The renamed builder must still expose the same v2 behavior:

```text
Impact(...)
PreferConservativePropagation()
Select(...)
Sum(...)
Count()
LongCount()
Any()
```

Do not remove or alter any of those methods.

## 4.4 Preserve aggregate planner behavior

Add or retain tests proving:

```text
From(relation).Sum(...)      -> same incremental Sum plan
From(relation).Count()       -> same incremental Count plan
From(relation).LongCount()   -> same incremental LongCount plan
From(relation).Any()         -> same incremental Any plan
```

The type rename must not change compiled plans.

Suggested commit:

```text
api!: rename DerivedUsingBuilder to DerivedRelationBuilder
```

---

# 5. Rename invariant builder types safely

Locate both public generic arities:

```csharp
InvariantUsingBuilder<TSource, TValue>
InvariantUsingBuilder<TSource, TFirst, TSecond>
```

Rename to:

```csharp
InvariantValueBuilder<TSource, TValue>
InvariantValueBuilder<TSource, TFirst, TSecond>
```

Update constructors and all public return types.

## 5.1 Single-input invariant

Before public signature conceptually:

```text
InvariantBuilder<TSource>.From<TValue>(Derived<TSource,TValue>)
    -> InvariantUsingBuilder<TSource,TValue>
```

After:

```text
InvariantBuilder<TSource>.From<TValue>(Derived<TSource,TValue>)
    -> InvariantValueBuilder<TSource,TValue>
```

## 5.2 Two-input invariant chaining

This must keep working:

```csharp
model.Invariant(lines)
    .From(first)
    .From(second)
    .Must((line, a, b) => ...);
```

The first `From` now returns:

```text
InvariantValueBuilder<TSource,TFirst>
```

and chained `From(second)` returns:

```text
InvariantValueBuilder<TSource,TFirst,TSecond>
```

## 5.3 Preserve invariant semantics exactly

Do not alter:

```text
Must(...)
Named(...)
ReactWith(...)
ScheduleRepairWith(...)
AllowIncompleteDependencies()
```

or any internal invariant definition/runtime type.

Suggested commit:

```text
api!: rename invariant Using builders to value builders
```

---

# 6. Do not retain compatibility type aliases

This is a deliberate breaking cleanup inside the `0.2.0-alpha` line.

Do **not** add:

```csharp
[Obsolete]
public sealed class DerivedUsingBuilder<...> : ...
```

Do **not** add:

```csharp
using DerivedUsingBuilder = ...
```

Do **not** keep old public wrapper types with forwarding members.

Reason:

```text
The entire purpose of this task is to remove v1 vocabulary from the public surface.
```

If the old type remains exported, the task is not complete.

---

# 7. Public API baseline cleanup

Update both:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
```

according to the repository's analyzer/baseline convention.

Required result:

```text
NO public type containing `UsingBuilder`
```

and public signatures must reference the renamed types.

Do not manually delete unrelated baseline lines.

Before committing, produce a focused diff and verify:

```text
Removed:
DerivedUsingBuilder<...>
InvariantUsingBuilder<...>

Added/updated:
DerivedRelationBuilder<...>
InvariantValueBuilder<...>
```

No unrelated shipped API should disappear.

---

# 8. Strengthen negative public-surface tests

The current `LegacyApiSurfaceTests` only checks method names. That is insufficient.

Update/move tests so Core API checks live in:

```text
tests/Raffinert.Consistency.Tests/
```

and EF-only checks live in:

```text
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/
```

Do not make Core surface tests depend on the EF package.

## 8.1 Core banned method checks

Keep checks that these public methods do not exist:

```text
DerivedBuilder.* Using
DerivedBuilder.* Compute
relation-derived builder Incrementally
relation-derived builder Compute
upstream builders Compute
projected builders Compute
InvariantBuilder Using
ConsistencyRuntime Get
```

## 8.2 Core banned exported type-name checks

Add an assembly-level guard.

Preferred explicit logic:

```csharp
var exported = typeof(ConsistencyModelBuilder)
    .Assembly
    .GetExportedTypes();

Assert.DoesNotContain(exported,
    t => t.Name.StartsWith("DerivedUsingBuilder", StringComparison.Ordinal));

Assert.DoesNotContain(exported,
    t => t.Name.StartsWith("InvariantUsingBuilder", StringComparison.Ordinal));
```

Also assert positive existence of:

```text
DerivedRelationBuilder<,>
InvariantValueBuilder<,>
InvariantValueBuilder<,,>
```

Do not use one broad assertion like:

```csharp
type.Name.Contains("Using")
```

because that could reject unrelated legitimate future APIs.

## 8.3 EF surface check

Keep/add separate EF test:

```text
ConsistencyEfCoreMappings has no public Materialize method
```

Do not reference EF types from the Core public-surface test file.

---

# 9. Add compile-level fluent-surface tests

Reflection tests alone are not enough.

Add compile/runtime tests that instantiate the renamed stages indirectly:

```csharp
var relationStage = model.Derived(lines).From(relation);
```

and verify the variable's runtime/reflection generic type definition is:

```text
DerivedRelationBuilder<,>
```

Likewise:

```csharp
var invariantStage = model.Invariant(lines).From(derived);
```

must be:

```text
InvariantValueBuilder<,>
```

and two-value chaining:

```csharp
var invariantStage2 = model.Invariant(lines)
    .From(first)
    .From(second);
```

must be:

```text
InvariantValueBuilder<,,>
```

Purpose: catch accidental return-signature regression even if old types become internal later.

---

# 10. Search for stale documentation vocabulary

Search current non-historical docs and README for:

```text
DerivedUsingBuilder
InvariantUsingBuilder
```

They should not appear in current-user documentation.

Historical documents may retain them only if explicitly discussing the old API or an experiment.

Do not rewrite old commit hashes or experiment evidence merely to remove a word.

Update CHANGELOG only if necessary to clarify that the `0.2.0-alpha.1` breaking cleanup also removes the legacy builder type names.

Suggested wording:

```text
- Removed the remaining pre-v2 `*UsingBuilder` public type names; v2 fluent stages now expose `DerivedRelationBuilder` and `InvariantValueBuilder`.
```

Do not create a new release section unless the repository's release policy requires it.

---

# 11. Verify README and samples do not expose concrete builder types

The preferred public documentation should show fluent expressions:

```csharp
model.Derived(lines)
    .From(relation)
    .Sum(...);
```

not:

```csharp
DerivedRelationBuilder<Line, Receipt> builder = ...;
```

Concrete staged-builder types are public because C# requires them for fluent return signatures, but they should not become the primary conceptual API.

Do not add docs teaching users to explicitly declare these builder types.

---

# 12. Semantic regression gates

After type renames, run the exact same v2 regression scenarios.

At minimum:

```text
PriceRate -> UnitRate logical flow
projected From
relation Sum
Count
LongCount
Any
invariant single From
invariant two From inputs
repair scheduling
Evaluate
Materialize(definition, source)
Materialize(source)
EF automatic MaterializeTo persistence
same-CLR different-set validation
```

The expected values/states/plans must not change.

This rename task has no excuse to alter semantics.

---

# 13. Required build/test commands

Run from repository root on the final exact head.

At minimum:

```bash
dotnet restore Raffinert.Consistency.sln
dotnet build Raffinert.Consistency.sln -c Release --no-restore
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release --no-build
```

Also run all repository test projects if there are additional ones.

Run package consumer builds for supported TFMs, including the existing net8/net10 consumer projects.

If the repo has a standard pack command/workflow, run local pack and consumer validation too.

Do not claim success from a subset if another test project exists.

---

# 14. Exact-head verification requirement

The previous review had no GitHub status checks on exact head.

For this task, the completion report must include:

```text
final commit SHA
commands run locally/agent-side
exit result for each command
GitHub Actions run/status for that exact SHA if CI is configured and available
```

If CI does not run automatically, say exactly:

```text
No exact-head GitHub Actions evidence is available.
```

Do not imply green CI without evidence.

Do not publish/release package as part of this task unless explicitly instructed separately.

---

# 15. Public API grep gate

Before declaring completion, inspect the final public API baselines and source tree.

These must return zero current-production/public occurrences, excluding historical docs where appropriate:

```text
DerivedUsingBuilder
InvariantUsingBuilder
```

These old public methods must also remain absent:

```text
Using
Compute
Incrementally
ConsistencyRuntime.Get
ConsistencyEfCoreMappings.Materialize
```

Important: do not grep for the bare English word `Using`, because normal prose or C# `using` directives will create noise.

Use exact identifiers/signatures.

---

# 16. Public API positive gate

Verify final public surface still contains:

```text
DerivedBuilder<TSource>.From(...)
DerivedBuilder<TSource>.Select(...)
DerivedRelationBuilder<TSource,TItem>
DerivedRelationBuilder.Sum
DerivedRelationBuilder.Count
DerivedRelationBuilder.LongCount
DerivedRelationBuilder.Any
DerivedUpstreamBuilder...
ProjectedDerivedUpstreamBuilder...
InvariantBuilder<TSource>.From(...)
InvariantValueBuilder<TSource,TValue>
InvariantValueBuilder<TSource,TFirst,TSecond>
ConsistencyRuntime.Evaluate(...)
ConsistencyRuntime.Materialize(definition, source)
ConsistencyRuntime.Materialize(source)
Derived.MaterializeTo(...)
```

Do not accidentally remove `DerivedUsingBuilder` and forget to expose the replacement.

---

# 17. Check public XML docs / generated API docs

If XML docs mention concrete old names due to `<see cref>` or summary text, update them.

Search specifically for:

```text
DerivedUsingBuilder
InvariantUsingBuilder
```

Do not change semantics or rewrite unrelated documentation.

---

# 18. Do not over-clean internal historical names without reason

If an **internal/private** symbol contains `Using` but is not exported and changing it risks unnecessary churn, it may remain.

However, if the public type's constructor/class declaration is renamed, corresponding private fields/locals should use sensible names such as:

```text
relationBuilder
relationInput
invariantValueBuilder
```

rather than `usingBuilder`.

Do not launch a repository-wide vocabulary rewrite unrelated to public API.

---

# 19. Commit sequence

Prefer no more than three commits:

```text
1. api!: rename remaining v1 builder types
2. tests: harden v2 public-surface regression guards
3. docs: document final v1 vocabulary cleanup
```

If all changes are tiny and inseparable, one implementation commit plus one docs commit is acceptable.

Do not mix feature changes into these commits.

---

# 20. Stop conditions

STOP and report instead of improvising if any of these occur:

```text
S1. Renaming DerivedUsingBuilder changes fluent type inference.
S2. Renaming InvariantUsingBuilder breaks From(...).From(...).Must(...).
S3. PublicAPI analyzer demands reintroducing an old compatibility type.
S4. Any old test only passes because it directly instantiates a legacy builder type.
S5. Aggregate compiled plans change after rename.
S6. Invariant reaction/repair behavior changes after rename.
S7. EF tests require old explicit Materialize API to compile.
S8. A package consumer still needs Using/Compute/Get rather than v2 API.
S9. Same-CLR object-set identity behavior changes.
S10. Build/test failures are unrelated but prevent exact-head verification.
```

Do not solve any stop condition with:

```text
obsolete compatibility wrappers
public type aliases
reflection hacks
dynamic
conditional compilation
skipping tests
weakening assertions
```

---

# 21. Definition of done

This cleanup is complete only when every item is true:

```text
[ ] `DerivedUsingBuilder` is not an exported public type
[ ] `InvariantUsingBuilder` is not an exported public type
[ ] `DerivedRelationBuilder<TSource,TItem>` is public and used by From(relation)
[ ] `InvariantValueBuilder<TSource,TValue>` is public and used by Invariant.From
[ ] `InvariantValueBuilder<TSource,TFirst,TSecond>` is public and used by chained From
[ ] no compatibility wrapper/alias preserves old builder type names
[ ] old v1 methods remain removed
[ ] Core surface tests do not depend on EF assembly
[ ] EF legacy Materialize absence has its own EF test
[ ] exported-type negative tests block old builder names
[ ] positive tests assert new builder names
[ ] PublicAPI baselines contain no old builder names
[ ] README/current docs contain no accidental old builder names
[ ] historical changelog text remains accurate
[ ] relation aggregate compiled plans unchanged
[ ] projected From behavior unchanged
[ ] invariant single/two-input behavior unchanged
[ ] repair semantics unchanged
[ ] Evaluate/Materialize semantics unchanged
[ ] EF persistence behavior unchanged
[ ] all solution projects build Release
[ ] all test projects pass Release
[ ] package consumer projects build
[ ] exact final SHA recorded
[ ] exact-head CI status recorded honestly
```

---

# 22. Completion report format

The agent must finish with exactly these sections:

```text
## Final SHA

## Public type renames
old -> new

## Public API removals verified absent

## Public API v2 members verified present

## Semantic regression evidence

## Build/test commands and results

## Package consumer results

## Exact-head CI evidence

## Remaining old-vocabulary occurrences
classify each as historical/internal/current-public

## Remaining risks
```

Do not write only “done”.

---

# 23. Final instruction to the agent

The implementation goal is extremely small:

```text
BEFORE
From(relation) -> DerivedUsingBuilder
Invariant.From(value) -> InvariantUsingBuilder

AFTER
From(relation) -> DerivedRelationBuilder
Invariant.From(value) -> InvariantValueBuilder
```

Everything else should behave exactly the same.

If your diff changes dependency propagation, runtime evaluation, materialization, repair behavior, EF orchestration, or aggregate planning, you are doing the task wrong.

The success condition is:

> A new user can inspect the complete exported public API for the `0.2` line and never encounter the removed v1 word `Using` as a builder/API concept.