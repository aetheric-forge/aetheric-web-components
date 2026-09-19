# Aetheric.Provisioning.Components

`src/Aetheric.Provisioning.Components` is a Razor class library with the working setup pages, bootstrap backend, optional authentication registration, endpoint mappings, and static web assets. It has no executable entry point or HTML document - a host app owns those responsibilities. It still depends on `aetheric-provisioning`'s own domain projects (`Application`, `Engine`, `Registry`, `Simulation`), consumed here via the `provisioning` git submodule.

Extracted from [`aetheric-provisioning`](https://github.com/aetheric-forge/aetheric-provisioning) (originally `samples/Aetheric.Provisioning.Web` there), which still hosts a sample/reference consumer of this library for standalone regression testing.

## Host integration

Reference `Aetheric.Provisioning.Components.csproj`. Register Razor components with Interactive Server support. Register `AddProvisioningBootstrap(connectionConfiguration)` for the bootstrap UI/orchestration layer. This call does not change authentication defaults or map endpoints, and does not supply a storage or validation backend - the library has no dependency on `Aetheric.Provisioning.Persistence` or `.Infrastructure`. The host must separately register `IRegistryBootstrapStore`, `IInfrastructureStateStore`, `IRootCredentialStore`, and `IRootConnectionValidator` before calling `AddProvisioningBootstrap`. `aetheric-provisioning`'s `samples/Aetheric.Provisioning.Web/Program.cs` shows the file-backed registrations (`FileRegistryBootstrapStore`, `FileInfrastructureStateStore`, `ManagedRootCredentialStore`, `RootConnectionValidator`); a different host is free to supply different implementations instead - this seam exists specifically so the component library can be reused by hosts that don't want that app's local file storage.

The host owns authentication. For the existing first-administrator flow it can explicitly call `builder.AddSetupAuthentication(connectionConfiguration, signInConfiguration)`, then use authentication, authorization and antiforgery middleware and map `app.MapSetupAuthentication(signInConfiguration)` and `app.MapInfrastructure()`. The sample shows the full order. The setup authentication helper chooses the default cookie scheme; an admin app with existing authentication must reconcile those schemes and the setup policies/claims deliberately rather than registering it blindly.

Include the library assembly in BOTH route discovery locations:

```csharp
app.MapRazorComponents<App>()
    .AddAdditionalAssemblies(typeof(ProvisioningComponentAssembly).Assembly)
    .AddInteractiveServerRenderMode();
```

```razor
<Router AppAssembly="typeof(App).Assembly"
        AdditionalAssemblies="new[] { typeof(ProvisioningComponentAssembly).Assembly }">
    @* Host-owned Found/NotFound and AuthorizeRouteView content *@
</Router>
```

Serve static web assets and include these in the host document:

```html
<link rel="stylesheet" href="_content/Aetheric.Provisioning.Components/app.css" />
<script src="_framework/blazor.web.js"></script>
<script type="module" src="_content/Aetheric.Provisioning.Components/infrastructure-setup.js"></script>
```

The setup page imports its password/autofill module from the library's `_content` path. Styles are scoped to `.aetheric-provisioning` surfaces. The host keeps ownership of `/`; library routes are `/setup`, its existing subroutes, `/simulation`, and `/error`. Current setup endpoint and navigation URLs require hosting at the origin root. Do not give another host page the same route.

`AddProvisioningSimulation()` explicitly enables the existing simulation backend. It is optional for bootstrap, but required when using `/simulation`.

## Runtime messaging boundary

This library assumes the runtime will supply `IPostSubscriber`. It does not declare a competing interface, implement a subscriber, or invent provisioning message contracts. The preserved bootstrap backend is local to whatever host registers it. Runtime provisioning commands/results will be wired through the consuming admin host's messaging adapter once those contracts are available; the browser does not connect directly to RabbitMQ.

## Verification

`aetheric-provisioning`'s HTTP/OIDC tests exercise this library's setup pages through its sample host: account creation/selection, administrator verification/resumption, credential testing and saving, and bootstrap completion. Run those with `dotnet test --configuration Release` in that repo. To build this library standalone, `dotnet build AethericWebComponents.slnx` here.
