# Roadmaps

- **Active implementation plan:** add explicit source-member dependency declarations for opaque source-derived computations so reusable calculators retain normal freshness/routing guarantees: [Tasks 174–181](../codex-plan-explicit-source-derived-dependencies.md)
- **Blocked / resume next:** domain-neutral executable dogfooding example for dependency maintenance in an anemic EF model. Tasks 167–173 are blocked at `4dc439dc3787bfbc3df981b405d24a8ee3c8e9c9` until Tasks 174–181 provide the required explicit dependency API: [Tasks 167–173](../codex-plan-anemic-dependency-maintenance-dogfooding-example.md)
- Completed store-side referential-action safety and persistence-authority preflight with local and remote
  release proof at `d3458575c58f6f5c22594b2906526fb855aba3c7`:
  [Tasks 159–166](../codex-plan-store-side-referential-actions-and-persistence-authority.md)
- Completed generated UPDATE value safety and policy-aware capture of unmapped nested dependency targets with
  local and remote release proof at `5623971a98d222c90cf388dc3c0eb23724fbc75e`:
  [Tasks 152–158](../codex-plan-generated-update-values-and-unmapped-dependency-capture.md)
- Completed generated-value/fixup safety and object-set-scoped persistence member metadata with local and
  remote release proof at `7140d712fe6fcb2c314ba8364ef29fe5a21b23e6`:
  [Tasks 145–151](../codex-plan-generated-value-fixup-and-set-scoped-policy-safety.md)
- Completed authoritative scope coverage for reverse-navigation consumers and policy-aware manual/generated-key
  EF persistence with local and remote release proof at `fdf8c010917c07c0ea57737a7dc2b0bbdbd96dee`:
  [Tasks 137–144](../codex-plan-scope-safety-completion-and-manual-persistence.md)
- Completed authoritative data-scope safety for enforced invariants and materialized derived values with
  local and remote release proof at `009da18f01ab2e0eb4495d705628ea4708ab60fd`:
  [Tasks 130–136](../codex-plan-authoritative-scope-safety.md)
- Completed neutral order-fulfillment sample/test/documentation terminology migration with local and remote
  release proof: [Tasks 123–129](../codex-plan-neutral-order-fulfillment-domain.md)
- Completed product/package/namespace rename with local and remote release proof: [Tasks 116–122 — `Raffinert.Consistency` rename v2](../codex-plan-rename-to-raffinert-consistency-v2.md)
- Superseded rename draft that unnecessarily required NuGet publication preflight: [Tasks 116–122 — original rename plan](../codex-plan-rename-to-raffinert-consistency.md)
- Implemented EF Core consistency continuation with full integration proof green at `a072685fc087fc52382a04ae804cf3000f6619f3`; superseded by the product rename before formal closeout: [Tasks 109–115](../codex-plan-ef-core-consistency-continuation-v2.md)
- Partially implemented EF Core consistency plan superseded by the continuation after Task 100 landed with proof gaps: [Tasks 100–108](../codex-plan-ef-core-consistency-guard-and-materialization.md)
- Completed pre-commit guard proof wave: [Tasks 94–99](../codex-plan-precommit-guard-proof-completion.md)
- Completed pre-commit guard dogfood wave: [Tasks 87–93](../codex-plan-dogfood-precommit-guard-validation.md)
- Completed post-alpha production integration plan: [Tasks 80–86 — durable policy work and release baseline](../codex-plan-post-alpha-production-integration-v1.md)
- Completed weaker-model final alpha v3 plan: [Tasks 73–79](../codex-plan-weaker-model-final-alpha-v3.md)
- Partially implemented mechanical alpha-finish wave with remaining acceptance gaps superseded by later plans: [Tasks 67–72](../codex-plan-mechanical-alpha-finish.md)
- Partially implemented final-alpha proof wave with remaining acceptance gaps superseded by later plans: [Tasks 61–66](../codex-plan-final-alpha-proof-causal-patches-and-rc.md)
- Partially implemented alpha-proof/journaling wave with remaining acceptance gaps superseded by later plans: [Tasks 55–60](../codex-plan-alpha-proof-causal-journaling-and-outbox.md)
- Implemented binding-plan/EF hardening wave with remaining acceptance gaps superseded by later plans: [Tasks 48–54](../codex-plan-binding-impact-plans-and-causal-proof.md)
- Implemented pre-commit planning wave with acceptance gaps superseded by later plans: [Tasks 41–47](../codex-plan-precommit-impact-planning-and-causal-integrity.md)
- Completed projected-dependency integrity and alpha gate: [Tasks 34–40](../codex-plan-projected-dependency-integrity-and-alpha-gate.md)
- Implemented causal/bootstrap/cross-source wave with remaining acceptance gaps superseded by later plans: [Tasks 27–33](../codex-plan-causal-integrity-bootstrap-and-cross-source.md)
- Completed explainability and domain-validation plan: [Tasks 20–26](../codex-plan-explainability-and-domain-validation.md)
- Completed post-DAG hardening plan: [Tasks 14–19](../codex-plan-tasks-10-13.md)
- Architecture review: [post-safety-hardening review](../architecture-review-after-safety-hardening.md)
- `archive/`: historical context only; these files are not current instructions.

Preserve completed/implemented plans and the archive
when researching why earlier design decisions were made; use `docs/architecture.md` for current runtime contracts.
