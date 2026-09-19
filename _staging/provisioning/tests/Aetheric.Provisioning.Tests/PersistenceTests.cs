using System.Collections.Immutable;
using System.Security.Cryptography;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Persistence;
using Aetheric.Provisioning.Simulation;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "provisioning-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private FileRunStateStore State() => new(Path.Combine(_root, "runs"));
    private EncryptedFileSecretStore Secrets() => new(Path.Combine(_root, "secrets"), _key);
    private static ProvisioningPlan Plan(IResourceProvider provider) => new ProvisioningPlanner([provider]).Plan(DecisionsFixture.Load()).Plan!;
    private ProvisioningEngine Engine(IResourceProvider provider, IRunStateStore? state = null, IParentCapabilityResolver? parent = null)
        => new([provider], parent ?? new SimulatedParentResolver(DecisionsFixture.Load()), state ?? State(), Secrets());

    [Fact]
    public async Task Fresh_stores_and_engines_retry_with_the_same_credential_and_skip_completed_work()
    {
        var first = new SimulatedWorkbenchProvider { FailNext = true };
        var plan = Plan(first);
        Assert.False((await Engine(first).ExecuteAsync(plan)).Succeeded);
        var reference = first.LastCredential!;
        var value = await Secrets().ReadAsync(reference, default);
        var retry = new SimulatedWorkbenchProvider();
        Assert.True((await Engine(retry).ExecuteAsync(Plan(retry))).Succeeded);
        Assert.Equal(reference, retry.LastCredential);
        Assert.Equal(value, await Secrets().ReadAsync(retry.LastCredential!, default));
        var repeat = new SimulatedWorkbenchProvider();
        Assert.True((await Engine(repeat).ExecuteAsync(Plan(repeat))).Succeeded);
        Assert.Equal(0, repeat.Attempts);
        Assert.DoesNotContain(value, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(_root, "runs"), "*.json"))));
        Assert.DoesNotContain(value, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(
            Assert.Single(Directory.GetFiles(Path.Combine(_root, "secrets"), "*.secret")))));
    }

    [Fact]
    public async Task Creation_before_checkpoint_failure_is_reconciled_on_restart()
    {
        // This provider represents external infrastructure, which survives an engine restart.
        var provider = new SimulatedWorkbenchProvider();
        var plan = Plan(provider);
        await Assert.ThrowsAsync<IOException>(() => Engine(provider, new FailOwnedCheckpoint(State())).ExecuteAsync(plan));
        var reference = provider.LastCredential;
        Assert.Equal(1, provider.CreatedCount);
        var retry = await Engine(provider).ExecuteAsync(plan);
        Assert.True(retry.Succeeded);
        Assert.Equal("resource.exists", retry.Outcomes.Last().Code);
        Assert.Equal(reference, provider.LastCredential);
        Assert.Equal(1, provider.CreatedCount);
        Assert.Equal(2, provider.Attempts);
    }

    [Fact]
    public async Task Parent_outage_after_restart_preserves_completed_owned_work()
    {
        var provider = new SimulatedWorkbenchProvider();
        var plan = Plan(provider);
        Assert.True((await Engine(provider).ExecuteAsync(plan)).Succeeded);
        var parent = new SimulatedParentResolver(DecisionsFixture.Load());
        parent.UnavailableContracts.Add("IArchive");
        Assert.False((await Engine(provider, parent: parent).ExecuteAsync(plan)).Succeeded);
        Assert.True((await Engine(provider).ExecuteAsync(plan)).Succeeded);
        Assert.Equal(1, provider.Attempts);
    }

    [Fact]
    public async Task Separate_engines_serialize_the_entire_run()
    {
        var provider = new SimulatedWorkbenchProvider { Delay = TimeSpan.FromMilliseconds(100) };
        var plan = Plan(provider);
        var results = await Task.WhenAll(Engine(provider).ExecuteAsync(plan), Engine(provider).ExecuteAsync(plan));
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(1, provider.Attempts);
    }

    [Fact]
    public async Task Lock_wait_is_cancellable_and_release_allows_reacquisition()
    {
        await using (var lease = await State().AcquireAsync("plan", default))
        {
            using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => State().AcquireAsync("plan", ct.Token));
        }
        await using var next = await State().AcquireAsync("plan", default);
    }

    [Fact]
    public async Task Concurrent_secret_creation_across_stores_reuses_one_value()
    {
        var references = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Secrets().GetOrCreateAsync("scope", "name", default)));
        Assert.Single(references.Distinct());
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "secrets"), "*.secret"));
    }

    [Fact]
    public async Task Wrong_key_and_tampering_do_not_regenerate_a_secret()
    {
        var reference = await Secrets().GetOrCreateAsync("scope", "name", default);
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "secrets"), "*.secret"));
        var original = await File.ReadAllBytesAsync(path);
        var wrongKey = new EncryptedFileSecretStore(Path.Combine(_root, "secrets"), RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrongKey.GetOrCreateAsync("scope", "name", default));
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        original[^1] ^= 1;
        await File.WriteAllBytesAsync(path, original);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Secrets().ReadAsync(reference, default));
    }

    [Theory]
    [InlineData("broken JSON")]
    [InlineData("{\"Version\":2,\"State\":null}")]
    [InlineData("{\"Version\":1,\"State\":{\"PlanId\":\"wrong\",\"Outcomes\":{}}}")]
    public async Task Invalid_checkpoint_stops_execution_before_provider_work(string invalid)
    {
        var provider = new SimulatedWorkbenchProvider();
        var plan = Plan(provider);
        await State().SaveAsync(new(plan.Id, ImmutableDictionary<string, StepOutcome>.Empty), default);
        await File.WriteAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(_root, "runs"), "*.json")), invalid);
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine(provider).ExecuteAsync(plan));
        Assert.Equal(0, provider.Attempts);
    }

    [Fact]
    public async Task Cancelled_save_leaves_the_previous_checkpoint_intact()
    {
        var state = new RunState("../../plan", ImmutableDictionary<string, StepOutcome>.Empty);
        await State().SaveAsync(state, default);
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => State().SaveAsync(state, ct.Token));
        Assert.Equal(state.PlanId, (await State().ReadAsync(state.PlanId, default))!.PlanId);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "runs"), "*.tmp"));
    }

    [Theory]
    [InlineData("fail", 0)]
    [InlineData("crash", 23)]
    public async Task Separate_processes_resume_with_stable_credentials_and_release_crashed_locks(string first, int expectedExit)
    {
        async Task<int> Run(string phase)
        {
            var start = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(DurableHarness).Assembly.Location);
            start.ArgumentList.Add("--durable");
            start.ArgumentList.Add(_root);
            start.ArgumentList.Add(phase);
            start.Environment["PROVISIONING_TEST_KEY"] = Convert.ToBase64String(_key);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            Assert.True(process.ExitCode is 0 or 23, await output + await error);
            return process.ExitCode;
        }
        Assert.Equal(expectedExit, await Run(first));
        var original = await File.ReadAllTextAsync(Path.Combine(_root, "credential-fingerprint"));
        Assert.Equal(0, await Run("retry"));
        Assert.Equal(0, await Run("repeat"));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_root, "credential-fingerprint")));
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(_root, "external-resource")));
    }

    [Fact]
    public async Task Completed_checkpoint_with_missing_credentials_does_not_report_success()
    {
        var provider = new SimulatedWorkbenchProvider();
        var plan = Plan(provider);
        Assert.True((await Engine(provider).ExecuteAsync(plan)).Succeeded);
        File.Delete(Assert.Single(Directory.GetFiles(Path.Combine(_root, "secrets"), "*.secret")));
        await Assert.ThrowsAsync<FileNotFoundException>(() => Engine(provider).ExecuteAsync(plan));
        Assert.Equal(1, provider.Attempts);
    }

    [Fact]
    public async Task Cancelled_attempt_can_resume_from_a_fresh_engine()
    {
        var provider = new SimulatedWorkbenchProvider { Delay = TimeSpan.FromSeconds(5) };
        var plan = Plan(provider);
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cancelled = await Engine(provider).ExecuteAsync(plan, cancellationToken: ct.Token);
        Assert.Equal(OutcomeStatus.Cancelled, cancelled.Outcomes.Last().Status);
        var retry = new SimulatedWorkbenchProvider();
        Assert.True((await Engine(retry).ExecuteAsync(plan)).Succeeded);
        Assert.Equal(1, retry.CreatedCount);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private sealed class FailOwnedCheckpoint(IRunStateStore inner) : IRunStateStore
    {
        public Task<RunState?> ReadAsync(string id, CancellationToken ct) => inner.ReadAsync(id, ct);
        public Task SaveAsync(RunState state, CancellationToken ct)
        {
            if (state.Outcomes.Values.Any(x => x.StepId.StartsWith("owned:", StringComparison.Ordinal) && x.IsSuccessful))
                throw new IOException("Simulated checkpoint failure after provider success.");
            return inner.SaveAsync(state, ct);
        }
    }
}
