# Codex implementation plan — anemic-model dependency-maintenance dogfooding example

Status: **COMPLETED LOCALLY — REMOTE PIPELINES NOT CHECKED**

Baseline commit: `616ba28dc820475d08ed59b771b79b8608a7e7aa`

Tasks: **167–173**

## Implementation blocker discovered 2026-09-16 — resolved

The required declaration:

```csharp
builder.Derived(associations)
    .Compute(association => UnitRateCalculator.Calculate(
        association.SourceItem.UnitValue,
        association.TargetItem.UnitValue))
```

is rejected by `ConsistencyModelBuilder.Build()` because the method call is classified as
`ContainsOpaqueCode`. The current public builder has no API for attaching explicit dependency paths to an
otherwise opaque derived computation. Its only available escape hatch is `AllowIncompleteDependencies()`,
which explicitly accepts weaker cached-freshness guarantees. That cannot satisfy this roadmap's required
proof that source/target changes reliably discover every affected association and keep selective routing
fresh after retargeting.

Tasks 174–181 added the narrow `DerivedBuilder<TSource>.DependsOn(...)` contract. The dogfooding declaration can
now retain its separate calculator and complete freshness by declaring both nested input paths. No fake endpoint
object sets and no `AllowIncompleteDependencies()` opt-in are required.

## Goal

Add one small, executable, domain-neutral dogfooding example that demonstrates the primary value of
`Raffinert.Consistency` in an anemic/persistence-oriented model:

> ordinary POCO properties can be mutated directly while dependency propagation, affected-object discovery,
> derived-value recomputation, and persisted mirror maintenance are declared structurally instead of hand-written
> as orchestration code.

The source dogfooding pattern being generalized is intentionally simple at the calculation level and expensive at
maintenance level:

```text
source endpoint value changes
or target endpoint value changes
or association endpoints change
        ↓
find every affected association
        ↓
reuse tracked endpoints where possible
        ↓
load missing endpoints when necessary
        ↓
recalculate a derived ratio
        ↓
persist the derived value
```

The new example must use only neutral terminology. Do not copy procurement/invoice/order/matching terminology into
this sample or its documentation.

This is an **example/documentation/proof** roadmap. It is not a Core or EF feature roadmap.

## Frozen vocabulary

Use these names exactly unless a compiler/API requirement forces a trivial syntactic change:

```text
SourceItem
TargetItem
Association
UnitValue
UnitRate
SourceItemId
TargetItemId
DependencyMaintenanceContext
UnitRateCalculator
```

Do not use these names in the new sample/docs:

```text
PurchaseOrder
PurchaseOrderLine
Invoice
InvoiceLine
GoodsReceipt
PriceRate
UnitPrice
Supplier
POIL
GRN
Matching
Fulfillment
Allocation
```

`Association` is the only Raffinert object-set root in this example.

`SourceItem` and `TargetItem` are ordinary EF-tracked navigation targets and **must not** be declared as Raffinert
object sets merely to make propagation work.

---

# Non-negotiable guardrails

1. **Do not modify `src/Raffinert.Consistency/**` or `src/Raffinert.Consistency.EntityFrameworkCore/**` as part of this roadmap.**
2. If the example cannot be implemented with the current public API, stop implementation and document the exact
   missing capability. Do not patch product code to make the example pass.
3. Do not add new public API.
4. Do not add new analyzers, runtime modes, scope kinds, persistence behaviors, or exceptions.
5. Do not create a second domain framework inside the sample.
6. Keep the entities intentionally anemic: ordinary auto-properties, no domain methods, no custom setters that
   perform consistency work, no events raised from property setters.
7. Do not implement an executable copy of the old manual invalidation service. The manual approach belongs in the
   documentation as a compact before-sketch only.
8. The sample must use a real SQLite in-memory database and the real EF adapter.
9. The sample must be self-verifying: incorrect results must throw and fail `dotnet run`; console output alone is
   not proof.
10. Do not hide authoritative-scope requirements. The example must explicitly use
    `new ConsistencyScope().Complete(associations)`.
11. Do not auto-load `SourceItem` or `TargetItem` through Raffinert. EF must already be tracking the graph used by
    the operation.
12. Do not add `Objects<SourceItem>()` or `Objects<TargetItem>()`.
13. Do not turn `UnitRate` into a graph input. It is a sink-only persisted mirror.
14. Do not publish packages, create a tag, or create a GitHub release in this roadmap.

