# Raffinert.Relations Dogfooding Plan — Production Ghost-Matching Rules

## 1. Goal

Expand `Raffinert.Relations.PurchaseOrderSample` so it exercises real purchase-order/invoice/goods-receipt consistency rules taken from the existing `GhostMatchingDetectionService`.

The purpose is **not** to reproduce the EF `ChangeTracker` guard implementation.

The purpose is to answer:

> Can the production consistency rules be represented naturally as relations, derived state, invariants, mutation batches, and impact propagation?

Where the answer is no, the sample must expose the missing capability explicitly rather than hiding it behind sample-specific orchestration.

The finished sample should therefore serve three purposes:

1. executable documentation of realistic procurement consistency;
2. regression coverage for Raffinert.Relations;
3. design pressure for missing framework capabilities.

---

# 2. Important modeling principle

Do not model EF implementation details as domain state unless they are themselves part of the business rule.

For example, this production rule:

```text
POIL changed
    =>
PurchaseOrderLine must be EntityState.Modified
```

is probably not the real domain invariant.

It is a defensive proxy for something closer to:

```text
active POIL quantities
    must agree with
PO-line quantity bookkeeping
```

Likewise:

```text
LinkedGoodsReceipt changed
    =>
POLGR must be EntityState.Modified
```

is evidence that bookkeeping must have been updated, not necessarily the fundamental domain equation.

Therefore dogfooding should proceed in this order:

```text
production guard
        ↓
identify semantic invariant
        ↓
model semantic invariant in Raffinert.Relations
        ↓
only retain transition-aware guard
when final-state semantics cannot express the requirement
```

This distinction is critical.

Do not add `EntityState` properties to the sample domain objects merely to make the old code easy to reproduce.

---

# 3. Keep the existing sample scenarios

Do not delete the current:

```text
receivedQuantity
availableQuantity
unitRate
linkValidity
linkValidityInvariant
```

flow.

Those scenarios test a different but important capability:

```text
PO / GR state
      ↓
derived PO state
      ↓
projected dependency
      ↓
persisted invoice/PO link validity
      ↓
repair/rematching
```

The Ghost Matching dogfood should sit next to that flow.

Refactor the sample from one large `Program.cs` into an executable scenario suite.

Suggested structure:

```text
samples/Raffinert.Relations.PurchaseOrderSample/

    Program.cs

    Domain/
        PurchaseOrderLine.cs
        InvoiceLine.cs
        PurchaseOrderInvoiceLine.cs
        GoodsReceipt.cs
        LinkedGoodsReceipt.cs
        PurchaseOrderLineGoodsReceipt.cs
        MatchingSettings.cs

    Model/
        ProcurementRelationModel.cs
        ProcurementModelHandles.cs

    Scenarios/
        ExistingLinkValidityScenarios.cs
        OrphanedPoilScenarios.cs
        NoGrnScenarios.cs
        GrnScenarios.cs
        QuantityInvariantScenarios.cs
        SettingResolutionScenarios.cs

    Support/
        Scenario.cs
        ScenarioRunner.cs
        ScenarioAssertions.cs
```

Do not introduce a test framework into the sample unless needed.

A small executable scenario runner is preferable.

---

# 4. Expand the sample domain

## 4.1 InvoiceLine

Add:

```csharp
internal sealed class InvoiceLine
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public bool IsDeleted { get; set; }
}
```

Its only initial purpose is the orphaned-POIL rule.

---

## 4.2 PurchaseOrderInvoiceLine

Replace or evolve the current `PurchaseOrderInvoiceLink`.

Suggested shape:

```csharp
internal sealed class PurchaseOrderInvoiceLine
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid InvoiceLineId { get; init; }

    public Guid PurchaseOrderLineId { get; init; }

    public decimal LinkedQuantity { get; set; }

    public bool IsDeleted { get; set; }
}
```

If the existing projected link-validity scenario still needs a direct PO-line reference, it is acceptable to additionally have:

```csharp
public required PurchaseOrderLine PurchaseOrderLine { get; init; }
```

Do not replace relation keys with navigation properties merely because projection is convenient.

The sample should dogfood both:

```text
ID/equality relations
and
direct-reference projected dependencies
```

---

## 4.3 GoodsReceipt

Keep the existing receipt but give it a stable identity:

```csharp
internal sealed class GoodsReceipt
{
    public long Id { get; init; }

    ...
}
```

Existing `OrderNumber`, `ItemNumber`, `Quantity`, and `Cancelled` behavior remains useful.

---

## 4.4 LinkedGoodsReceipt

Introduce the production concept explicitly:

```csharp
internal sealed class LinkedGoodsReceipt
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid PurchaseOrderInvoiceLineId { get; init; }

    public long GoodsReceiptId { get; init; }

    public decimal Quantity { get; set; }
}
```

Use runtime lifecycle add/remove for genuine creation/deletion.

Do not invent `EntityState`.

---

## 4.5 PurchaseOrderLineGoodsReceipt

Introduce:

```csharp
internal sealed class PurchaseOrderLineGoodsReceipt
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid PurchaseOrderLineId { get; init; }

    public long GoodsReceiptId { get; init; }

    public decimal QuantityReceived { get; set; }

    public decimal QuantityAvailable { get; set; }

    public decimal QuantityMatched { get; set; }

    public decimal QuantitySentToErp { get; set; }
}
```

This gives the sample the actual quantity equation already present in production.

---

## 4.6 PurchaseOrderLine

Extend it with:

```csharp
public bool? IsServiceItemLine { get; set; }
```

Use nullable state deliberately:

```text
true    service item
false   normal item
null    unresolved
```

Do not convert `null` to `false`.

The production guard explicitly avoids conclusions when required external information cannot be resolved.

---

## 4.7 GRN setting

Represent setting resolution explicitly:

```csharp
internal enum GrnMode
{
    Unknown,
    Disabled,
    Enabled
}
```

Prefer an explicit enum over `bool?` in the sample because the three semantic states matter.

The sample does not need to reproduce supplier/settings caches.

Those are data-resolution concerns outside Raffinert.Relations.

---

# 5. Register the expanded object graph

Add object sets:

```text
purchase-order-lines
invoice-lines
purchase-order-invoice-lines
goods-receipts
linked-goods-receipts
purchase-order-line-goods-receipts
```

All sets should be `.Named(...)` so dogfood scenarios also exercise durable identities.

Keep keys equivalent to production identities where practical.

---

# 6. Define the core relations

Create the following relations.

## 6.1 InvoiceLine -> POIL

Conceptually:

```text
InvoiceLine.Id == POIL.InvoiceLineId
&& !POIL.IsDeleted
```

Name:

```text
active-poils-by-invoice-line
```

This powers orphan detection.

---

## 6.2 PurchaseOrderLine -> POIL

Conceptually:

```text
PurchaseOrderLine.Id == POIL.PurchaseOrderLineId
&& !POIL.IsDeleted
```

Name:

```text
active-poils-by-po-line
```

This should later support No-GRN aggregate bookkeeping.

---

## 6.3 POIL -> LinkedGoodsReceipt

```text
POIL.Id == LGR.PurchaseOrderInvoiceLineId
```

Name:

```text
linked-receipts-by-poil
```

---

## 6.4 GoodsReceipt -> LinkedGoodsReceipt

```text
GoodsReceipt.Id == LGR.GoodsReceiptId
```

Name:

```text
receipt-links
```

Only add this if a scenario actually needs reverse traversal.

Do not create unused relations for architectural symmetry.

---

## 6.5 PurchaseOrderLine -> POLGR

```text
PurchaseOrderLine.Id == POLGR.PurchaseOrderLineId
```

Name:

```text
polgr-by-po-line
```

---

## 6.6 POLGR identity by `(PO line, goods receipt)`

The important production identity is:

```text
(PurchaseOrderLineId, GoodsReceiptId)
```

Ensure the sample can efficiently identify the corresponding POLGR for that logical pair.

Start with ordinary relation semantics.

Do not add a new composite-index feature unless the existing relation planner demonstrably cannot handle the equality pair.

---

# 7. Rule group A — orphaned POILs

Production semantics:

```text
InvoiceLine.IsDeleted
    =>
there must be no non-deleted POIL for that InvoiceLine
```

This is an excellent pure final-state invariant.

Implement:

```text
activePoilCount
    = Count(active-poils-by-invoice-line)
```

Then:

```text
invoiceLineIntegrity
    = !invoice.IsDeleted || activePoilCount == 0
```

Then invariant:

```text
Must(valid == true)
```

Suggested name:

```text
deleted-invoice-has-no-active-poils
```

Scenarios:

```text
A1 invoice active + active POIL                  valid
A2 invoice soft deleted + active POIL            invalid
A3 invoice soft deleted + POIL soft deleted      valid
A4 invoice soft deleted + multiple deleted POILs valid
A5 one surviving active POIL among deleted POILs invalid
```

Also perform A2/A3 as a single `MutationSet` where appropriate.

The interesting dogfood condition is:

```text
InvoiceLine.IsDeleted change
+
POIL.IsDeleted change
```

must propagate through the relation and invariant automatically.

No manual:

```text
invalidate invoice
refresh poil
check orphan
```

logic is allowed.

---

# 8. Rule group B — POIL linked-quantity invariant

For GRN mode, production already has:

```text
added POIL.LinkedQuantity
    ==
sum(LinkedGoodsReceipt.Quantity)
```

Model this as a permanent semantic relation rather than an "added entity only" rule.

Derived:

```text
linkedReceiptQuantity =
    linkedReceipts.Sum(x => x.Quantity)
```

Invariant:

```text
POIL.LinkedQuantity == linkedReceiptQuantity
```

Call it:

```text
poil-linked-receipt-quantity-balanced
```

This is deliberately stronger than checking only additions.

That is acceptable if it represents the intended domain invariant.

Before adopting it, verify that production permits an existing persisted POIL to temporarily violate this equation.

If it does, retain the production scope instead of silently strengthening the rule.

Scenarios:

```text
B1 LinkedQuantity = 10, LGR total = 10    valid
B2 LinkedQuantity = 10, LGR total = 9     invalid
B3 no LGR, LinkedQuantity = 0             valid
B4 no LGR, LinkedQuantity != 0            invalid
B5 multiple LGR rows sum correctly        valid
B6 LGR quantity changes                   invalidates derived total
```

---

# 9. Rule group C — POLGR quantity balance

This maps almost directly to the production equation:

```text
QuantityAvailable
+ QuantityMatched
+ QuantitySentToErp
==
QuantityReceived
```

Derived:

```csharp
accountedQuantity =
    polgr.QuantityAvailable
    + polgr.QuantityMatched
    + polgr.QuantitySentToErp;
```

Invariant:

```text
service item
OR
accountedQuantity == QuantityReceived
```

However unresolved service-item state must not be treated as normal-item state.

Use three-state result semantics:

```text
IsServiceItemLine == true
    => NotApplicable / valid

IsServiceItemLine == false
    => evaluate equation

IsServiceItemLine == null
    => Unknown / skip
```

Do not force `Unknown` into `true` or `false`.

If current Raffinert invariants cannot naturally represent:

```text
Valid / Violated / NotEvaluable
```

record that as a dogfood finding.

Do not hack around it with:

```csharp
isServiceItem != false || equation
```

because that makes `Unknown` indistinguishable from actual validity.

Scenarios:

```text
C1 normal line, balanced quantities           valid
C2 normal line, unbalanced quantities         invalid
C3 service line, unbalanced quantities        skipped
C4 unresolved service status                  skipped/unknown
C5 quantity field mutation breaks balance     affected
C6 second mutation restores balance           valid again
```

---

# 10. Rule group D — POIL requires corresponding POLGR

For GRN-enabled matching, a non-zero linked receipt implies that the bookkeeping row for:

```text
(PurchaseOrderLineId, GoodsReceiptId)
```

must exist.

This should be modeled semantically as:

```text
LinkedGoodsReceipt
    -> POIL
    -> PurchaseOrderLineId

LinkedGoodsReceipt.GoodsReceiptId
```

requires a matching:

```text
PurchaseOrderLineGoodsReceipt
```

for the same pair.

This is a strong dogfood scenario because it crosses multiple sets.

Do not implement the first version using dictionary lookups in scenario code.

Try to express it through relations/derived state.

Expected scenario matrix:

```text
D1 LGR + corresponding POLGR                       valid
D2 LGR + no corresponding POLGR                    invalid
D3 multiple LGR rows same PO/GR + one POLGR        valid
D4 POLGR removed while LGR survives                invalid
D5 LGR and POLGR removed in same MutationSet       valid
D6 LGR and POLGR added in same MutationSet         valid
```

