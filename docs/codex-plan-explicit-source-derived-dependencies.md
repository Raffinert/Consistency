# Codex implementation plan — explicit dependencies for opaque source-derived computations

Status: **ACTIVE IMPLEMENTATION PLAN**

Baseline commit: `4dc439dc3787bfbc3df981b405d24a8ee3c8e9c9`

Tasks: **174–181**

## Why this roadmap exists

The domain-neutral dependency-maintenance dogfooding roadmap (Tasks 167–173) correctly stopped at commit
`4dc439dc3787bfbc3df981b405d24a8ee3c8e9c9`.

The blocked declaration is intentionally production-shaped:

```csharp
var unitRate = builder.Derived(associations)
    .Compute(association => UnitRateCalculator.Calculate(
        association.SourceItem.UnitValue,
        association.TargetItem.UnitValue));
```

`UnitRateCalculator.Calculate(...)` is ordinary reusable business logic. The expression analyzer can still see the
member-access arguments, but the method call itself is classified as `ContainsOpaqueCode`, so `Build()` rejects the
model. `AllowIncompleteDependencies()` is not an acceptable answer because it explicitly gives up cached-freshness
guarantees.

This roadmap adds one deliberately narrow product capability:

> a source-derived computation may explicitly declare the complete source member paths hidden behind opaque code,
> so Raffinert can retain normal freshness, reverse-navigation routing, scope requirements, and persistence metadata
> without forcing business logic to be inlined into an expression tree.

This feature exists because dogfooding exposed a real application boundary. Do not expand it into a general expression
annotation framework.

---

# Frozen public API

Add exactly this fluent method to `DerivedBuilder<TSource>`:

```csharp
public DerivedBuilder<TSource> DependsOn<TDependency>(
    Expression<Func<TSource, TDependency>> dependency)
```

Required usage:

```csharp
var unitRate = builder.Derived(associations)
    .DependsOn(a => a.SourceItem.UnitValue)
    .DependsOn(a => a.TargetItem.UnitValue)
    .Compute(a => UnitRateCalculator.Calculate(
        a.SourceItem.UnitValue,
        a.TargetItem.UnitValue));
```

Do **not** introduce alternative public spellings in this wave:

```text
WithDependencies
ExplicitDependency
TrustDependency
ComputeOpaque
ComputeWithDependencies
DependencyPath
MemberPath
```

Do not expose internal dependency-analysis types publicly.

## Contract of `DependsOn`

Each call is an explicit semantic assertion by the model author:

> this member path is one of the source-state inputs that determines the derived value, including dependencies that
> Raffinert cannot prove by inspecting opaque computation code.

Declared dependencies:

1. **augment** automatically inferred dependencies; they never replace them;
2. participate in normal runtime invalidation and reverse-navigation routing;
3. participate in scope requirement calculation;
4. participate in EF semantic-member usage queries because those already consume compiled dependency metadata;
5. may satisfy the `ContainsOpaqueCode` completeness blocker for a source-derived computation;
6. do **not** satisfy or suppress `ContainsExternalState`;
7. do not change `AllowIncompleteDependencies()` semantics.

The library trusts the declaration. If the opaque method secretly depends on another source member and the model author
fails to declare it, that is a model-definition bug and freshness can be incorrect. Documentation must say this
explicitly.

This is still stronger and materially different from `AllowIncompleteDependencies()`:

```text
DependsOn(...)
    caller asserts a complete hidden dependency contract
    Raffinert keeps normal freshness guarantees relative to that contract

AllowIncompleteDependencies()
    caller explicitly accepts that the dependency graph may be incomplete
    cached freshness is not guaranteed
```

---

# Non-negotiable scope

Implement only **source-derived** explicit dependencies in this roadmap.

Supported:

```csharp
builder.Derived(sourceSet)
    .DependsOn(x => x.Value)
    .DependsOn(x => x.Parent.Value)
    .Compute(x => OpaqueCalculator.Calculate(...));
```

Out of scope:

- relation predicates;
- relation-backed `Derived(...).Using(relation)` builders;
- composed/upstream derived builders;
- projected-upstream builders;
- invariant predicates;
- collection selectors;
- arbitrary delegates as dependency selectors;
- string property paths;
- public `MemberInfo` APIs;
- automatic purity inference for arbitrary methods;
- source generators/analyzers for dependency declarations;
- attributes such as `[Pure]`;
- changing EF persistence behavior;
- changing `AllowIncompleteDependencies()`.

