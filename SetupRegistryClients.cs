using Aetheric.Provisioning.Registry;

namespace Aetheric.Provisioning.Components;

public interface ISetupRegistryClients
{
    KeycloakAdministratorCreator Accounts(string secret);
    KeycloakRegistryBootstrapStaff Staff(string secret);
}

public sealed class SetupRegistryClients(BootstrapConnectionConfiguration configuration) : ISetupRegistryClients
{
    public KeycloakAdministratorCreator Accounts(string secret) =>
        new(configuration.Options(configuration.ClientId, secret), configuration.AdminRole);
    public KeycloakRegistryBootstrapStaff Staff(string secret) =>
        new(configuration.Settings, configuration.Options(configuration.ClientId, secret));
}
