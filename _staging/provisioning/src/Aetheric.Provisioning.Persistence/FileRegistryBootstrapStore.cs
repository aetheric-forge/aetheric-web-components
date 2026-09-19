using System.Text.Json;
using Aetheric.Provisioning.Application;

namespace Aetheric.Provisioning.Persistence;

// One deployment per private directory. Missing state never implicitly enables bootstrap.
public sealed class FileRegistryBootstrapStore(string directory) : IRegistryBootstrapStore
{
    private readonly FileStorage _files = new(directory);
    private sealed record Envelope(int Version, RegistryBootstrapState State);
    private string StatePath => _files.PathFor("registry-bootstrap", ".json");
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct) =>
        await FileStorage.LockAsync(_files.PathFor("registry-bootstrap", ".lock"), ct);

    // Deployment-only initialization. Never invoke this automatically on application startup.
    public async Task InitializeAsync(RegistryBootstrapSettings settings, CancellationToken ct = default)
    {
        await using var lease = await AcquireAsync(ct);
        if (File.Exists(StatePath)) throw new InvalidOperationException("Bootstrap state already exists.");
        await SaveAsync(new(settings, RegistryBootstrapPhase.AwaitingPrincipal, null), ct);
    }

    public async Task<RegistryBootstrapState> ReadAsync(CancellationToken ct)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllBytesAsync(StatePath, ct));
            if (envelope is null || envelope.Version != 1) throw new InvalidDataException("Invalid bootstrap state version.");
            Validate(envelope.State);
            return envelope.State;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid bootstrap state."); }
    }

    // Read/modify/write callers must hold AcquireAsync for the whole operation.
    public Task SaveAsync(RegistryBootstrapState state, CancellationToken ct)
    {
        Validate(state);
        return FileStorage.WriteAsync(StatePath, JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, state)), ct);
    }

    private static void Validate(RegistryBootstrapState state)
    {
        if (state is null || state.Settings is null || !Enum.IsDefined(state.Phase)
            || !Uri.TryCreate(state.Settings.Issuer, UriKind.Absolute, out var issuer)
            || issuer.Scheme != "https" || !string.IsNullOrEmpty(issuer.UserInfo)
            || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment)
            || string.IsNullOrWhiteSpace(state.Settings.ClientId) || string.IsNullOrWhiteSpace(state.Settings.AdminRole)
            || (state.Phase is RegistryBootstrapPhase.AwaitingPrincipal or RegistryBootstrapPhase.CreatingPrincipal ? state.SubjectId is not null : string.IsNullOrWhiteSpace(state.SubjectId)))
            throw new InvalidDataException("Invalid bootstrap state.");
    }
}
