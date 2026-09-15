# Codex implementation plan: replace procurement terminology with a neutral order-fulfillment domain

Status: **ACTIVE AFTER ROADMAP INDEX UPDATE**

Baseline when this plan was authored:

```text
9e37b46d0d89f30ff7f83da0e5797962f6379a37
refactor: remove redundant synchronization success flag
```

This plan is intentionally mechanical. It is written for a weaker coding agent that must not improvise terminology or redesign behavior.

The purpose of this wave is **not** to change Raffinert.Consistency semantics. The purpose is to remove the current purchase-order / invoice / goods-receipt / POIL / LGR / POLGR / GRN vocabulary from the active examples and dogfood tests and replace it with a neutral, familiar **order fulfillment / allocation** vocabulary.

The public library is a general incremental-consistency engine. Its primary examples should therefore read as a broadly understandable consistency problem rather than as an extraction of one procurement production model.

---

# Non-negotiable scope boundary

This is a **sample/test/documentation rename only**.

Do not change runtime behavior.

Do not change public library API.

Do not change package IDs, package version, target frameworks, or release semantics.

Do not introduce compatibility wrappers.

Do not add a new domain abstraction to `src/`.

The following directories are expected to remain behaviorally untouched:

```text
src/Raffinert.Consistency/**
src/Raffinert.Consistency.EntityFrameworkCore/**
```

The only acceptable `src/` change during this wave is **none**. If the compiler appears to require a source-library change, stop and inspect the rename; the sample/domain rename must not require a library change.

The checked-in public API baselines must remain byte-for-byte unchanged:

```text
src/Raffinert.Consistency/PublicAPI.Shipped.txt
src/Raffinert.Consistency/PublicAPI.Unshipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Shipped.txt
src/Raffinert.Consistency.EntityFrameworkCore/PublicAPI.Unshipped.txt
```

Hard stop:

```text
If `git diff -- src/` is non-empty because of this task, do not continue until the accidental product change is removed.
```

---

# Frozen replacement vocabulary

Use the following names exactly. Do not invent alternatives such as `Booking`, `StockReceipt`, `Resource`, `Demand`, `PO`, `GR`, or abbreviations.

## Entity/type rename map

| Old procurement name | New neutral name |
|---|---|
| `InvoiceLine` | `RequestLine` |
| `PurchaseOrderLine` | `OrderLine` |
| `PurchaseOrderInvoiceLine` | `Allocation` |
| `GoodsReceipt` | `Fulfillment` |
| `LinkedGoodsReceipt` | `AllocationFulfillment` |
| `PurchaseOrderLineGoodsReceipt` | `FulfillmentBalance` |
| `GrnMode` | `FulfillmentMode` |
| `RuleEvaluation` | **keep `RuleEvaluation`** |

The resulting mental model is:

```text
RequestLine
    ↓ has zero or more active
Allocation
    ↓ allocates to
OrderLine
    ↓ has fulfillment activity
Fulfillment

Allocation + Fulfillment
    ↓
AllocationFulfillment

OrderLine + Fulfillment
    ↓
FulfillmentBalance
```

## Member rename map

Apply these exact member renames in the sample/test domain models:

| Old member | New member |
|---|---|
| `InvoiceLineId` | `RequestLineId` |
| `PurchaseOrderLineId` | `OrderLineId` |
| `PurchaseOrderLine` | `OrderLine` |
| `PurchaseOrderInvoiceLineId` | `AllocationId` |
| `PurchaseOrderInvoiceLine` | `Allocation` |
| `GoodsReceiptId` | `FulfillmentId` |
| `PriceRate` | `UnitRate` |
| `IsServiceItemLine` | `IsServiceLine` |
| `LinkedQuantity` | `AllocatedQuantity` |
| `CapturedRate` | `CapturedRate` — keep |
| `ReservedQuantity` | `ReservedQuantity` — keep |
| `OrderNumber` | `OrderNumber` — keep |
| `ItemNumber` | `ItemNumber` — keep |
| `OrderedQuantity` | `OrderedQuantity` — keep |
| `QuantityReceived` | `TotalQuantity` |
| `QuantityAvailable` | `AvailableQuantity` |
| `QuantityMatched` | `AllocatedQuantity` |
| `QuantitySentToErp` | `ProcessedQuantity` |
| property `GrnMode` | property `FulfillmentMode` |
| `Quantity` on `Fulfillment` / `AllocationFulfillment` | keep `Quantity` |
| `Cancelled` | keep `Cancelled` |
| `IsDeleted` | keep `IsDeleted` |

Important: `Allocation.AllocatedQuantity` and `FulfillmentBalance.AllocatedQuantity` are different members on different types and are intentionally both called `AllocatedQuantity`.

