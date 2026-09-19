# Codex plan — mockable application-facing consistency runtime

Status: **IMPLEMENTATION PLAN — ADDITIVE API/DI ERGONOMICS ONLY**

Audience: an extremely weak coding agent. Follow this document literally and in order. Do not improvise, redesign the runtime, broaden the abstraction, or turn engine internals into mockable APIs.

Baseline after the `0.2.0-rc.1` release and README EF/DI landing-page update:

```text
e02f3003cfbc836844e38e6c6d8dee5f2d5b39a2
```

The published `0.2.0-rc.1` tag/package is immutable history. This work is for the next release line on `main`.

---

# 0. Goal

Make the common application-facing Raffinert runtime dependency easy to substitute in ordinary service unit tests without weakening or redesigning `ConsistencyRuntime`.

The desired consumer experience is:

```csharp
public sealed class LinkService(
    AppDbContext db,
    IConsistencyRuntime consistency)
{
    public async Task ChangeAsync(
        long id,
        decimal value,
        CancellationToken cancellationToken)
    {
        var link = await db.Links
            .Include(x => x.Left)
            .Include(x => x.Right)
            .SingleAsync(x => x.Id == id, cancellationToken);

        link.Left.Value = value;

        consistency.Materialize(link); // only if the mirror is needed before SaveChanges

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

and in a unit test with a normal interface-based mocking library such as NSubstitute:

```csharp
var consistency = Substitute.For<IConsistencyRuntime>();

var service = new LinkService(db, consistency);

await service.ChangeAsync(id, newValue, cancellationToken);

consistency.Received(1).Materialize(
    Arg.Is<Link>(x => x.Id == id));
```

The actual EF integration must still use the exact same scoped `ConsistencyRuntime` instance internally.

---

# 1. Frozen design decisions

These decisions are part of the task. Do not revisit them during implementation.

## 1.1 Keep `ConsistencyRuntime` sealed

Do **not** change:

```csharp
public sealed partial class ConsistencyRuntime
```

into a non-sealed class.

Do **not** make runtime methods `virtual` merely to support mocking.

Reason:

```text
ConsistencyRuntime is a stateful consistency engine.
Mockability belongs at the application boundary, not in subclassable engine internals.
```

## 1.2 Add one small application-facing interface

Add:

```csharp
public interface IConsistencyRuntime
```

in the Core package/namespace:

```text
Raffinert.Consistency
```

The interface is intentionally a **small application-facing subset**, not a mirror of the entire concrete runtime API.

It must expose exactly the common logical-value/materialization operations needed by application services:

```csharp
public interface IConsistencyRuntime
{
    TValue Evaluate<TSource, TValue>(
        Derived<TSource, TValue> derived,
        TSource source)
        where TSource : class;

    TValue Materialize<TSource, TValue>(
        Derived<TSource, TValue> derived,
        TSource source)
        where TSource : class;

    void Materialize<TSource>(TSource source)
        where TSource : class;
}
```

Do not add anything else to this interface in this task.

Specifically do **not** add:

```text
Add
Remove
Related
Apply
ApplyDetailed
Prepare
Commit
Dispatch
PlanDetailed
PreviewDetailed
Diagnostics
Version
scope APIs
mutation APIs
relation APIs
repair APIs
```

Those remain advanced engine/runtime APIs on concrete `ConsistencyRuntime`.

## 1.3 `ConsistencyRuntime` implements the interface directly

Change the class declaration only as required:

```csharp
public sealed partial class ConsistencyRuntime : IConsistencyRuntime
```

The existing public `Evaluate` / `Materialize` methods already have the desired behavior.

Do not duplicate their implementation.

Do not introduce an adapter object around `ConsistencyRuntime` unless a concrete technical blocker proves direct implementation impossible.

Do not use explicit interface implementations unless required by a compiler conflict. The current concrete public methods should remain directly callable exactly as before.

## 1.4 No behavior changes

This task must not change:

```text
Evaluate semantics
Materialize semantics
EF pending-plan semantics
baseline revision semantics
runtime Version semantics
transaction behavior
repair dispatch behavior
scope/completeness behavior
external consumer discovery
runtime registration behavior
```

This is an API/DI seam only.

---

# 2. Add the Core interface

Preferred file:

```text
src/Raffinert.Consistency/Runtime/IConsistencyRuntime.cs
```

Expected source shape:

```csharp
namespace Raffinert.Consistency;

