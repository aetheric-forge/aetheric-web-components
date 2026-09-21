using System.Security.Claims;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Components;

public sealed class InfrastructureSetup(IHttpContextAccessor http, SetupBootstrap bootstrap,
    IInfrastructureStateStore states, IRootCredentialStore credentials, IRootConnectionValidator validator,
    InfrastructureReceipts receipts)
{
    public async Task<(RegistryBootstrapState Registry, string Session)> RequireAdministratorAsync(CancellationToken ct)
    {
        var user = http.HttpContext?.User ?? throw new UnauthorizedAccessException();
        var state = await bootstrap.StateAsync(ct);
        var session = user.FindFirstValue(SetupAuthentication.AdminSessionClaim);
        if (user.Identity?.IsAuthenticated != true || !user.HasClaim(SetupAuthentication.VerifiedClaim, "true")
            || state.Phase != RegistryBootstrapPhase.Completed || user.FindFirstValue("sub") != state.SubjectId
            || user.FindFirstValue(SetupAuthentication.IssuerClaim) != state.Settings.Issuer
            || string.IsNullOrEmpty(session) || !long.TryParse(user.FindFirstValue(SetupAuthentication.AdminExpiryClaim), out var expiry)
            || expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new UnauthorizedAccessException();
        return (state, session);
    }
    public async Task<InfrastructureState> StateAsync(CancellationToken ct)
    {
        var (registry, _) = await RequireAdministratorAsync(ct);
        await using var lease = await states.AcquireAsync(ct);
        return await ReadOrInitializeAsync(registry, ct);
    }
    private async Task<InfrastructureState> ReadOrInitializeAsync(RegistryBootstrapState registry, CancellationToken ct)
    {
        var state = await states.ReadAsync(ct);
        if (state is null)
        {
            foreach (var system in InfrastructureConnections.Systems)
                if (await credentials.TryReadAsync(system, ct) is not null)
                    throw new InvalidDataException("Infrastructure progress is missing. Recover its saved state.");
            state = new(registry.Settings, registry.SubjectId!, false, null);
            await states.SaveAsync(state, ct);
        }
        if (state.Deployment != registry.Settings || state.SubjectId != registry.SubjectId)
            throw new InvalidDataException("Infrastructure deployment binding changed.");
        return state;
    }
    public async Task<Dictionary<string, RootCredential>> SavedAsync(CancellationToken ct)
    {
        var state = await StateAsync(ct);
        var saved = new Dictionary<string, RootCredential>();
        foreach (var system in InfrastructureConnections.Systems)
        {
            var credential = await credentials.TryReadAsync(system, ct);
            if (credential is not null) saved[system] = InfrastructureConnections.Normalize(system, credential);
            else if (state.Completed) throw new InvalidDataException("Completed infrastructure credentials are missing.");
        }
        return saved;
    }
    public async Task<RootCredential> ResolveAsync(string system, RootCredential value, bool keepPassword, CancellationToken ct)
    {
        await RequireAdministratorAsync(ct);
        if (keepPassword)
        {
            var saved = await credentials.TryReadAsync(system, ct) ?? throw new ArgumentException("No saved password is available.");
            value = value with { Password = saved.Password };
        }
        return InfrastructureConnections.Normalize(system, value);
    }
    public async Task<(ConnectionCheck Check, string? Receipt)> TestAsync(string system, RootCredential value, CancellationToken ct)
    {
        var (registry, session) = await RequireAdministratorAsync(ct);
        await using (var lease = await states.AcquireAsync(ct))
            if ((await ReadOrInitializeAsync(registry, ct)).Completed) throw new InvalidOperationException("Bootstrap is complete.");
        value = InfrastructureConnections.Normalize(system, value);
        var result = await validator.TestAsync(system, value, ct);
        // A probe started while signed in must not grant a receipt after session expiry.
        await RequireAdministratorAsync(ct);
        return (result, result.Succeeded ? receipts.Issue(session, system, value) : null);
    }
    public async Task SaveAsync(IReadOnlyDictionary<string, (RootCredential Credential, string Receipt)> values, CancellationToken ct)
    {
        var (registry, session) = await RequireAdministratorAsync(ct);
        await using var lease = await states.AcquireAsync(ct);
        if ((await ReadOrInitializeAsync(registry, ct)).Completed) throw new InvalidOperationException("Bootstrap is complete.");
        if (values.Count != InfrastructureConnections.Systems.Length) throw new ArgumentException("Test all connections first.");
        var snapshot = InfrastructureConnections.Systems.ToDictionary(system => system, system =>
        {
            if (!values.TryGetValue(system, out var value)) throw new ArgumentException("Test all connections first.");
            var normalized = InfrastructureConnections.Normalize(system, value.Credential);
            if (!receipts.Matches(value.Receipt, session, system, normalized))
                throw new ArgumentException("A connection changed or its test expired. Test it again.");
            return normalized;
        });
        foreach (var system in InfrastructureConnections.Systems)
        {
            await RequireAdministratorAsync(ct);
            try { await credentials.SetAsync(system, snapshot[system], ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new InfrastructureSaveException(system); }
        }
        foreach (var system in InfrastructureConnections.Systems)
            if (await credentials.TryReadAsync(system, ct) != snapshot[system])
                throw new InfrastructureSaveException(system);
        await RequireAdministratorAsync(ct);
        await states.SaveAsync(new(registry.Settings, registry.SubjectId!, true, DateTimeOffset.UtcNow), ct);
        foreach (var value in values.Values) receipts.Remove(value.Receipt);
    }
}
public sealed class InfrastructureSaveException(string system) : Exception("Could not save " + system + ". Successful saves are retained; retry to finish.");
