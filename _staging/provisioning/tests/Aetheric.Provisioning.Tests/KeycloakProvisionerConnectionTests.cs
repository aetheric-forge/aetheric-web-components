using System.Net;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Registry;
using AethericForge.Runtime.Providers.Identity.Keycloak;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class KeycloakProvisionerConnectionTests
{
    private static KeycloakOptions Options => new()
    {
        Authority = "https://identity.example", Realm = "root", ClientId = "provisioner", ClientSecret = "test-secret"
    };

    [Fact]
    public async Task Realm_admin_token_connects_without_any_human_role_lookup_or_admin_write()
    {
        var handler = new Handler();
        using var connection = new KeycloakProvisionerConnection(Options, handler);
        await connection.CheckAsync();
        Assert.Equal(new[] { "/realms/root/protocol/openid-connect/token", "/admin/realms/root/clients" }, handler.Paths);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"realm_access\":{\"roles\":[\"admin\",\"realm-admin\",\"provisioner-admin\"]}}")]
    [InlineData("{\"resource_access\":{\"other-client\":{\"roles\":[\"realm-admin\"]}}}")]
    [InlineData("{\"resource_access\":{\"realm-management\":{\"roles\":[\"view-clients\",\"manage-users\"]}}}")]
    [InlineData("{\"resource_access\":{\"realm-management\":{\"roles\":[\"Realm-Admin\"]}}}")]
    public async Task Realm_roles_and_unrelated_or_scoped_out_client_roles_do_not_prove_realm_admin(string claims)
    {
        var handler = new Handler { RoleClaims = claims };
        await Fails(handler, "registry.realm_admin_required");
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Invalid_credentials_are_distinct_from_missing_permissions(int status)
    {
        var handler = new Handler { TokenStatus = (HttpStatusCode)status };
        await Fails(handler, "registry.token_http_" + status);
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("invalid_client")]
    [InlineData("unauthorized_client")]
    [InlineData("invalid_grant")]
    [InlineData("invalid_request")]
    [InlineData("invalid_scope")]
    [InlineData("unsupported_grant_type")]
    [InlineData("access_denied")]
    public async Task Token_rejection_reports_only_known_error_and_status(string error) =>
        await Fails(new Handler
        {
            TokenStatus = HttpStatusCode.BadRequest,
            TokenErrorBody = JsonSerializer.Serialize(new { error, error_description = "sensitive test-secret" })
        }, "registry.token_" + error + "_http_400");

    [Fact]
    public async Task Unknown_oauth_error_never_leaks_provider_content() =>
        await Fails(new Handler { TokenStatus = HttpStatusCode.Forbidden,
            TokenErrorBody = "{\"error\":\"sensitive test-secret\"}" }, "registry.token_http_403");

    [Theory]
    [InlineData(401, "registry.admin_api_unauthorized")]
    [InlineData(403, "registry.admin_api_unauthorized")]
    [InlineData(500, "registry.admin_http_500")]
    [InlineData(302, "registry.admin_http_302")]
    public async Task Admin_api_must_accept_the_issued_token(int status, string code) =>
        await Fails(new Handler { ClientStatus = (HttpStatusCode)status }, code);

    [Theory]
    [InlineData("[]", "registry.client_missing")]
    [InlineData("[{\"clientId\":\"other\"}]", "registry.client_missing")]
    [InlineData("{}", "registry.client_lookup_failed")]
    [InlineData("invalid json", "registry.client_lookup_failed")]
    [InlineData("[{\"clientId\":\"provisioner\"}]", "registry.client_unavailable")]
    [InlineData("[{\"clientId\":\"provisioner\",\"enabled\":false,\"publicClient\":false,\"serviceAccountsEnabled\":true}]", "registry.client_unavailable")]
    [InlineData("[{\"clientId\":\"provisioner\",\"enabled\":true,\"publicClient\":true,\"serviceAccountsEnabled\":true}]", "registry.client_unavailable")]
    [InlineData("[{\"clientId\":\"provisioner\",\"enabled\":true,\"publicClient\":false,\"serviceAccountsEnabled\":false}]", "registry.client_unavailable")]
    public async Task Registration_must_be_usable(string body, string code) =>
        await Fails(new Handler { ClientBody = body }, code);

    [Theory]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-client")]
    [InlineData("expired")]
    [InlineData("malformed")]
    [InlineData("empty")]
    public async Task Invalid_issued_tokens_cannot_pass(string change)
    {
        var handler = new Handler { TokenChange = change };
        await Fails(handler, change switch
        {
            "wrong-issuer" => "registry.token_issuer_mismatch",
            "wrong-client" => "registry.token_client_mismatch",
            "expired" => "registry.token_expired",
            _ => "registry.client_authentication_failed"
        });
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task Cancellation_during_api_read_is_preserved()
    {
        using var cts = new CancellationTokenSource();
        var handler = new Handler { BeforeClient = cts.Cancel };
        using var connection = new KeycloakProvisionerConnection(Options, handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.CheckAsync(cts.Token));
    }

    [Fact]
    public async Task Transport_failure_is_redacted() =>
        await Fails(new Handler { BeforeClient = () => throw new HttpRequestException("sensitive test-secret") }, "registry.http_transport_failed");

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, "registry.tls_failed")]
    [InlineData(HttpRequestError.NameResolutionError, "registry.dns_failed")]
    [InlineData(HttpRequestError.ConnectionError, "registry.network_failed")]
    public async Task Transport_diagnostics_are_specific_and_redacted(HttpRequestError error, string code) =>
        await Fails(new Handler { BeforeClient = () => throw new HttpRequestException(error, "sensitive test-secret") }, code);

    [Fact]
    public async Task Timeout_is_distinct_from_caller_cancellation() =>
        await Fails(new Handler { BeforeClient = () => throw new TaskCanceledException("sensitive") }, "registry.request_timeout");

    [Theory]
    [InlineData("http")]
    [InlineData("realm")]
    [InlineData("override")]
    public void Invalid_destinations_are_rejected(string change)
    {
        var options = Options;
        if (change == "http") options.Authority = "http://identity.example";
        if (change == "realm") options.Realm = "../elsewhere";
        if (change == "override") options.AdminApiBaseAddress = "https://elsewhere.example";
        Assert.Throws<ArgumentException>(() => new KeycloakProvisionerConnection(options, new Handler()));
    }

    private static async Task Fails(Handler handler, string code)
    {
        using var connection = new KeycloakProvisionerConnection(Options, handler);
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => connection.CheckAsync());
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.DoesNotContain("test-secret", error.ToString());
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public string RoleClaims { get; init; } = """{"resource_access":{"realm-management":{"roles":["realm-admin"]}}}""";
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode ClientStatus { get; init; } = HttpStatusCode.OK;
        public string ClientBody { get; init; } = """[{"id":"id","clientId":"provisioner","enabled":true,"publicClient":false,"serviceAccountsEnabled":true}]""";
        public string TokenErrorBody { get; init; } = "sensitive upstream error";
        public string? TokenChange { get; init; }
        public Action? BeforeClient { get; init; }
        private string? _issuedToken;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/realms/root/protocol/openid-connect/token")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                var body = await request.Content!.ReadAsStringAsync(ct);
                Assert.Contains("grant_type=client_credentials", body);
                Assert.Contains("client_id=provisioner", body);
                Assert.Contains("client_secret=test-secret", body);
                var claims = JsonSerializer.Deserialize<Dictionary<string, object>>(RoleClaims)!;
                claims["iss"] = TokenChange == "wrong-issuer" ? "https://elsewhere.example" : "https://identity.example/realms/root";
                claims["azp"] = TokenChange == "wrong-client" ? "other" : "provisioner";
                claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(TokenChange == "expired" ? -5 : 5).ToUnixTimeSeconds();
                _issuedToken = TokenChange switch
                {
                    "malformed" => "not-a-jwt", "empty" => "",
                    _ => "eyJhbGciOiJSUzI1NiJ9." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".test-signature"
                };
                return Json(TokenStatus, TokenStatus == HttpStatusCode.OK
                    ? JsonSerializer.Serialize(new { access_token = _issuedToken, token_type = "Bearer" }) : TokenErrorBody);
            }
            Assert.Equal("/admin/realms/root/clients", path);
            Assert.Equal("?clientId=provisioner&exact=true", request.RequestUri.Query);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(_issuedToken, request.Headers.Authorization?.Parameter);
            BeforeClient?.Invoke();
            ct.ThrowIfCancellationRequested();
            return Json(ClientStatus, ClientStatus == HttpStatusCode.OK ? ClientBody : "sensitive upstream error");
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