Do not generalize merely because similar APIs could be useful later.

---

# Task 174 — Reproduce the blocker and freeze baseline behavior

Work first in:

```text
tests/Raffinert.Consistency.Tests/DependencyCompletenessTests.cs
```

Do not modify product code until the failing tests below exist.

## 174.1 Preserve current rejection without explicit dependencies

Add a simple source-only type with a nested reference, for example:

```csharp
private sealed class DependencyRoot
{
    public int Id { get; set; }
    public DependencyEndpoint Endpoint { get; set; } = null!;
    public decimal LocalValue { get; set; }
}

private sealed class DependencyEndpoint
{
    public decimal Value { get; set; }
}
```

and an opaque calculator:

```csharp
private static decimal OpaqueScale(decimal value) => value * 2m;
```

Baseline declaration:

```csharp
var model = new ConsistencyModelBuilder();
var roots = model.Objects<DependencyRoot>().Key(x => x.Id);
_ = model.Derived(roots)
    .Compute(x => OpaqueScale(x.Endpoint.Value));
```

Required assertion before implementation:

```text
model.Build() throws InvalidOperationException
message contains ContainsOpaqueCode
message still mentions AllowIncompleteDependencies
```

Do not weaken or delete the existing opaque-computation rejection test.

## 174.2 Add the intended API compile target

Add a second test using the frozen API shape:

```csharp
var derived = model.Derived(roots)
    .DependsOn(x => x.Endpoint.Value)
    .Compute(x => OpaqueScale(x.Endpoint.Value));
```

This test will not compile until Task 175. Leave it staged only as part of the implementation sequence; do not invent a
different API to make it compile.

## 174.3 Freeze external-state behavior

Add a test where the computation uses both an opaque call and captured mutable state:

```csharp
var multiplier = 2m;
var derived = model.Derived(roots)
    .DependsOn(x => x.Endpoint.Value)
    .Compute(x => OpaqueScale(x.Endpoint.Value) * multiplier);
```

After implementation this **must still fail** at `Build()` with `ContainsExternalState`.

`DependsOn` is not permission to hide external mutable state.

---

# Task 175 — Add `DerivedBuilder<TSource>.DependsOn(...)`

Primary file:

```text
src/Raffinert.Consistency/Derived/DerivedBuilders.cs
```

Add private builder state for declared dependencies. Keep it internal; do not create a new public dependency type.

Recommended internal field shape:

```csharp
private readonly List<TrackedExpressionDependency> _declaredDependencies = [];
```

If keeping expression objects until `Compute()` is cleaner, this is also acceptable:

```csharp
private readonly List<LambdaExpression> _declaredDependencies = [];
```

but there must be one canonical validation/normalization path before a definition is constructed.

Add exactly:

```csharp
/// <summary>
/// Declares a source member path that determines this derived value when the computation contains opaque code.
/// Declared paths augment inferred dependencies and are trusted as part of the computation's completeness contract.
/// </summary>
public DerivedBuilder<TSource> DependsOn<TDependency>(
    Expression<Func<TSource, TDependency>> dependency)
```

Required behavior:

```text
null expression                         -> ArgumentNullException
pure source member path                 -> accepted
nested source member path               -> accepted
value-object member path                -> accepted
reference-navigation member path        -> accepted
same path declared twice                -> accepted and deduplicated in compiled analysis
method call in dependency selector      -> ArgumentException immediately
binary/calculated selector              -> ArgumentException immediately
conditional selector                    -> ArgumentException immediately
captured/external expression            -> ArgumentException immediately
collection path                         -> ArgumentException immediately in v1
parameter/root itself                   -> ArgumentException immediately
```

Do not defer malformed `DependsOn` selectors until `Build()` if they can be rejected at the call site.

### Path-validation rule

A v1 declaration must be a single property/field chain rooted at the source parameter, optionally through reference
or value-object members:

```text
VALID
x => x.Value
x => x.Parent.Value
x => x.Address.PostCode
x => x.Parent

INVALID
x => Calculate(x.Value)
x => x.Left + x.Right
x => condition ? x.Left : x.Right
x => external.Value
x => x.Items
x => x.Items.Count
x => x
```

