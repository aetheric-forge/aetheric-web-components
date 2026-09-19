using Aetheric.Provisioning.Registry;
using AethericForge.Runtime.Providers.Identity.Keycloak;

namespace Aetheric.Provisioning.Components;

// The destination is deployment-owned; browser input can never redirect client credentials.
public sealed class BootstrapConnectionConfiguration
{
    public string Authority { get; init; } = "";
    public string Realm { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string AdminRole { get; init; } = "forge-admin";
    public string StateDirectory { get; init; } = "data/bootstrap";
    public Aetheric.Provisioning.Application.RegistryBootstrapSettings Settings => new(Issuer, ClientId, AdminRole);
    public string PublicOrigin { get; init; } = "";
    public string Issuer => Authority.TrimEnd('/') + "/realms/" + Uri.EscapeDataString(Realm);
    public KeycloakOptions Options(string clientId, string secret) => new()
        { Authority = Authority, Realm = Realm, ClientId = clientId, ClientSecret = secret };
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority)
        && !string.IsNullOrWhiteSpace(Realm) && !string.IsNullOrWhiteSpace(ClientId);
}

public sealed class BootstrapConnection(BootstrapConnectionConfiguration configuration, SetupSessions sessions, SetupBootstrap bootstrap)
{
    public async Task<string> CheckAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        if (!configuration.IsConfigured)
            throw new RegistryBootstrapStaffException("registry.connection_not_configured");
        if (!string.Equals(clientId, configuration.ClientId, StringComparison.Ordinal))
            throw new RegistryBootstrapStaffException("registry.client_not_configured");
        await bootstrap.RequireOpenAsync(ct);
        using var connection = new KeycloakProvisionerConnection(configuration.Options(clientId, clientSecret));
        await connection.CheckAsync(ct);
        return sessions.CreateTicket(clientSecret);
    }
}