/// <summary>
/// Application-facing logical evaluation and materialization operations for a consistency runtime.
/// </summary>
public interface IConsistencyRuntime
{
    /// <summary>
    /// Makes the logical derived value current and returns it without writing a materialized mirror.
    /// </summary>
    TValue Evaluate<TSource, TValue>(
        Derived<TSource, TValue> derived,
        TSource source)
        where TSource : class;

    /// <summary>
    /// Evaluates and synchronizes exactly one configured materialized representation.
    /// </summary>
    TValue Materialize<TSource, TValue>(
        Derived<TSource, TValue> derived,
        TSource source)
        where TSource : class;

    /// <summary>
    /// Synchronizes every configured materialized representation physically located on the source object.
    /// </summary>
    void Materialize<TSource>(TSource source)
        where TSource : class;
}
```

Keep XML documentation concise and semantically aligned with the existing concrete methods.

Do not mention NSubstitute, Moq, FakeItEasy, or any specific mocking framework in production API XML docs.

---

# 3. Make the concrete runtime implement the interface

File:

```text
src/Raffinert.Consistency/Runtime/ConsistencyRuntime.cs
```

or whichever partial declaration is the canonical class declaration.

Change:

```csharp
public sealed partial class ConsistencyRuntime
```

to:

```csharp
public sealed partial class ConsistencyRuntime : IConsistencyRuntime
```

Do not touch the implementations of:

```csharp
Evaluate<TSource, TValue>(...)
Materialize<TSource, TValue>(...)
Materialize<TSource>(...)
```

except for documentation/compiler fixes that are strictly necessary.

Compile before proceeding further. If the interface does not match the existing methods exactly, fix the interface signature to match the existing intended public methods; do not alter runtime behavior to satisfy a mistaken interface declaration.

---

# 4. Register the interface as an alias to the same scoped runtime in EF Core DI

Current EF registration creates the concrete runtime with a scoped factory:

```csharp
services.AddScoped<ConsistencyRuntime>(serviceProvider =>
{
    var runtime = serviceProvider
        .GetRequiredService<CompiledConsistencyModel>()
        .CreateRuntime();

    serviceProvider
        .GetRequiredService<ConsistencyEfCoreSession<TDbContext>>()
        .BindRuntime(runtime);

    return runtime;
});
```

Keep that factory as the **single creator/owner of the scoped runtime instance**.

Immediately after the concrete registration, add an alias registration:

```csharp
services.AddScoped<IConsistencyRuntime>(serviceProvider =>
    serviceProvider.GetRequiredService<ConsistencyRuntime>());
```

The required invariant is:

```csharp
ReferenceEquals(
    serviceProvider.GetRequiredService<ConsistencyRuntime>(),
    serviceProvider.GetRequiredService<IConsistencyRuntime>())
```

must be `true` inside one DI scope.

## 4.1 Forbidden DI implementations

Do **not** write:

```csharp
services.AddScoped<IConsistencyRuntime, ConsistencyRuntime>();
```

That registration shape can create a second scoped concrete activation path and is not the intended ownership model.

Do **not** create a second runtime with:

```csharp
compiledModel.CreateRuntime()
```

for the interface registration.

Do **not** change the EF session to bind a separate interface-owned runtime.

Do **not** change the interceptor/session lifecycle.

There must remain one scoped concrete runtime instance shared by:

```text
application service via IConsistencyRuntime
advanced consumer via ConsistencyRuntime
ConsistencyEfCoreSession<TDbContext>
SaveChanges interceptor integration
```

---

# 5. Preserve the concrete runtime registration

Do not replace the existing concrete DI registration with interface-only registration.

Both of these must resolve after `AddRaffinertConsistency<TDbContext>(...)`:

```csharp
ConsistencyRuntime
IConsistencyRuntime
```

Why:

```text
- IConsistencyRuntime is the recommended narrow dependency for ordinary application services.
- ConsistencyRuntime remains necessary for advanced/manual runtime APIs not exposed by the narrow interface.
- Existing consumers injecting ConsistencyRuntime must remain source/binary behavior compatible.
```

This task is additive.

---

# 6. Core tests

Add focused tests in the Core test project.

Do not require the Core production package to reference any mocking framework.

## 6.1 Interface implementation proof

Add a test that proves a real runtime is assignable to `IConsistencyRuntime`.

Example intent:

```csharp
var runtime = compiled.CreateRuntime(...);

IConsistencyRuntime abstraction = runtime;

