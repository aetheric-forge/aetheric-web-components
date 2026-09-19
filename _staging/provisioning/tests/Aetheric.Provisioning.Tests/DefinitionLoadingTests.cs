using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Simulation;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class DefinitionLoadingTests
{
    public static DefinitionSourceRequest Request(string revision = "main") => new(
        "https://github.com/aetheric-forge/adr-campus", revision,
        "institution/decisions-institution.yaml", "institution/decisions-institution.bindings.yaml");

    [Theory]
    [InlineData("main")]
    [InlineData("v1.0.0")]
    [InlineData("feature/a-definition")]
    [InlineData(DecisionsFixture.Commit)]
    public async Task Branch_tag_or_commit_resolves_once_and_pins_both_documents(string revision)
    {
        var handler = new GitHubHandler();
        var source = new PublicGitHubSource(new HttpClient(handler));
        var result = await source.LoadAsync(Request(revision));
        Assert.Empty(result.Issues);
        var bundle = Assert.IsType<SourceBundle>(result.Bundle);
        Assert.Equal(DecisionsFixture.Commit, bundle.Definition.Provenance.Commit);
        Assert.Equal(bundle.Definition.Provenance.Commit, bundle.Bindings.Provenance.Commit);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests.Skip(1), uri => Assert.EndsWith("?ref=" + DecisionsFixture.Commit, uri.Query));
        handler.Commit = new string('a', 40);
        Assert.Equal(DecisionsFixture.Commit, bundle.Definition.Provenance.Commit);
        var newer = await source.LoadAsync(Request());
        Assert.Equal(handler.Commit, newer.Bundle!.Definition.Provenance.Commit);
    }

    [Theory]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("https://localhost/owner/repo")]
    [InlineData("https://github.com.evil.example/owner/repo")]
    [InlineData("https://user:password@github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo?token=unsafe")]
    [InlineData("https://github.com/owner/repo#fragment")]
    public async Task Unsupported_repository_is_rejected_before_network_access(string repository)
    {
        var handler = new GitHubHandler();
        var result = await new PublicGitHubSource(new HttpClient(handler)).LoadAsync(Request() with { Repository = repository });
        Assert.Null(result.Bundle);
        Assert.Equal("source.repository", Assert.Single(result.Issues).Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("../institution.yaml")]
    [InlineData("/institution.yaml")]
    [InlineData("institution/%2e%2e/file.yaml")]
    [InlineData("institution/file.cs")]
    public async Task Invalid_path_is_rejected_before_network_access(string path)
    {
        var handler = new GitHubHandler();
        var result = await new PublicGitHubSource(new HttpClient(handler)).LoadAsync(Request() with { DefinitionPath = path });
        Assert.Equal("source.path", Assert.Single(result.Issues).Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "source.not_found")]
    [InlineData(HttpStatusCode.Forbidden, "source.rate_limited")]
    [InlineData(HttpStatusCode.TooManyRequests, "source.rate_limited")]
    [InlineData(HttpStatusCode.Redirect, "source.http")]
    public async Task Source_errors_are_actionable_and_do_not_expose_response_bodies(HttpStatusCode status, string code)
    {
        var result = await new PublicGitHubSource(new HttpClient(new GitHubHandler { Status = status })).LoadAsync(Request());
        Assert.Null(result.Bundle);
        Assert.Equal(code, Assert.Single(result.Issues).Code);
        Assert.DoesNotContain("untrusted-response-body", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Oversized_file_is_rejected()
    {
        var result = await new PublicGitHubSource(new HttpClient(new GitHubHandler { DeclaredSize = 300000 })).LoadAsync(Request());
        Assert.Equal("source.too_large", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Supplied_yaml_validates_and_preserves_metadata_without_provider_inference()
    {
        var result = new InstitutionYamlReader().Read(BundledDecisionsSource.Load());
        Assert.Empty(result.Issues);
        var institution = Assert.IsType<LoadedInstitution>(result.Institution);
        Assert.Equal("Decisions", institution.Name);
        Assert.Equal(2, institution.Requirements.Resources.Length);
        Assert.Equal(4, institution.Requirements.ParentContracts.Length);
        Assert.Single(institution.Bindings.Resources);
        Assert.Equal("workbench", institution.Bindings.Resources["draft-workspace"].Provider);
        Assert.Contains("active-member-only", institution.Source.Definition.Text);
    }

    [Theory]
    [InlineData("reference", "capability.reference")]
    [InlineData("duplicate", "id.duplicate")]
    [InlineData("unknown", "yaml.unknown_field")]
    [InlineData("ownership", "resource.ownership")]
    [InlineData("technology", "yaml.unknown_field")]
    [InlineData("version", "version.invalid")]
    [InlineData("anchor", "yaml.feature")]
    [InlineData("alias", "yaml.feature")]
    [InlineData("tag", "yaml.feature")]
    [InlineData("documents", "yaml.documents")]
    public void Unsupported_or_inconsistent_institution_input_is_not_silently_ignored(string scenario, string expected)
    {
        var bundle = BundledDecisionsSource.Load();
        var text = bundle.Definition.Text;
        text = scenario switch
        {
            "reference" => text.Replace("      - revise-draft", "      - missing-capability"),
            "duplicate" => text.Replace("id: decision-record", "id: draft-workspace"),
            "unknown" => text + "unexpected: value\n",
            "ownership" => text.Replace("ownership: owned", "ownership: unknown"),
            "technology" => text.Replace("    ownership: owned", "    ownership: owned\n    provider: mongodb"),
            "version" => text.Replace("  version: 1.0.0", "  version: not-a-version"),
            "anchor" => text + "unexpected: &anchor value\n",
            "alias" => text + "unexpected: *anchor\n",
            "tag" => text + "unexpected: !!str value\n",
            "documents" => text + "\n---\nother: document\n",
            _ => throw new InvalidOperationException()
        };
        var result = new InstitutionYamlReader().Read(bundle with { Definition = Replace(bundle.Definition, text) });
        Assert.Null(result.Institution);
        Assert.Equal(expected, Assert.Single(result.Issues).Code);
    }

    [Theory]
    [InlineData("identity", "bindings.identity")]
    [InlineData("parent", "parent.binding")]
    [InlineData("unknown", "binding.unknown")]
    [InlineData("secret", "binding.secret")]
    public void Binding_errors_preserve_the_institutional_boundary(string scenario, string expected)
    {
        var bundle = BundledDecisionsSource.Load();
        var text = scenario switch
        {
            "identity" => bundle.Bindings.Text.Replace("institution: decisions", "institution: another"),
            "parent" => bundle.Bindings.Text + "\n  decision-record:\n    provider: mongodb\n",
            "unknown" => bundle.Bindings.Text + "\n  unknown-resource:\n    provider: mongodb\n",
            "secret" => bundle.Bindings.Text.Replace("    backing: redis", "    backing: redis\n    password: untrusted-test-secret"),
            _ => throw new InvalidOperationException()
        };
        var result = new InstitutionYamlReader().Read(bundle with { Bindings = Replace(bundle.Bindings, text) });
        Assert.Null(result.Institution);
        Assert.Equal(expected, Assert.Single(result.Issues).Code);
        Assert.DoesNotContain("untrusted-test-secret", JsonSerializer.Serialize(result.Issues));
    }

    [Fact]
    public void Definition_and_bindings_cannot_mix_commits()
    {
        var bundle = BundledDecisionsSource.Load();
        var result = new InstitutionYamlReader().Read(bundle with
        {
            Bindings = bundle.Bindings with { Provenance = bundle.Bindings.Provenance with { Commit = new string('a', 40) } }
        });
        Assert.Null(result.Institution);
        Assert.Equal("source.mixed_revision", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Duplicate_mapping_keys_are_rejected()
    {
        var bundle = BundledDecisionsSource.Load();
        var text = bundle.Definition.Text.Replace("  id: decisions\n", "  id: decisions\n  id: duplicate\n");
        var result = new InstitutionYamlReader().Read(bundle with { Definition = Replace(bundle.Definition, text) });
        Assert.Null(result.Institution);
        Assert.Contains(Assert.Single(result.Issues).Code, new[] { "yaml.syntax", "yaml.structure" });
    }

    [Fact]
    public void Hash_mismatch_is_rejected()
    {
        var bundle = BundledDecisionsSource.Load();
        var result = new InstitutionYamlReader().Read(bundle with { Definition = bundle.Definition with { Text = bundle.Definition.Text + "\n" } });
        Assert.Equal("source.hash", Assert.Single(result.Issues).Code);
    }

    internal static SourceDocument Replace(SourceDocument original, string text) => original with
    {
        Text = text,
        Provenance = original.Provenance with { ContentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))) }
    };
    private sealed class GitHubHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public string Commit { get; set; } = DecisionsFixture.Commit;
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public int? DeclaredSize { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Null(request.Headers.Authorization);
            var uri = request.RequestUri!;
            Assert.Equal("api.github.com", uri.Host);
            Requests.Add(uri);
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("untrusted-response-body") });
            if (uri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)) return Json(new { sha = Commit });
            var bundle = BundledDecisionsSource.Load();
            var document = uri.AbsolutePath.EndsWith(".bindings.yaml", StringComparison.Ordinal) ? bundle.Bindings : bundle.Definition;
            var bytes = Encoding.UTF8.GetBytes(document.Text);
            return Json(new { type = "file", encoding = "base64", size = DeclaredSize ?? bytes.Length, content = Convert.ToBase64String(bytes) });
        }
        private static Task<HttpResponseMessage> Json(object value) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
    }
}
