using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Workbench;

// A provisioning boundary, not a replacement for the runtime's IWorkbench institution.
public sealed record WorkspaceRequest(string Environment, string Institution, string Resource, string Stage);
public sealed record WorkspaceResult(bool AlreadyExists);
public interface IWorkbenchBackend
{
    // Must reconcile ownership without overwriting conflicts and verify staging access on every call.
    Task<WorkspaceResult> EnsureAsync(WorkspaceRequest request, CancellationToken cancellationToken);
}

// The host supplies a fixed target identity and its backend. Connections never enter a plan.
public sealed class WorkbenchProvider(string target, IWorkbenchBackend backend) : IResourceProvider
{
    public string Key => "workbench";
    public ImmutableArray<ValidationIssue> Validate(ResourceRequirement resource, ResourceBinding binding)
    {
        var valid = resource.Ownership == "owned" && resource.Category == "staging"
            && binding.Provider == Key && binding.Settings.GetValueOrDefault("backing") == "redis"
            && !string.IsNullOrWhiteSpace(target) && binding.Settings.GetValueOrDefault("target") == target
            && binding.Settings.TryGetValue("stage", out var stage)
            && Regex.IsMatch(stage, @"\A[a-z0-9][a-z0-9-]{0,62}\z")
            && binding.Settings.GetValueOrDefault("fallback") == "none"
            && binding.Settings.Keys.All(k => k is "backing" or "stage" or "target" or "fallback")
            && binding.Secrets.IsEmpty;
        return valid ? [] : [new("workbench.binding", resource.Id,
            "Workbench requires owned staging, Redis backing, a registered target, a lowercase stage (1–63 characters), and fallback: none. Credentials are supplied by the host.")];
    }
    public async Task<ProviderResult> EnsureAsync(ProviderContext context, CancellationToken cancellationToken)
    {
        if (!Validate(context.Resource, context.Binding).IsEmpty)
            throw new InvalidOperationException("Invalid Workbench binding.");
        var result = await backend.EnsureAsync(new(context.Environment, context.InstitutionId,
            context.Resource.Id, context.Binding.Settings["stage"]), cancellationToken);
        // Existing service credentials are host-owned; no synthetic workspace password is generated.
        return new(result.AlreadyExists, []);
    }
}
