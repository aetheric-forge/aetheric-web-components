using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Simulation;

namespace Aetheric.Provisioning.Components;

public static class ProvisioningServiceCollectionExtensions
{
    /// <summary>Registers the existing local bootstrap backend. Does not change host authentication or middleware.</summary>
    public static IServiceCollection AddProvisioningBootstrap(this IServiceCollection services,
        IConfiguration configuration, BootstrapConnectionConfiguration connectionConfiguration)
    {
        services.AddSingleton<IRegistryBootstrapStore>(_ =>
            new Aetheric.Provisioning.Persistence.FileRegistryBootstrapStore(connectionConfiguration.StateDirectory));
        services.AddSingleton<ISetupRegistryClients, SetupRegistryClients>();
        services.AddScoped<SetupBootstrap>();
        services.AddHttpContextAccessor();
        services.AddSingleton<IInfrastructureStateStore>(_ =>
            new Aetheric.Provisioning.Persistence.FileInfrastructureStateStore(connectionConfiguration.StateDirectory));
        services.AddSingleton<IRootCredentialStore>(_ => new Aetheric.Provisioning.Persistence.ManagedRootCredentialStore(
            configuration["RootCredentials:Directory"] ?? "data/root-credentials",
            configuration["RootCredentials:KeyDirectory"] ?? "data/root-key"));
        services.AddSingleton<IRootConnectionValidator, Aetheric.Provisioning.Infrastructure.RootConnectionValidator>();
        services.AddSingleton<InfrastructureReceipts>();
        services.AddScoped<InfrastructureSetup>();
        services.AddSingleton(connectionConfiguration);
        // Authentication is registered separately by the host.
        services.AddScoped<BootstrapConnection>();

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