Assert.Same(runtime, abstraction);
```

The test should use the existing test model helpers where practical; do not build a giant new fixture.

## 6.2 Evaluate through interface

Build a tiny model with one derived value and seed one source.

Call:

```csharp
IConsistencyRuntime runtime = compiled.CreateRuntime(...);
var value = runtime.Evaluate(derived, source);
```

Assert the expected logical value.

Also assert that a configured physical mirror remains unchanged after `Evaluate` if the existing test model includes a materialized target.

This proves the interface does not accidentally introduce different semantics.

## 6.3 Targeted Materialize through interface

Using a derived definition with `MaterializeTo`, call:

```csharp
IConsistencyRuntime runtime = ...;
var value = runtime.Materialize(derived, source);
```

Assert:

```text
- returned value is correct
- target mirror is synchronized
```

## 6.4 Object Materialize through interface

Call:

```csharp
runtime.Materialize(source);
```

through `IConsistencyRuntime` and assert existing object-level materialization semantics.

Do not duplicate every existing materialization edge-case test. Existing concrete-runtime tests remain authoritative for rollback/order/error semantics.

These new tests prove only that the abstraction reaches the same implementation.

---

# 7. EF Core DI tests — mandatory

These are the most important tests in this task.

Use the existing EF DI test infrastructure and existing `AddRaffinertConsistency<TDbContext>` setup.

## 7.1 Same instance in one scope

Resolve:

```csharp
var concrete = scope.ServiceProvider
    .GetRequiredService<ConsistencyRuntime>();

var abstraction = scope.ServiceProvider
    .GetRequiredService<IConsistencyRuntime>();
```

Assert:

```csharp
Assert.Same(concrete, abstraction);
```

This test is mandatory.

## 7.2 Interface is scoped

Inside one scope:

```csharp
var first = provider.GetRequiredService<IConsistencyRuntime>();
var second = provider.GetRequiredService<IConsistencyRuntime>();
```

Assert `Same`.

Create a second DI scope and resolve again.

Assert it is not the same runtime instance as the first scope.

Do not assume DbContext pooling semantics that are not configured by the test fixture; test only the library's service registration lifetime.

## 7.3 Application-style materialization through interface + ordinary SaveChanges

Add or adapt an integration test shaped like the recommended consumer workflow:

```csharp
public sealed class TestService(
    TestDbContext db,
    IConsistencyRuntime consistency)
{
    public async Task Execute(...)
    {
        var entity = await db.Entities...

        entity.Input = newValue;
        consistency.Materialize(entity);

        await db.SaveChangesAsync();
    }
}
```

Assert all relevant outcomes:

```text
- the materialized mirror has the expected value before/after save as appropriate
- SQL persistence succeeds
- the EF interceptor/session uses the same runtime state
- no duplicate runtime/baseline is created
```

Reuse a current DI/materialization scenario rather than inventing a semantically unrelated model.

## 7.4 Existing concrete injection remains valid

Keep or add a small test proving `ConsistencyRuntime` still resolves directly after the new interface alias is added.

Existing consumer code must not be forced to migrate immediately.

---

# 8. Mockability proof without coupling production code to a mocking framework

The public design itself must be ordinary interface-based and therefore compatible with NSubstitute/Moq/FakeItEasy.

At minimum add a documentation example using NSubstitute syntax.

Preferred location:

```text
README.md
```

or an appropriate consumer recipe if the README would become too noisy.

Use a small example such as:

```csharp
var consistency = Substitute.For<IConsistencyRuntime>();

var service = new LinkService(db, consistency);

await service.ChangeAsync(id, value, cancellationToken);

consistency.Received(1).Materialize(
    Arg.Is<Link>(x => x.Id == id));
