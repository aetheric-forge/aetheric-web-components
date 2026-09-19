using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Simulation;

public sealed class InMemoryRunStateStore : IRunStateStore
{
    private readonly ConcurrentDictionary<string, RunState> _states = new();
    public Task<RunState?> ReadAsync(string planId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_states.GetValueOrDefault(planId));
    }
    public Task SaveAsync(RunState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _states[state.PlanId] = state;
        return Task.CompletedTask;
    }
}

// Test/demo only: secret values live in process memory, never in ordinary run state.
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string, string), SecretReference> _keys = [];
    private readonly Dictionary<string, string> _values = [];
    public SecretReference AddOverride(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        lock (_gate)
        {
            var reference = new SecretReference(Guid.NewGuid().ToString("N"));
            _values.Add(reference.Id, value);
            return reference;
        }
    }
    public Task<SecretReference> GetOrCreateAsync(string scope, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_keys.TryGetValue((scope, name), out var reference))
            {
                reference = AddOverride(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                _keys.Add((scope, name), reference);
            }
            return Task.FromResult(reference);
        }
    }
    public Task<string> ReadAsync(SecretReference reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult(_values[reference.Id]);
    }
}

public sealed class SimulatedParentResolver(PlanningInput input) : IParentCapabilityResolver
{
    public HashSet<string> UnavailableContracts { get; } = [];
    public Task<bool> IsAvailableAsync(ParentContext parent, string contract, string source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(parent == input.Parent && !UnavailableContracts.Contains(contract)
            && input.Bindings.ParentSources.TryGetValue(contract, out var expected) && source == expected);
    }
}

public sealed class SimulatedWorkbenchProvider : IResourceProvider
{
    private readonly Dictionary<string, (ResourceBinding Binding, SecretReference Secret)> _resources = [];
    public string Key => "workbench";
    public bool FailNext { get; set; }
    public int Attempts { get; private set; }
    public int CreatedCount => _resources.Count;
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    public SecretReference? LastCredential { get; private set; }

    public ImmutableArray<ValidationIssue> Validate(ResourceRequirement resource, ResourceBinding binding)
    {
        var valid = resource.Category == "staging" && binding.Provider == Key
            && binding.Settings.GetValueOrDefault("backing") == "redis"
            && binding.Settings.TryGetValue("stage", out var stage) && !string.IsNullOrWhiteSpace(stage)
            && binding.Settings.GetValueOrDefault("fallback") is null or "in-memory"
            && binding.Settings.Keys.All(k => k is "backing" or "stage" or "fallback")
            && binding.Secrets.Keys.All(k => k == "simulation-access");
        return valid ? [] : [new("simulation.binding", resource.Id,
            "The simulation supports a staging workspace with Redis backing and an explicit stage only.")];
    }

    public async Task<ProviderResult> EnsureAsync(ProviderContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Attempts++;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        // This synthetic credential exercises the secret seam; it is not a claim about Redis allocation.
        var credential = context.Binding.Secrets.GetValueOrDefault("simulation-access")
            ?? await context.Secrets.GetOrCreateAsync(context.PlanId, context.Resource.Id + "/simulation-access", ct);
        _ = await context.Secrets.ReadAsync(credential, ct);
        LastCredential = credential;
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("Simulated transient failure.");
        }
        var key = string.Join("/", context.Environment, context.InstitutionId, context.Resource.Id);
        if (_resources.TryGetValue(key, out var existing))
        {
            if (!existing.Binding.Settings.OrderBy(x => x.Key).SequenceEqual(context.Binding.Settings.OrderBy(x => x.Key))
                || existing.Secret != credential)
                throw new InvalidOperationException("Existing simulated resource conflicts with the requested binding.");
            return new(true, [existing.Secret]);
        }
        _resources.Add(key, (context.Binding, credential));
        return new(false, [credential]);
    }
}

public sealed class SimulationSession
{
    public PlanningInput Input { get; } = DecisionsFixture.Load();
    public SimulatedWorkbenchProvider Provider { get; } = new();
    public InMemoryRunStateStore State { get; } = new();
    public InMemorySecretStore Secrets { get; } = new();
    public SimulatedParentResolver Parent { get; }
    public ProvisioningPlanner Planner { get; }
    public ProvisioningEngine Engine { get; }
    public SimulationSession()
    {
        Parent = new(Input);
        Planner = new([Provider]);
        Engine = new([Provider], Parent, State, Secrets);
    }
}
