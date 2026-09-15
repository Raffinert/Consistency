# Ghost matching guard dogfooding findings

The sample now represents four production guard semantics as named Raffinert relations and derived values: deleted invoice/active-POIL integrity, POIL/LGR quantity balance, POLGR quantity conservation, and composite `(purchase order line, goods receipt)` bookkeeping existence. Scenarios distinguish `Valid`, `Violation`, and `Unknown`, and coordinated changes use one `MutationSet`.

These families are also Boolean invariants over their tri-state results. `Violation` blocks; `Valid` and `Unknown` do not. Binding plans requested with `PlannedInvariantEvaluationMode.Affected` evaluate affected invariant sources in the reversible planned final state, so application and EF integrations can reject a batch before persistence. The evaluations and cache state are bound into the plan, and `Commit(plan)` installs them without rerunning predicates.

Discarding a plan restores Raffinert runtime state, but does not undo changes already made to application objects or an EF change tracker. The caller must rollback, reload, or reconcile that domain state before reusing it.

## Deliberate gaps

The inspected `GhostMatchingDetectionService` proves two transition proxies: a POIL change requires a modified purchase-order line in no-GRN mode, and an LGR change requires a modified POLGR in GRN mode. The available source contains neither the quantity-adjustment implementation nor a final-state equation connecting POIL quantities to purchase-order-line bookkeeping fields. No synthetic `WasModified` or guessed quantity field was added.

Those rules are therefore not presented as final-state invariants. The no-GRN proxy nets non-zero POIL add/delete deltas by `PurchaseOrderLineId`, never `InvoiceLineId`. The GRN proxy nets non-zero LGR add/remove deltas by `(PurchaseOrderLineId, GoodsReceiptId)` and exempts zero-quantity LGRs. If production has no stronger equation, a future mutation-batch invariant would need normalized mutation provenance, grouped old/new deltas, and companion mutations. This roadmap deliberately does not add that generic DSL.

## Framework evidence

- Composite equality across an LGR's POIL navigation and a POLGR works with an ordinary relation; no custom rematching code is needed.
- Incremental `Sum` and `Count` cover the aggregate rules.
- Three-valued domain decisions can be declarative derived values, but `Invariant.Must` is Boolean. Treating `Unknown` as success would conflate "not evaluated" with "valid", so the runner reports it explicitly.
- Runtime impact traces are useful for add/remove scenario evidence, while invariant truth is read from the post-mutation model.
- Evaluated binding plans close the pre-persistence validation gap for final-state invariants without dispatching callbacks during planning.

## Completed proof matrix

- Core tests cover affected-only selection, add/remove lifecycle, direct invariant dependencies, stale plans, domain drift, exception rollback, callback isolation, detail-level parity, and predicate-free plan installation.
- The executable sample covers orphan repair, LGR aggregate add/remove, service and GRN Unknown transitions, and composite receipt-key retargeting.
- SQLite tests cover stable-key rejection with explicit EF reload, successful database-first installation, and generated-key transaction commit/rollback paths.
- Packed .NET 8, .NET 10, and EF consumers compile and inspect affected invariant evaluations.

`HasInvariantViolations` is affected-plan data, not a global health scan. `Source` remains an in-process object reference; consumers needing durable identification use `SourceIdentity`. Applications—not Raffinert—own persistence rejection and transaction rollback.