A terminal reference such as `x => x.Parent` is valid because reference identity itself can be a semantic input.

A member whose type implements `IEnumerable` (except `string`) is a collection dependency and must be rejected in this
first version. Do not invent collection semantics here.

### Do not add behavior to other builders

Do not copy `DependsOn` onto:

```text
DerivedUsingBuilder
DerivedUpstreamBuilder
ProjectedDerivedUpstreamBuilder
InvariantBuilder
RelationBuilder
```

This roadmap is intentionally narrow.

---

# Task 176 — Centralize declared-path validation and merge dependency analysis correctly

Primary files:

```text
src/Raffinert.Consistency/Expressions/ExpressionAnalysis.cs
src/Raffinert.Consistency/Expressions/ExpressionDependencyAnalyzer.cs
src/Raffinert.Consistency/Derived/DerivedDefinitions.cs
```

Do not create a second dependency-routing representation. The final `SourceDerivedDefinition.Analysis` must remain the
single source of truth consumed by runtime routing, scope compilation, diagnostics, and EF usage classification.

## 176.1 Add one internal declared-path analyzer

Add one internal helper in the existing expression-analysis area. Suggested shape:

```csharp
internal static TrackedExpressionDependency AnalyzeDeclaredSourceDependency(
    LambdaExpression expression)
```

or a strongly typed equivalent.

It must:

1. strip ordinary conversions;
2. prove the body is one member chain rooted at parameter 0;
3. produce role `ExpressionParameterRole.DerivedSource`;
4. produce a normal `DependencyPath` with root parameter index `0`;
5. reject collections;
6. throw `ArgumentException` with a stable useful message for unsupported selectors.

Reuse existing helpers such as `MemberPath.TryCreate`, `MemberPath.StripConvert`, `DependencyPath`, and
`DependencyPathNavigation`. Do not duplicate member-chain parsing.

Suggested error text:

```text
"A declared derived dependency must be a single non-collection member path rooted in the derived source."
```

Tests should assert the important phrase, not punctuation.

## 176.2 Merge inferred and declared dependencies

`SourceDerivedDefinition<TSource,TValue>` currently computes:

```csharp
ExpressionDependencyAnalyzer.AnalyzeDerived(computationExpression)
```

Change construction so source-derived definitions receive the builder's declared dependencies and merge them with the
inferred analysis.

Required merge semantics:

```text
Dependencies
    inferred dependencies
    UNION declared dependencies
    deduplicated deterministically

HasRelationMembershipDependency
    unchanged from inferred source analysis

LinqSemantics
    unchanged from inferred source analysis

Flags
    start with inferred flags
    if at least one valid DependsOn declaration exists:
        clear ContainsOpaqueCode
    NEVER clear ContainsExternalState
```

Do not simply replace `Analysis` with the declared list.

Example:

```csharp
builder.Derived(roots)
    .DependsOn(x => x.Endpoint.Value)
    .Compute(x => OpaqueScale(x.Endpoint.Value) + x.LocalValue);
```

Compiled dependencies must contain both:

```text
DependencyRoot.Endpoint.Value     // declared and/or inferred
DependencyRoot.LocalValue         // inferred automatically
```

The `LocalValue` dependency must not disappear because explicit dependencies were supplied.

## 176.3 Preserve the trust boundary

The implementation is allowed to clear `ContainsOpaqueCode` only because calling `DependsOn(...)` is an explicit
completeness assertion by the model author.

Do not make these unsafe global changes:

```text
method calls are no longer opaque
static methods are assumed pure
method calls with analyzable arguments are automatically complete
ContainsOpaqueCode is ignored for source derived values
```

Without `DependsOn`, current rejection behavior must remain byte-for-byte equivalent in spirit.

## 176.4 Keep `AllowIncompleteDependencies()` independent

Do not make `DependsOn` set `AllowIncompleteDependencies = true`.

Do not change the meaning or diagnostics of `AllowIncompleteDependencies()`.

A derived value may technically use both APIs, but `DependsOn` should normally make the opaque portion complete while
`AllowIncompleteDependencies()` remains an explicit opt-in only for any genuinely unresolved flags. No special public
combination API is needed.

---

# Task 177 — Prove runtime fan-out, selective routing, and retargeting

