using System.Collections.Immutable;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Simulation;
using Aetheric.Provisioning.Workbench;
using Aetheric.Provisioning.Workbench.Redis;
using StackExchange.Redis;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class WorkbenchTests
{
    private static ResourceBinding Binding => new("workbench",
        new Dictionary<string, string> { ["backing"] = "redis", ["stage"] = "test-workbench",
            ["target"] = "local-redis-v1", ["fallback"] = "none" }.ToImmutableDictionary(),
        ImmutableDictionary<string, SecretReference>.Empty);
    private static ResourceRequirement Resource => new("workspace", "Workspace", "staging", "owned");

    [Theory]
    [InlineData("fallback", "in-memory")]
    [InlineData("target", "different-target")]
    [InlineData("stage", "unsafe:*:stage")]
    [InlineData("connection", "sensitive")]
    [InlineData("backing", "memory")]
    public void Invalid_bindings_never_reach_backend(string key, string value)
    {
        var backend = new Backend();
        var provider = new WorkbenchProvider("local-redis-v1", backend);
        Assert.NotEmpty(provider.Validate(Resource, Binding with { Settings = Binding.Settings.SetItem(key, value) }));
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task Adapter_passes_ownership_and_reuses_workspace_without_creating_secrets()
    {
        var backend = new Backend();
        var provider = new WorkbenchProvider("local-redis-v1", backend);
        var context = new ProviderContext("plan", "development", "standalone-workbench", Resource, Binding, new NoSecrets());
        Assert.Empty(provider.Validate(Resource, Binding));
        Assert.False((await provider.EnsureAsync(context, default)).AlreadyExists);
        var repeated = await provider.EnsureAsync(context, default);
        Assert.True(repeated.AlreadyExists);
        Assert.Empty(repeated.Secrets);
        Assert.Equal(new WorkspaceRequest("development", "standalone-workbench", "workspace", "test-workbench"), backend.Last);
    }

    [Fact]
    public async Task Decisions_parent_failure_prevents_live_workbench_calls()
    {
        var input = DecisionsFixture.Load();
        input = input with { Bindings = input.Bindings with { Resources = input.Bindings.Resources.SetItem("draft-workspace", Binding) } };
        var backend = new Backend();
        var provider = new WorkbenchProvider("local-redis-v1", backend);
        var parent = new SimulatedParentResolver(input);
        parent.UnavailableContracts.Add("IArchive");
        var plan = new ProvisioningPlanner([provider]).Plan(input);
        Assert.True(plan.IsValid);
        var engine = new ProvisioningEngine([provider], parent, new InMemoryRunStateStore(), new NoSecrets());
        var result = await engine.ExecuteAsync(plan.Plan!);
        Assert.False(result.Succeeded);
        Assert.Equal(0, backend.Calls);
        Assert.Contains(result.Outcomes, x => x.StepId == "owned:draft-workspace" && x.Status == OutcomeStatus.Blocked);
    }

    [RedisFact]
    public async Task Redis_registration_reconnect_conflict_and_probe_cleanup()
    {
        var connection = Environment.GetEnvironmentVariable("WORKBENCH_TEST_REDIS")!;
        using var redis = await ConnectionMultiplexer.ConnectAsync(connection);
        var db = redis.GetDatabase();
        var stage = "test-" + Guid.NewGuid().ToString("N");
        var request = new WorkspaceRequest("integration", "standalone-workbench", "workspace", stage);
        var key = RedisWorkbenchBackend.RegistrationKey(stage);
        try
        {
            Assert.False((await new RedisWorkbenchBackend(db).EnsureAsync(request, default)).AlreadyExists);
            var marker = await db.StringGetAsync(key);
            using var restarted = await ConnectionMultiplexer.ConnectAsync(connection);
            Assert.True((await new RedisWorkbenchBackend(restarted.GetDatabase()).EnsureAsync(request, default)).AlreadyExists);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new RedisWorkbenchBackend(db).EnsureAsync(request with { Institution = "other-owner" }, default));
            Assert.Equal(marker, await db.StringGetAsync(key));
            Assert.Null(await db.KeyTimeToLiveAsync(key));
            var server = redis.GetServer(redis.GetEndPoints().Single());
            Assert.Empty(server.Keys(db.Database, stage + ":*"));
        }
        finally { await db.KeyDeleteAsync(key); }
    }

    [RedisFact]
    public async Task Redis_refuses_unregistered_data_and_expiring_ownership()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("WORKBENCH_TEST_REDIS")!);
        var db = redis.GetDatabase();
        var stage = "test-" + Guid.NewGuid().ToString("N");
        var request = new WorkspaceRequest("integration", "standalone-workbench", "workspace", stage);
        var key = RedisWorkbenchBackend.RegistrationKey(stage);
        var data = stage + ":data:existing-draft";
        try
        {
            await db.StringSetAsync(data, "untouched");
            var backend = new RedisWorkbenchBackend(db);
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.EnsureAsync(request, default));
            Assert.Equal("untouched", (string?)await db.StringGetAsync(data));
            Assert.False(await db.KeyExistsAsync(key));
            await db.KeyDeleteAsync(data);
            await backend.EnsureAsync(request, default);
            await db.KeyExpireAsync(key, TimeSpan.FromMinutes(10));
            await Assert.ThrowsAsync<InvalidOperationException>(() => backend.EnsureAsync(request, default));
        }
        finally { await db.KeyDeleteAsync([key, data]); }
    }

    [RedisFact]
    public async Task Redis_concurrent_owners_cannot_both_claim_a_stage()
    {
        using var redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("WORKBENCH_TEST_REDIS")!);
        var db = redis.GetDatabase();
        var stage = "test-" + Guid.NewGuid().ToString("N");
        async Task<bool> Attempt(string owner)
        {
            try { await new RedisWorkbenchBackend(db).EnsureAsync(new("integration", owner, "workspace", stage), default); return true; }
            catch (InvalidOperationException) { return false; }
        }
        try
        {
            var results = await Task.WhenAll(Attempt("first"), Attempt("second"));
            Assert.Single(results, x => x);
        }
        finally { await db.KeyDeleteAsync(RedisWorkbenchBackend.RegistrationKey(stage)); }
    }

    private sealed class Backend : IWorkbenchBackend
    {
        public int Calls { get; private set; }
        public WorkspaceRequest? Last { get; private set; }
        public Task<WorkspaceResult> EnsureAsync(WorkspaceRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Last = request;
            return Task.FromResult(new WorkspaceResult(Calls++ > 0));
        }
    }
    private sealed class NoSecrets : ISecretStore
    {
        public Task<SecretReference> GetOrCreateAsync(string scope, string name, CancellationToken ct) => throw new InvalidOperationException();
        public Task<string> ReadAsync(SecretReference reference, CancellationToken ct) => throw new InvalidOperationException();
    }
}

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WORKBENCH_TEST_REDIS")))
            Skip = "Set WORKBENCH_TEST_REDIS to an isolated standalone Redis database.";
    }
}
