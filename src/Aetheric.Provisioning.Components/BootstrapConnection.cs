using Aetheric.Provisioning.Registry;
using AethericForge.Runtime.Providers.Identity.Keycloak;

namespace Aetheric.Provisioning.Components;

// The destination is deployment-owned; browser input can never redirect client credentials.
public sealed class BootstrapConnectionConfiguration
{
    public bool AllowClientSelection { get; set; }
    public bool PersistClientSecret { get; set; }
    public string Authority { get; init; } = "";
    public string Realm { get; init; } = "";
    public string ClientId { get; set; } = "";
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

public interface ISetupClientVerifier
{
    Task VerifyAsync(BootstrapConnectionConfiguration configuration, string clientId, string secret, CancellationToken ct);
}

public sealed class SetupClientVerifier : ISetupClientVerifier
{
    public async Task VerifyAsync(BootstrapConnectionConfiguration configuration, string clientId, string secret, CancellationToken ct)
    {
        using var connection = new KeycloakProvisionerConnection(configuration.Options(clientId, secret));
        await connection.CheckAsync(ct);
    }
}

public sealed class BootstrapConnection(BootstrapConnectionConfiguration configuration, SetupSessions sessions, SetupBootstrap bootstrap,
    Aetheric.Provisioning.Application.IRegistryBootstrapStore store, ISetupClientVerifier verifier)
{
    public async Task<string> CheckAsync(string clientId, string clientSecret, CancellationToken ct)
    {
        if (!configuration.IsConfigured)
            throw new RegistryBootstrapStaffException("registry.connection_not_configured");
        if (string.IsNullOrWhiteSpace(clientId) || clientId != clientId.Trim() || clientId.Length > 200)
            throw new ArgumentException("Enter a valid client ID.");
        await bootstrap.RequireOpenAsync(ct);
        await using var lease = await store.AcquireAsync(ct);
        var state = await store.ReadAsync(ct);
        if (state.Settings != configuration.Settings) throw new InvalidOperationException("Bootstrap deployment settings changed.");
        var changing = clientId != state.Settings.ClientId;
        if (changing && (!configuration.AllowClientSelection
            || state.Phase != Aetheric.Provisioning.Application.RegistryBootstrapPhase.AwaitingPrincipal || state.SubjectId is not null))
            throw new RegistryBootstrapStaffException("registry.client_not_configured");
        // Prove access before changing durable state. Failed verification leaves the old binding intact.
        await verifier.VerifyAsync(configuration, clientId, clientSecret, ct);
        if (changing)
        {
            await store.SaveAsync(state with { Settings = state.Settings with { ClientId = clientId } }, ct);
            sessions.Clear();
            configuration.ClientId = clientId;
        }
        return sessions.CreateTicket(clientSecret);
    }
}
