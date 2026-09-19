using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Aetheric.Provisioning.Engine;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Aetheric.Provisioning.Definitions;

/// <summary>Validates the first supported ADR Campus YAML shape, independent of institution version.</summary>
public sealed class InstitutionYamlReader
{
    public DefinitionReadResult Read(SourceBundle source)
    {
        try
        {
            if (source.Definition.Provenance.Repository != source.Bindings.Provenance.Repository
                || source.Definition.Provenance.Commit != source.Bindings.Provenance.Commit)
                Fail("source.mixed_revision", "source", "Definition and bindings must come from the same repository commit.");
            var definition = Parse(source.Definition, "definition");
            var bindings = Parse(source.Bindings, "bindings");
            Keys(definition, "definition", ["descriptor", "dependencies", "domains", "organizations", "roles", "capabilities", "resources", "workflows", "policies", "initialState"]);
            var descriptor = Map(Required(definition, "descriptor", "definition"), "descriptor");
            Keys(descriptor, "descriptor", ["id", "name", "version", "description"]);
            var id = Id(descriptor, "id", "descriptor");
            var name = Text(descriptor, "name", "descriptor");
            var version = VersionText(descriptor, "version", "descriptor");
            var description = Text(descriptor, "description", "descriptor");
            var capabilityIds = Entries(definition, "capabilities", []).Select(x => Id(x, "id", "capabilities")).ToHashSet(StringComparer.Ordinal);
            foreach (var domain in Entries(definition, "domains", ["requiredCapabilities"])) References(domain, "requiredCapabilities", capabilityIds, "domains");
            foreach (var role in Entries(definition, "roles", ["capabilities"])) References(role, "capabilities", capabilityIds, "roles");
            _ = Entries(definition, "organizations", []);
            _ = Entries(definition, "workflows", []);
            _ = Entries(definition, "policies", []);
            var resourceNodes = Entries(definition, "resources", ["type", "ownership"]);
            var resources = resourceNodes.Select(r =>
            {
                var ownership = Text(r, "ownership", "resources");
                if (ownership is not ("owned" or "parent")) Fail("resource.ownership", "resources", "Ownership must be owned or parent.");
                return new ResourceRequirement(Id(r, "id", "resources"), Text(r, "name", "resources"), Text(r, "type", "resources"), ownership);
            }).ToImmutableArray();
            var contracts = ImmutableArray.CreateBuilder<string>();
            if (definition.Children.TryGetValue("dependencies", out var dependencyNode))
                foreach (var item in Seq(dependencyNode, "dependencies"))
                {
                    var dependency = Map(item, "dependencies");
                    Keys(dependency, "dependencies", ["contract", "reason"]);
                    var contract = Text(dependency, "contract", "dependencies");
                    if (!Regex.IsMatch(contract, @"\A[A-Za-z_][A-Za-z0-9_.]*\z") || contracts.Contains(contract))
                        Fail("dependency.invalid", "dependencies", "Dependency contracts must be valid, unique interface identifiers.");
                    _ = Text(dependency, "reason", "dependencies");
                    contracts.Add(contract);
                }
            if (resources.Any(x => contracts.Contains(x.Id))) Fail("id.ambiguous", "resources", "Resource IDs and dependency contracts must not collide.");
            var initial = Map(Required(definition, "initialState", "definition"), "initialState");
            Keys(initial, "initialState", ["configuration"]);
            var configuration = Map(Required(initial, "configuration", "initialState"), "initialState.configuration");
            foreach (var item in configuration.Children)
            {
                _ = Scalar(item.Key, "initialState.configuration");
                _ = Scalar(item.Value, "initialState.configuration");
            }
            Keys(bindings, "bindings", ["institution", "version", "deployment", "bindings"]);
            var bindingId = Id(bindings, "institution", "bindings");
            var bindingVersion = VersionText(bindings, "version", "bindings");
            if (bindingId != id || bindingVersion != version) Fail("bindings.identity", "bindings", "Bindings must match the institution ID and version.");
            var deployment = Map(Required(bindings, "deployment", "bindings"), "deployment");
            Keys(deployment, "deployment", ["name"]);
            var environment = Text(deployment, "name", "deployment");
            var resourceBindings = ImmutableDictionary.CreateBuilder<string, ResourceBinding>();
            var parentSources = ImmutableDictionary.CreateBuilder<string, string>();
            var bindingMap = Map(Required(bindings, "bindings", "bindings"), "bindings.bindings");
            foreach (var item in bindingMap.Children)
            {
                var key = Scalar(item.Key, "bindings.bindings");
                var value = Map(item.Value, "bindings." + key);
                if (contracts.Contains(key))
                {
                    Keys(value, "bindings." + key, ["source"]);
                    parentSources.Add(key, Text(value, "source", "bindings." + key));
                }
                else
                {
                    var resource = resources.FirstOrDefault(r => r.Id == key);
                    if (resource is null) Fail("binding.unknown", "bindings." + key, "Binding does not refer to a resource or parent contract.");
                    if (resource!.Ownership == "parent") Fail("parent.binding", "bindings." + key, "Parent-owned resources must be realized through inherited capabilities.");
                    var provider = Text(value, "provider", "bindings." + key);
                    var settings = ImmutableDictionary.CreateBuilder<string, string>();
                    foreach (var setting in value.Children)
                    {
                        var settingKey = Scalar(setting.Key, "bindings." + key);
                        if (settingKey == "provider") continue;
                        if (Regex.IsMatch(settingKey, "secret|password|token|credential|connectionstring", RegexOptions.IgnoreCase))
                            Fail("binding.secret", "bindings." + key, "Do not store credentials in public bindings; use secret references through the host.");
                        settings.Add(settingKey, Scalar(setting.Value, "bindings." + key + "." + settingKey));
                    }
                    resourceBindings.Add(key, new(provider, settings.ToImmutable(), ImmutableDictionary<string, SecretReference>.Empty));
                }
            }
            return new(new(name, description, new(id, version, resources, contracts.ToImmutable()),
                new(bindingId, bindingVersion, environment, resourceBindings.ToImmutable(), parentSources.ToImmutable()), source), []);
        }
        catch (InputException ex) { return new(null, [new(ex.Code, ex.Target, ex.Message)]); }
        catch (YamlException) { return new(null, [new("yaml.syntax", "yaml", "Invalid YAML syntax or duplicate mapping keys.")]); }
        catch (ArgumentException) { return new(null, [new("yaml.structure", "yaml", "Invalid or duplicate YAML entries.")]); }
    }

