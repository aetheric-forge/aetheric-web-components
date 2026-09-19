using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using AethericForge.Runtime.Providers.Identity.Keycloak;

namespace Aetheric.Provisioning.Registry;

// Service-account preflight and explicit browser callback registration for administrator sign-in.
public sealed class KeycloakProvisionerConnection : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _issuer;
    private readonly Uri _clients;
    private readonly string _clientId;
    private readonly string _clientSecret;

    public KeycloakProvisionerConnection(KeycloakOptions options) : this(options, null) { }

    internal KeycloakProvisionerConnection(KeycloakOptions options, HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority)
            || authority.Scheme != "https" || authority.UserInfo.Length != 0
            || authority.Query.Length != 0 || authority.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(options.Realm) || options.Realm != options.Realm.Trim()
            || options.Realm is "." or ".." || options.Realm.IndexOfAny(['/', '\\', '?', '#']) >= 0
            || string.IsNullOrWhiteSpace(options.ClientId) || options.ClientId != options.ClientId.Trim()
            || string.IsNullOrWhiteSpace(options.ClientSecret)
            || !string.IsNullOrWhiteSpace(options.AdminApiBaseAddress))
            throw new ArgumentException("Invalid Keycloak connection configuration.");
        var server = new Uri(authority.AbsoluteUri.TrimEnd('/') + "/");
        _issuer = new Uri(server, "realms/" + Uri.EscapeDataString(options.Realm));
        _clients = new Uri(server, "admin/realms/" + Uri.EscapeDataString(options.Realm)
            + "/clients?clientId=" + Uri.EscapeDataString(options.ClientId) + "&exact=true");
        _clientId = options.ClientId;
        _clientSecret = options.ClientSecret;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(30) };
    }

    public Task CheckAsync(CancellationToken ct = default) => CheckCoreAsync(null, null, ct);

    public Task PrepareAdministratorSignInAsync(Uri callback, string administratorRole, CancellationToken ct = default)
    {
        if (!callback.IsAbsoluteUri || callback.UserInfo.Length != 0 || callback.Query.Length != 0
            || callback.Fragment.Length != 0 || callback.AbsolutePath != "/setup/signin-oidc"
            || (callback.Scheme != "https" && !(callback.Scheme == "http" && callback.IsLoopback)))
            throw new ArgumentException("An exact HTTPS callback or local development callback is required.");
        if (string.IsNullOrWhiteSpace(administratorRole)) throw new ArgumentException("An administrator role is required.");
        return CheckCoreAsync(callback, administratorRole, ct);
    }

    private async Task CheckCoreAsync(Uri? callback, string? administratorRole, CancellationToken ct)
    {
        var failureCode = "registry.client_authentication_failed";
        try
        {
            using var credentials = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = _clientId, ["client_secret"] = _clientSecret
            });
            using var response = await _http.PostAsync(_issuer.AbsoluteUri + "/protocol/openid-connect/token", credentials, ct);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new RegistryBootstrapStaffException(await TokenRejectionCodeAsync(response, ct));
            if (!response.IsSuccessStatusCode) throw new RegistryBootstrapStaffException("registry.token_http_" + (int)response.StatusCode);
            using var tokenResponse = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var token = Text(tokenResponse.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(token) || token.Length > 131072
                || !string.Equals(Text(tokenResponse.RootElement, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new RegistryBootstrapStaffException(failureCode);
            VerifyIssuedToken(token);

            failureCode = "registry.client_lookup_failed";
            using var request = new HttpRequestMessage(HttpMethod.Get, _clients);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var clientResponse = await _http.SendAsync(request, ct);
            if (clientResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new RegistryBootstrapStaffException("registry.admin_api_unauthorized");
            if (!clientResponse.IsSuccessStatusCode) throw new RegistryBootstrapStaffException("registry.admin_http_" + (int)clientResponse.StatusCode);
            using var clients = await JsonDocument.ParseAsync(await clientResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (clients.RootElement.ValueKind != JsonValueKind.Array) throw new RegistryBootstrapStaffException(failureCode);
            var matches = clients.RootElement.EnumerateArray().Where(x => Text(x, "clientId") == _clientId).ToArray();
            if (matches.Length == 0) throw new RegistryBootstrapStaffException("registry.client_missing");
            if (matches.Length != 1) throw new RegistryBootstrapStaffException(failureCode);
            var client = matches[0];
            if (!Flag(client, "enabled", JsonValueKind.True) || !Flag(client, "publicClient", JsonValueKind.False)
                || !Flag(client, "serviceAccountsEnabled", JsonValueKind.True))
                throw new RegistryBootstrapStaffException("registry.client_unavailable");
            if (callback is not null)
            {
                failureCode = "registry.callback_registration_failed";
                var id = Text(client, "id");
                if (string.IsNullOrWhiteSpace(id)) throw new RegistryBootstrapStaffException(failureCode);
                var endpoint = new Uri(_clients.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + Uri.EscapeDataString(id));
                using var read = new HttpRequestMessage(HttpMethod.Get, endpoint);
                read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var currentResponse = await _http.SendAsync(read, ct);
                if (!currentResponse.IsSuccessStatusCode) throw new RegistryBootstrapStaffException(failureCode);
                var current = await currentResponse.Content.ReadFromJsonAsync<JsonObject>(ct)
                    ?? throw new RegistryBootstrapStaffException(failureCode);
                if (current["clientId"]?.GetValue<string>() != _clientId || current["id"]?.GetValue<string>() != id)
                    throw new RegistryBootstrapStaffException(failureCode);
                var redirects = current["redirectUris"] is JsonArray existing ? (JsonArray)existing.DeepClone() : new JsonArray();
                var present = redirects.Any(x => x?.GetValue<string>() == callback.AbsoluteUri);
                if (!present) redirects.Add(callback.AbsoluteUri);
                // Include only the Forge role in this client's allowed realm-role scope.
                // Existing role scopes and the client's full-scope setting remain unchanged.
                failureCode = "registry.role_scope_registration_failed";
                var realmPath = _clients.GetLeftPart(UriPartial.Path)[..^"/clients".Length];
                using var roleRead = new HttpRequestMessage(HttpMethod.Get, realmPath + "/roles/" + Uri.EscapeDataString(administratorRole!));
                roleRead.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var roleResponse = await _http.SendAsync(roleRead, ct);
                if (!roleResponse.IsSuccessStatusCode) throw new RegistryBootstrapStaffException(failureCode);
                using var roleJson = await JsonDocument.ParseAsync(await roleResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var role = roleJson.RootElement;
                var roleId = Text(role, "id");
                if (string.IsNullOrWhiteSpace(roleId) || Text(role, "name") != administratorRole
                    || !Flag(role, "composite", JsonValueKind.False) || !Flag(role, "clientRole", JsonValueKind.False))
                    throw new RegistryBootstrapStaffException(failureCode);
                var scopeEndpoint = endpoint.AbsoluteUri + "/scope-mappings/realm";
                using var scopeRead = new HttpRequestMessage(HttpMethod.Get, scopeEndpoint);
                scopeRead.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var scopeResponse = await _http.SendAsync(scopeRead, ct);
                if (!scopeResponse.IsSuccessStatusCode) throw new RegistryBootstrapStaffException(failureCode);
                using var scopes = await JsonDocument.ParseAsync(await scopeResponse.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (scopes.RootElement.ValueKind != JsonValueKind.Array) throw new RegistryBootstrapStaffException(failureCode);
                if (!scopes.RootElement.EnumerateArray().Any(x => Text(x, "id") == roleId && Text(x, "name") == administratorRole))
                {
                    using var scopeWrite = new HttpRequestMessage(HttpMethod.Post, scopeEndpoint)
                        { Content = JsonContent.Create(new[] { new { id = roleId, name = administratorRole } }) };
                    scopeWrite.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var scoped = await _http.SendAsync(scopeWrite, ct);
                    if (!scoped.IsSuccessStatusCode) throw new RegistryBootstrapStaffException(failureCode);
                }
                failureCode = "registry.callback_registration_failed";
                if (!present || current["standardFlowEnabled"]?.GetValue<bool>() != true)
                {
                    // Keycloak updates only supplied properties. Do not send a replacement
                    // client registration that resets secrets, origins, scopes, or attributes.
                    var patch = new JsonObject { ["standardFlowEnabled"] = true, ["redirectUris"] = redirects };
                    using var update = new HttpRequestMessage(HttpMethod.Put, endpoint) { Content = JsonContent.Create(patch) };
                    update.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using var updated = await _http.SendAsync(update, ct);
                    if (!updated.IsSuccessStatusCode) throw new RegistryBootstrapStaffException(failureCode);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RegistryBootstrapStaffException) { throw; }
        catch (HttpRequestException ex)
        {
            throw new RegistryBootstrapStaffException(ex.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => "registry.tls_failed",
                HttpRequestError.NameResolutionError => "registry.dns_failed",
                HttpRequestError.ConnectionError => "registry.network_failed",
                _ => "registry.http_transport_failed"
            });
        }
        catch (OperationCanceledException) { throw new RegistryBootstrapStaffException("registry.request_timeout"); }
        catch (Exception) { throw new RegistryBootstrapStaffException(failureCode); }
    }

    private static async Task<string> TokenRejectionCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Never expose error_description or an arbitrary response body: a proxy or provider
        // may include credentials. Only known OAuth error names and the HTTP status leave here.
        var code = "registry.token_http_" + (int)response.StatusCode;
        try
        {
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var error = Text(body.RootElement, "error");
            if (error is "invalid_client" or "unauthorized_client" or "invalid_grant"
                or "invalid_request" or "invalid_scope" or "unsupported_grant_type" or "access_denied")
                return "registry.token_" + error + "_http_" + (int)response.StatusCode;
        }
        catch (JsonException) { }
        return code;
    }

    private void VerifyIssuedToken(string token)
    {
        // Inspect only the token obtained directly from the configured HTTPS token endpoint.
        // This is not a JWT authentication handler and never accepts a browser-supplied token
        // or establishes a human session. The Admin API must also accept this same token.
        var parts = token.Split('.');
        if (parts.Length != 3) throw new RegistryBootstrapStaffException("registry.client_authentication_failed");
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
        using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
        var claims = json.RootElement;
        if (Text(claims, "iss") != _issuer.AbsoluteUri)
            throw new RegistryBootstrapStaffException("registry.token_issuer_mismatch");
        if (Text(claims, "azp") != _clientId)
            throw new RegistryBootstrapStaffException("registry.token_client_mismatch");
        if (!claims.TryGetProperty("exp", out var expiry) || expiry.ValueKind != JsonValueKind.Number
            || !expiry.TryGetInt64(out var seconds))
            throw new RegistryBootstrapStaffException("registry.token_expiry_invalid");
        if (seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new RegistryBootstrapStaffException("registry.token_expired");
        if (!claims.TryGetProperty("resource_access", out var access) || access.ValueKind != JsonValueKind.Object
            || !access.TryGetProperty("realm-management", out var management) || management.ValueKind != JsonValueKind.Object
            || !management.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array
            || !roles.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "realm-admin"))
            throw new RegistryBootstrapStaffException("registry.realm_admin_required");
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool Flag(JsonElement value, string name, JsonValueKind kind) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == kind;

    public void Dispose() => _http.Dispose();
}
