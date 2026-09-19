# M1 foundation

This records the M1 implementation boundary. See [M2 loading and review](m2-loading-review.md) for the current source and review workflow.

## Implemented boundary

The engine accepts immutable projections of institution requirements, deployment bindings, parent context, and source provenance. These are engine contracts, not a replacement institutional schema. It validates them before constructing an executable plan. A plan captures effective bindings and parent identity/revision; its SHA-256 identity changes with relevant inputs and does not depend on map/set enumeration order.

Provider selection comes exclusively from deployment bindings. The engine itself knows no Workbench, Redis, MongoDB, S3, or Keycloak operations. Parent-owned resources never produce provider creation actions. All declared parent contracts are explicit checks, and all owned actions conservatively depend on those checks for M1. Fine-grained resource dependency semantics remain a later contract decision.

The executor reports an outcome per step. Failed parent checks block owned work. Failed or cancelled owned actions can be retried; completed owned actions are skipped using the same in-memory checkpoint. Parent capabilities are rechecked on every attempt, including when owned work was previously completed. Raw provider exception messages are not exposed in results or progress because they can contain secrets.

## Extension points

| Contract | Purpose | M1 implementation |
| --- | --- | --- |
| `IResourceProvider` | Pure binding validation and idempotent owned-resource execution | Simulated Workbench with Redis-shaped binding validation |
| `IParentCapabilityResolver` | Verify a required contract at a parent source | Explicit test double; no live capability probing |
| `IRunStateStore` | Read/save checkpoint snapshots by plan identity | In-memory dictionary |
| `ISecretStore` | Atomically create/reuse a secret and retrieve by opaque reference | In-memory store with cryptographic random generation |
| `IProgress<ProvisioningProgress>` | Host-independent per-step outcomes | Captured in tests; usable by any caller |

Bindings carry secret references rather than secret values. Non-secret settings must remain non-secret; provider validators define their allowed keys. The simulated provider accepts only its known fixture settings. A synthetic `simulation-access` credential exercises default generation and explicit overrides; it is not a claim that the Redis binding requires a new user or access token.

## Fixture handling

The two YAML files under [fixtures/decisions](../fixtures/decisions/README.md) are byte-for-byte copies from ADR Campus commit `4e398147f9a7cd8ba47258a8e416f3e86f404a93`. The simulation assembly embeds them and checks their SHA-256 hashes before projecting the relevant resource/binding fields through YamlDotNet. This reader accepts only the embedded, pinned fixture. It does not accept URLs, external file paths, or arbitrary YAML.

The fixture contains one owned staging workspace, one parent-owned decision record, and four parent contracts. The simulation retains the Redis binding and declared in-memory fallback as data. It does not switch backends, contact Redis, instantiate runtime services, or provision a parent.

General public Git loading, complete YAML/schema validation, and override review belong to M2. The pinned fixture projection does not satisfy those release criteria.

## Verification evidence

The SDK is pinned to 10.0.112 so CI and local builds select the same implicit Blazor assets package recorded in the lock file. Update the SDK and dependency locks together.

Local verification used .NET SDK 10.0.112 on Ubuntu 24.04:

- Release build: zero warnings and errors.
- 19 tests pass, covering fixture ownership, invalid input, stable plan identity, context/binding changes, failure/retry, secret reuse and overrides, parent loss/recovery, cancellation, in-memory checkpoint reuse, serialization of calls on one engine, and exception redaction.
- The non-UI harness observes the injected failure, succeeds on retry, preserves the credential reference, and repeats without duplicating the resource.
- HTTP smoke checks return 200 for the rendered Decisions page, CSS, Blazor framework script, and error route. The page contains the owned action and all four parent checks. This is not an automated browser interaction test.
- CI restores locked dependencies, builds Release, runs tests, and executes the same harness on pushes to `main` and pull requests.

The repeatable commands are in [the README](../README.md). The engine assembly dependency test also verifies that Blazor, YAML, and simulation libraries do not enter the engine dependency graph.

## Deliberate limitations

- State, generated secrets, and simulated resources live in memory. A new process or browser session loses them. Reusing a store in a new engine instance is not process-restart recovery.
- Execution is serialized within one engine instance. Shared-store/distributed locking is not implemented.
- The simulator is not a real provider and the parent resolver is not a live readiness check. No v0.1 integration evidence is implied.
- Completed owned actions are skipped from checkpoints; live drift detection is not implemented. Changing inputs creates a distinct plan and does not reconcile or migrate previous state.
- The provider failure code is intentionally generic. A richer safe error taxonomy can be added when real adapters define actionable failures.
- The Blazor page is an isolated demonstration, not the full M2 source/configuration/review flow or a production administration service.

M0 remains open for parent-context verification, concrete Workbench/Redis allocation and fallback semantics, protected durable secrets, and runtime configuration output. M1 provides the seams and simulation needed to develop those decisions without coupling institutions to technologies.