Do not invert Boolean semantics while renaming `IsServiceItemLine` to `IsServiceLine`. `true`, `false`, and `null` retain exactly the same meaning as before.

## Derived/concept rename map

Use the following terminology in variables, `.Named(...)` keys, messages, comments, README examples, and the replacement documentation:

| Old term | New term |
|---|---|
| invoice | request |
| purchase-order line / PO line | order line |
| POIL | allocation |
| LGR | allocation fulfillment |
| POLGR | fulfillment balance |
| GRN | fulfillment |
| goods receipt / receipt | fulfillment |
| linked receipt | allocation fulfillment |
| matching receipt | matching fulfillment |
| received quantity | fulfilled quantity |
| available quantity when defined as `OrderedQuantity - fulfilled` | remaining quantity |
| link validity | allocation validity |
| ghost matching | allocation integrity |
| procurement scenario | order-fulfillment scenario |
| purchasing context | fulfillment context |

Do not blindly replace every English word `receipt`, `invoice`, or `order` repository-wide. Apply this vocabulary only to active example/test domain terminology. Historical roadmap/evidence documents are treated separately below.

---

# Semantic invariants that must not change

The rename must preserve the following exact behaviors.

## A. Request/allocation orphan integrity

Old meaning:

```text
deleted InvoiceLine must not retain an active POIL
```

New wording:

```text
deleted RequestLine must not retain an active Allocation
```

The predicate remains logically identical:

```text
!request.IsDeleted || activeAllocationCount == 0
```

## B. Allocation/fulfillment quantity balance

Old meaning:

```text
POIL.LinkedQuantity == sum(active LGR.Quantity)
```

New wording:

```text
Allocation.AllocatedQuantity == sum(active AllocationFulfillment.Quantity)
```

The tri-state mode behavior remains identical:

```text
FulfillmentMode.Unknown  -> RuleEvaluation.Unknown
FulfillmentMode.Disabled -> RuleEvaluation.Valid
FulfillmentMode.Enabled  -> compare quantities
```

## C. Fulfillment conservation balance

Old meaning:

```text
QuantityAvailable + QuantityMatched + QuantitySentToErp == QuantityReceived
```

New meaning with renamed members:

```text
AvailableQuantity + AllocatedQuantity + ProcessedQuantity == TotalQuantity
```

`IsServiceLine == true` still bypasses the conservation rule exactly as before.

`IsServiceLine == null` still produces `Unknown` exactly as before.

## D. Allocation-fulfillment bookkeeping existence

Old composite identity:

```text
(PurchaseOrderLineId, GoodsReceiptId)
```

New composite identity:

```text
(OrderLineId, FulfillmentId)
```

Every active `AllocationFulfillment` still requires a matching `FulfillmentBalance` for the same composite identity when fulfillment tracking is enabled.

## E. Legacy dependency chain

Preserve the same propagation shape but rename it to:

```text
Fulfillment membership/value
    -> FulfilledQuantity
    -> RemainingQuantity

OrderLine.UnitRate
    -> allocation rate dependency

RemainingQuantity + UnitRate
    -> AllocationValidity
    -> repair invariant
```

The current asymmetric severity behavior must remain identical:

```text
OrderedQuantity increase -> Dirty
OrderedQuantity decrease -> Invalid
Fulfillment cancellation -> invalidates fulfilled/remaining state
UnitRate change -> invalidates allocation validity
```

This task is not allowed to change those policies.

---

# Task 123 — baseline, inventory, and rename safety gate

## Goal

Start from the actual repository, account for any commits after the authoring baseline, and record every active procurement-domain occurrence before editing.

## 123.1 Check current HEAD

Run:

```bash
git rev-parse HEAD
```

The authoring baseline is:

```text
9e37b46d0d89f30ff7f83da0e5797962f6379a37
```

If HEAD differs, inspect all commits after that baseline before changing files:

```bash
git log --oneline 9e37b46d0d89f30ff7f83da0e5797962f6379a37..HEAD
```

Do not reset or discard newer work.

## 123.2 Prove baseline is green

Run at minimum:

```bash
dotnet restore Raffinert.Consistency.sln
dotnet build Raffinert.Consistency.sln -c Release --no-restore
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build
```

Do not begin a naming-only refactor from a red baseline.

## 123.3 Create an active-surface inventory

Run:

```bash
git grep -n -E 'PurchaseOrder|PurchaseLine|GoodsReceipt|InvoiceLine|PurchaseOrderInvoiceLine|LinkedGoodsReceipt|PurchaseOrderLineGoodsReceipt|\bPOIL\b|\bPOLGR\b|\bLGR\b|\bGRN\b|ghost matching|GhostMatching|procurement|PurchasingContext' -- \
  README.md CHANGELOG.md Raffinert.Consistency.sln .github samples tests docs \
  ':!docs/codex-plan-*.md' ':!docs/roadmaps/archive/**'
```

