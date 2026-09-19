# Future roadmap

The order below expresses intended priorities, not committed release dates or fixed version assignments. v0.1 is defined separately in [the release scope](v0.1-release.md).

## Foundation: v0.1

Public Git definition + deployment bindings + parent context → owned-resource actions and inherited-capability checks → operator review → execution through a standalone engine → usable deployment configuration.

The initial fixture is the Decisions Office: a Redis-backed owned draft workspace with Archive, Library, Post Office, and Registrar inherited from its parent. Generated credentials where required by the deployment, explicit overrides, and restart-safe retry are part of this foundation.

## Next: unattended operation

Make the existing engine convenient to run without the SPA:

- CLI commands for validation, planning, execution, status, and configuration output.
- Explicit non-interactive inputs, stable exit codes, and machine-readable results.
- CI examples using a pinned institution commit, deployment bindings, parent context, and supplied provider connections.
- Complete automated setup from known inputs, including generated resource credentials, while retaining overrides.
- A repeatable way to approve and execute the same saved plan in separate stages.

Readiness gate: the non-UI engine path and durable state are reliable in v0.1. Automation must not depend on UI-only behavior or on silently supplying missing administrative access.

## Next: private sources and managed secrets

- Authenticated Git sources, beginning with the repository hosts in actual use.
- Credential references and integrations with the team's chosen secret manager.
- Clear separation of source-access, provider-administration, and institution-runtime credentials.
- Explicit credential rotation and configuration refresh workflows.

Readiness gate: agree the supported authentication mechanisms and secret lifecycle before expanding integrations.

## Later: institution lifecycle management

- Compare desired resource requirements with deployed state and show drift.
- Review changes when an institution definition advances to a new commit.
- Apply supported non-destructive updates with clear compatibility rules.
- Plan migrations and deliberate deprovisioning, including data-retention choices.
- Improve recovery from provider-side changes made outside the engine.

Readiness gate: define ownership and change semantics for each provider. Creation/retry support alone does not imply safe update or deletion support.

## Later: orchestration and service hosting

- Hosted API using the same engine.
- Queued runs, scheduled execution, and event-driven triggers.
- Multi-institution and environment management.
- Authentication, authorization, audit history, and approval policies appropriate to a shared service.
- Distributed execution coordination where deployment scale requires it.

Readiness gate: settle service tenancy and authorization boundaries before exposing remote execution.

## Demand-driven expansion

- MongoDB, S3, and Keycloak adapters for owned resources selected by concrete deployment bindings, including future parent Campus deployments.
- Additional providers justified by institution requirements and deployment choices.
- Broader tested S3 and provider-version compatibility.
- Reusable environment profiles and institution catalogs.
- Extensible provider packages with versioned contracts.
- Stronger source provenance and artifact verification where executable definitions require it.

## Principles that carry forward

- One engine serves the SPA, CLI, API, and automation.
- Institutions and Organizations remain technology-independent under the runtime constitution.
- Provisioning combines institutional requirements with separate deployment bindings and parent context.
- Inherited capabilities are resolved and verified; provisioning a child does not implicitly provision its parent.
- Defaults reduce setup work; overrides remain explicit and reviewable.
- Retries preserve identity and secrets; rotation and destructive changes are deliberate operations.
- Report unsupported requirements and incomplete work honestly.
- Add capabilities in response to actual institutions and deployment needs.
