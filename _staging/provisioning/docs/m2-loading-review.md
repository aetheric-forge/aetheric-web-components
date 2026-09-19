# M2: definition loading and plan review

## Operator workflow

The Blazor host supports public GitHub loading and an offline bundled example. The source form accepts a repository URL, a branch/tag/commit, and separate definition/bindings paths. After loading, the operator can override deployment name, provider settings, and parent resolution paths without editing the institution definition.

The review table shows owned actions, inherited resources, parent checks, and all effective non-secret provider settings. A separate approval applies to the exact plan ID. A changed source, provider setting, deployment name, parent identity/revision, or advertised capability invalidates the review and approval. Reloading invalid input also clears the old plan. A late response from an earlier load cannot restore cleared inputs.

Execution remains simulated and requires a declared parent catalog. The catalog records which contracts the simulated parent provides and at which sources. Missing/mismatched entries block review. This is an explicit planning input, not evidence of access to a live parent.

## Public Git source adapter

`IDefinitionSource` separates source access from interpretation. `PublicGitHubSource` is the first implementation:

- Supports canonical HTTPS `github.com/owner/repository` URLs, with an optional `.git` suffix. No credentials, alternate hosts, query strings, or URL fragments.
- Resolves the selected revision through the public GitHub commits API, then reads both YAML files using that exact commit. A moving branch cannot change the already loaded bundle or reviewed plan.
- Records canonical repository, commit, path, and SHA-256 content hash for each document.
- Uses unauthenticated requests with redirects disabled. It does not use local `gh` credentials, clone repositories, traverse submodules, follow download URLs, or execute source code.
- Limits each document to 256 KiB, HTTP responses to 2 MiB, and a load to 30 seconds. Only relative `.yaml`/`.yml` paths with supported simple path segments are accepted.
- Returns structured errors for unsupported input, missing public files, rate limits, timeouts, and unsupported responses. Remote response bodies are not displayed as error messages.

API references: [GitHub repository contents](https://docs.github.com/en/rest/repos/contents#get-repository-content) and [GitHub commits](https://docs.github.com/en/rest/commits/commits#get-a-commit).

Public GitHub is the supported first source, not a claim of compatibility with all Git servers. Another host or private authentication should implement the source interface without entering the engine.

## First supported YAML profile

`InstitutionYamlReader` validates the existing ADR Campus artifact shape. This is the provisioner's documented first profile; it does not add a `schemaVersion` field or reinterpret the institution's `1.0.0` version as a schema version.

The reader requires one document per file and validates:

- Descriptor identity, display metadata, and numeric institution version.
- Domain, Organization, role, capability, resource, workflow, and policy sections, including stable lowercase slug IDs and section-local uniqueness.
- Domain and role capability references against declared capability IDs.
- Resource category and `owned`/`parent` ownership; provider fields do not belong in institutional sections.
- Unique parent dependency contracts and reasons; these are never silently ignored.
- Matching institution identity/version in deployment bindings, known binding targets, and parent-source bindings.
- `initialState.configuration` as a scalar map. Nested initial-state structures are not supported by this profile.
- Unknown fields, duplicate mapping keys, extra documents, explicit tags, anchors, aliases, excessive nesting, and excessive node counts are rejected.

Both files must have matching repository/commit provenance and matching content hashes. Original source text is retained along with the validated planning projection; institutional descriptions, policy, and initial configuration are not converted into provider actions or lost from the loaded source. Runtime configuration export remains M4.

Provider-specific setting semantics remain in adapters. Unknown providers or unsupported settings block review through the existing planner. Public bindings do not accept credential fields; the engine retains its secret-reference interface for host-provided secrets in later provider work.

## Application boundary

`ProvisioningReview` has no dependency on Blazor or the simulation library. It handles loading, configuration snapshots, parent-catalog completeness, review, approval, and dispatch to the engine. It rejects execution without approval, rejects edits during execution, and prevents superseded asynchronous loads from restoring stale state.

The engine's parent context now carries the catalog as immutable data. Plan identity includes the catalog in sorted order, in addition to its identity/revision and effective bindings. The actual resolver still determines whether capabilities can be used during execution.

The current host uses `SimulatedCatalogParentResolver`, `SimulatedWorkbenchProvider`, and in-memory state/secrets. A new circuit/process loses simulation state. Live Redis allocation, real parent probes, persistent secrets, and durable restart recovery are not implemented by M2.

## Validation

Automated tests cover public source revision pinning (branch, tag, commit), invalid URLs/paths, rate-limit/missing-file responses, file limits, institutional and binding validation, source hash validation, approval invalidation, missing parent capabilities, superseded loads, and unsupported providers. The original M1 tests and non-UI retry harness remain regression checks.

Use the build/test commands in [the README](../README.md). Tests use deterministic HTTP responses; normal CI does not depend on GitHub API availability. Chromium checks passed for the bundled load, overrides, approval/invalidation, simulated execution, missing parent context, and invalid source input. A live unauthenticated load of `adr-campus/main` resolved to `9441223f9e0ef444d07dd253a591577d79e44465`; loading that full commit reproduced the snapshot. The browser reported no page errors, and the mobile viewport fit without page-level horizontal overflow. These browser checks were run separately from CI.

## Remaining release gates

SC-01 through SC-03 are exercised for the supported source/profile and simulated parent context. This does not satisfy SC-04 through SC-11's live integration and durable recovery requirements. M0 still needs actual parent verification, Workbench/Redis allocation/fallback rules, protected durable secret storage, and runtime output integration before M3/M4 can close.
