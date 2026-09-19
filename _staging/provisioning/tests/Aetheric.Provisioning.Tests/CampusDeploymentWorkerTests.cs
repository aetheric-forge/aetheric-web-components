using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Persistence;
using Aetheric.Provisioning.Simulation;
using Aetheric.Provisioning.Worker;
using AethericForge.Runtime.Abstractions.Interfaces.Post;
using AethericForge.Runtime.Abstractions.Interfaces.Post.Consumers;
using AethericForge.Runtime.Abstractions.Interfaces.Post.Primitives;
using AethericForge.Runtime.Models.Post;
using AethericForge.Runtime.Providers.Post.RabbitMq;
using Xunit;

namespace Aetheric.Provisioning.Tests;

/// <summary>
/// Proves the Stage 3 message pipe end-to-end against a real RabbitMQ broker: publishing a
/// CampusDeploymentRequested drives the worker's consumer through the actual plan pipeline
/// (ProvisioningReview/Planner/Engine, not a mock), and the correct CampusDeploymentCompleted
/// comes back. Uses a fixture IDefinitionSource instead of live GitHub, and an empty provider
/// list - the same state the real worker runs in today, pending Stage 6's resource providers -
/// so the expected, checked-in outcome here is a clean provider.unsupported failure, not success.
/// </summary>
public sealed class CampusDeploymentWorkerTests
{
    [RabbitMqFact]
    public async Task Worker_consumer_reports_provider_unsupported_for_every_resource()
    {
        var connection = Environment.GetEnvironmentVariable("PROVISIONING_TEST_RABBITMQ")!;
        await using var postProvider = new RabbitMqPostProvider(ProvisioningPost.Domain, connection);

        var providers = Array.Empty<IResourceProvider>();
        var planner = new ProvisioningPlanner(providers);
        var tempDirectory = Path.Combine(Path.GetTempPath(), "aetheric-provisioning-worker-test-" + Guid.NewGuid().ToString("N"));
        var engine = new ProvisioningEngine(
            providers,
            new NoParentCapabilityResolver(),
            new FileRunStateStore(Path.Combine(tempDirectory, "run-state")),
            new EncryptedFileSecretStore(Path.Combine(tempDirectory, "secrets"), new byte[32]));
        var consumer = new CampusDeploymentRequestConsumer(postProvider, new FixtureSource(), new InstitutionYamlReader(), planner, engine);

        var resultReceived = new TaskCompletionSource<CampusDeploymentCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await postProvider.SubscribeAsync(ProvisioningPost.ResultReference(), new ResultConsumer(resultReceived));
        await postProvider.SubscribeAsync(ProvisioningPost.RequestReference(), consumer);

        try
        {
            var request = new CampusDeploymentRequested(
                Guid.NewGuid(),
                "https://github.com/aetheric-forge/aetheric-runtime",
                "main",
                "institution/campus.yaml",
                "institution/campus.bindings.yaml",
                new Dictionary<string, RootCredentialPayload>(),
                DateTimeOffset.UtcNow);
            var envelope = new PostEnvelope<CampusDeploymentRequested>(
                ProvisioningPost.RequestReference(), request, new PostMetadata());
            await postProvider.PublishAsync(envelope);

            var completed = await resultReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(request.RequestId, completed.RequestId);
            Assert.False(completed.Succeeded);
            Assert.NotEmpty(completed.Issues);
            Assert.Contains(completed.Issues, issue => issue.StartsWith("provider.unsupported", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class FixtureSource : IDefinitionSource
    {
        public Task<SourceLoadResult> LoadAsync(DefinitionSourceRequest request, CancellationToken ct = default)
            => Task.FromResult(new SourceLoadResult(BundledDecisionsSource.Load(), []));
    }

    private sealed class ResultConsumer(TaskCompletionSource<CampusDeploymentCompleted> completion)
        : MessageConsumerBase<CampusDeploymentCompleted>
    {
        public override IPostContract Contract => ProvisioningPost.ResultReference().Contract;

        public override Task ConsumeAsync(CampusDeploymentCompleted message, IPostContext context, CancellationToken ct = default)
        {
            completion.TrySetResult(message);
            return Task.CompletedTask;
        }
    }
}

public sealed class RabbitMqFactAttribute : FactAttribute
{
    public RabbitMqFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROVISIONING_TEST_RABBITMQ")))
            Skip = "Set PROVISIONING_TEST_RABBITMQ to an amqp:// connection string for an isolated broker.";
    }
}