If expressing the composite cross-object requirement becomes unnatural, stop and document exactly what relation/invariant capability is missing.

Do not add general framework functionality before that evidence exists.

---

# 11. Rule group E — No-GRN PO-line bookkeeping

This rule needs special care.

The current production guard only proves:

```text
POIL was added/deleted
    =>
PurchaseOrderLine must have been modified
```

unless added/deleted linked quantities net to zero for that PO line.

Do NOT reproduce this by adding:

```csharp
bool WasModified
```

to `PurchaseOrderLine`.

Instead, first find the actual production fields changed when:

```text
POIL.LinkedQuantity
```

is added or removed.

The implementation task must document the real equation in a comment before writing Raffinert code.

Target semantic model should look like:

```text
derived expected PO quantity bookkeeping
        =
aggregate of active POIL.LinkedQuantity

actual PO bookkeeping fields
        must equal
expected bookkeeping
```

The exact expression depends on the production adjustment logic and cannot be invented from the guard alone.

### Required dogfood property

The semantic aggregate must naturally make this case valid:

```text
+ POIL quantity 10
- POIL quantity 10
same PurchaseOrderLine

net change = 0
```

and also the copy/replace case:

```text
old invoice line
    POIL -10

new invoice line
    POIL +10

same PurchaseOrderLine

net = 0
```

The invoice line must therefore **not** be part of the grouping key.

That matches the production behavior.

Scenarios after the real equation is known:

```text
E1 added POIL + correct POL bookkeeping                 valid
E2 added POIL + stale POL bookkeeping                   invalid
E3 removed POIL + correct POL bookkeeping               valid
E4 removed POIL + stale POL bookkeeping                 invalid
E5 +10/-10 same POL                                     valid
E6 replacement across different InvoiceLines           valid
E7 +10/-9 same POL                                      invalid
E8 equal net changes on different POLs do not cancel   invalid
```

---

# 12. Rule group F — GRN adjustment/net-zero behavior

The production GRN guard contains another transition-oriented proxy:

```text
LGR added/deleted
    =>
corresponding POLGR must be adjusted
```

with exceptions for:

```text
all linked quantities == 0
```

and:

```text
net change for (PO line, goods receipt) == 0
```

Again, do not initially model "POLGR was modified."

First determine whether existing production fields provide a stronger semantic equation relating:

```text
LinkedGoodsReceipt quantities
```

to:

```text
POLGR.QuantityAvailable
POLGR.QuantityMatched
POLGR.QuantitySentToErp
POLGR.QuantityReceived
```

If such an equation exists, model that instead.

If no final-state equation exists and "must have been modified" is intentionally the rule, mark it as a genuine **mutation-batch invariant**.

This distinction is one of the most valuable outputs of the dogfood exercise.

Required transition scenarios:

```text
F1 added non-zero LGR + appropriate POLGR change
F2 added non-zero LGR + unchanged POLGR
F3 removed non-zero LGR + appropriate POLGR change
F4 removed non-zero LGR + unchanged POLGR
F5 zero-quantity LGR + unchanged POLGR
F6 +10/-10 LGR for same (POL, GR)
F7 +10/-9 LGR for same (POL, GR)
F8 equal changes for different GoodsReceiptId values must not net
```

---

# 13. Rule group G — GRN setting resolution

Do not bake GRN mode into model construction.

A runtime scenario must be able to represent:

```text
Enabled
Disabled
Unknown
```

Expected behavior:

```text
orphaned POIL rule
    always active

GRN Enabled
    run GRN semantics

GRN Disabled
    run No-GRN semantics

GRN Unknown
    skip both GRN and No-GRN dependent semantics
```

This is an important dogfood test for conditional domain rules.

Try the simplest explicit model first.

Do not build dynamic model recompilation per setting.

If Raffinert cannot cleanly express conditional activation of invariants based on another tracked value, record that as a framework gap.

---

# 14. Build a scenario runner around expected violations

Do not copy `GhostMatchingDetectionService` into the sample as a second implementation.

Each scenario should instead declare its expected semantic result.

Suggested shape:

```csharp
Scenario.Define("deleted invoice with live POIL")
    .Arrange(...)
    .Mutate(...)
    .ExpectViolation(GhostRule.OrphanedPurchaseOrderInvoiceLine);
```

