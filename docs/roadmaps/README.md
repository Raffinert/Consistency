# Roadmaps

- **ACTIVE IMPLEMENTATION PLAN — Tasks 256–263:** harden the dependency DAG before the first public release by making `CompiledDependencyGraph` the authoritative runtime topology, retaining semantic edge metadata for projected upstream mapping, removing redundant runtime topology reconstruction, replacing `CaptureState` fixed-point convergence with one topological pass, strengthening deterministic cycle diagnostics, and proving the refactor with deep-chain/diamond/projected/randomized tests plus benchmarks and exact-head CI: [Tasks 256–263](../codex-plan-compiled-dag-runtime-authority-and-traversal-hardening.md)
- **PAUSED RELEASE-PREP PLAN — Tasks 247–255:** this plan is intentionally paused because runtime/architecture work is being added before the first release. Do not continue RC verification/publication from the old baseline. After Tasks 256–263 complete, restart release preparation from the new verified DAG-hardening final SHA: [Tasks 247–255](../codex-plan-0.1.0-rc.1-release-preparation.md)
- Tasks 237–243 completed the external consumer discovery structural-admission, scope, overlay, durability, and manual-UoW closeout. The verified feature baseline is `197913a329f6de006798d89b4bfb3eb428de99e9`, with exact-head GitHub Actions CI run 288 (`35216826855`) successful: [Tasks 237–243](../codex-plan-external-consumer-discovery-post-ccfb24f-gap-closeout.md)
- Tasks 220–228 implemented the broader external-consumer-discovery structural-admission and verification wave at `e3d050fa0fe6a62422da1ce261f4183bf66e347e`, reached remote `main` with green CI at `ccfb24fd485488d5680271545232e7ed6f26f031`, and were completed by the narrower Tasks 237–243 gap closeout: [Tasks 220–228](../codex-plan-external-consumer-discovery-post-96c076d-verification-and-closeout.md)
- Tasks 211–219 remain the superseded structural-admission implementation wave; the post-`96c076d` follow-up was the broader proof/closeout wave and is now superseded by the completed gap work plus docs-only Tasks 244–246.
- Tasks 202–210 were **partially implemented** at `1d4487fec60da7cf24567ce74efcaa7b8027500a`; that exact commit has green GitHub Actions CI and correctly separates `CoverageAdmission` from `IAddedMutation`, but it added no regression tests and left structural relation/projection patch scope, pending-admission dedupe, generic evaluation closure, and the broader proof matrix incomplete. Superseded by later structural-admission and closeout waves: [Tasks 202–210](../codex-plan-external-consumer-discovery-hardening-completion.md)
- Tasks 192–201 were **partially implemented** at `730de44756aeced16af8b7bb011ab9e2ed4e0374`; that exact commit has green GitHub Actions CI and includes useful policy/scope/EF-metadata/materialization-rollback hardening, but it left structural `CoverageAdmission` semantics, full evaluation closure, and most required regression proof incomplete. Superseded by later waves: [Tasks 192–201](../codex-plan-external-consumer-discovery-hardening-and-proof.md)
- Implemented mutation-driven external consumer discovery for direct reference-navigation dependencies at `b57b4ba98922abbb61c2a86bcdc3b81adf377897`; later waves hardened structural admission, scope substitution, evaluation closure, overlay behavior, durability, and exact-head proof: [Tasks 182–191](../codex-plan-external-consumer-discovery-and-incomplete-graph-resolution.md)
- Completed domain-neutral executable dogfooding example for dependency maintenance in an anemic EF model with local
  proof at `8da58b4` (remote pipelines intentionally not checked):
  [Tasks 167–173](../codex-plan-anemic-dependency-maintenance-dogfooding-example.md)
- Completed explicit source-member dependency declarations for opaque source-derived computations with local release proof at
  `9a5fc06ab8344635552e11946c3631fbc9cb62b2` (remote pipelines intentionally not checked):
  [Tasks 174–181](../codex-plan-explicit-source-derived-dependencies.md)
- Completed store-side referential-action safety and persistence-authority preflight with local and remote
  release proof at `d3458575c58f6f5c22594b2906526fb855aba3c7`:
  [Tasks 159–166](../codex-plan-store-side-referential-actions-and-persistence-authority.md)
- Completed generated UPDATE value safety and policy-aware capture of unmapped nested dependency targets with
  local and remote release proof at `5623971a98d222c90cf388dc3c0eb23724fbc75e`:
  [Tasks 152–158](../codex-plan-generated-update-values-and-unmapped-dependency-capture.md)
- Completed generated-value/fixup safety and object-set-scoped persistence member metadata with local and remote
  release proof at `7140d712fe6fcb2c314ba8364ef29fe5a21b23e6`:
  [Tasks 145–151](../codex-plan-generated-value-fixup-and-set-scoped-policy-safety.md)
- Completed authoritative scope coverage for reverse-navigation consumers and policy-aware manual/generated-key
  EF persistence with local and remote release proof at `fdf8c010917c07c0ea57737a7dc2b0bbdbd96dee`:
  [Tasks 137–144](../codex-plan-scope-safety-completion-and-manual-persistence.md)
- Completed authoritative data-scope safety for enforced invariants and materialized derived values with
  local and remote release proof at `009da18f01ab2e0eb4495d705628ea4708ab60fd`:
  [Tasks 130–136](../codex-plan-authoritative-scope-safety.md)
- Completed neutral order-fulfillment sample/test/documentation terminology migration with local and remote
  release proof: [Tasks 123–129](../codex-plan-neutral-order-fulfillment-domain.md)
- Completed product/package/namespace rename with local and remote proof: [Tasks 116–122 — `Raffinert.Consistency` rename v2](../codex-plan-rename-to-raffinert-consistency-v2.md)
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
