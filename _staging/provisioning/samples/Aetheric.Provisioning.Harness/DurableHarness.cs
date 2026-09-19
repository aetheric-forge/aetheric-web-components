using System.Collections.Immutable;
using System.Security.Cryptography;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Persistence;
using Aetheric.Provisioning.Simulation;

// Process-restart acceptance fixture only. Resource files represent external provider state.
public static class DurableHarness
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 3 || args[0] != "--durable" || args[2] is not ("fail" or "retry" or "repeat" or "crash")) return 2;
        var root = Path.GetFullPath(args[1]);
        var key = Convert.FromBase64String(Environment.GetEnvironmentVariable("PROVISIONING_TEST_KEY")
            ?? throw new InvalidOperationException("PROVISIONING_TEST_KEY is required for this test fixture."));
        var state = new FileRunStateStore(Path.Combine(root, "runs"));
        var secrets = new EncryptedFileSecretStore(Path.Combine(root, "secrets"), key);
        var provider = new DurableTestProvider(root, args[2]);
        var input = DecisionsFixture.Load();
        var plan = new ProvisioningPlanner([provider]).Plan(input).Plan!;
        var result = await new ProvisioningEngine([provider], new SimulatedParentResolver(input), state, secrets).ExecuteAsync(plan);
        var passed = args[2] switch
        {
            "fail" => !result.Succeeded && provider.Attempts == 1,
            "retry" => result.Succeeded && provider.Attempts == 1,
            "repeat" => result.Succeeded && provider.Attempts == 0,
            _ => false
        };
        Console.WriteLine(passed ? "Durable simulation phase passed." : "Durable simulation phase failed.");
        return passed ? 0 : 1;
    }

    private sealed class DurableTestProvider(string root, string phase) : IResourceProvider
    {
        public string Key => "workbench";
        public int Attempts { get; private set; }
        public ImmutableArray<ValidationIssue> Validate(ResourceRequirement resource, ResourceBinding binding)
            => new SimulatedWorkbenchProvider().Validate(resource, binding);
        public async Task<ProviderResult> EnsureAsync(ProviderContext context, CancellationToken ct)
        {
            Attempts++;
            var reference = await context.Secrets.GetOrCreateAsync(context.PlanId, "test-access", ct);
            var value = await context.Secrets.ReadAsync(reference, ct);
            var fingerprint = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
            var identity = reference.Id + ":" + fingerprint;
            var credentialPath = Path.Combine(root, "credential-fingerprint");
            if (File.Exists(credentialPath))
            {
                if (await File.ReadAllTextAsync(credentialPath, ct) != identity) throw new InvalidOperationException("Credential changed.");
            }
            else await File.WriteAllTextAsync(credentialPath, identity, ct);
            if (phase == "fail") throw new IOException("Simulated transient provider failure.");
            var resourcePath = Path.Combine(root, "external-resource");
            var exists = File.Exists(resourcePath);
            if (exists && await File.ReadAllTextAsync(resourcePath, ct) != identity) throw new InvalidOperationException("Resource conflict.");
            if (!exists) await File.WriteAllTextAsync(resourcePath, identity, ct);
            if (phase == "crash") Environment.Exit(23); // Kill process before engine can checkpoint success or release its lease.
            return new(exists, [reference]);
        }
    }
}