Add focused core tests. Prefer a new file:

```text
tests/Raffinert.Consistency.Tests/ExplicitDerivedDependencyTests.cs
```

Do not use EF for the core routing proof.

Use neutral test types:

```csharp
private sealed class Association
{
    public int Id { get; set; }
    public Endpoint Source { get; set; } = null!;
    public Endpoint Target { get; set; } = null!;
}

private sealed class Endpoint
{
    public decimal? Value { get; set; }
}
```

Calculator:

```csharp
private static decimal? Calculate(decimal? source, decimal? target) =>
    source is null || target is null || target == 0m ? null : source / target;
```

Model:

```csharp
var model = new ConsistencyModelBuilder();
var associations = model.Objects<Association>().Key(x => x.Id);
var rate = model.Derived(associations)
    .DependsOn(x => x.Source.Value)
    .DependsOn(x => x.Target.Value)
    .Compute(x => Calculate(x.Source.Value, x.Target.Value));
```

Required proofs:

## 177.1 Shared nested target fans out

Create two associations sharing the same `Source` endpoint and different targets.

Prime both derived values with `runtime.Get(...)`.

Mutate:

```csharp
source.Value = 20m;
runtime.Apply(Change.Property(source, x => x.Value, oldValue, 20m));
```

Assert:

```text
both consuming associations become Dirty before Get
both recompute to correct values
unrelated associations remain Fresh
```

This is the exact behavior needed by the blocked dogfooding sample.

## 177.2 Target mutation is selective

Change one target endpoint. Assert only associations that reference that target become Dirty.

## 177.3 Retargeting updates reverse navigation routing

Start:

```text
association B.Source = source A
```

Then retarget:

```csharp
associationB.Source = sourceB;
runtime.Apply(Change.Property(
    associations,
    associationB,
    x => x.Source,
    sourceA,
    sourceB));
```

Assert the association becomes Dirty and recomputes against `sourceB`.

Then mutate `sourceA.Value` and prove association B remains Fresh.

Then mutate `sourceB.Value` and prove association B becomes Dirty.

The test must prove routing topology changed, not merely immediate recomputation after the reference assignment.

## 177.4 Inferred dependencies are still active

Use:

```csharp
var result = model.Derived(associations)
    .DependsOn(x => x.Source.Value)
    .Compute(x => Calculate(x.Source.Value, 1m) + x.Id);
```

Changing `Association.Id` is not a valid runtime mutation because it is the key, so use a separate ordinary property
such as `Adjustment` in the test type and expression.

Change `Adjustment`; assert the value becomes Dirty even though it was never listed in `DependsOn`.

This proves augmentation, not replacement.

## 177.5 Duplicate declarations do not duplicate consequences

Declare the same path twice. Build must succeed, debug/analysis must contain one semantic dependency path, and one
property mutation must produce one normal affected-source consequence, not duplicate callback/policy effects.

---

# Task 178 — Prove scope and semantic metadata consume declared dependencies

The feature is incomplete if only cache invalidation works. Existing architecture derives additional correctness
metadata from `IDerivedDefinition.Analysis`; prove the new dependencies flow through those consumers automatically.

## 178.1 Scope requirement proof

Extend:

```text
tests/Raffinert.Consistency.Tests/ConsistencyScopeRequirementTests.cs
```

Create an opaque source-derived computation with:

```csharp
.DependsOn(x => x.Parent!.Value)
```

After build:

```csharp
runtime.GetScopeRequirements(derived.Definition)
```

must contain exactly:

```text
(source set id, NavigationConsumerCoverage)
```

for the source set.

Also prove:

```csharp
.DependsOn(x => x.Value)
```

requires no whole-set scope coverage merely because it was explicit.

The scope result must be based on path topology, not on whether the path was inferred or declared.

## 178.2 Debug/diagnostic proof

Extend dependency-completeness/debug-view tests so a declared opaque source-derived computation:

- builds without the `Incomplete, explicitly allowed` diagnostic;
- reports the declared dependency path in the same format as inferred dependencies;
- does not claim `AllowIncompleteDependencies` was used.

Do not add a parallel diagnostics vocabulary solely for declared paths.

## 178.3 EF usage metadata smoke proof

Add one focused test in the EF test project only if no existing test reaches this behavior through the resumed dogfood
sample yet.