Also search lower-case prose variants:

```bash
git grep -ni -E 'purchase order|goods receipt|ghost matching|procurement' -- \
  README.md CHANGELOG.md .github samples tests docs \
  ':!docs/codex-plan-*.md' ':!docs/roadmaps/archive/**'
```

Expected important hits include at least:

```text
samples/Raffinert.Consistency.PurchaseOrderSample/**
tests/Raffinert.Consistency.Tests/GhostMatchingDogfoodTests.cs
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/EndToEndDomainTests.cs
samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Program.cs
README.md
docs/purchase-order-example.md
.github/workflows/ci.yml
.github/workflows/release-candidate.yml
Raffinert.Consistency.sln
CHANGELOG.md
```

There may be additional active hits. The agent must handle them; this list is not permission to ignore extra results.

## Acceptance

No code changed yet. Baseline is green and the current occurrence inventory is understood.

---

# Task 124 — rename the executable sample identity and domain model

## Goal

Rename the primary sample from a procurement-specific identity to an order-fulfillment identity, then apply the frozen entity/member map without changing behavior.

## 124.1 Rename project directory and csproj

Use `git mv`:

```bash
git mv samples/Raffinert.Consistency.PurchaseOrderSample \
       samples/Raffinert.Consistency.OrderFulfillmentSample

git mv samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.PurchaseOrderSample.csproj \
       samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj
```

Do not create a second sample and do not leave an old compatibility project.

The project remains `IsPackable=false`, targets `net10.0`, and keeps the same core project reference.

## 124.2 Rename namespaces

In every `.cs` file under the renamed sample:

```text
Raffinert.Consistency.PurchaseOrderSample
    ->
Raffinert.Consistency.OrderFulfillmentSample
```

This includes `.Domain`, `.Scenarios`, and `.Support` namespaces/usings.

## 124.3 Rename the domain file

Use:

```bash
git mv \
  samples/Raffinert.Consistency.OrderFulfillmentSample/Domain/ProcurementEntities.cs \
  samples/Raffinert.Consistency.OrderFulfillmentSample/Domain/OrderFulfillmentEntities.cs
```

Apply the exact entity/member rename map from the top of this document.

Target domain skeleton:

```csharp
internal enum FulfillmentMode { Unknown, Disabled, Enabled }
internal enum RuleEvaluation { Valid, Violation, Unknown }

internal sealed class RequestLine
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool IsDeleted { get; set; }
}

internal sealed class OrderLine
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string OrderNumber { get; init; } = "";
    public int ItemNumber { get; init; }
    public decimal OrderedQuantity { get; set; }
    public decimal UnitRate { get; set; }
    public bool? IsServiceLine { get; set; }
}

internal sealed class Allocation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid RequestLineId { get; init; }
    public Guid OrderLineId { get; init; }
    public required OrderLine OrderLine { get; init; }
    public decimal AllocatedQuantity { get; set; }
    public bool IsDeleted { get; set; }
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Enabled;
    public decimal ReservedQuantity { get; init; }
    public decimal CapturedRate { get; init; }
}

internal sealed class Fulfillment
{
    // preserve current identity-generation behavior
    public long Id { get; init; }
    public string OrderNumber { get; init; } = "";
    public int ItemNumber { get; init; }
    public decimal Quantity { get; set; }
    public bool Cancelled { get; set; }
}

internal sealed class AllocationFulfillment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid AllocationId { get; init; }
    public required Allocation Allocation { get; init; }
    public long FulfillmentId { get; set; }
    public decimal Quantity { get; set; }
    public bool IsDeleted { get; set; }
}

internal sealed class FulfillmentBalance
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid OrderLineId { get; init; }
    public required OrderLine OrderLine { get; init; }
    public long FulfillmentId { get; init; }
    public decimal TotalQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }
    public decimal AllocatedQuantity { get; set; }
    public decimal ProcessedQuantity { get; set; }
    public FulfillmentMode FulfillmentMode { get; set; } = FulfillmentMode.Enabled;
}
```

Important: preserve the existing static `Interlocked.Increment` implementation for `Fulfillment.Id`; the skeleton above abbreviates that detail. Do not accidentally change identity behavior.

## 124.4 Do not perform a repository-wide global replace

The following are examples/domain names, not product concepts. Restrict symbol replacement to sample/tests/docs identified in this roadmap.

No core source file should change.

## Acceptance

The renamed sample domain compiles far enough for compiler errors to point only at scenario files that still use old domain names.