---

# Task 167 — Create the new executable sample project

Create exactly:

```text
samples/Raffinert.Consistency.DependencyMaintenanceSample/
    Raffinert.Consistency.DependencyMaintenanceSample.csproj
    Program.cs
```

The project file should mirror the existing EF Core sample project conventions:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.0.12" />
    <ProjectReference Include="..\..\src\Raffinert.Consistency\Raffinert.Consistency.csproj" />
    <ProjectReference Include="..\..\src\Raffinert.Consistency.EntityFrameworkCore\Raffinert.Consistency.EntityFrameworkCore.csproj" />
  </ItemGroup>
</Project>
```

Do not reference test projects.

Add the project to `Raffinert.Consistency.sln` under the existing `samples` solution folder. Generate a new stable
project GUID once and use it consistently in the solution configuration blocks. Do not reuse another sample GUID.

Acceptance:

```bash
dotnet restore samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj
dotnet build samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release
```

Both must pass before proceeding.

---

# Task 168 — Implement the frozen neutral domain and calculator

Put the example types in `Program.cs` unless splitting the file materially improves readability. Do not create a
large sample architecture.

Use this domain shape:

```csharp
internal sealed class SourceItem
{
    public long Id { get; set; }
    public decimal? UnitValue { get; set; }
}

internal sealed class TargetItem
{
    public long Id { get; set; }
    public decimal UnitValue { get; set; }
}

internal sealed class Association
{
    public long Id { get; set; }

    public long SourceItemId { get; set; }
    public SourceItem SourceItem { get; set; } = null!;

    public long TargetItemId { get; set; }
    public TargetItem TargetItem { get; set; } = null!;

    // Persisted sink-only mirror of derived state.
    public decimal? UnitRate { get; set; }
}
```

Use an EF `DependencyMaintenanceContext` with `DbSet<SourceItem>`, `DbSet<TargetItem>`, and `DbSet<Association>`.
Configure both association references explicitly and use `DeleteBehavior.Restrict` so this example is about
incremental dependency maintenance, not cascade behavior.

Use a pure calculator matching the dogfooded semantics:

```csharp
internal static class UnitRateCalculator
{
    public const int Scale = 6;
    public const decimal MaximumSupportedRate = 9999999999999999999999.999999m;

