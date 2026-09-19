using System.Text.Json;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Persistence;

/// <summary>Versioned atomic checkpoints and per-plan execution leases on a local filesystem.</summary>
public sealed class FileRunStateStore(string directory) : IRunStateStore, IRunExecutionLock
{
    private readonly FileStorage _files = new(directory);
    private sealed record Checkpoint(int Version, RunState State);

    public async Task<IAsyncDisposable> AcquireAsync(string planId, CancellationToken ct)
        => await FileStorage.LockAsync(_files.PathFor(planId, ".run.lock"), ct);

    public async Task<RunState?> ReadAsync(string planId, CancellationToken ct)
    {
        byte[] bytes;
        try { bytes = await File.ReadAllBytesAsync(_files.PathFor(planId, ".json"), ct); }
        catch (FileNotFoundException) { return null; }
        try
        {
            var checkpoint = JsonSerializer.Deserialize<Checkpoint>(bytes);
            if (checkpoint is null || checkpoint.Version != 1 || checkpoint.State is null
                || checkpoint.State.PlanId != planId || checkpoint.State.Outcomes is null
                || checkpoint.State.Outcomes.Any(x => x.Value is null || x.Key != x.Value.StepId
                    || !Enum.IsDefined(x.Value.Status) || string.IsNullOrWhiteSpace(x.Value.Code)
                    || x.Value.Secrets.IsDefault || x.Value.Secrets.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id))))
                throw new InvalidDataException("Invalid or unsupported run checkpoint.");
            return checkpoint.State;
        }
        catch (JsonException) { throw new InvalidDataException("Invalid run checkpoint JSON."); }
    }

    public Task SaveAsync(RunState state, CancellationToken ct)
        => FileStorage.WriteAsync(_files.PathFor(state.PlanId, ".json"),
            JsonSerializer.SerializeToUtf8Bytes(new Checkpoint(1, state)), ct);
}
