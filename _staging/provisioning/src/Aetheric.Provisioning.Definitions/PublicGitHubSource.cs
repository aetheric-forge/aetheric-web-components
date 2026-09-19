using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Definitions;

/// <summary>Unauthenticated public GitHub reads. Never clones or executes repository code.</summary>
public sealed class PublicGitHubSource(HttpClient http) : IDefinitionSource
{
    public const int MaxDocumentBytes = 256 * 1024;
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    public static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<SourceLoadResult> LoadAsync(DefinitionSourceRequest request, CancellationToken ct = default)
    {
        if (!TryRepository(request.Repository, out var repository, out var apiPath))
            return Error("source.repository", "repository", "Enter a public https://github.com/owner/repository URL without credentials, query parameters, or a fragment.");
        if (string.IsNullOrWhiteSpace(request.Revision) || request.Revision.Length > 256 || request.Revision.Any(char.IsControl))
            return Error("source.revision", "revision", "Enter a branch, tag, or full commit SHA.");
        if (!ValidPath(request.DefinitionPath) || !ValidPath(request.BindingsPath) || request.DefinitionPath == request.BindingsPath)
            return Error("source.path", "paths", "Choose two distinct relative .yaml or .yml file paths without traversal or encoded separators.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var operationToken = timeout.Token;
        try
        {
            using var commit = await GetJsonAsync($"{apiPath}/commits/{Uri.EscapeDataString(request.Revision)}", operationToken);
            var sha = commit.RootElement.GetProperty("sha").GetString() ?? "";
            if (!Regex.IsMatch(sha, @"\A[0-9a-f]{40}\z")) throw new SourceException("source.response", "GitHub returned an invalid commit identity.");
            if (Regex.IsMatch(request.Revision, @"\A[0-9a-fA-F]{40}\z") && !sha.Equals(request.Revision, StringComparison.OrdinalIgnoreCase))
                throw new SourceException("source.commit_mismatch", "GitHub returned a different commit than requested.");
            // Both reads use the resolved commit, even if the selected branch moves during loading.
            var definition = await ReadFileAsync(apiPath, repository, sha, request.DefinitionPath, operationToken);
            var bindings = await ReadFileAsync(apiPath, repository, sha, request.BindingsPath, operationToken);
            return new(new(definition, bindings), []);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return Error("source.timeout", "source", "GitHub did not respond in time. Retry the load."); }
        catch (SourceException ex) { return Error(ex.Code, "source", ex.Message); }
        catch (IOException) { return Error("source.network", "source", "The GitHub response was interrupted. Retry the load."); }
        catch (HttpRequestException) { return Error("source.network", "source", "Could not reach public GitHub. Check connectivity and retry."); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or DecoderFallbackException)
        { return Error("source.response", "source", "GitHub returned an unsupported file or response format."); }
    }

    private async Task<SourceDocument> ReadFileAsync(string apiPath, string repository, string sha, string path, CancellationToken ct)
    {
        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var response = await GetJsonAsync($"{apiPath}/contents/{encoded}?ref={sha}", ct);
        var file = response.RootElement;
        if (file.ValueKind != JsonValueKind.Object || file.GetProperty("type").GetString() != "file"
            || file.TryGetProperty("submodule_git_url", out _) || file.GetProperty("encoding").GetString() != "base64")
            throw new SourceException("source.file_type", "Both paths must identify YAML files in the selected repository.");
        if (file.GetProperty("size").GetInt64() > MaxDocumentBytes)
            throw new SourceException("source.too_large", "Each YAML file must be at most 256 KiB.");
        var bytes = Convert.FromBase64String(file.GetProperty("content").GetString() ?? "");
        if (bytes.Length > MaxDocumentBytes) throw new SourceException("source.too_large", "Each YAML file must be at most 256 KiB.");
        return new(new UTF8Encoding(false, true).GetString(bytes), new(repository, sha, path,
            Convert.ToHexStringLower(SHA256.HashData(bytes))));
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        request.Headers.UserAgent.ParseAdd("Aetheric-Provisioning/0.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new SourceException("source.rate_limited", "Public GitHub access is rate-limited or denied. Wait before retrying; this loader uses no credentials.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new SourceException("source.not_found", "Repository, revision, or file was not found publicly. Check the URL, revision, and paths.");
        if (!response.IsSuccessStatusCode)
            throw new SourceException("source.http", "GitHub could not provide the source. Redirects and private repositories are not supported.");
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new SourceException("source.too_large", "GitHub response exceeds the supported size.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new SourceException("source.too_large", "GitHub response exceeds the supported size.");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private static bool TryRepository(string value, out string repository, out string apiPath)
    {
        repository = apiPath = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "") return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 2) return false;
        var owner = parts[0];
        var name = parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1];
        if (!Regex.IsMatch(owner, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}\z")
            || !Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]{1,100}\z") || name is "." or "..") return false;
        repository = $"https://github.com/{owner}/{name}";
        apiPath = $"repos/{owner}/{name}";
        return true;
    }
    private static bool ValidPath(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512
        && (value.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
        && value.Split('/').All(x => x is not ("" or "." or "..") && Regex.IsMatch(x, @"\A[A-Za-z0-9_. -]+\z"));
    private static SourceLoadResult Error(string code, string target, string message) => new(null, [new(code, target, message)]);
    private sealed class SourceException(string code, string message) : Exception(message) { public string Code { get; } = code; }
}
