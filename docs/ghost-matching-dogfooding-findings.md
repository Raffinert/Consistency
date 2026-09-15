# Ghost matching guard dogfooding findings

The sample now represents four production guard semantics as named Raffinert relations and derived values: deleted invoice/active-POIL integrity, POIL/LGR quantity balance, POLGR quantity conservation, and composite `(purchase order line, goods receipt)` bookkeeping existence. Scenarios distinguish `Valid`, `Violation`, and `Unknown`, and coordinated changes use one `MutationSet`.

## Deliberate gaps

The inspected `GhostMatchingDetectionService` proves two transition proxies: a POIL change requires a modified purchase-order line in no-GRN mode, and an LGR change requires a modified POLGR in GRN mode. The available source contains neither the quantity-adjustment implementation nor a final-state equation connecting POIL quantities to purchase-order-line bookkeeping fields. No synthetic `WasModified` or guessed quantity field was added.

Those rules are therefore not presented as final-state invariants. If production has no stronger equation, Raffinert needs a mutation-batch invariant that can evaluate old/new aggregate deltas and coordinated writes. The same facility should preserve the production net-zero keys: purchase-order line for no-GRN changes and `(purchase-order line, goods receipt)` for GRN changes.

## Framework evidence

- Composite equality across an LGR's POIL navigation and a POLGR works with an ordinary relation; no custom rematching code is needed.
- Incremental `Sum` and `Count` cover the aggregate rules.
- Three-valued domain decisions can be declarative derived values, but `Invariant.Must` is Boolean. Treating `Unknown` as success would conflate "not evaluated" with "valid", so the runner reports it explicitly.
- Runtime impact traces are useful for add/remove scenario evidence, while invariant truth is read from the post-mutation model.