```

Do not add NSubstitute to production package dependencies.

Repository test projects do not need a new mocking-framework dependency merely to prove that a normal public interface can be substituted. Prefer existing test infrastructure plus compile/runtime tests above.

If an existing repository test project already gains a mocking library for another accepted reason, a tiny mockability test is fine, but adding that dependency is **not required by this plan**.

---

# 9. README — make the interface the recommended application dependency

The top README currently presents EF Core + DI as the common application path.

Update only the application-service dependency from concrete runtime to interface.

Preferred landing-page example:

```csharp
public sealed class LinkService(
    AppDbContext db,
    IConsistencyRuntime consistency)
{
    public async Task ChangeAsync(
        long id,
        decimal value,
        CancellationToken cancellationToken)
    {
        var link = await db.Links
            .Include(x => x.Left)
            .Include(x => x.Right)
            .SingleAsync(x => x.Id == id, cancellationToken);

        link.Left.Value = value;

        consistency.Materialize(link); // only if mirror is needed before SaveChanges

        await db.SaveChangesAsync(cancellationToken);
    }
}
```

Immediately explain the split:

```text
Use IConsistencyRuntime in ordinary application services when only Evaluate/Materialize is needed.
Inject concrete ConsistencyRuntime only when advanced engine APIs such as mutation planning or diagnostics are required.
```

Do not imply that concrete `ConsistencyRuntime` is deprecated.

Do not claim the full runtime engine is mockable through `IConsistencyRuntime`.

Do not turn the README into a mocking tutorial.

---

# 10. Consumer skill / recipes

Review current consumer-facing guidance:

```text
.agents/skills/raffinert-consistency-consumer/SKILL.md
.agents/skills/raffinert-consistency-consumer/references/recipes.md
.agents/skills/raffinert-consistency-consumer/references/verification.md
```

Where application services currently inject:

```csharp
ConsistencyRuntime
```

prefer:

```csharp
IConsistencyRuntime
```

**only** when the example needs no advanced concrete runtime methods.

Keep concrete `ConsistencyRuntime` in advanced/manual runtime recipes that call methods outside the narrow interface.

Explicitly teach the agent:

```text
- ordinary EF app service needing Evaluate/Materialize -> IConsistencyRuntime
- advanced engine/mutation/planning/diagnostic code -> ConsistencyRuntime
```

Do not mechanically replace every `ConsistencyRuntime` occurrence.

---

# 11. EF Core documentation

Review:

```text
docs/ef-core-consistency.md
```

Update the canonical dependency-injected application-service example to use `IConsistencyRuntime` when the service only calls `Materialize`/`Evaluate`.

Keep advanced transaction/unit-of-work examples on concrete APIs where necessary.

Document the crucial DI identity guarantee:

> `IConsistencyRuntime` resolves to the same scoped `ConsistencyRuntime` instance bound to the EF consistency session/interceptor.

Do not imply that the interface itself owns EF behavior. The concrete runtime/session integration remains the implementation.

---

# 12. Public API baseline handling

This work happens after the shipped `0.2.0-rc.1` API freeze.

Current Core `PublicAPI.Unshipped.txt` starts empty except:

```text
#nullable enable
```

The new interface is a new public API for the next release and therefore belongs in:

```text
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
```

Expected new entries will include the interface and its three methods, with signatures generated/required by the PublicApiAnalyzers.

Do **not** manually guess analyzer syntax if the build tells you the exact expected declarations.

Use the analyzer/compiler output to add exact entries.

Do not move these new entries into `PublicAPI.Shipped.txt` during implementation.

`PublicAPI.Shipped.txt` represents APIs already shipped in `0.2.0-rc.1` and must remain historically accurate.

The EF package public signature of:

```csharp
AddRaffinertConsistency<TDbContext>(...)
```

is unchanged, so no EF public API baseline entry should be added merely because its internal registrations changed.

If the EF analyzer reports no public API delta, leave:

```text
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
```

unchanged.

---

# 13. CHANGELOG handling

Do not rewrite the already published `0.2.0-rc.1` release notes as if this feature had shipped there.

If `CHANGELOG.md` has no current unreleased section, add a concise top section:

```markdown
## Unreleased

### Added

