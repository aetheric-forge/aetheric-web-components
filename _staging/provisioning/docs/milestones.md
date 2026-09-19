# Milestones

Status: M1 merged; M2 loading/review implemented for review. M0 live deployment decisions remain open. No completion dates are committed. Each milestone closes when its evidence is recorded, not merely when code has been written.

## M0 — Scope and contract agreement

Deliverables:

- v0.1 scope, acceptance criteria, and future roadmap.
- The pinned ADR Campus Decisions definition and bindings as the concrete fixture, interpreted under the runtime constitution.
- A parent-context input and explicit separation of owned actions from inherited capability validation.
- Decisions on YAML validation, parent resolution, Redis/Workbench operations and fallback policy, secret storage, and configuration output.

Exit gate: the first institution's complete resource list can be explained and mapped to supported provider operations. Remaining unknowns do not require inventing the institution contract.

Current state: the Decisions definition and bindings are available at ADR Campus commit `4e398147f9a7cd8ba47258a8e416f3e86f404a93`. M0 remains open for parent-context verification, binding operations, and the other integration decisions in [the input model](input-model.md).

## M1 — Engine and repository foundation

Deliverables:

- .NET solution with a standalone engine, provider adapter boundaries, Blazor host, and tests.
- Contracts for source provenance, validation, plans, progress, outcomes, state, and secret references.
- A simulated provider and parent-context test double using the supplied Decisions artifacts.
- Build/test commands and initial CI.

Exit gate: a non-UI test host can plan and execute the example, observe failures, and retry. Engine dependencies contain no Blazor components or interactive prompts.

Dependencies: architectural scope from M0. Simulation can proceed against the supplied artifacts while deployment integration details are resolved; it does not satisfy live release verification.

Evidence: the standalone harness passes the failure/retry/repeat scenario against the pinned Decisions fixture. Release build and 19 automated tests pass; the Blazor host passes HTTP smoke checks. See [M1 evidence and limitations](m1-foundation.md). The full v0.1 success criteria are not claimed by this milestone.

## M2 — Public Git loading and plan review

Deliverables:

- Public Git source loading with commit pinning and definition validation.
- YAML loading and validation for the definition and bindings, combined with parent context to produce the engine’s plan.
- Blazor source/configuration/review workflow, including defaults and overrides.
- Actionable missing-input, unsupported-resource, and invalid-definition results.

Exit gate: SC-01, SC-02, and SC-03 pass against the actual institution. Reviewing a plan does not mutate provider infrastructure.

Dependencies: M1 and the YAML validation and parent-context decisions from M0.

Implementation: a documented first YAML profile and an explicit capability catalog support source/configuration review. Parent checks and execution remain simulated; the catalog does not certify live availability. See [M2 behavior and evidence](m2-loading-review.md). M0 remains open for actual parent access verification and provider deployment details.

## M3 — Real provisioning and credential handling

Deliverables:

- Workbench/Redis operations selected by the Decisions bindings, plus inherited capability checks.
- No implicit provisioning or mutation of parent-owned services.
- Existing-resource inspection and conflict handling.
- Secure credential generation, explicit overrides, and protected secret persistence.
- Live per-resource progress and results in the SPA.

Exit gate: SC-04, SC-05, and SC-08 pass against documented provider targets. A simulated run cannot satisfy this gate.

Dependencies: M2 and test infrastructure with suitable operator credentials.

Standalone increment: a Workbench provisioning abstraction, Redis workspace registration/verification, and a separate acceptance host are implemented. See [the standalone contract](standalone-workbench.md). This does not establish Decisions readiness or close M3; live parent capabilities and resource credential policy remain open.

## M4 — Resume and usable outputs

Deliverables:

- Durable run state, duplicate-run protection, cancellation, and restart/resume behavior.
- Retry behavior that preserves resource identity and credentials.
- Runtime configuration output validated with the actual institution.

Exit gate: SC-06, SC-07, SC-09, SC-10, and SC-11 pass. Record at least one partial-failure/restart/resume demonstration using real providers.

Dependencies: M3. State and secret abstractions are designed in M1; persistence should be implemented alongside M3 where required for credential handling.

Persistence foundation: local atomic checkpoints, per-plan process leases, encrypted secret persistence, and separate-process failure/restart/retry tests are implemented. See [durable-state behavior and evidence](durable-state.md). The web simulation remains in memory; real-provider restart evidence and runtime configuration output are still required to close M4.

## M5 — v0.1 release verification

Deliverables:

- Operator setup guide, supported provider matrix, permissions requirements, troubleshooting, and known limitations.
- Clean-checkout build/test verification and recorded integration evidence.
- Release notes and a versioned release candidate.

Exit gate: all SC-01 through SC-12 pass; the concrete institution can use the resulting configuration. Tag v0.1 only after the evidence is reviewed.

Dependencies: M4.

## Tracking convention

When implementation begins, link issues or pull requests to a milestone and applicable success-criteria IDs. Record evidence alongside each completed gate, including the institution commit, effective bindings, parent context, and provider versions. Update this document when scope changes; do not silently redefine a gate to match partial implementation.