    public static decimal? Calculate(decimal? sourceValue, decimal targetValue)
    {
        if (sourceValue is null || targetValue == 0m)
            return null;

        decimal roundedRate;
        try
        {
            roundedRate = decimal.Round(
                sourceValue.Value / targetValue,
                Scale,
                MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return null;
        }

        return roundedRate == decimal.MinValue || decimal.Abs(roundedRate) > MaximumSupportedRate
            ? null
            : roundedRate;
    }
}
```

Do not simplify away the zero, null, overflow, rounding, or maximum-supported-rate behavior. The point is to keep
business calculation separate from dependency orchestration.

Add tiny self-checks for the pure calculator before the EF scenario runs:

```text
null / 4      -> null
10 / 0        -> null
10 / 4        -> 2.500000m (numeric equality is enough; formatting need not preserve trailing zeroes)
1 / 3         -> 0.333333m
decimal.MaxValue / 1 -> null because it exceeds MaximumSupportedRate
decimal.MaxValue / 0.1m -> null if decimal division overflows
```

Use a local `RequireEqual<T>(...)` / `Require(...)` helper that throws `InvalidOperationException` on failure.
Do not add a test framework dependency to the sample.

---

# Task 169 — Build the Raffinert model with only `Association` as a root set

The sample must make the architectural point obvious in code.

Build exactly one Raffinert object set:

```csharp
var builder = new ConsistencyModelBuilder();

var associations = builder.Objects<Association>()
    .Named("associations")
    .Key(x => x.Id);
```

Do **not** add:

```csharp
builder.Objects<SourceItem>()
builder.Objects<TargetItem>()
```

Declare the derived value from ordinary navigation paths:

```csharp
var unitRate = builder.Derived(associations)
    .DependsOn(a => a.SourceItem.UnitValue)
    .DependsOn(a => a.TargetItem.UnitValue)
    .Compute(association => UnitRateCalculator.Calculate(
        association.SourceItem.UnitValue,
        association.TargetItem.UnitValue))
    .Named("association-unit-rate");
```

If the current public API does not allow the final `.Named(...)` on this builder shape, omit only that `.Named(...)`;
do not change product code.

Build and seed the runtime with the complete association set only:

```csharp
var runtime = builder.Build().CreateRuntime(seed =>
    seed.Add(associations, allAssociations));
```

Configure EF persistence:

```csharp
var mappings = new ConsistencyEfCoreMappings()
    .Map(associations)
    .Materialize(unitRate, association => association.UnitRate);

var saveOptions = new ConsistencySaveOptions
{
    Scope = new ConsistencyScope().Complete(associations)
};
```

Important acceptance properties:

```text
SourceItem.UnitValue is a nested dependency target.
TargetItem.UnitValue is a nested dependency target.
Association.SourceItem / TargetItem retargeting is a root navigation mutation.
Association.UnitRate is a sink-only persisted mirror.
Only Association requires authoritative whole-set coverage for reverse navigation discovery.
```

Add a comment directly above the model declaration explaining in one sentence that source/target objects remain
ordinary EF-tracked objects and do not need fake Raffinert object sets.

---

# Task 170 — Implement deterministic end-to-end scenarios

Use one open SQLite in-memory connection for the complete executable run.

Seed these objects with explicit IDs so the example does not introduce generated-key noise:

```text
Source A: Id=1, UnitValue=12
Source B: Id=2, UnitValue=30

Target A: Id=10, UnitValue=4
Target B: Id=11, UnitValue=5

Association A: Id=100, Source A -> Target A, UnitRate=3
Association B: Id=101, Source A -> Target B, UnitRate=2.4
```

Persist the seed before constructing/running the consistency scenarios. The seed `UnitRate` values may be initialized
with `UnitRateCalculator.Calculate(...)`; do not hand-code different arithmetic.

Load/retain the complete tracked graph, then build the runtime from both associations.

Implement the following scenarios in this exact order.

## 170.1 Shared source change fans out to both associations

Mutation:

```csharp
sourceA.UnitValue = 20m;
```

Call:

```csharp
context.SaveChangesConsistently(runtime, mappings, saveOptions);
```

Assert both CLR/runtime/persisted results:

```text
Association A UnitRate = 5
Association B UnitRate = 4
```

This is the central dogfooding proof: one ordinary anemic property assignment affects multiple associations without a
manual invalidation collector or affected-association query in application code.

## 170.2 Target change affects only its consumers

Mutation:

```csharp
targetA.UnitValue = 10m;
```

Expected:

```text
Association A UnitRate = 2
Association B UnitRate = 4
```

Assert `Association B` remains unchanged.

## 170.3 Association retargeting changes dependency topology

Mutation:

```csharp
associationB.SourceItem = sourceB;
associationB.SourceItemId = sourceB.Id; // include this assignment only if EF relationship fixup does not set it automatically
```

Do not blindly set both navigation and FK if EF already fixes up the FK. Prefer setting the navigation and assert the
FK is correct before saving.

Expected after consistent save:

```text
Association B UnitRate = 6
```

Then mutate:

```csharp
sourceA.UnitValue = 25m;
```

and prove Association B no longer responds to Source A while Association A does. This is important: the sample must
prove the reverse-navigation dependency index was updated after retargeting, not only that the immediate retarget
recalculated once.

## 170.4 Nullable source produces null mirror

Mutation:

```csharp
sourceB.UnitValue = null;
```

Expected:

```text
Association B UnitRate = null
```

Persisted DB value and runtime derived value must both be null.

## 170.5 Zero target produces null mirror

Restore:

```csharp
sourceB.UnitValue = 30m;
```

then mutate:

```csharp
targetB.UnitValue = 0m;
```

Expected:

```text
Association B UnitRate = null
```

Again assert CLR mirror, runtime value, and no-tracking DB read.

## Verification style

After every scenario:

1. assert the tracked `Association.UnitRate` value;
2. assert `runtime.Get(unitRate, association)`;
3. query the relevant association row with `AsNoTracking()` and assert the persisted mirror;
4. assert `runtime.Version` advanced exactly once for that successful consistent save.

Do not rely on console text as the assertion mechanism.

At the end print a short success summary, for example:

```text
Dependency-maintenance dogfood sample passed:
- source changes propagated to all consumers
- target changes stayed selective
- association retargeting updated dependency routing
- null/zero semantics were materialized consistently
```

---

# Task 171 — Add the before/after dogfooding document

Create:

```text
docs/anemic-model-dependency-maintenance-example.md
```

Title:

```text
# Dogfooding: dependency maintenance in an anemic model
```

The document must be domain-neutral and must not mention the original procurement/invoice implementation.

## Required structure

### 1. The problem

Start from ordinary POCO mutation:

```csharp
source.UnitValue = 20m;
```

Ask the concrete question:

> Which associations now contain stale `UnitRate` values?

Explain that the arithmetic is trivial; affected-object discovery and orchestration are the real maintenance burden.

### 2. Typical manual orchestration

Show only compact neutral pseudocode / a responsibility list. Do not reproduce a proprietary service implementation.
The sketch must include these responsibilities:

```text
collect changed source IDs
collect changed target IDs
collect directly changed associations
query associations touching those IDs
union queried and already-tracked associations
resolve/load missing endpoints
recalculate each affected UnitRate
write the persisted value
```

State explicitly that this logic must remain synchronized with every property/navigation that participates in the
derived value.

### 3. Raffinert declaration

Show the exact small declaration used by the executable sample:

```csharp
var associations = builder.Objects<Association>()
    .Key(x => x.Id);

var unitRate = builder.Derived(associations)
    .DependsOn(a => a.SourceItem.UnitValue)
    .DependsOn(a => a.TargetItem.UnitValue)
    .Compute(a => UnitRateCalculator.Calculate(
        a.SourceItem.UnitValue,
        a.TargetItem.UnitValue));

var mappings = new ConsistencyEfCoreMappings()
    .Map(associations)
    .Materialize(unitRate, a => a.UnitRate);
```

Then show:

```csharp
source.UnitValue = 20m;
await db.SaveChangesConsistentlyAsync(...);
```

### 4. What disappeared

Use one compact list/table comparing responsibilities, not marketing adjectives.

At minimum compare:

```text
change detection
reverse affected-association discovery
relationship retargeting maintenance
derived recomputation selection
persisted mirror update
```

Manual approach: application orchestration.
Raffinert approach: dependency graph + EF adapter.

### 5. What did not disappear

This section is mandatory so the example stays honest.

State clearly:

- the host still owns authoritative runtime coverage;
- `ConsistencyScope.Complete(associations)` is an assertion, not a DB query;
- Raffinert does not auto-load missing associations;
- database changes invisible to EF/Raffinert still require the documented reconciliation/rebuild boundary;
- the calculator/business formula still exists and should stay explicit;
- this example demonstrates dependency maintenance, not generic replacement of repositories/querying.

### 6. Why this matters most in anemic models

Explain the intended thesis without attacking DDD:

```text
Rich model: behavioral methods can centralize some mutation consequences.
Anemic model: setters may be used from many handlers/services/imports, so "remember to also recalculate X" spreads.
Raffinert: entities stay ordinary POCOs while cross-object consequences are declared externally once.
```

Do not claim Raffinert replaces rich-domain modeling or event-driven integration.

### 7. Link to executable sample

Link to:

```text
../samples/Raffinert.Consistency.DependencyMaintenanceSample/Program.cs
```

---

# Task 172 — Integrate the example into README, solution, and CI/RC verification

## README

Add a short section or bullet near the existing examples/documentation links:

```text
Dogfooding: dependency maintenance in an anemic model
```

Describe it in one or two sentences:

> A domain-neutral SQLite example showing how changes to ordinary navigation targets automatically identify
> affected associations, recalculate a derived `UnitRate`, and persist the mirror without hand-written
> invalidation/query orchestration.

Link both the dogfooding document and executable project.

Do not turn the top-level README into a long essay. The detailed explanation belongs in the new doc.

## CI

In `.github/workflows/ci.yml`, after the existing sample runs, add:

```yaml
- name: Run dependency maintenance dogfood sample
  run: dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release --no-build
```

## Release-candidate verification

In `.github/workflows/release-candidate.yml`, add the equivalent command next to the other sample runs:

```yaml
- run: dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release --no-build
```

Do not change package publication behavior.

## Neutral terminology residue gate

Run:

```bash
git grep -niE "PurchaseOrder|PurchaseOrderLine|InvoiceLine|GoodsReceipt|PriceRate|UnitPrice|POIL|GRN|Supplier" -- \
  samples/Raffinert.Consistency.DependencyMaintenanceSample \
  docs/anemic-model-dependency-maintenance-example.md
```

Expected: **no hits**.

Also run:

```bash
git grep -n "Objects<SourceItem>\|Objects<TargetItem>" -- \
  samples/Raffinert.Consistency.DependencyMaintenanceSample
```

Expected: **no hits**.

---

# Task 173 — Full verification and closeout

Before marking the roadmap complete, run all of the following from repository root.

## Focused sample

```bash
dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release
```

Must exit 0 and print the success summary.

## Full local gate

```bash
dotnet restore Raffinert.Consistency.sln
dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes

dotnet pack Raffinert.Consistency.sln -c Release --no-build -o artifacts/packages
pwsh ./eng/VerifyReleaseCandidate.ps1 -PackageDirectory artifacts/packages -RequireEmptyUnshipped
```

Then run the existing package-consumer smoke tests or the same script/commands used by CI.

## Remote gate

Push the implementation head and require:

1. normal CI success on that exact implementation commit;
2. manual `Release candidate verification` workflow success on that exact implementation commit;
3. the new dependency-maintenance sample visibly executed in both runs;
4. no package publication, tag, or release.

## Closeout documentation

Only after both remote gates are green:

1. change this plan status to `COMPLETED`;
2. record the implementation head SHA;
3. record CI and RC run IDs;
4. record final core/EF test counts;
5. state that all three executable samples passed;
6. update `docs/release-candidate-verification.md` with a short dogfooding-example verification entry;
7. replace the active-roadmap line in `docs/roadmaps/README.md` with a completed entry.

Local closeout (2026-09-16, implementation head `8da58b4`):

- The dependency-maintenance sample built and ran successfully against SQLite in-memory.
- The solution build, formatting verification, core tests, EF/SQLite tests, and both existing executable samples
  passed locally.
- Remote CI and release-candidate workflows were intentionally not queried or waited on, per the implementation
  instruction for this closeout.

No package was published, and no tag or GitHub release was created.

---

# Expected implementation footprint

The normal successful implementation should touch approximately:

```text
samples/Raffinert.Consistency.DependencyMaintenanceSample/
Raffinert.Consistency.sln
README.md
docs/anemic-model-dependency-maintenance-example.md
.github/workflows/ci.yml
.github/workflows/release-candidate.yml
```

and closeout documentation afterward.

It should **not** touch:

```text
src/Raffinert.Consistency/**
src/Raffinert.Consistency.EntityFrameworkCore/**
PublicAPI.Shipped.txt
PublicAPI.Unshipped.txt
```

If product-source changes appear necessary, stop and report why.

---

# Acceptance checklist

The roadmap is complete only when every item below is true:

- [ ] New executable project is named `Raffinert.Consistency.DependencyMaintenanceSample`.
- [ ] Sample vocabulary is `SourceItem`, `TargetItem`, `Association`, `UnitValue`, `UnitRate`.
- [ ] No procurement/invoice/order-matching terminology appears in the new sample/doc.
- [ ] Entities are intentionally anemic POCOs.
- [ ] Only `Association` is a Raffinert object set.
- [ ] `SourceItem` and `TargetItem` remain unmapped Raffinert nested dependency targets.
- [ ] `UnitRate` is a materialized sink-only mirror.
- [ ] The calculator preserves null, zero, rounding, overflow, and maximum-range semantics.
- [ ] Shared source mutation updates every consuming association.
- [ ] Target mutation updates only its consumers.
- [ ] Association retargeting changes future dependency routing, not only the immediate rate.
- [ ] Null source value materializes null rate.
- [ ] Zero target value materializes null rate.
- [ ] Every scenario asserts tracked mirror, runtime derived value, and persisted DB value.
- [ ] Runtime version behavior is asserted.
- [ ] `ConsistencyScope.Complete(associations)` is explicit.
- [ ] The dogfooding doc shows both what Raffinert removes and what responsibilities remain with the host.
- [ ] No Core/EF product implementation or public API changed.
- [ ] Solution build, full tests, all samples, formatting, pack, RC verification, and package consumers pass.
- [ ] CI and release-candidate workflows pass on the exact implementation head before closeout.