Goal: prove a member present **only because of `DependsOn`** is still considered a semantic dependency by the EF adapter.
The cleanest proof is a generated semantic member on a tracked nested target:

```text
opaque derived computation declares DependsOn(root => root.Endpoint.DatabaseGeneratedValue)
EF metadata says DatabaseGeneratedValue is store-generated
convenience save must treat it as semantic and fail closed using the existing generated-value guard
```

Do not change EF production code to make this test pass unless analysis metadata is genuinely not flowing through an
existing consumer. If an EF product change appears necessary, stop and inspect why before adding a second metadata
path.

---

# Task 179 — Public API baseline and documentation

## 179.1 API baseline

The only intended new public API is:

```csharp
Raffinert.Consistency.DerivedBuilder<TSource>.DependsOn<TDependency>(
    System.Linq.Expressions.Expression<System.Func<TSource, TDependency>> dependency)
    -> Raffinert.Consistency.DerivedBuilder<TSource>
```

No new public types are expected.

Follow the repository's API-baseline workflow exactly:

1. add the signature to `src/Raffinert.Consistency/PublicAPI.Unshipped.txt` during implementation if required by the
   analyzer;
2. before final RC verification, move/finalize the accepted signature in `PublicAPI.Shipped.txt` using the same format
   as neighboring builder methods;
3. leave `PublicAPI.Unshipped.txt` empty except for its standard header because the release-candidate verification uses
   `-RequireEmptyUnshipped`.

Do not suppress public API analyzer diagnostics.

## 179.2 README / architecture documentation

Document a short section in the most appropriate current public docs (`README.md` and/or `docs/architecture.md`). Do
not write a long conceptual essay.

Required example:

```csharp
var score = model.Derived(items)
    .DependsOn(x => x.InputA)
    .DependsOn(x => x.Config.Value)
    .Compute(x => ExistingCalculator.Calculate(x.InputA, x.Config.Value));
```

Required explanation:

- ordinary analyzable expressions need no `DependsOn`;
- use `DependsOn` when reusable/opaque computation code hides dependencies from expression analysis;
- declarations augment inferred dependencies;
- nested declared paths participate in reverse-navigation routing and scope requirements;
- every hidden semantic source dependency must be declared;
- `DependsOn` does not make mutable external/static state safe;
- `AllowIncompleteDependencies` remains the explicit weaker-freshness escape hatch.

Use this wording or equivalent:

> `DependsOn` is a model-author assertion of dependency completeness for opaque source computation. Raffinert can
> maintain freshness only for dependencies represented by the expression analysis plus these declarations.

Do not claim Raffinert introspects the opaque method body.

---

# Task 180 — Unblock Tasks 167–173 without weakening their example

After Tasks 174–179 are green, return to:

```text
docs/codex-plan-anemic-dependency-maintenance-dogfooding-example.md
```

Do **not** rewrite the sample around a weaker or different architecture.

Change its blocker section/status from blocked to a clear resume state, for example:

```text
Status: READY TO RESUME — BLOCKER RESOLVED BY TASKS 174–181
```

Update the frozen declaration in Task 169 to use the new API:

```csharp
var unitRate = builder.Derived(associations)
    .DependsOn(a => a.SourceItem.UnitValue)
    .DependsOn(a => a.TargetItem.UnitValue)
    .Compute(a => UnitRateCalculator.Calculate(
        a.SourceItem.UnitValue,
        a.TargetItem.UnitValue))
    .Named("association-unit-rate");
```

Preserve all original dogfooding constraints:

```text
Association is still the only Raffinert ObjectSet root.
No Objects<SourceItem>().
No Objects<TargetItem>().
No AllowIncompleteDependencies().
UnitRateCalculator remains a separate pure method.
SQLite executable scenarios remain unchanged.
Fan-out and retargeting proofs remain mandatory.
No manual invalidation collector is added to application code.
```

The product feature is successful only if the original sample can resume **without compromising those constraints**.

Do not implement the dogfooding sample inside this task unless the agent was explicitly instructed to continue into
Tasks 167–173. This task only makes the blocked roadmap executable again and verifies its declaration builds in a small
focused test.

---

# Task 181 — Full regression and release gate

Before marking this roadmap complete, run the complete repository proof.

