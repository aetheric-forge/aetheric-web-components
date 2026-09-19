using System.Collections.Immutable;
using System.Text.Json;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Simulation;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class EngineTests
{
    private static ProvisioningPlan Plan(SimulationSession session, PlanningInput? input = null)
    {
        var result = session.Planner.Plan(input ?? session.Input);
        Assert.Empty(result.Issues);
        return Assert.IsType<ProvisioningPlan>(result.Plan);
    }

    [Fact]
    public void Fixture_preserves_owned_and_inherited_requirements()
    {
        var session = new SimulationSession();
        var plan = Plan(session);
        Assert.Equal(DecisionsFixture.Commit, plan.Input.DefinitionSource.Commit);
        Assert.Equal(2, plan.Input.Institution.Resources.Length);
        Assert.Equal("parent", plan.Input.Institution.Resources.Single(r => r.Id == "decision-record").Ownership);
        Assert.Equal(4, plan.Steps.Count(s => s.Kind == StepKind.CheckParent));
        var owned = Assert.Single(plan.Steps.Where(s => s.Kind == StepKind.ProvisionOwned));
        Assert.Equal("draft-workspace", owned.Target);
        Assert.Equal("workbench", owned.Binding!.Provider);
        Assert.Equal("redis", owned.Binding.Settings["backing"]);
        Assert.Equal(4, owned.DependsOn.Length);
        Assert.Equal(0, session.Provider.Attempts);
    }

    [Theory]
    [InlineData("ownership", "resource.ownership")]
    [InlineData("identity", "bindings.identity")]
    [InlineData("missing", "resource.binding_missing")]
    [InlineData("provider", "provider.unsupported")]
    [InlineData("parent", "parent.binding")]
    [InlineData("dependency", "dependency.binding_missing")]
    [InlineData("duplicate", "resource.id")]
    [InlineData("setting", "simulation.binding")]
    [InlineData("commit", "source.invalid")]
    public void Invalid_inputs_produce_issues_and_no_executable_plan(string scenario, string code)
    {
        var session = new SimulationSession();
        var input = session.Input;
        var binding = input.Bindings.Resources["draft-workspace"];
        input = scenario switch
        {
            "ownership" => input with { Institution = input.Institution with
                { Resources = input.Institution.Resources.SetItem(0, input.Institution.Resources[0] with { Ownership = "unknown" }) } },
            "identity" => input with { Bindings = input.Bindings with { InstitutionId = "another" } },
            "missing" => input with { Bindings = input.Bindings with { Resources = input.Bindings.Resources.Clear() } },
            "provider" => input with { Bindings = input.Bindings with
                { Resources = input.Bindings.Resources.SetItem("draft-workspace", binding with { Provider = "unknown" }) } },
            "parent" => input with { Bindings = input.Bindings with
                { Resources = input.Bindings.Resources.Add("decision-record", binding) } },
            "dependency" => input with { Bindings = input.Bindings with
                { ParentSources = input.Bindings.ParentSources.Remove("IArchive") } },
            "duplicate" => input with { Institution = input.Institution with
                { Resources = input.Institution.Resources.Add(input.Institution.Resources[0]) } },
            "setting" => input with { Bindings = input.Bindings with { Resources = input.Bindings.Resources.SetItem(
                "draft-workspace", binding with { Settings = binding.Settings.Add("password", "must-not-be-a-setting") }) } },
            "commit" => input with { DefinitionSource = input.DefinitionSource with { Commit = "main" } },
            _ => throw new InvalidOperationException()
        };
        var result = session.Planner.Plan(input);
        Assert.False(result.IsValid);
        Assert.Null(result.Plan);
        Assert.Contains(result.Issues, i => i.Code == code);
        Assert.Equal(0, session.Provider.Attempts);
    }

    [Fact]
    public void Identity_is_stable_for_equivalent_inputs_and_changes_with_context_or_bindings()
    {
        var session = new SimulationSession();
        var original = Plan(session);
        var reversed = session.Input with { Institution = session.Input.Institution with
        {
            Resources = session.Input.Institution.Resources.Reverse().ToImmutableArray(),
            ParentContracts = session.Input.Institution.ParentContracts.Reverse().ToImmutableArray()
        } };
        Assert.Equal(original.Id, Plan(session, reversed).Id);
        Assert.NotEqual(original.Id, Plan(session, session.Input with { Parent = new("other-parent", "1") }).Id);
        Assert.NotEqual(original.Id, Plan(session, session.Input with { Parent = new("simulated-campus", "2") }).Id);
        Assert.NotEqual(original.Id, Plan(session, session.Input with
            { Bindings = session.Input.Bindings with { Environment = "other-environment" } }).Id);
        Assert.NotEqual(original.Id, Plan(session, session.Input with
            { DefinitionSource = session.Input.DefinitionSource with { Commit = new string('a', 40) } }).Id);
        var binding = session.Input.Bindings.Resources["draft-workspace"];
        var changed = session.Input with { Bindings = session.Input.Bindings with
            { Resources = session.Input.Bindings.Resources.SetItem("draft-workspace", binding with
                { Settings = binding.Settings.SetItem("stage", "another-stage") }) } };
        Assert.NotEqual(original.Id, Plan(session, changed).Id);
        Assert.Equal("adr-campus-workbench", original.Steps.Last().Binding!.Settings["stage"]);
    }

    [Fact]
    public async Task Failed_owned_action_retries_with_same_secret_and_repeat_skips_completed_work()
    {
        var session = new SimulationSession();
        var plan = Plan(session);
        session.Provider.FailNext = true;
        var progress = new CaptureProgress();
        var failed = await session.Engine.ExecuteAsync(plan, progress);
        Assert.False(failed.Succeeded);
        Assert.Equal(OutcomeStatus.Failed, failed.Outcomes.Last().Status);
        Assert.Equal(5, progress.Items.Count);
        var credential = Assert.IsType<SecretReference>(session.Provider.LastCredential);
        var retried = await session.Engine.ExecuteAsync(plan);
        Assert.True(retried.Succeeded);
        Assert.Equal(credential, session.Provider.LastCredential);
        Assert.Equal(1, session.Provider.CreatedCount);
        var repeated = await session.Engine.ExecuteAsync(plan);
        Assert.True(repeated.Succeeded);
        Assert.Equal(OutcomeStatus.AlreadySatisfied, repeated.Outcomes.Last().Status);
        Assert.Equal(2, session.Provider.Attempts);
        var secretValue = await session.Secrets.ReadAsync(credential, default);
        var state = await session.State.ReadAsync(plan.Id, default);
        Assert.DoesNotContain(secretValue, JsonSerializer.Serialize(state));
        Assert.DoesNotContain(secretValue, JsonSerializer.Serialize(plan));
        Assert.DoesNotContain(secretValue, JsonSerializer.Serialize(progress.Items));
    }

    [Fact]
    public async Task Missing_parent_blocks_owned_work_and_is_rechecked_on_retry()
    {
        var session = new SimulationSession();
        var plan = Plan(session);
        session.Parent.UnavailableContracts.Add("IArchive");
        var failed = await session.Engine.ExecuteAsync(plan);
        Assert.False(failed.Succeeded);
        Assert.Contains(failed.Outcomes, x => x.StepId == "parent:IArchive" && x.Code == "parent.unavailable");
        Assert.Equal(OutcomeStatus.Blocked, failed.Outcomes.Last().Status);
        Assert.Equal(0, session.Provider.Attempts);
        session.Parent.UnavailableContracts.Clear();
        Assert.True((await session.Engine.ExecuteAsync(plan)).Succeeded);
        session.Parent.UnavailableContracts.Add("IArchive");
        Assert.False((await session.Engine.ExecuteAsync(plan)).Succeeded);
        session.Parent.UnavailableContracts.Clear();
        Assert.True((await session.Engine.ExecuteAsync(plan)).Succeeded);
        Assert.Equal(1, session.Provider.Attempts);
    }

    [Fact]
    public async Task Secret_override_is_referenced_and_never_replaced_with_a_generated_value()
    {
        var session = new SimulationSession();
        var supplied = session.Secrets.AddOverride("test-only-override");
        var binding = session.Input.Bindings.Resources["draft-workspace"];
        var input = session.Input with { Bindings = session.Input.Bindings with
            { Resources = session.Input.Bindings.Resources.SetItem("draft-workspace", binding with
                { Secrets = binding.Secrets.Add("simulation-access", supplied) }) } };
        var result = await session.Engine.ExecuteAsync(Plan(session, input));
        Assert.True(result.Succeeded);
        Assert.Equal(supplied, session.Provider.LastCredential);
        Assert.DoesNotContain("test-only-override", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Cancellation_records_outcome_and_allows_retry()
    {
        var session = new SimulationSession();
        var plan = Plan(session);
        using var cancellation = new CancellationTokenSource();
        var progress = new CaptureProgress(p =>
        {
            if (p.Outcome.StepId == plan.Steps[^2].Id) cancellation.Cancel();
        });
        var result = await session.Engine.ExecuteAsync(plan, progress, cancellation.Token);
        Assert.False(result.Succeeded);
        Assert.Equal(OutcomeStatus.Cancelled, result.Outcomes.Last().Status);
        Assert.Equal(0, session.Provider.Attempts);
        Assert.True((await session.Engine.ExecuteAsync(plan)).Succeeded);
    }

    [Fact]
    public async Task A_new_engine_can_reuse_the_same_in_memory_checkpoint()
    {
        var session = new SimulationSession();
        var plan = Plan(session);
        await session.Engine.ExecuteAsync(plan);
        var another = new ProvisioningEngine([session.Provider], session.Parent, session.State, session.Secrets);
        Assert.True((await another.ExecuteAsync(plan)).Succeeded);
        Assert.Equal(1, session.Provider.Attempts);
    }

    [Fact]
    public async Task Concurrent_calls_on_one_engine_do_not_duplicate_work()
    {
        var session = new SimulationSession();
        session.Provider.Delay = TimeSpan.FromMilliseconds(20);
        var plan = Plan(session);
        var results = await Task.WhenAll(session.Engine.ExecuteAsync(plan), session.Engine.ExecuteAsync(plan));
        Assert.All(results, x => Assert.True(x.Succeeded));
        Assert.Equal(1, session.Provider.Attempts);
    }

    [Fact]
    public async Task Provider_exceptions_are_not_exposed_to_progress_or_results()
    {
        var provider = new ThrowingProvider();
        var input = DecisionsFixture.Load();
        var plan = new ProvisioningPlanner([provider]).Plan(input).Plan!;
        var engine = new ProvisioningEngine([provider], new SimulatedParentResolver(input),
            new InMemoryRunStateStore(), new InMemorySecretStore());
        var progress = new CaptureProgress();
        var result = await engine.ExecuteAsync(plan, progress);
        Assert.False(result.Succeeded);
        Assert.Equal("operation.failed", result.Outcomes.Last().Code);
        Assert.DoesNotContain("sensitive-provider-value", JsonSerializer.Serialize(result));
        Assert.DoesNotContain("sensitive-provider-value", JsonSerializer.Serialize(progress.Items));
    }

    [Fact]
    public void Engine_has_no_host_or_yaml_dependencies()
    {
        var references = typeof(ProvisioningEngine).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(references, a => a.Name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
            || a.Name.StartsWith("YamlDotNet", StringComparison.Ordinal)
            || a.Name.StartsWith("Aetheric.Provisioning.Simulation", StringComparison.Ordinal));
    }

    private sealed class CaptureProgress(Action<ProvisioningProgress>? action = null) : IProgress<ProvisioningProgress>
    {
        public List<ProvisioningProgress> Items { get; } = [];
        public void Report(ProvisioningProgress value) { Items.Add(value); action?.Invoke(value); }
    }
    private sealed class ThrowingProvider : IResourceProvider
    {
        public string Key => "workbench";
        public ImmutableArray<ValidationIssue> Validate(ResourceRequirement r, ResourceBinding b) => [];
        public Task<ProviderResult> EnsureAsync(ProviderContext context, CancellationToken ct)
            => throw new InvalidOperationException("sensitive-provider-value");
    }
}