    private static YamlMappingNode Parse(SourceDocument document, string target)
    {
        var bytes = Encoding.UTF8.GetBytes(document.Text);
        if (bytes.Length > PublicGitHubSource.MaxDocumentBytes) Fail("yaml.too_large", target, "Each YAML document must be at most 256 KiB.");
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != document.Provenance.ContentHash)
            Fail("source.hash", target, "Document bytes do not match recorded source provenance.");
        var parser = new Parser(new StringReader(document.Text));
        var depth = 0;
        var nodes = 0;
        while (parser.MoveNext())
        {
            if (parser.Current is AnchorAlias || parser.Current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                Fail("yaml.feature", target, "YAML anchors, aliases, and explicit tags are not supported.");
            if (parser.Current is MappingStart or SequenceStart) depth++;
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
            if (depth > 24 || ++nodes > 12000) Fail("yaml.complexity", target, "YAML exceeds the supported nesting or node count.");
        }
        var stream = new YamlStream();
        stream.Load(new StringReader(document.Text));
        if (stream.Documents.Count != 1) Fail("yaml.documents", target, "Provide exactly one YAML document per file.");
        return Map(stream.Documents[0].RootNode, target);
    }
    private static ImmutableArray<YamlMappingNode> Entries(YamlMappingNode root, string key, string[] extra)
    {
        var result = ImmutableArray.CreateBuilder<YamlMappingNode>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Seq(Required(root, key, "definition"), key))
        {
            var map = Map(item, key);
            Keys(map, key, ["id", "name", "description", .. extra]);
            var id = Id(map, "id", key);
            if (!ids.Add(id)) Fail("id.duplicate", key, "IDs must be unique within each section.");
            _ = Text(map, "name", key);
            _ = Text(map, "description", key);
            result.Add(map);
        }
        return result.ToImmutable();
    }
    private static void References(YamlMappingNode map, string key, HashSet<string> known, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Seq(Required(map, key, target), target + "." + key))
        {
            var value = Scalar(item, target + "." + key);
            if (!known.Contains(value) || !seen.Add(value)) Fail("capability.reference", target, "Capability references must be declared IDs and must not repeat.");
        }
    }
    private static string Id(YamlMappingNode map, string key, string target)
    {
        var value = Text(map, key, target);
        if (!Regex.IsMatch(value, @"\A[a-z][a-z0-9-]*\z")) Fail("id.invalid", target + "." + key, "Use a stable lowercase slug ID.");
        return value;
    }
    private static string VersionText(YamlMappingNode map, string key, string target)
    {
        var value = Text(map, key, target);
        if (!System.Version.TryParse(value, out _)) Fail("version.invalid", target + "." + key, "Use a numeric institution version, such as 1.0.0.");
        return value;
    }
    private static void Keys(YamlMappingNode map, string target, string[] allowed)
    {
        foreach (var item in map.Children.Keys)
            if (!allowed.Contains(Scalar(item, target))) Fail("yaml.unknown_field", target, "Unknown field in the supported institutional YAML profile.");
    }
    private static YamlNode Required(YamlMappingNode map, string key, string target)
    {
        if (!map.Children.TryGetValue(key, out var value)) Fail("yaml.required", target + "." + key, "Required field is missing.");
        return value!;
    }
    private static string Text(YamlMappingNode map, string key, string target) => Scalar(Required(map, key, target), target + "." + key);
    private static string Scalar(YamlNode node, string target)
    {
        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            Fail("yaml.scalar", target, "A nonempty scalar value is required.");
        return ((YamlScalarNode)node).Value!;
    }
    private static YamlMappingNode Map(YamlNode node, string target)
    {
        if (node is not YamlMappingNode) Fail("yaml.mapping", target, "A mapping is required.");
        return (YamlMappingNode)node;
    }
    private static IEnumerable<YamlNode> Seq(YamlNode node, string target)
    {
        if (node is not YamlSequenceNode) Fail("yaml.sequence", target, "A sequence is required.");
        return ((YamlSequenceNode)node).Children;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string code, string target, string message) => throw new InputException(code, target, message);
    private sealed class InputException(string code, string target, string message) : Exception(message)
    { public string Code { get; } = code; public string Target { get; } = target; }
}
