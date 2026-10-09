using System.Text.Json;

namespace Aetheric.Provisioning.Components.Terminology;

/// <summary>What a deployment has chosen: a built-in template id, or custom terms.</summary>
public sealed record TerminologySelection(string TemplateId, Terms? CustomTerms = null)
{
    public static TerminologySelection Default { get; } = new(TerminologyTemplate.Academic.Id);
}

/// <summary>Host-supplied persistence for the selection, in the same spirit as the other bootstrap stores.</summary>
public interface ITerminologyStore
{
    Task<TerminologySelection> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(TerminologySelection selection, CancellationToken ct = default);
}

public sealed class InMemoryTerminologyStore : ITerminologyStore
{
    private TerminologySelection _selection = TerminologySelection.Default;
    public Task<TerminologySelection> LoadAsync(CancellationToken ct = default) => Task.FromResult(_selection);
    public Task SaveAsync(TerminologySelection selection, CancellationToken ct = default)
    {
        _selection = selection;
        return Task.CompletedTask;
    }
}

public sealed class FileTerminologyStore(string path) : ITerminologyStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<TerminologySelection> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return TerminologySelection.Default;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<TerminologySelection>(stream, Json, ct) ?? TerminologySelection.Default;
        }
        catch (JsonException)
        {
            // A damaged file must not stop the UI from loading; fall back to the default terms.
            return TerminologySelection.Default;
        }
    }

    public async Task SaveAsync(TerminologySelection selection, CancellationToken ct = default)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(selection, Json), ct);
        File.Move(temp, path, overwrite: true);
    }
}
