# Aetheric Web Components

Shared Razor component libraries for the Aetheric Forge web family - reusable UI, not app-specific business logic. Each library lives under `src/`, consumed by host apps as a git submodule with a `ProjectReference`, matching how `aetheric-contracts`/`aetheric-runtime`/`primitives` are already consumed across this org.

## Libraries

### `src/Aetheric.Provisioning.Components`

The provisioning bootstrap workflow (first-administrator setup, infrastructure root-credential collection) extracted from [`aetheric-provisioning`](https://github.com/aetheric-forge/aetheric-provisioning). No executable entry point or HTML document of its own - a host registers it and owns authentication, routing, and the actual HTML document. See [`docs/component-library.md`](docs/component-library.md) for host integration.

This library still depends on `aetheric-provisioning`'s own domain projects (`Application`, `Engine`, `Registry`, `Simulation`, consumed via the `provisioning` submodule) - it does not have a file-based storage or Keycloak-client dependency baked in; the host supplies those (`IRegistryBootstrapStore`, `IInfrastructureStateStore`, `IRootCredentialStore`, `IRootConnectionValidator`).

## Setup

```bash
git clone --recurse-submodules git@github.com:aetheric-forge/aetheric-web-components.git
cd aetheric-web-components
dotnet build AethericWebComponents.slnx
```