Possible rule identifiers:

```csharp
internal enum GhostRule
{
    OrphanedPurchaseOrderInvoiceLine,
    PurchaseOrderLineQuantityMismatch,
    MissingLinkedGoodsReceipt,
    MissingPurchaseOrderLineGoodsReceipt,
    PurchaseOrderInvoiceLineQuantityMismatch,
    PurchaseOrderLineGoodsReceiptQuantityMismatch
}
```

Do not necessarily reuse production exception names if they encode implementation details such as "MissingAdjustment."

Prefer semantic names in the dogfood model.

At the end of every scenario print:

```text
PASS <scenario>
  expected: ...
  actual: ...
  impacts: ...
```

For selected invalid scenarios also render:

```csharp
RuntimeImpactTraceRenderer.Render(...)
```

This makes causal output part of the dogfood exercise.

---

# 15. Separate three kinds of outcomes

The scenario harness should explicitly distinguish:

```text
Valid
Violation
Unknown
```

Do not use only `bool`.

This matters for:

```text
GRN setting unresolved
service-item classification unresolved
```

Suggested sample abstraction:

```csharp
internal enum RuleEvaluation
{
    Valid,
    Violation,
    Unknown
}
```

This abstraction can live in the sample initially.

If it repeatedly fights the Raffinert invariant model, that becomes evidence for a potential core concept later.

---

# 16. Test mutation batching explicitly

Many real production scenarios involve several coordinated changes.

The sample must use one `MutationSet` for operations such as:

```text
soft-delete InvoiceLine
+
soft-delete its POILs
```

and:

```text
add POIL
+
add LinkedGoodsReceipts
+
update POLGR
```

and:

```text
remove old POIL
+
add replacement POIL
+
move linked quantities
```

The framework must reason over the final batch.

The sample should not artificially sequence these into multiple successful commits just to make invariants pass temporarily.

---

# 17. Add a production-like copy/replace scenario

This deserves its own scenario because the production code explicitly contains an exception for it.

Construct:

```text
InvoiceLine A
    POIL quantity 5

operation:
    soft-delete old POIL
    create InvoiceLine B
    create replacement POIL quantity 5
    both point to same PurchaseOrderLine
```

Expected No-GRN result:

```text
net PO-line quantity delta == 0
```

No PO quantity adjustment required.

Then change replacement quantity to `4`.

Expected:

```text
net delta != 0
```

Adjustment required / final semantic balance violated.

This is one of the best real-world tests of whether the library can replace orchestration code with domain semantics.

---

# 18. Add GRN replacement/net-zero scenario

Similarly test:

```text
old LGR:
    PO = P1
    GR = G1
    quantity = 5

new LGR:
    PO = P1
    GR = G1
    quantity = 5
```

as one mutation batch.

Expected:

```text
net (P1, G1) quantity delta == 0
```

Then repeat with:

```text
old G1
new G2
```

Even if quantities are equal, they must **not** net because the production key is:

```text
(PurchaseOrderLineId, GoodsReceiptId)
```

This is an important grouping/indexing dogfood case.

---

# 19. Run the sample in two layers

## Layer 1 — semantic invariants

These should be implemented first:

```text
deleted invoice -> no active POIL
POIL linked quantity == LGR quantity sum
POLGR accounting equation
LGR -> corresponding POLGR existence
existing link-validity/rematching graph
```

These should ideally require no manual invalidation orchestration.

## Layer 2 — transition-specific guards

Only after Layer 1 is working, address:

```text
POL must have been modified
POLGR must have been modified
net-zero mutation exceptions
```

For every transition rule ask:

```text
Can this be replaced by a stronger final-state invariant?
```

If yes, do that.

If no, record it as:

```text
MISSING CAPABILITY:
batch-aware / transition-aware invariant
```

Do not disguise the missing capability in helper code.

---

# 20. Expected framework gaps to actively look for

The dogfood exercise should explicitly investigate these potential gaps.

### Gap 1 — invariant evaluation during planning

The production guard runs before persistence.

Current Raffinert planning can predict impacts, but the sample must determine whether it can obtain:

```text
final planned invariant evaluation
```

before installing runtime state.

If not, document:

```text
Need a way to evaluate guard-style invariants against planned final runtime state.
```

Do not immediately design the API.

---