Commit boundary:

```text
refactor: rename sample domain to order fulfillment
```

---

# Task 125 — rewrite scenarios and durable definition keys with no semantic drift

## Goal

Remove all POIL/LGR/POLGR/GRN/ghost-matching vocabulary from executable scenario code while preserving the exact mutation waves, expected states, causal detail assertions, and plan/commit behavior.

## 125.1 Scenario class/file renames

Use `git mv`:

```text
Scenarios/LegacyDependencyScenarios.cs
    -> Scenarios/DependencyPropagationScenarios.cs

Scenarios/GhostMatchingScenarios.cs
    -> Scenarios/AllocationIntegrityScenarios.cs

Scenarios/PrecommitGuardScenarios.cs
    -> Scenarios/PrecommitValidationScenarios.cs
```

Rename classes accordingly:

```text
LegacyDependencyScenarios -> DependencyPropagationScenarios
GhostMatchingScenarios    -> AllocationIntegrityScenarios
PrecommitGuardScenarios   -> PrecommitValidationScenarios
```

Update `Program.cs` to invoke exactly these three classes.

Final success message:

```text
All order-fulfillment scenarios passed.
```

## 125.2 Dependency propagation scenario

Use variable names:

```text
lines
fulfillments
allocations
matchingFulfillments
fulfilledQuantity
remainingQuantity
unitRate
allocationValidity
allocationInvariant
```

The definitions should read conceptually like:

```csharp
var fulfilledQuantity = model.Derived(lines)
    .Using(matchingFulfillments)
    ...
    .Compute((_, rows) => rows.Sum(x => x.Quantity))
    .Named("fulfilled-quantity");

var remainingQuantity = model.Derived(lines)
    .Using(fulfilledQuantity)
    ...
    .Compute((line, fulfilled) => line.OrderedQuantity - fulfilled)
    .Named("remaining-quantity");

var unitRate = model.Derived(lines)
    ...
    .Compute(x => x.UnitRate)
    .Named("unit-rate");

var allocationValidity = model.Derived(allocations)
    .Using(x => x.OrderLine, remainingQuantity, unitRate)
    ...
    .Compute((allocation, remaining, rate) =>
        allocation.ReservedQuantity <= remaining &&
        allocation.CapturedRate == rate)
    .Named("allocation-validity");
```

Do not change `DependencySeverity` choices or repair scheduling.

Change sample literal values such as:

```text
PO-100 -> ORDER-100
PO-1   -> ORDER-1
```

so no hidden PO abbreviation remains in active example output/data.

## 125.3 Allocation integrity scenario object-set names

Use these object-set definition keys:

```text
request-lines
order-lines
allocations
fulfillments
allocation-fulfillments
fulfillment-balances
```

Use these relation/concept names as the baseline vocabulary:

```text
request-active-allocations
order-line-active-allocations
allocation-active-fulfillments
order-line-fulfillment-balances
allocation-fulfillment-matching-balance
active-allocation-count
deleted-request-allocation-integrity
deleted-request-allocation-integrity-invariant
allocation-fulfillment-quantity
allocation-quantity-balance
allocation-quantity-balance-invariant
fulfillment-balance-conservation
fulfillment-balance-conservation-invariant
matching-fulfillment-balance-count
allocation-fulfillment-balance-existence
allocation-fulfillment-balance-existence-invariant
```

The exact strings above intentionally replace old durable/sample definition keys. There is no compatibility requirement for sample durable identities because these are not published product contracts.

## 125.4 Case/property naming

Rename the `Cases` collections and helpers so there are no acronym containers such as `Poils`, `Polgrs`, `LgrCases`.

Use:

```text
Requests
Lines
Allocations
Fulfillments
AllocationFulfillments
FulfillmentBalances
RequestCases
AllocationCases
FulfillmentBalanceCases
AllocationFulfillmentCases
RemovableFulfillmentBalance
RemovalAllocationFulfillment
```

Helper methods:

```text
InvoiceCase -> RequestCase
PoilCase    -> AllocationCase
PolgrCase   -> FulfillmentBalanceCase
LgrCase     -> AllocationFulfillmentCase
```

Update scenario display text. Examples:

```text
A1 deleted request with no allocation
A2 deleted request with deleted allocation
A3 deleted request with active allocation
B1 allocated quantity equals fulfillment total
B2 allocated quantity differs from fulfillment total
C1 normal line balanced
C2 normal line unbalanced
D1 allocation fulfillment has corresponding balance
D2 allocation fulfillment has no corresponding balance
```

Keep the A/B/C/D identifiers if useful for comparing old proof matrices. The human-readable suffix must use new terminology.

