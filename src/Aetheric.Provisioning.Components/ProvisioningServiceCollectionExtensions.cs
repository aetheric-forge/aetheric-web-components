using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Components.Terminology;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Simulation;

namespace Aetheric.Provisioning.Components;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>
    /// Registers the provisioning bootstrap UI and orchestration layer. Does not change host
    /// authentication or middleware, and does not supply storage/validation backends - the host
    /// must separately register IRegistryBootstrapStore, IInfrastructureStateStore,
    /// IRootCredentialStore, and IRootConnectionValidator before calling this (e.g. the file-backed
    /// implementations from Aetheric.Provisioning.Persistence/.Infrastructure, as the sample host
    /// does). This keeps the component library itself free of any specific storage/validation
    /// backend, so a different host can supply different implementations.
    /// </summary>
    public static IServiceCollection AddProvisioningBootstrap(this IServiceCollection services,
        BootstrapConnectionConfiguration connectionConfiguration)
    {
        services.AddSingleton<ISetupRegistryClients, SetupRegistryClients>();
        services.AddScoped<SetupBootstrap>();
        services.AddHttpContextAccessor();
        services.AddSingleton<InfrastructureReceipts>();
        services.AddScoped<InfrastructureSetup>();
        services.AddSingleton(connectionConfiguration);
        services.AddTerminology();
        // Authentication is registered separately by the host.
        services.AddSingleton<ISetupClientVerifier, SetupClientVerifier>();
        services.AddScoped<BootstrapConnection>();

        return services;
    }
    /// <summary>
    /// Registers the terminology service the UI reads its level names from. Defaults to an in-memory
    /// store (academic terms); register your own ITerminologyStore (before or after) to persist the choice.
    /// AddProvisioningBootstrap already calls this.
    /// </summary>
    public static IServiceCollection AddTerminology(this IServiceCollection services)
    {
        services.TryAddSingleton<ITerminologyStore, InMemoryTerminologyStore>();
        services.TryAddSingleton<TerminologyService>();
        return services;
    }

    /// <summary>Registers the optional simulation backend; not the production messaging transport.</summary>
    public static IServiceCollection AddProvisioningSimulation(this IServiceCollection services)
    {
        services.AddSingleton(_ => PublicGitHubSource.CreateHttpClient());
        services.AddScoped<IDefinitionSource, PublicGitHubSource>();
        services.AddSingleton<InstitutionYamlReader>();
        services.AddScoped<IResourceProvider, SimulatedWorkbenchProvider>();
        services.AddScoped<IParentCapabilityResolver, SimulatedCatalogParentResolver>();
        services.AddScoped<IRunStateStore, InMemoryRunStateStore>();
        services.AddScoped<ISecretStore, InMemorySecretStore>();
        services.AddScoped<ProvisioningPlanner>();
        services.AddScoped<ProvisioningEngine>();
        services.AddScoped<ProvisioningReview>();
        return services;
    }
}
