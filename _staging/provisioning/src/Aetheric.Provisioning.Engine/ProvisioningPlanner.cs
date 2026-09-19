using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aetheric.Provisioning.Engine;

public sealed class ProvisioningPlanner(IEnumerable<IResourceProvider> providers)
{
    private readonly ImmutableDictionary<string, IResourceProvider> _providers =
        providers.ToImmutableDictionary(x => x.Key, StringComparer.Ordinal);

    public PlanningResult Plan(PlanningInput input)
    {
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        var steps = ImmutableArray.CreateBuilder<PlanStep>();
        void Error(string code, string target, string message) => issues.Add(new(code, target, message));
        var institution = input.Institution;
        var bindings = input.Bindings;
        if (string.IsNullOrWhiteSpace(institution.Id) || string.IsNullOrWhiteSpace(institution.Version))
            Error("institution.identity", "institution", "Institution ID and version are required.");
        if (institution.Id != bindings.InstitutionId || institution.Version != bindings.InstitutionVersion)
            Error("bindings.identity", "bindings", "Bindings must match the institution ID and version.");
        if (string.IsNullOrWhiteSpace(bindings.Environment) || string.IsNullOrWhiteSpace(input.Parent.Id)
            || string.IsNullOrWhiteSpace(input.Parent.Revision))
            Error("context.missing", "context", "Environment and parent identity/revision are required.");
        foreach (var source in new[] { input.DefinitionSource, input.BindingsSource })
            if (!Uri.TryCreate(source.Repository, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
                || !Regex.IsMatch(source.Commit, @"\A[0-9a-f]{40}\z")
                || !Regex.IsMatch(source.ContentHash, @"\A[0-9a-f]{64}\z")
                || string.IsNullOrWhiteSpace(source.Path))
                Error("source.invalid", "source", "Provide HTTPS provenance with a pinned commit, path, and SHA-256 content hash.");
        if (institution.Resources.IsDefault || institution.ParentContracts.IsDefault)
            return new(null, [new("requirements.missing", "institution", "Resource and dependency collections are required.")]);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var contracts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var contract in institution.ParentContracts.Order(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(contract) || !contracts.Add(contract))
            {
                Error("dependency.invalid", "dependencies", "Parent contracts must be nonempty and unique.");
                continue;
            }
            if (!bindings.ParentSources.TryGetValue(contract, out var source) || string.IsNullOrWhiteSpace(source))
                Error("dependency.binding_missing", contract, "A parent resolution source is required.");
            else
                steps.Add(new("parent:" + contract, StepKind.CheckParent, contract, source, null, null, []));
        }
        var dependencies = steps.Select(x => x.Id).ToImmutableArray();
        foreach (var resource in institution.Resources.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(resource.Id) || !ids.Add(resource.Id))
                Error("resource.id", "resources", "Resource IDs must be nonempty and unique.");
            if (string.IsNullOrWhiteSpace(resource.Category))
                Error("resource.category", resource.Id, "A resource category is required.");
            if (resource.Ownership == "parent")
            {
                if (contracts.Count == 0)
                    Error("parent.requirements_missing", resource.Id, "Inherited resources require explicit parent contracts.");
                if (bindings.Resources.ContainsKey(resource.Id))
                    Error("parent.binding", resource.Id, "A child cannot select a provider for a parent-owned resource.");
                continue;
            }
            if (resource.Ownership != "owned")
            {
                Error("resource.ownership", resource.Id, "Supported ownership values are owned and parent.");
                continue;
            }
            if (!bindings.Resources.TryGetValue(resource.Id, out var binding))
                Error("resource.binding_missing", resource.Id, "An owned resource needs a deployment binding.");
            else if (!_providers.TryGetValue(binding.Provider, out var provider))
                Error("provider.unsupported", resource.Id, "No provider is registered for this deployment binding.");
            else
            {
                issues.AddRange(provider.Validate(resource, binding));
                steps.Add(new("owned:" + resource.Id, StepKind.ProvisionOwned, resource.Id, null,
                    resource, binding, dependencies));
            }
        }
        foreach (var key in bindings.Resources.Keys.Where(x => !ids.Contains(x)))
            Error("binding.unknown", key, "Resource binding does not match a declared resource.");
        foreach (var key in bindings.ParentSources.Keys.Where(x => !contracts.Contains(x)))
            Error("dependency.unknown", key, "Parent binding does not match a declared contract.");
        if (issues.Count != 0) return new(null, issues.ToImmutable());
        return new(new(Fingerprint(input), input, steps.ToImmutable()), []);
    }

    private static string Fingerprint(PlanningInput input)
    {
        // Sort unordered maps/sets so equivalent inputs have the same plan identity.
        var canonical = new
        {
            input.DefinitionSource, input.BindingsSource,
            Parent = new { input.Parent.Id, input.Parent.Revision,
                Capabilities = input.Parent.Capabilities.OrderBy(x => x.Key, StringComparer.Ordinal) },
            input.Institution.Id, input.Institution.Version,
            Resources = input.Institution.Resources.OrderBy(x => x.Id, StringComparer.Ordinal),
            Dependencies = input.Institution.ParentContracts.Order(StringComparer.Ordinal),
            input.Bindings.Environment,
            Bindings = input.Bindings.Resources.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new
            {
                x.Key, x.Value.Provider,
                Settings = x.Value.Settings.OrderBy(v => v.Key, StringComparer.Ordinal),
                Secrets = x.Value.Secrets.OrderBy(v => v.Key, StringComparer.Ordinal)
            }),
            Parents = input.Bindings.ParentSources.OrderBy(x => x.Key, StringComparer.Ordinal)
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }
}
