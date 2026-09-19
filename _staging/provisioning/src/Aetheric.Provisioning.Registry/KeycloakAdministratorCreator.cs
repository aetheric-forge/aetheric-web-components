using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Aetheric.Provisioning.Application;
using AethericForge.Runtime.Providers.Identity.Keycloak;

namespace Aetheric.Provisioning.Registry;

public sealed record ExistingKeycloakAdministrator(string SubjectId, string Username, string? Email, string DisplayName);

public sealed class KeycloakAdministratorCreator : IRegistryBootstrapAccountCreator, IDisposable
{
    private readonly HttpClient _http;
    private readonly KeycloakOptions _options;
    private readonly Uri _realm;
    private readonly string _role;
    private string? _token;
    public KeycloakAdministratorCreator(KeycloakOptions options, string role) : this(options, role, null) { }
    internal KeycloakAdministratorCreator(KeycloakOptions options, string role, HttpMessageHandler? handler)
    {
        using var validation = new KeycloakProvisionerConnection(options);
        if (string.IsNullOrWhiteSpace(role) || role != role.Trim() || role.StartsWith("default-roles-", StringComparison.Ordinal)
            || role is "admin" or "realm-admin" or "offline_access" or "uma_authorization")
            throw new ArgumentException("A dedicated Forge administrator role is required.");
        _options = options; _role = role;
        _realm = new Uri(options.Authority.TrimEnd('/') + "/admin/realms/" + Uri.EscapeDataString(options.Realm) + "/");
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task PrepareAsync(CancellationToken ct)
    {
        try
        {
            await AuthenticateAsync(ct);
            using var get = await SendAsync(HttpMethod.Get, "roles/" + Uri.EscapeDataString(_role), null, ct);
            if (get.StatusCode == HttpStatusCode.NotFound)
            {
                using var create = await SendAsync(HttpMethod.Post, "roles", new { name = _role, description = "Aetheric Forge administrator", composite = false, clientRole = false }, ct);
                if (create.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.Conflict)) throw Failure("role_creation");
            }
            else if (!get.IsSuccessStatusCode) throw Failure("role_lookup");
            // Re-read even after a conflict: never assign a composite or client role.
            using var read = await SendAsync(HttpMethod.Get, "roles/" + Uri.EscapeDataString(_role), null, ct);
            if (!read.IsSuccessStatusCode) throw Failure("role_lookup");
            using var role = await JsonDocument.ParseAsync(await read.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var r = role.RootElement;
            if (r.GetProperty("name").GetString() != _role || r.GetProperty("composite").GetBoolean()
                || r.GetProperty("clientRole").GetBoolean()) throw Failure("role_incompatible");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RegistryBootstrapStaffException) { throw; }
        catch (Exception) { throw Failure("preparation"); }
    }

    // Search is read-only. Role creation/assignment happens only after confirmation.
    public async Task<ExistingKeycloakAdministrator?> FindExistingAsync(string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Length > 200 || username != username.Trim())
            throw new ArgumentException("Enter an exact Keycloak username.");
        try
        {
            await AuthenticateAsync(ct);
            using var response = await SendAsync(HttpMethod.Get, "users?username=" + Uri.EscapeDataString(username)
                + "&exact=true&max=2&briefRepresentation=false", null, ct);
            if (!response.IsSuccessStatusCode) throw Failure("lookup");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw Failure("lookup");
            var matches = json.RootElement.EnumerateArray().ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1 || !string.Equals(Text(matches[0], "username"), username, StringComparison.OrdinalIgnoreCase))
                throw Failure("lookup");
            var subject = Text(matches[0], "id");
            ValidateSubject(subject);
            var account = await ReadExistingAsync(subject!, ct);
            if (!string.Equals(account.Username, username, StringComparison.OrdinalIgnoreCase)) throw Failure("lookup");
            return account;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RegistryBootstrapStaffException) { throw; }
        catch (Exception) { throw Failure("lookup"); }
    }

    public async Task<ExistingKeycloakAdministrator> GetExistingAsync(string subjectId, CancellationToken ct)
    {
        ValidateSubject(subjectId);
        try
        {
            await AuthenticateAsync(ct);
            return await ReadExistingAsync(subjectId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RegistryBootstrapStaffException) { throw; }
        catch (Exception) { throw Failure("lookup"); }
    }

    private async Task<ExistingKeycloakAdministrator> ReadExistingAsync(string subjectId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "users/" + Uri.EscapeDataString(subjectId), null, ct);
        if (!response.IsSuccessStatusCode) throw Failure("lookup");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var user = json.RootElement;
        var username = Text(user, "username");
        if (Text(user, "id") != subjectId || string.IsNullOrWhiteSpace(username)
            || !user.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True
            || user.TryGetProperty("serviceAccountClientId", out var serviceAccount) && serviceAccount.ValueKind != JsonValueKind.Null)
            throw Failure("unavailable");
        var displayName = string.Join(" ", new[] { Text(user, "firstName"), Text(user, "lastName") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new(subjectId, username, Text(user, "email"), string.IsNullOrWhiteSpace(displayName) ? username : displayName);
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static void ValidateSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200 || subject is "." or ".."
            || subject.Any(c => !char.IsAsciiLetterOrDigit(c) && !"._:@-".Contains(c)))
            throw new ArgumentException("An exact Keycloak user ID is required.");
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret
            });
            using var response = await _http.PostAsync(_options.Authority.TrimEnd('/') + "/realms/"
                + Uri.EscapeDataString(_options.Realm) + "/protocol/openid-connect/token", form, ct);
            if (!response.IsSuccessStatusCode) throw Failure("authentication");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            _token = json.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(_token) || !string.Equals(json.RootElement.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw Failure("authentication");
    }

    public async Task<string> CreateAsync(NewRegistryAdministrator administrator, CancellationToken ct)
    {
        try
        {
            if (_token is null) throw Failure("preparation");
            using var response = await SendAsync(HttpMethod.Post, "users", new
            {
                username = administrator.Username, email = administrator.Email,
                firstName = administrator.FirstName, lastName = administrator.LastName,
                enabled = true, emailVerified = false,
                credentials = new[] { new { type = "password", value = administrator.Password, temporary = false } }
            }, ct);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new RegistryAccountRejectedException(response.StatusCode == HttpStatusCode.Conflict
                    ? "registry.account_exists" : "registry.account_rejected");
            if (response.StatusCode != HttpStatusCode.Created) throw Failure("creation_unknown");
            var location = response.Headers.Location;
            if (location is null) throw Failure("creation_unknown");
            var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri(_realm, "users"), location);
            var prefix = new Uri(_realm, "users/").AbsoluteUri;
            if (!absolute.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) || absolute.Query.Length != 0 || absolute.Fragment.Length != 0)
                throw Failure("creation_unknown");
            var subject = Uri.UnescapeDataString(absolute.AbsoluteUri[prefix.Length..]);
            if (string.IsNullOrWhiteSpace(subject) || subject is "." or ".." || subject.Any(c => !char.IsAsciiLetterOrDigit(c) && !"._:@-".Contains(c)))
                throw Failure("creation_unknown");
            return subject;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RegistryAccountRejectedException) { throw; }
        catch (RegistryBootstrapStaffException) { throw; }
        catch (Exception) { throw Failure("creation_unknown"); }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(_realm, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await _http.SendAsync(request, ct);
    }
    private static RegistryBootstrapStaffException Failure(string operation) => new("registry.account_" + operation + "_failed");
    public void Dispose() => _http.Dispose();
}