- Added `IConsistencyRuntime`, a narrow application-facing abstraction for logical evaluation and materialization, with EF Core DI resolving it to the same scoped `ConsistencyRuntime` instance.
```

Keep this change under `Unreleased` until a later release-preparation task chooses the actual next version (`0.2.0-rc.2`, `0.2.0`, etc.).

Do not change package version in this implementation task.

Do not retag or modify `v0.2.0-rc.1`.

---

# 14. Do not introduce these tempting but wrong designs

The following are explicit task failures.

## 14.1 Do not make every runtime method mockable

Wrong:

```csharp
public interface IConsistencyRuntime
{
    // dozens of engine APIs copied from ConsistencyRuntime
}
```

Reason: the application seam becomes a second giant runtime surface and couples unit tests to engine internals.

## 14.2 Do not unseal the runtime

Wrong:

```csharp
public class ConsistencyRuntime
```

## 14.3 Do not make methods virtual for mocking

Wrong:

```csharp
public virtual void Materialize<T>(T source)
```

## 14.4 Do not create two scoped runtimes

Wrong:

```csharp
services.AddScoped<ConsistencyRuntime>(...CreateRuntime...);
services.AddScoped<IConsistencyRuntime>(_ => compiledModel.CreateRuntime());
```

Wrong:

```csharp
services.AddScoped<IConsistencyRuntime, ConsistencyRuntime>();
```

The interface must alias the existing concrete registration.

## 14.5 Do not put the interface in the EF package

`IConsistencyRuntime` describes Core runtime operations and belongs to:

```text
Raffinert.Consistency
```

The EF package only wires it into DI.

## 14.6 Do not add a separate `IConsistencyMaterializer` in this task

The accepted design is one small `IConsistencyRuntime` with:

```text
Evaluate
Materialize(definition, source)
Materialize(source)
```

Do not proliferate tiny interfaces unless a future concrete use case requires it.

## 14.7 Do not change release version/tag

The published `0.2.0-rc.1` is immutable.

This task is next-release development on `main`.

---

# 15. Suggested implementation order

Follow this order exactly.

## Task 1 — Add `IConsistencyRuntime`

Create:

```text
src/Raffinert.Consistency/Runtime/IConsistencyRuntime.cs
```

with exactly the three accepted methods.

## Task 2 — Implement interface on `ConsistencyRuntime`

Add:

```csharp
: IConsistencyRuntime
```

No behavior changes.

## Task 3 — Build Core immediately

Run:

```bash
dotnet build src/Raffinert.Consistency/Raffinert.Consistency.csproj -c Release
```

Resolve only interface/API analyzer compile errors.

## Task 4 — Update Core `PublicAPI.Unshipped.txt`

Add exactly the analyzer-requested interface entries.

Build Core again until clean.

## Task 5 — Add Core interface behavior tests

Add focused interface-based Evaluate/Materialize tests.

Run Core tests on both TFMs.

## Task 6 — Add EF DI alias

Add:

```csharp
services.AddScoped<IConsistencyRuntime>(serviceProvider =>
    serviceProvider.GetRequiredService<ConsistencyRuntime>());
```

Do not alter the concrete runtime factory.

## Task 7 — Add mandatory EF DI identity/lifetime tests

At minimum:

```text
same concrete/interface instance in one scope
same interface instance on repeated resolve in one scope
different interface runtime in different scopes
concrete runtime still resolves
```

## Task 8 — Add application-style EF integration test

Use `IConsistencyRuntime.Materialize(entity)` followed by ordinary `SaveChangesAsync`.

Prove the same runtime/session path works end-to-end.

## Task 9 — Update README/docs/skill

Prefer the interface in ordinary application-service examples; keep concrete runtime in advanced engine examples.

## Task 10 — Add `Unreleased` changelog entry

Do not choose/rewrite a release version.

## Task 11 — Full build/test/sample/format verification

Run all commands below.

---

# 16. Mandatory verification commands

From repository root.

## 16.1 Restore

```bash
dotnet restore Raffinert.Consistency.sln
```

## 16.2 Release build

```bash
dotnet build Raffinert.Consistency.sln -c Release --no-restore
```

Required:

```text
0 warnings
0 errors
```

## 16.3 Core tests — .NET 8

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net8.0 --no-build
```

## 16.4 Core tests — .NET 10

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj \
  -c Release -f net10.0 --no-build
```

## 16.5 EF Core tests — .NET 10

```bash
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj \
  -c Release -f net10.0 --no-build
```

## 16.6 Samples

Run all current release workflow samples:

```bash
dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj \
  -c Release --no-build

dotnet run --project samples/Raffinert.Consistency.DependencyMaintenanceSample/Raffinert.Consistency.DependencyMaintenanceSample.csproj \
  -c Release --no-build
```

## 16.7 Formatting

```bash
dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

Do not auto-format unrelated files unless `dotnet format` identifies a real changed-file issue.

---

# 17. Search-based anti-regression checks

Run these before declaring completion.

## 17.1 Runtime remains sealed

Search for the declaration and confirm:

```text
public sealed partial class ConsistencyRuntime : IConsistencyRuntime
```

There must not be a non-sealed public `ConsistencyRuntime` declaration.

## 17.2 No virtual mocking hooks

Search changed runtime files for new `virtual` methods.

Expected result for this task:

```text
none
```

## 17.3 Interface remains narrow

Inspect `IConsistencyRuntime.cs`.