### Gap 2 — batch-aware transition invariants

Rules like:

```text
if POIL added/removed,
some correlated object must also change
```

depend on the mutation itself, not merely current state.

If semantic balance cannot replace them, document:

```text
Need model-level rules over normalized MutationSet / PreparedMutation.
```

---

### Gap 3 — Unknown / NotEvaluable invariant state

Production intentionally skips rules when prerequisite facts cannot be resolved.

If Raffinert forces every invariant into:

```text
true / false
```

record the requirement for:

```text
Valid / Violated / Unknown
```

rather than treating Unknown as valid.

---

### Gap 4 — cross-object composite existence

The LGR -> `(POL, GR)` -> POLGR rule may expose a need for cleaner multi-hop/composite dependency declaration.

Do not generalize until the sample proves the pain.

---

# 21. Acceptance criteria

The dogfood wave is complete when:

1. The current link-validity sample still passes.
2. All pure semantic production rules listed above have executable scenarios.
3. Orphaned-POIL behavior is completely represented declaratively.
4. POIL/LGR quantity balance is represented declaratively.
5. POLGR quantity balance is represented declaratively.
6. GRN Enabled / Disabled / Unknown behavior is explicitly exercised.
7. Service-item true / false / unknown behavior is exercised.
8. Copy-and-replace No-GRN net-zero behavior has a scenario.
9. `(PO line, GR)` net-zero behavior has a scenario.
10. Coordinated mutations are executed as single `MutationSet`s.
11. Invalid scenarios demonstrate causal output where useful.
12. No scenario manually calls a custom `InvalidateX`, `RefreshY`, or `RematchZ`.
13. No `EntityState` property is added to sample domain entities.
14. Any rule that cannot be modeled cleanly is listed as an explicit Raffinert capability gap.
15. No framework API is added solely to make the sample green without first documenting the failing business scenario.

---

# 22. Recommended implementation sequence

### Commit 1 — restructure sample

```text
sample: split procurement sample into domain model and scenarios
```

No behavior changes.

### Commit 2 — add production-shaped entities and relations

```text
sample: model invoice poil lgr and polgr relationships
```

No invariants yet.

### Commit 3 — orphan consistency

```text
sample: dogfood deleted invoice poil integrity
```

Implement scenarios A1–A5.

### Commit 4 — POIL/LGR quantity semantics

```text
sample: dogfood linked receipt quantity invariants
```

Implement B scenarios.

### Commit 5 — POLGR balance

```text
sample: dogfood goods receipt bookkeeping invariants
```

Implement C scenarios.

### Commit 6 — cross-object POLGR existence

```text
sample: dogfood linked receipt bookkeeping existence
```

Implement D scenarios.

### Commit 7 — No-GRN production quantity semantics

Only after finding the actual production adjustment equation.

```text
sample: model no-grn po line quantity bookkeeping
```

Implement E scenarios.

### Commit 8 — GRN net-change semantics

Only after determining whether a final-state equation can replace the Modified check.

```text
sample: model grn bookkeeping net changes
```

Implement F scenarios.

### Commit 9 — conditional settings

```text
sample: exercise grn and service-item unknown states
```

Implement G scenarios.

### Commit 10 — dogfood findings

Create:

```text
docs/dogfood-ghost-matching-findings.md
```

with only findings demonstrated by executable scenarios.

Categories:

```text
works naturally
works but API is awkward
requires manual orchestration
not currently expressible
candidate framework capability
```

Do not implement candidate framework capabilities in this commit.

---

# 23. Most important design constraint

The sample must answer:

> What would the domain consistency rule look like if EF ChangeTracker did not exist?

That is the useful test for Raffinert.Relations.

If the dogfood implementation ends up looking like:

```text
if POIL added
    inspect mutation list
    find POL
    verify POL was also changed

if LGR removed
    inspect mutation list
    find POLGR
    verify POLGR changed
```

then we have merely moved `GhostMatchingDetectionService` into another project.

The desired end state is closer to:

```text
POIL / LGR / POLGR relationships
              ↓
derived aggregate domain state
              ↓
declarative consistency invariants
              ↓
mutation automatically identifies affected sources
              ↓
violation / repair / explanation
```

Transition-specific checks should remain only where no equivalent semantic final-state invariant exists.

That distinction is the core value of this dogfooding exercise.