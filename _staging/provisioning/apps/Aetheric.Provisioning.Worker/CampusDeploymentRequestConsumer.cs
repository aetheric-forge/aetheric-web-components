using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;
using AethericForge.Runtime.Abstractions.Interfaces.Post;
using AethericForge.Runtime.Abstractions.Interfaces.Post.Consumers;
using AethericForge.Runtime.Abstractions.Interfaces.Post.Primitives;
using AethericForge.Runtime.Abstractions.Interfaces.Post.Providers;
using AethericForge.Runtime.Models.Post;

namespace Aetheric.Provisioning.Worker;

/// <summary>
/// A template campus has no parent - institution/campus.yaml's five resources are all
/// ownership: "owned". This resolver exists only to satisfy ProvisioningEngine's constructor;
/// a root/no-parent plan never generates a CheckParent step that would call it.
/// </summary>
public sealed class NoParentCapabilityResolver : IParentCapabilityResolver
{
    public Task<bool> IsAvailableAsync(ParentContext parent, string contract, string source, CancellationToken ct) =>
        Task.FromResult(false);
}

/// <summary>
/// Runs the plan/execute pipeline for a requested campus deployment and reports the outcome
/// back. Today this is expected to report failure - provider.unsupported for every resource
/// except Workbench, since no IResourceProvider beyond Workbench exists yet, and even Workbench
/// has no RootCredential -> ProviderContext wiring (deferred to when real providers are built).
/// This stage proves the message pipe end-to-end, not a successful provisioning run.
///
/// Takes the review pipeline's pieces via constructor injection - not built per-message - so a
/// test can substitute a fake IDefinitionSource instead of requiring live GitHub access to
/// exercise this consumer's logic.
/// </summary>
public sealed class CampusDeploymentRequestConsumer(
    IPostProvider postProvider,
    IDefinitionSource source,
    InstitutionYamlReader reader,
    ProvisioningPlanner planner,
    ProvisioningEngine engine)
    : MessageConsumerBase<CampusDeploymentRequested>
{
    public override IPostContract Contract => ProvisioningPost.RequestReference().Contract;

    public override async Task ConsumeAsync(CampusDeploymentRequested message, IPostContext context, CancellationToken ct = default)
    {
        var review = new ProvisioningReview(source, reader, planner, engine);

        await review.LoadAsync(
            new DefinitionSourceRequest(message.Repository, message.Revision, message.DefinitionPath, message.BindingsPath),
            ct);

        var result = review.Review();
        var issues = review.Issues
            .Select(issue => $"{issue.Code}: {issue.Target} - {issue.Message}")
            .ToArray();

        var completed = new CampusDeploymentCompleted(message.RequestId, result.IsValid, issues, DateTimeOffset.UtcNow);
        var envelope = new PostEnvelope<CampusDeploymentCompleted>(
            ProvisioningPost.ResultReference(),
            completed,
            new PostMetadata(correlationId: message.RequestId.ToString()));

        await postProvider.PublishAsync(envelope, ct);
    }
}
