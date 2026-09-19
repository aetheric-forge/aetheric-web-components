using AethericForge.Runtime.Abstractions.Interfaces.Post.Primitives;
using AethericForge.Runtime.Models.Post;

namespace Aetheric.Provisioning.Worker;

/// <summary>
/// Independently defined here, matching aetheric-admin's copy shape-for-shape - Post's RabbitMQ
/// wire format is JSON-structural (matched by Domain/Address/Contract.Name/Version), not a
/// shared compiled type, so the two repos don't need a shared contracts package for this.
/// </summary>
public sealed record CampusDeploymentRequested(
    Guid RequestId,
    string Repository,
    string Revision,
    string DefinitionPath,
    string BindingsPath,
    IReadOnlyDictionary<string, RootCredentialPayload> RootCredentials,
    DateTimeOffset RequestedAtUtc);

public sealed record RootCredentialPayload(
    string Host,
    int Port,
    string? Username,
    string Password,
    string? AuthDatabase = null,
    string? Database = null,
    string? Scheme = null,
    string? BasePath = null);

public sealed record CampusDeploymentCompleted(
    Guid RequestId,
    bool Succeeded,
    IReadOnlyList<string> Issues,
    DateTimeOffset CompletedAtUtc);

public static class ProvisioningPost
{
    public const string Domain = "provisioning";

    public static IPostReference RequestReference() => new PostReference(
        Domain,
        "campus/deploy",
        new PostContract("campus-deployment-requested", "1.0", PostIntent.Command));

    public static IPostReference ResultReference() => new PostReference(
        Domain,
        "campus/deploy/result",
        new PostContract("campus-deployment-completed", "1.0", PostIntent.Event));
}