## 125.5 Precommit validation scenario

Rename every guard `.Named(...)` string and scenario label using the same vocabulary.

Examples:

```text
guard-invoices            -> guard-requests
guard-poils                -> guard-allocations
guard-active-poils         -> guard-active-allocations
guard-active-poil-count    -> guard-active-allocation-count
guard-quantity-poils       -> guard-quantity-allocations
guard-quantity-lgrs        -> guard-allocation-fulfillments
guard-polgrs               -> guard-fulfillment-balances
guard-matching-polgr       -> guard-matching-fulfillment-balance
```

Never use `poil`, `polgr`, `lgr`, or `grn` as variable names after this task.

## 125.6 Preserve plan semantics

`PlanDetailed` scenarios intentionally mutate domain objects before planning and sometimes manually restore rejected object state. Preserve those sequences exactly.

Do not “clean up” these scenarios by introducing cloning, transaction abstractions, automatic rollback, or a new helper API.

## Acceptance

Run:

```bash
dotnet run --project \
  samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj \
  -c Release
```

All existing scenario counts/outcomes must still pass.

Commit boundary:

```text
refactor: neutralize order fulfillment scenario terminology
```

---

# Task 126 — neutralize dogfood tests and EF examples

## Goal

Remove procurement terminology from active tests/examples outside the renamed executable sample.

## 126.1 Core dogfood test rename

Rename:

```bash
git mv \
  tests/Raffinert.Consistency.Tests/GhostMatchingDogfoodTests.cs \
  tests/Raffinert.Consistency.Tests/AllocationIntegrityDogfoodTests.cs
```

Rename class:

```text
GhostMatchingDogfoodTests -> AllocationIntegrityDogfoodTests
```

Apply this local test-type map:

```text
Poil  -> Allocation
Lgr   -> AllocationFulfillment
Polgr -> FulfillmentBalance
```

Apply member map:

```text
PoilId              -> AllocationId
PurchaseOrderLineId -> OrderLineId
GoodsReceiptId      -> FulfillmentId
LinkedQuantity      -> AllocatedQuantity
```

Rename test methods to readable names. At minimum:

```text
Poil_lgr_sum_reports_precommit_violation_and_discard_preserves_runtime
    ->
Allocation_fulfillment_sum_reports_precommit_violation_and_discard_preserves_runtime

Composite_bookkeeping_relation_retargets_in_planned_final_state
    ->
Composite_fulfillment_balance_relation_retargets_in_planned_final_state
```

Rename `.Named(...)` strings consistently:

```text
poils                 -> allocations
lgrs                  -> allocation-fulfillments
poil-lgrs             -> allocation-fulfillments
lgr-sum               -> fulfillment-sum
poil-balanced         -> allocation-balanced
poil-balance-invariant -> allocation-balance-invariant
retarget-lgrs         -> retarget-allocation-fulfillments
polgrs                -> fulfillment-balances
matching-polgr        -> matching-fulfillment-balance
matching-polgr-count  -> matching-fulfillment-balance-count
lgr-existence-invariant -> allocation-fulfillment-existence-invariant
```

Keep test assertions/plan calls unchanged in meaning.

## 126.2 EF end-to-end domain tests

In:

```text
tests/Raffinert.Consistency.EntityFrameworkCore.Tests/EndToEndDomainTests.cs
```

rename:

```text
PurchaseLine      -> OrderLine
GoodsReceipt      -> Fulfillment
PurchasingContext -> FulfillmentContext
GoodsReceipts     -> Fulfillments
scenario.Receipts -> scenario.Fulfillments
scenario.Received -> scenario.Fulfilled
```

Rename test methods:

```text
Goods_receipt_update_and_cancellation_affect_only_matching_purchase_order_line
    ->
Fulfillment_update_and_cancellation_affect_only_matching_order_line

Ef_adapter_drives_the_same_goods_receipt_dependency_flow
    ->
Ef_adapter_drives_the_same_fulfillment_dependency_flow
```

Replace in-memory database prefix:

```text
purchasing- -> fulfillment-
```

Replace PO-looking string data:

```text
PO-1     -> ORDER-1
PO-2     -> ORDER-2
PO-guard -> ORDER-guard
PO       -> ORDER
```

Do not change EF behavior, test coverage, or assertions.

## 126.3 EF sample

In:

```text
samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Program.cs
```

rename the demo entity:

```text
PurchaseOrderLine -> OrderLine
```

Use more general fulfillment terminology:

```text
ReceivedQuantity  -> FulfilledQuantity
AvailableQuantity -> RemainingQuantity
```

The deterministic mirror remains:

```text
RemainingQuantity = OrderedQuantity - FulfilledQuantity
```

Rename variables:

```text
available    -> remaining
availability -> nonNegativeRemaining
```

Update messages:

```text
Rejected invalid fulfillment before SQL; database and runtime remain unchanged.
Persisted RemainingQuantity=...; runtime version=...
```

Keep the sample project name `Raffinert.Consistency.EntityFrameworkCore.Sample`; it is already product-neutral.

## 126.4 Search all active tests

Run:

```bash
git grep -n -E 'PurchaseOrder|PurchaseLine|GoodsReceipt|InvoiceLine|\bPOIL\b|\bPOLGR\b|\bLGR\b|\bGRN\b|GhostMatching|ghost matching|PurchasingContext' -- tests samples/Raffinert.Consistency.EntityFrameworkCore.Sample
```

Expected result: **no hits**.

If extra hits exist, rename them using the frozen vocabulary without changing behavior.

## Acceptance

Run both test projects:

```bash
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0
```

Commit boundary:

```text
refactor: neutralize dogfood and ef example domain names
```

---

# Task 127 — rewrite active README/docs and archive procurement-specific historical evidence

## Goal

Make the public-facing documentation teach Raffinert.Consistency through order fulfillment/allocation terminology while preserving historical production-analysis evidence as history.

## 127.1 Rename the main example document

Use:

```bash
git mv docs/purchase-order-example.md docs/order-fulfillment-example.md
```

New title:

```text
# Order fulfillment and allocation-validity flow
```

Document the graph as:

```text
Fulfillment membership/value -> FulfilledQuantity -> RemainingQuantity
OrderLine.UnitRate -----------------------------------> UnitRate
RemainingQuantity + UnitRate -------------------------> AllocationValidity
AllocationValidity -----------------------------------> repair invariant
```

The narrative must explain:

```text
OrderedQuantity increase -> Dirty
OrderedQuantity decrease -> Invalid
Fulfillment cancellation -> exact membership removal / invalidation
UnitRate change -> allocation validity invalidation
```

Run command must point to:

```text
samples/Raffinert.Consistency.OrderFulfillmentSample
```

## 127.2 README examples

Replace the purchase-order-specific teaching examples with the same vocabulary.

### Start with a relation

Use conceptual names such as:

```csharp
var requests = model.Objects<RequestLine>()
    .Key(x => x.Id);

var orderLines = model.Objects<OrderLine>()
    .Key(x => x.Id);

var candidates = model.Relation(requests, orderLines)
    .Where((request, line) =>
        request.OrderNumber == line.OrderNumber &&
        request.ItemNumber == line.ItemNumber);
```

Do not use `invoices`, `poLines`, `PurchaseOrderNumber`, or `PurchaseOrderLine` in the active README examples.

### Derived values

Use:

```text
Fulfillment -> FulfilledQuantity -> RemainingQuantity
```

rather than goods receipt / received / available wording.

### Projected upstream example

Use:

```text
Allocation.OrderLine
RemainingQuantity
UnitRate
AllocationValidity
```

### Mermaid diagram

Use:

```text
Fulfillment change
 -> FulfilledQuantity
 -> RemainingQuantity
 -> AllocationValidity
 -> invariant / repair
```

### Dirty vs Invalid example

Replace `PO quantity decreased` with `Order quantity decreased`.

### Documentation index

Change:

```text
End-to-end purchase order and goods receipt example
```

into:

```text
End-to-end order fulfillment and allocation example
```

and link to `docs/order-fulfillment-example.md`.

### Supported-domain list

It is acceptable to keep the word `procurement` in the broad sentence listing domains where Raffinert.Consistency can be useful. That is a product-applicability statement, not the identity of the example.

## 127.3 CHANGELOG

No package has been published yet, so rewrite unreleased example wording rather than preserving obsolete sample terminology.

For example:

```text
Added a compiling purchase-order/goods-receipt/link-validity vertical slice.
```

becomes:

```text
Added a compiling order-fulfillment/allocation-validity vertical slice.
```

Do not rewrite unrelated changelog entries.

## 127.4 Historical dogfooding documents

These two documents describe the original procurement production investigation and contain real source-system terms such as `GhostMatchingDetectionService`, POIL, LGR, POLGR, and GRN:

```text
docs/Ghost Matching Guard Dogfooding Plan.md
docs/ghost-matching-dogfooding-findings.md
```

Do **not** fake-neutralize their historical evidence.

Move them into the historical roadmap archive using `git mv`:

```text
docs/roadmaps/archive/Ghost Matching Guard Dogfooding Plan.md
docs/roadmaps/archive/ghost-matching-dogfooding-findings.md
```

Keep their contents materially unchanged. They are provenance, not current user-facing domain examples.

