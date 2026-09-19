using System.Text.Json;
using Aetheric.Provisioning.Application;

namespace Aetheric.Provisioning.Persistence;

public sealed class FileInfrastructureStateStore(string directory) : IInfrastructureStateStore
{
    private readonly FileStorage _files = new(directory);
    private sealed record Envelope(int Version, InfrastructureState State);
    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken ct) =>
        await FileStorage.LockAsync(_files.PathFor("infrastructure", ".lock"), ct);
    public async Task<InfrastructureState?> ReadAsync(CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(_files.PathFor("infrastructure", ".json"), ct);
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes);
            if (envelope is null || envelope.Version != 1) throw new InvalidDataException("Invalid infrastructure state.");
            Validate(envelope.State);
            return envelope.State;
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException) { throw new InvalidDataException("Invalid infrastructure state."); }
    }
    public Task SaveAsync(InfrastructureState state, CancellationToken ct)
    {
        Validate(state);
        return FileStorage.WriteAsync(_files.PathFor("infrastructure", ".json"), JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, state)), ct);
    }
    private static void Validate(InfrastructureState state)
    {
        if (state is null || state.Deployment is null || string.IsNullOrWhiteSpace(state.SubjectId)
            || state.Completed != state.CompletedAt.HasValue)
            throw new InvalidDataException("Invalid infrastructure state.");
    }
}
