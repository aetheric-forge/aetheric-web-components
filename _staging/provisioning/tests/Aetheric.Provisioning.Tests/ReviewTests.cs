using System.Collections.Immutable;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Simulation;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class ReviewTests
{
    private static (ProvisioningReview Review, SimulatedWorkbenchProvider Provider) Create(IDefinitionSource? source = null)
    {
        var provider = new SimulatedWorkbenchProvider();
        var planner = new ProvisioningPlanner([provider]);
        var engine = new ProvisioningEngine([provider], new SimulatedCatalogParentResolver(), new InMemoryRunStateStore(), new InMemorySecretStore());
        var review = new ProvisioningReview(source ?? new FixtureSource(), new(), planner, engine);
        review.LoadBundle(BundledDecisionsSource.Load());
        review.Configure(review.Bindings!, new("simulated-campus", "1") { Capabilities = review.Bindings!.ParentSources });
        return (review, provider);
    }

    [Fact]
    public async Task Review_has_no_side_effects_and_execution_requires_exact_plan_approval()
    {
        var (review, provider) = Create();
        var result = review.Review();
        Assert.True(result.IsValid);
        Assert.Equal(0, provider.Attempts);
        Assert.Equal("review.approval_required", Assert.Single((await review.ExecuteApprovedAsync()).Issues).Code);
        Assert.False(review.Approve("wrong-plan"));
        Assert.True(review.Approve(result.Plan!.Id));
        Assert.True((await review.ExecuteApprovedAsync()).Run!.Succeeded);
        Assert.Equal(1, provider.CreatedCount);
    }

    [Fact]
    public async Task Override_invalidates_approval_and_changes_plan_without_rewriting_definition()
    {
        var (review, provider) = Create();
        var original = review.Review().Plan!;
        Assert.True(review.Approve(original.Id));
        var source = review.Loaded!.Source.Definition;
        var binding = review.Bindings!.Resources["draft-workspace"];
        review.Configure(review.Bindings with { Resources = review.Bindings.Resources.SetItem("draft-workspace", binding with
            { Settings = binding.Settings.SetItem("stage", "overridden-stage") }) }, review.Parent);
        Assert.Null(review.Plan);
        Assert.False(review.IsApproved);
        Assert.Null((await review.ExecuteApprovedAsync()).Run);
        var changed = review.Review().Plan!;
        Assert.NotEqual(original.Id, changed.Id);
        Assert.False(review.Approve(original.Id));
        Assert.Equal(source, review.Loaded.Source.Definition);
        Assert.Equal("overridden-stage", changed.Steps.Last().Binding!.Settings["stage"]);
        Assert.Equal(0, provider.Attempts);
    }

    [Fact]
    public void Parent_catalog_changes_affect_identity_even_with_same_label_and_revision()
    {
        var (review, _) = Create();
        var original = review.Review().Plan!;
        review.Configure(review.Bindings!, review.Parent with { Capabilities = review.Parent.Capabilities.Add("IExtra", "campus.extra") });
        Assert.NotEqual(original.Id, review.Review().Plan!.Id);
    }

    [Fact]
    public async Task Missing_parent_capability_prevents_review_and_execution()
    {
        var (review, provider) = Create();
        review.Configure(review.Bindings!, review.Parent with { Capabilities = review.Parent.Capabilities.Remove("IArchive") });
        var result = review.Review();
        Assert.False(result.IsValid);
        Assert.Equal("IArchive", Assert.Single(result.Issues).Target);
        Assert.Null((await review.ExecuteApprovedAsync()).Run);
        Assert.Equal(0, provider.Attempts);
    }

    [Fact]
    public async Task Editing_source_or_reloading_invalid_data_cannot_execute_an_old_plan()
    {
        var (review, provider) = Create();
        review.Approve(review.Review().Plan!.Id);
        review.ClearSource();
        Assert.Null(review.Loaded);
        Assert.Null((await review.ExecuteApprovedAsync()).Run);
        var bundle = BundledDecisionsSource.Load();
        review.LoadBundle(bundle with { Definition = DefinitionLoadingTests.Replace(bundle.Definition, "invalid: yaml") });
        Assert.Null(review.Loaded);
        Assert.Null(review.Plan);
        Assert.False(review.IsApproved);
        Assert.Equal(0, provider.Attempts);
    }

    [Fact]
    public async Task An_outdated_load_response_cannot_restore_cleared_source_or_approval()
    {
        var source = new DelayedSource();
        var (review, _) = Create(source);
        var pending = review.LoadAsync(DefinitionLoadingTests.Request());
        review.ClearSource();
        source.Completion.SetResult(new(BundledDecisionsSource.Load(), []));
        await pending;
        Assert.Null(review.Loaded);
        Assert.Null(review.Plan);
    }

    [Fact]
    public void Unsupported_provider_choice_blocks_review()
    {
        var (review, _) = Create();
        var binding = review.Bindings!.Resources["draft-workspace"];
        review.Configure(review.Bindings with { Resources = review.Bindings.Resources.SetItem("draft-workspace", binding with { Provider = "mongodb" }) }, review.Parent);
        Assert.Contains(review.Review().Issues, x => x.Code == "provider.unsupported");
    }

    [Fact]
    public async Task Revoking_approval_keeps_review_but_prevents_execution()
    {
        var (review, _) = Create();
        review.Approve(review.Review().Plan!.Id);
        review.RevokeApproval();
        Assert.NotNull(review.Plan);
        Assert.Null((await review.ExecuteApprovedAsync()).Run);
    }

    private sealed class FixtureSource : IDefinitionSource
    {
        public Task<SourceLoadResult> LoadAsync(DefinitionSourceRequest request, CancellationToken ct = default)
            => Task.FromResult(new SourceLoadResult(BundledDecisionsSource.Load(), []));
    }
    private sealed class DelayedSource : IDefinitionSource
    {
        public TaskCompletionSource<SourceLoadResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SourceLoadResult> LoadAsync(DefinitionSourceRequest request, CancellationToken ct = default) => Completion.Task;
    }
}