If any current docs link to them, either update the link to the archive path or remove the current navigation entry if it is no longer appropriate.

## 127.5 Other active docs

Search active docs excluding historical plans/archive:

```bash
git grep -ni -E 'purchase order|goods receipt|ghost matching|\bPOIL\b|\bPOLGR\b|\bLGR\b|\bGRN\b' -- docs \
  ':!docs/codex-plan-*.md' ':!docs/roadmaps/archive/**'
```

For every remaining hit, decide whether it is:

```text
A) current teaching/example terminology -> rewrite to frozen vocabulary
B) historical/source provenance -> move/leave under archive and do not falsify it
```

Do not rewrite old `docs/codex-plan-*.md` implementation plans. They document the development history.

## Acceptance

The current README and current non-historical docs present order fulfillment/allocation terminology consistently.

Commit boundary:

```text
docs: replace procurement examples with order fulfillment
```

---

# Task 128 — update solution and CI/RC project references

## Goal

Make build automation use the renamed sample path and prove no operational file still references the old sample identity.

## 128.1 Solution

In `Raffinert.Consistency.sln`, change only the sample display name/path:

```text
Raffinert.Consistency.PurchaseOrderSample
    -> Raffinert.Consistency.OrderFulfillmentSample

samples\Raffinert.Consistency.PurchaseOrderSample\Raffinert.Consistency.PurchaseOrderSample.csproj
    ->
samples\Raffinert.Consistency.OrderFulfillmentSample\Raffinert.Consistency.OrderFulfillmentSample.csproj
```

Preserve the existing project GUID:

```text
{D6A8D5D3-7FA9-4E11-93EF-5A75387B1E76}
```

Do not regenerate the solution or churn unrelated GUID/configuration sections.

## 128.2 CI workflow

In `.github/workflows/ci.yml`:

Rename the step:

```text
Run procurement dogfood scenarios
    ->
Run order fulfillment dogfood scenarios
```

Change the project path to:

```text
samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj
```

Do not change test/pack behavior.

## 128.3 Release-candidate workflow

Update the same sample path in `.github/workflows/release-candidate.yml`.

No release semantics should change.

## 128.4 Release documentation

If `docs/release-candidate-verification.md` or `RELEASING.md` contains the old sample path/name, update only that path/name.

Do not mix the separate future `alpha.1 -> rc.1` version cut into this naming refactor unless the user explicitly asks for release versioning in the same commit series.

## Acceptance

Run:

```bash
git grep -n 'Raffinert.Consistency.PurchaseOrderSample' -- \
  Raffinert.Consistency.sln .github README.md RELEASING.md docs samples tests
```

Expected result outside historical plans/archive: **no hits**.

Commit boundary:

```text
build: update order fulfillment sample references
```

---

# Task 129 — behavioral parity and residue proof

## Goal

Prove that this wave changed terminology only and did not alter library behavior or release surface.

## 129.1 Product-source diff hard stop

Run:

```bash
git diff 9e37b46d0d89f30ff7f83da0e5797962f6379a37 -- src/
```

Expected: **empty** for this roadmap, except if the baseline advanced before work began and newer unrelated source changes were already present. In that case compare against the actual implementation-start commit recorded by the agent.

Record the true start commit and use it for this check.

Public API files must have no diff attributable to this roadmap.

## 129.2 Full active terminology residue gate

Run:

```bash
git grep -n -E 'PurchaseOrder|PurchaseLine|GoodsReceipt|InvoiceLine|PurchaseOrderInvoiceLine|LinkedGoodsReceipt|PurchaseOrderLineGoodsReceipt|\bPOIL\b|\bPOLGR\b|\bLGR\b|\bGRN\b|GhostMatching|ghost matching|PurchasingContext|Raffinert\.Consistency\.PurchaseOrderSample' -- \
  README.md CHANGELOG.md Raffinert.Consistency.sln .github samples tests docs/order-fulfillment-example.md
```

Expected: **no hits**.

Then run:

```bash
git grep -ni -E 'purchase order|goods receipt|ghost matching' -- \
  README.md CHANGELOG.md .github samples tests docs/order-fulfillment-example.md
```

Expected: **no hits**.

Historical roadmap/archive documents are intentionally excluded from this gate.

A generic `procurement` word may remain only in README's list of supported problem domains. It must not identify the primary sample, scenario, test, or documentation flow.

## 129.3 Definition-key abbreviation gate

Run:

```bash
git grep -n -E '"[^"\n]*(poil|polgr|lgr|grn)[^"\n]*"' -- samples tests
```

Expected: **no hits**.

This prevents a superficial class rename that leaves durable/sample diagnostics full of old acronyms.

## 129.4 Build/test/sample gate

Run:

```bash
dotnet restore Raffinert.Consistency.sln
dotnet build Raffinert.Consistency.sln -c Release --no-restore

dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net8.0 --no-build
dotnet test tests/Raffinert.Consistency.Tests/Raffinert.Consistency.Tests.csproj -c Release -f net10.0 --no-build
dotnet test tests/Raffinert.Consistency.EntityFrameworkCore.Tests/Raffinert.Consistency.EntityFrameworkCore.Tests.csproj -c Release -f net10.0 --no-build

dotnet run --project samples/Raffinert.Consistency.OrderFulfillmentSample/Raffinert.Consistency.OrderFulfillmentSample.csproj -c Release --no-build
dotnet run --project samples/Raffinert.Consistency.EntityFrameworkCore.Sample/Raffinert.Consistency.EntityFrameworkCore.Sample.csproj -c Release --no-build

dotnet format Raffinert.Consistency.sln --no-restore --verify-no-changes
```

## 129.5 Pack/smoke gate

Although no package API changes are intended, run the existing pack and smoke flow because sample/project-path mistakes can break CI before release:

```bash
dotnet pack Raffinert.Consistency.sln -c Release --no-build -o artifacts/packages
```

Then run the package consumers exactly as CI does.

Do not publish packages as part of this roadmap.

## 129.6 Remote proof

Push the final implementation commit(s) and require normal main CI to pass.

Because this refactor touches the sample path used by the release-candidate workflow, manually run `release-candidate.yml` once after merge and require success before the later RC release cut.

Do not claim completion from local tests alone.

## Final commit

If a final cleanup commit is needed:

```text
test: prove neutral sample terminology and behavior parity
```

---

# Required commit sequence

Prefer this order:

```text
1. refactor: rename sample domain to order fulfillment
2. refactor: neutralize order fulfillment scenario terminology
3. refactor: neutralize dogfood and ef example domain names
4. docs: replace procurement examples with order fulfillment
5. build: update order fulfillment sample references
6. test: prove neutral sample terminology and behavior parity   # only if fixes/proof files are needed
7. docs: close neutral order fulfillment terminology roadmap
```

Do not collapse this into an unrelated product/API refactor.

---

# Explicit non-goals

Do not change:

```text
Raffinert.Consistency public API
relation semantics
dependency analysis
impact propagation
Dirty / Invalid meaning
invariant truth behavior
planning/commit lifecycle
EF transaction behavior
materialization behavior
package IDs
package version
release tag
NuGet publication
```

Do not add a generic domain layer to the product because the sample uses `Allocation` or `Fulfillment`.

Do not rename real library concepts such as:

```text
Relation
Derived
Invariant
MutationSet
PreparedImpactPlan
ConsistencyRuntime
ConsistencyUnitOfWork
```

Do not rewrite historical roadmap documents to pretend the old procurement dogfooding work never existed.

---

# Final completion checklist

Do not mark Tasks 123–129 complete unless every item is true:

```text
Primary executable sample is Raffinert.Consistency.OrderFulfillmentSample.
No active sample namespace contains PurchaseOrderSample.
ProcurementEntities.cs became OrderFulfillmentEntities.cs.
RequestLine, OrderLine, Allocation, Fulfillment, AllocationFulfillment, and FulfillmentBalance are used consistently.
FulfillmentMode replaces GrnMode in active examples/tests.
No POIL/LGR/POLGR/GRN variable, type, test, definition-key, or scenario label remains in active examples/tests.
GhostMatchingDogfoodTests became AllocationIntegrityDogfoodTests.
GhostMatchingScenarios became AllocationIntegrityScenarios.
LegacyDependencyScenarios became DependencyPropagationScenarios.
PrecommitGuardScenarios became PrecommitValidationScenarios.
EF end-to-end tests use OrderLine/Fulfillment terminology.
EF sample uses OrderLine/FulfilledQuantity/RemainingQuantity terminology.
README teaches RequestLine/OrderLine/Allocation/Fulfillment concepts instead of invoice/PO/goods-receipt concepts.
docs/order-fulfillment-example.md replaces docs/purchase-order-example.md.
Historical ghost-matching production evidence is archived, not falsified.
CHANGELOG unreleased sample wording uses order-fulfillment/allocation terminology.
Solution keeps the old sample project GUID while using the new display name/path.
CI and release-candidate workflows run the renamed sample.
No product source or public API baseline changed because of this roadmap.
Core net8 tests pass.
Core net10 tests pass.
EF net10 tests pass.
Both samples run.
Formatting passes.
Pack/package-consumer smoke tests pass.
Normal remote CI passes after the rename.
Manual release-candidate verification passes after the rename.
No packages are published by this roadmap.
```
