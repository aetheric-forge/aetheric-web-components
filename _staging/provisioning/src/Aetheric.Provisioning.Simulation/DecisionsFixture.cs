using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Aetheric.Provisioning.Engine;
using YamlDotNet.RepresentationModel;

namespace Aetheric.Provisioning.Simulation;

/// <summary>Reads only the hash-pinned, embedded fixture. Not a general YAML/Git loader.</summary>
public static class DecisionsFixture
{
    public const string Commit = "4e398147f9a7cd8ba47258a8e416f3e86f404a93";
    private const string Repository = "https://github.com/aetheric-forge/adr-campus";
    public static PlanningInput Load()
    {
        var (definition, definitionSource) = Read("decisions-institution.yaml",
            "73a81f72a0f2233946035712accbb394263afe159847ce00c69cd826fefec0a1");
        var (deployment, bindingsSource) = Read("decisions-institution.bindings.yaml",
            "5b99ba92c44ce97b228b2951c71a7bf30772edb9ad0f8c1388f8a386ea10fe69");
        var descriptor = (YamlMappingNode)definition["descriptor"];
        var resources = ((YamlSequenceNode)definition["resources"]).Children.Cast<YamlMappingNode>()
            .Select(r => new ResourceRequirement(Text(r, "id"), Text(r, "name"), Text(r, "type"), Text(r, "ownership")))
            .ToImmutableArray();
        var dependencies = ((YamlSequenceNode)definition["dependencies"]).Children.Cast<YamlMappingNode>()
            .Select(d => Text(d, "contract")).ToImmutableArray();
        var resourceBindings = ImmutableDictionary.CreateBuilder<string, ResourceBinding>();
        var parents = ImmutableDictionary.CreateBuilder<string, string>();
        foreach (var entry in ((YamlMappingNode)deployment["bindings"]).Children)
        {
            var id = ((YamlScalarNode)entry.Key).Value!;
            var value = (YamlMappingNode)entry.Value;
            if (dependencies.Contains(id)) parents[id] = Text(value, "source");
            else resourceBindings[id] = new(Text(value, "provider"), value.Children
                .Where(x => ((YamlScalarNode)x.Key).Value != "provider")
                .ToImmutableDictionary(x => ((YamlScalarNode)x.Key).Value!, x => ((YamlScalarNode)x.Value).Value!),
                ImmutableDictionary<string, SecretReference>.Empty);
        }
        return new(new(Text(descriptor, "id"), Text(descriptor, "version"), resources, dependencies),
            new(Text(deployment, "institution"), Text(deployment, "version"),
                Text((YamlMappingNode)deployment["deployment"], "name"), resourceBindings.ToImmutable(), parents.ToImmutable()),
            new("simulated-campus", "1"), definitionSource, bindingsSource);
    }

    private static string Text(YamlMappingNode node, string key) => ((YamlScalarNode)node[key]).Value!;
    private static (YamlMappingNode, SourceProvenance) Read(string name, string expectedHash)
    {
        var assembly = typeof(DecisionsFixture).Assembly;
        var resourceName = assembly.GetManifestResourceNames().Single(x => x.EndsWith(name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
        if (hash != expectedHash) throw new InvalidOperationException("Embedded fixture differs from its pinned source.");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(Encoding.UTF8.GetString(bytes.ToArray())));
        return ((YamlMappingNode)yaml.Documents.Single().RootNode,
            new(Repository, Commit, "institution/" + name, hash));
    }
}
