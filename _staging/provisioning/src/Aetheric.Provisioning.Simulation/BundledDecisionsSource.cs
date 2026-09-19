using System.Security.Cryptography;
using System.Text;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Simulation;

public static class BundledDecisionsSource
{
    public static SourceBundle Load() => new(Read("decisions-institution.yaml",
        "73a81f72a0f2233946035712accbb394263afe159847ce00c69cd826fefec0a1"),
        Read("decisions-institution.bindings.yaml", "5b99ba92c44ce97b228b2951c71a7bf30772edb9ad0f8c1388f8a386ea10fe69"));
    private static SourceDocument Read(string name, string expectedHash)
    {
        var assembly = typeof(DecisionsFixture).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(x => x.EndsWith(name, StringComparison.Ordinal)))!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedHash) throw new InvalidOperationException("Bundled fixture hash mismatch.");
        return new(Encoding.UTF8.GetString(bytes), new("https://github.com/aetheric-forge/adr-campus", DecisionsFixture.Commit, "institution/" + name, expectedHash));
    }
}

// Explicitly simulated: an advertised catalog is not evidence of live provider access.
public sealed class SimulatedCatalogParentResolver : IParentCapabilityResolver
{
    public Task<bool> IsAvailableAsync(ParentContext parent, string contract, string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(parent.Capabilities.TryGetValue(contract, out var available) && available == source);
    }
}
