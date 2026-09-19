# Aetheric Provisioning

A standalone provisioning engine and a Blazor single-page app for combining technology-independent institution requirements with deployment bindings and parent context to plan and provision infrastructure.

The intended flow is **load definition → configure bindings and parent context → plan → review → provision → export configuration**. The engine owns provisioning behavior; the UI is one consumer. Automated callers will be able to use the same engine without interactive prompts.

## Release planning

- [v0.1 release scope and success criteria](docs/v0.1-release.md)
- [Milestones and completion gates](docs/milestones.md)
- [Future roadmap](docs/roadmap.md)

M2 adds public GitHub loading, YAML validation, and a configuration/review workflow around the standalone engine. Execution is still simulated; live provisioning remains planned work. Milestones have no committed dates.

## Architectural commitments

- Keep the .NET engine independent of Blazor and HTTP hosting.
- Treat institution definitions as the source of capabilities, resource categories, ownership, and dependencies. Do not bind an Institution or Organization directly to a technology.
- Select technologies through deployment bindings and provider adapters.
- Resolve and validate inherited capabilities against parent context; create only resources owned by the institution being provisioned.
- Separate definition loading, planning, execution, provider adapters, and state/secret storage.
- Generate credentials by default and allow explicit overrides.
- Preserve generated credentials across retries; rotation is a separate operation.
- Keep plans inspectable and execution results usable by both people and automation.
- Start with public Git; leave a clear extension point for authenticated sources.

The first concrete input is ADR Campus’s declarative Decisions Office definition and its separate deployment bindings. See [the input model and source references](docs/input-model.md) for the ownership boundary, known resource requirements, and remaining integration decisions.

## Standalone Workbench

A Workbench provisioning abstraction and Redis backend now support registration and staging verification on an existing standalone Redis database. See [the standalone Workbench contract and acceptance host](docs/standalone-workbench.md). This path assumes no deployed Campus capabilities; the Blazor workflow remains simulated.

See also [the Registry bootstrap foundation](docs/registry-bootstrap.md) for the persisted lifecycle around a sysadmin-created initial principal. The runtime-backed Keycloak Clerk adapter is implemented; host authentication and UI wiring remain pending.

## Build and run

Requires the .NET 10 SDK (the SDK policy is in `global.json`).

```sh
git submodule update --init --recursive
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build
dotnet run --project samples/Aetheric.Provisioning.Harness --configuration Release --no-build
dotnet run --project samples/Aetheric.Provisioning.Web --configuration Release --no-build --urls http://localhost:5180
```

Open `http://localhost:5180` for the setup landing page, or `http://localhost:5180/simulation` for the Blazor simulation workflow. No provider accounts or credentials are needed.

1. Load the default public ADR Campus repository, selecting a branch, tag, or commit and the two YAML paths. Alternatively, use the bundled Decisions example for offline development.
2. Edit deployment defaults and provide a parent identity/revision. Mark the capabilities available in the **simulated** parent context. The bundled example supplies this simulated context explicitly.
3. Review owned actions, inherited requirements, effective bindings, and the pinned source commit.
4. Approve the exact plan and run the simulation. Editing any source/configuration input invalidates the review and approval.

GitHub requests are unauthenticated and subject to public API limits. Other Git hosts and private repositories are not supported yet. The app never clones, builds, or executes repository code.

The harness deliberately fails once, retries, and repeats the completed plan. It exits with code 0 only if the failure is observed, retry succeeds, the synthetic credential is reused, and one simulated resource is created.

See [durable state and restart recovery](docs/durable-state.md) for the opt-in local persistence stores and process-restart tests. The web simulation still uses in-memory stores.

## Solution layout

| Project | Responsibility |
| --- | --- |
| `src/Aetheric.Provisioning.Persistence` | Atomic local run checkpoints, execution leases, and encrypted secrets |
| `src/Aetheric.Provisioning.Engine` | Host-independent contracts, validation, immutable planning, and execution |
| `src/Aetheric.Provisioning.Definitions` | Public GitHub source adapter and supported YAML-profile validation |
| `src/Aetheric.Provisioning.Application` | Host-independent load/configure/review/approval lifecycle |
| `src/Aetheric.Provisioning.Simulation` | Pinned Decisions fixture projection, simulated Workbench and parent, in-memory state and secrets |
| `samples/Aetheric.Provisioning.Web` | Blazor Interactive Server source/configuration/review workflow with simulated execution |
| `samples/Aetheric.Provisioning.Harness` | Non-UI M1 acceptance demonstration; not the future CLI product |
| `tests/Aetheric.Provisioning.Tests` | Planning, ownership, failure/retry, state, cancellation, and secret-reference tests |

See [M2 loading and review](docs/m2-loading-review.md) for current behavior, validation, and limitations, and [M1 architecture](docs/m1-foundation.md) for the foundation. The engine has no YAML, Blazor, hosting, or provider SDK dependencies.

## Run on Vulcan with Docker

See [Docker deployment](docs/docker.md) for the image build, Compose settings, and nginx configuration.

See [infrastructure bootstrap](docs/infrastructure-bootstrap.md) for the final four-service credential setup page, storage configuration, and integration tests.

The reusable Razor component library moved to [`aetheric-web-components`](https://github.com/aetheric-forge/aetheric-web-components) (`src/Aetheric.Provisioning.Components` there), consumed here as the `external/web-components` submodule. `samples/Aetheric.Provisioning.Web` is an optional sample host for it.
