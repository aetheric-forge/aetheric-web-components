# Standalone Workbench provisioning

The available deployment baseline is Keycloak and existing infrastructure. Campus, Archive, Library, Post Office, and Registrar are not assumed deployed or usable. Keycloak availability does not establish those institutional capabilities.

This increment adds a Workbench provisioning abstraction over an existing standalone Redis database. It has no reference to ADR Campus or the runtime implementation, and does not deploy either. The existing Decisions simulation and its four required parent checks remain intact. A successful standalone workspace check is not a successful Decisions deployment.

## Boundaries

- `Aetheric.Provisioning.Workbench` supplies `WorkbenchProvider` and `IWorkbenchBackend`. The engine sees a Workbench provider, not Redis commands.
- `Aetheric.Provisioning.Workbench.Redis` implements workspace registration and staging verification with StackExchange.Redis 2.8.24, matching the version in the inspected runtime provider.
- The host injects an existing Redis database and a stable target identity. Connections/passwords remain host inputs and never appear in plans, checkpoints, or harness output. Changing the database/endpoint behind a target requires a new target identity and a new reviewed plan.
- This allocates a logical stage, not a Redis server, database, ACL user, or standalone HTTP service. Redis service credentials are supplied by the operator; no artificial resource password is generated. Workspace ACL isolation and credential generation are not claimed by this increment.

The binding requires `provider: workbench`, `backing: redis`, `target: <registered-target>`, `stage: <lowercase-slug>`, and `fallback: none`. Unknown settings, credentials embedded in bindings, inherited resources, and implicit in-memory fallback are rejected. The existing Decisions fixture's `fallback: in-memory` must be explicitly overridden for this adapter.

## Redis contract

The runtime's Redis staging provider uses `<stage>:data:<key>` hashes with `content` and `metadata` fields and `<stage>:lock:<key>` locks. This adapter verifies the hash representation using a unique temporary probe. It does not instantiate the runtime Workbench institution or certify its full API.

A versioned non-expiring registration key, `aetheric:workbench:v1:<sha256(stage)>`, records the environment, institution, resource, stage, and storage format. An atomic Lua claim accepts only an identical registration. A different owner, incompatible record, wrong Redis type, or expiring registration fails without replacing it. Registration remains after a failed probe so retry can reconcile the same ownership.

Before first registration, SCAN rejects any existing `<stage>:*` keys; existing unregistered data is not automatically adopted. First initialization requires coordination with runtime writers: no external writer may populate an unregistered stage during the scan/claim interval. Registered stages can contain runtime data, which this adapter does not modify. A stage is exclusive to one owner in one database.

The unique probe writes and reads a hash, then deletes only that probe. An expiry bounds leftover probe lifetime if the process exits before cleanup. Operations are awaited before cancellation cleanup; Redis client timeouts bound in-flight calls. A matching registration is reverified on each direct backend invocation. The engine retains its existing completed-checkpoint behavior, which skips completed actions; it does not perform drift checks.

This first backend supports a standalone Redis database, not Redis Cluster. It needs SCAN, EXISTS, GET, SET, PTTL, HSET, HMGET, PEXPIRE, DEL, and script execution access to the registration and stage namespaces, plus client connection/handshake permissions. Server persistence, backups, eviction policy, and ACLs remain operator responsibilities. A staging round trip proves access, not power-loss durability.

Atomic registration and expiring writes follow [Redis Lua scripting](https://redis.io/docs/latest/develop/programmability/eval-intro/) and [SET semantics](https://redis.io/docs/latest/commands/set/).

## Run the standalone acceptance host

Supply `WORKBENCH_REDIS_CONNECTION` through the host's protected environment, plus a dedicated `WORKBENCH_STAGE` and `WORKBENCH_ENVIRONMENT`. Then run:

```sh
dotnet run --project samples/Aetheric.Provisioning.Workbench.Harness --configuration Release
```

The host registers/verifies the standalone workspace. Repeating it reuses the matching registration. Output contains stage, existing/new status, staging verification, and `DecisionsReady: false`. It never prints the connection or raw provider exceptions. This acceptance host is not a configuration export or general-purpose CLI. It does not wire the adapter into the Blazor simulation.

For optional integration tests, set `WORKBENCH_TEST_REDIS` to a disposable standalone Redis database:

```sh
dotnet test --configuration Release
```

Without that variable, Redis integration tests are explicitly skipped. Other unit and recovery tests continue to run. Integration cases cover reconnect/retry, incompatible and concurrent owners, unregistered data, expiring ownership, and temporary-probe cleanup. No FLUSHDB or shared-data deletion is used.

## Remaining work

Live target selection and review in the host, explicit runtime credentials/ACL policy, runtime-consumable configuration export, full Workbench runtime integration, and live parent resolution remain separate work. M3/M4 and the Decisions v0.1 release gates remain open.

## Recorded verification

On 2026-09-16, locked restore and Release build passed with no warnings or errors. All 89 tests passed with no skips against an isolated `redis:7-alpine` container, including the three Redis integration cases. The original engine failure/retry harness passed. Two separate standalone Workbench harness processes reported new, then existing, for the same workspace; both verified staging and reported Decisions not ready. The disposable container was removed after verification. Existing deployed infrastructure was not used. CI now supplies an isolated Redis service for these tests.