It must contain exactly these operation names:

```text
Evaluate
Materialize
Materialize
```

No mutation/planning/diagnostic members.

## 17.4 DI alias does not create a runtime

Search the `IConsistencyRuntime` registration.

It must resolve:

```csharp
GetRequiredService<ConsistencyRuntime>()
```

and must not call:

```csharp
CreateRuntime()
```

## 17.5 Public API baseline placement

Confirm new `IConsistencyRuntime` entries are in:

```text
PublicAPI.Unshipped.txt
```

and not prematurely promoted to:

```text
PublicAPI.Shipped.txt
```

## 17.6 Published RC remains untouched

Confirm no task changed:

```text
v0.2.0-rc.1 tag
0.2.0-rc.1 historical changelog section
published package contents
```

A future release-preparation task may choose the next package version separately.

---

# 18. Acceptance criteria

Do not report completion unless every item below is true.

```text
[ ] Public IConsistencyRuntime exists in Raffinert.Consistency Core package.
[ ] IConsistencyRuntime exposes exactly Evaluate + targeted Materialize + object Materialize.
[ ] ConsistencyRuntime remains sealed.
[ ] Existing runtime methods remain non-virtual.
[ ] ConsistencyRuntime implements IConsistencyRuntime directly.
[ ] Existing concrete ConsistencyRuntime API remains unchanged.
[ ] AddRaffinertConsistency<TDbContext> still creates exactly one scoped ConsistencyRuntime.
[ ] IConsistencyRuntime resolves to that exact same scoped concrete runtime instance.
[ ] Concrete ConsistencyRuntime still resolves from DI.
[ ] Same-scope identity test passes.
[ ] Cross-scope lifetime test passes.
[ ] Application-style IConsistencyRuntime.Materialize + ordinary SaveChangesAsync integration test passes.
[ ] Core interface Evaluate/Materialize tests pass on net8.0.
[ ] Core interface Evaluate/Materialize tests pass on net10.0.
[ ] EF test suite passes on net10.0.
[ ] PublicAPI.Unshipped contains the new Core interface entries.
[ ] PublicAPI.Shipped is not rewritten for this next-release API.
[ ] README recommends IConsistencyRuntime for ordinary EF application services.
[ ] Advanced docs continue to use concrete ConsistencyRuntime where advanced APIs are needed.
[ ] Consumer skill/recipes distinguish narrow application interface from full runtime.
[ ] CHANGELOG records the change under Unreleased rather than rewriting 0.2.0-rc.1.
[ ] No production mocking-library dependency was introduced.
[ ] Full Release build has 0 warnings and 0 errors.
[ ] Core net8 tests all pass.
[ ] Core net10 tests all pass.
[ ] EF net10 tests all pass.
[ ] All samples pass.
[ ] dotnet format --verify-no-changes passes.
```

---

# 19. Required completion report

The implementation agent must report all of the following, not merely “done”:

```text
1. Final implementation commit SHA.
2. Exact IConsistencyRuntime public members.
3. Confirmation ConsistencyRuntime is still sealed.
4. Confirmation no runtime method was made virtual for mocking.
5. Exact EF DI alias registration used.
6. Same-scope concrete/interface identity test result.
7. Cross-scope lifetime test result.
8. Application-style interface Materialize + SaveChanges test result.
9. Core net8 test count/result.
10. Core net10 test count/result.
11. EF net10 test count/result.
12. Sample results.
13. dotnet format result.
14. Core PublicAPI.Unshipped entries added.
15. Confirmation Core PublicAPI.Shipped was not rewritten for the new API.
16. Documentation files updated.
17. Confirmation no package version/tag/publication action was performed.
```

If any acceptance criterion is not satisfied, report the task as incomplete and state the exact blocker.

---

# 20. Final intended architecture

After this task, the common application path should be:

```text
EF application service
        |
        v
IConsistencyRuntime
        |
        | same scoped object
        v
ConsistencyRuntime  <---->  ConsistencyEfCoreSession<TDbContext>
        |                         |
        |                         v
        +-----------------> SaveChanges interceptor
                                  |
                                  v
                              database
```

The abstraction boundary is deliberately narrow:

```text
ordinary application service:
    IConsistencyRuntime
        Evaluate
        Materialize

advanced consistency engine code:
    ConsistencyRuntime
        mutations
        relations
        planning
        diagnostics
        advanced transaction APIs
```

That is the design. Do not widen it accidentally.