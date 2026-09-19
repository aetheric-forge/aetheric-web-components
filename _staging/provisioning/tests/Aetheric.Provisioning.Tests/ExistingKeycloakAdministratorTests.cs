using System.Net;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Registry;
using AethericForge.Runtime.Providers.Identity.Keycloak;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class ExistingKeycloakAdministratorTests
{
    private static KeycloakAdministratorCreator Accounts(Handler handler) => new(new KeycloakOptions
    { Authority = "https://identity.example", Realm = "root", ClientId = "provisioner", ClientSecret = "secret" }, "forge-admin", handler);

    [Fact]
    public async Task Exact_username_lookup_reads_full_account_without_admin_writes()
    {
        var handler = new Handler();
        using var accounts = Accounts(handler);
        var result = await accounts.FindExistingAsync("existing+admin@example.com", default);
        Assert.NotNull(result);
        Assert.Equal("existing-subject", result.SubjectId);
        Assert.Equal("Existing Admin", result.DisplayName);
        Assert.Equal("existing+admin@example.com", result.Username);
        Assert.Equal("admin@example.com", result.Email);
        Assert.Equal(new[] { "/realms/root/protocol/openid-connect/token", "/admin/realms/root/users", "/admin/realms/root/users/existing-subject" }, handler.Paths);
    }
    [Fact]
    public async Task Confirmation_rechecks_immutable_subject_without_searching_username()
    {
        var handler = new Handler { Username = "renamed-admin" };
        using var accounts = Accounts(handler);
        var result = await accounts.GetExistingAsync("existing-subject", default);
        Assert.Equal("renamed-admin", result.Username);
        Assert.DoesNotContain("/admin/realms/root/users", handler.Paths);
    }
    [Fact]
    public async Task Missing_username_returns_no_candidate()
    {
        using var accounts = Accounts(new Handler { SearchBody = "[]" });
        Assert.Null(await accounts.FindExistingAsync("existing+admin@example.com", default));
    }
    [Theory]
    [InlineData("disabled")]
    [InlineData("service-account")]
    [InlineData("wrong-subject")]
    [InlineData("missing-enabled")]
    [InlineData("missing-username")]
    public async Task Unusable_accounts_cannot_be_confirmed(string scenario)
    {
        using var accounts = Accounts(new Handler { Scenario = scenario });
        await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => accounts.GetExistingAsync("existing-subject", default));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"existing-subject\",\"username\":\"other\"}]")]
    [InlineData("[{\"id\":\"existing-subject\",\"username\":\"existing+admin@example.com\"},{\"id\":\"other\",\"username\":\"existing+admin@example.com\"}]")]
    [InlineData("[{\"id\":\"../other\",\"username\":\"existing+admin@example.com\"}]")]
    public async Task Malformed_ambiguous_or_mismatched_search_fails_closed(string body)
    {
        using var accounts = Accounts(new Handler { SearchBody = body });
        await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => accounts.FindExistingAsync("existing+admin@example.com", default));
    }
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(404)] [InlineData(500)]
    public async Task Provider_errors_are_redacted(int status)
    {
        using var accounts = Accounts(new Handler { Status = (HttpStatusCode)status });
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => accounts.GetExistingAsync("existing-subject", default));
        Assert.DoesNotContain("sensitive", error.ToString());
    }
    [Fact]
    public async Task Cancellation_is_preserved()
    {
        using var accounts = Accounts(new Handler());
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accounts.FindExistingAsync("existing+admin@example.com", cts.Token));
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string Username = "existing+admin@example.com";
        public string? SearchBody, Scenario;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public List<string> Paths = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.EndsWith("/token"))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                return Task.FromResult(Json("{\"access_token\":\"token\",\"token_type\":\"Bearer\"}"));
            }
            Assert.Equal(HttpMethod.Get, request.Method); // No role/user/password writes during lookup or confirmation read.
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            if (path.EndsWith("/users"))
            {
                Assert.Equal("?username=existing%2Badmin%40example.com&exact=true&max=2&briefRepresentation=false", request.RequestUri.Query);
                return Task.FromResult(Json(SearchBody ?? JsonSerializer.Serialize(new[] { new { id = "existing-subject", username = Username } })));
            }
            Assert.Equal("/admin/realms/root/users/existing-subject", path);
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("sensitive provider body") });
            var user = new Dictionary<string, object?>
            {
                ["id"] = Scenario == "wrong-subject" ? "other" : "existing-subject", ["username"] = Username,
                ["firstName"] = "Existing", ["lastName"] = "Admin", ["email"] = "admin@example.com",
                ["enabled"] = Scenario != "disabled"
            };
            if (Scenario == "service-account") user["serviceAccountClientId"] = "provisioner";
            if (Scenario == "missing-enabled") user.Remove("enabled");
            if (Scenario == "missing-username") user.Remove("username");
            return Task.FromResult(Json(JsonSerializer.Serialize(user)));
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