Required local commands, adapted only for actual target framework names already present in the repo:

```bash
dotnet restore Raffinert.Consistency.sln
dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
dotnet pack Raffinert.Consistency.sln -c Release --no-build -o artifacts/packages
```

Also run the existing release verification script / package consumers exactly as CI does.

Then push the implementation head and require:

```text
CI workflow: success
Release candidate verification workflow: success
```

Do not publish a NuGet package, create a tag, or create a GitHub release.

Closeout documentation must record:

```text
implementation head SHA
CI run id and result
RC verification run id and result
core test counts for net8/net10
EF/SQLite test count
confirmation that both existing executable samples ran
confirmation that package/API verification and fresh package consumers passed
```

After successful closeout:

1. mark this roadmap **COMPLETED**;
2. update `docs/roadmaps/README.md` so Tasks 174–181 are completed;
3. make Tasks 167–173 the active roadmap again, now explicitly marked as resumed after the explicit-dependency feature.

---

# Mandatory acceptance matrix

The implementation is not complete until every row is proven.

| Case | Required result |
|---|---|
| opaque source computation, no `DependsOn` | Build rejects with `ContainsOpaqueCode` |
| opaque source computation + valid `DependsOn` | Build succeeds |
| direct declared scalar path changes | derived source becomes Dirty |
| nested declared path changes | every current consumer becomes Dirty |
| unrelated nested object changes | unrelated roots remain Fresh |
| root navigation retarget | routing index moves from old target to new target |
| old target changes after retarget | former consumer stays Fresh |
| new target changes after retarget | current consumer becomes Dirty |
| inferred normal dependency + declared dependency | both remain active |
| duplicate declaration | deduplicated semantic dependency |
| declared nested navigation path | NavigationConsumerCoverage scope requirement |
| declared direct scalar path | no unnecessary whole-set scope requirement |
| opaque computation + captured external state | Build still rejects `ContainsExternalState` |
| malformed dependency selector | immediate `ArgumentException` |
| collection dependency selector | immediate `ArgumentException` in v1 |
| `AllowIncompleteDependencies()` existing tests | unchanged |
| existing analyzable source-derived expressions | unchanged |
| relation/composed/projected/invariant behavior | unchanged |
| public API baseline | exactly one intended new builder method |
| dogfood frozen declaration with calculator | builds without `AllowIncompleteDependencies()` |

---

# Explicit anti-shortcut rules

A weaker agent must **not** do any of the following:

```text
DO NOT inline UnitRateCalculator into the dogfood expression just to avoid opaque code.
DO NOT call AllowIncompleteDependencies in the dogfood example.
DO NOT globally whitelist static method calls as pure.
DO NOT clear ContainsExternalState when DependsOn is present.
DO NOT replace inferred dependency analysis with only declared paths.
DO NOT create fake ObjectSet roots for nested endpoint objects.
DO NOT add DependsOn to every builder in one sweep.
DO NOT support collection selectors in v1.
DO NOT expose MemberInfo/DependencyPath/TrackedExpressionDependency publicly.
DO NOT add a second runtime routing table for explicit dependencies.
DO NOT modify EF behavior unless an existing metadata consumer demonstrably fails to read merged Analysis.
DO NOT weaken strict freshness language in docs.
DO NOT publish packages or create releases.
```

If any required behavior cannot be achieved by merging declared source paths into the existing
`SourceDerivedDefinition.Analysis` pipeline, stop and document why before designing a parallel mechanism.

---

# Expected final architecture

The feature should remain mechanically small:

```text
DerivedBuilder<TSource>
    DependsOn(path)
        ↓
validate/normalize source member path
        ↓
SourceDerivedDefinition
    inferred analysis
    + declared dependencies
    - ContainsOpaqueCode when explicit contract exists
    + preserve ContainsExternalState
        ↓
existing Analysis consumers unchanged
    runtime impact routing
    reverse navigation index
    scope requirements
    EF member usage
    diagnostics
```

No new subsystem should exist after this roadmap.

The success criterion is not merely “the new API compiles.” The success criterion is:

> the previously blocked production-shaped dogfooding declaration can use a separate reusable calculator while
> Raffinert still proves and maintains fan-out, selective invalidation, retargeting, scope safety, and normal cached
> freshness without `AllowIncompleteDependencies()`.
