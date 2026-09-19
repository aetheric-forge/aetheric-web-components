using System.Net;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Registry;
using AethericForge.Runtime.Providers.Identity.Keycloak;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class KeycloakAdministratorCreatorTests
{
    private static KeycloakOptions Options => new() { Authority = "https://identity.example", Realm = "root", ClientId = "provisioner", ClientSecret = "client-secret" };
    private static NewRegistryAdministrator Account => new() { Username = "new-admin", Email = "admin@example.com", FirstName = "New", LastName = "Admin", Password = "account-password" };
    [Fact]
    public async Task Creates_dedicated_role_and_enabled_user_with_password_in_single_post()
    {
        var handler = new Handler { MissingRole = true };
        using var creator = new KeycloakAdministratorCreator(Options, "forge-admin", handler);
        await creator.PrepareAsync(default);
        Assert.Equal("new-subject", await creator.CreateAsync(Account, default));
        Assert.Equal(1, handler.UserPosts);
        using var body = JsonDocument.Parse(handler.UserBody!);
        Assert.Equal("new-admin", body.RootElement.GetProperty("username").GetString());
        Assert.True(body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.False(body.RootElement.GetProperty("emailVerified").GetBoolean());
        Assert.Equal(Account.Password, body.RootElement.GetProperty("credentials")[0].GetProperty("value").GetString());
        Assert.DoesNotContain("realm-admin", handler.UserBody);
    }
    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(403)] [InlineData(409)]
    public async Task Rejected_account_never_overwrites_existing_user(int status)
    {
        var handler = new Handler { UserStatus = (HttpStatusCode)status };
        using var creator = new KeycloakAdministratorCreator(Options, "forge-admin", handler);
        await creator.PrepareAsync(default);
        var error = await Assert.ThrowsAsync<RegistryAccountRejectedException>(() => creator.CreateAsync(Account, default));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(1, handler.UserPosts);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("https://attacker.example/users/new-subject")]
    [InlineData("https://identity.example/admin/realms/other/users/new-subject")]
    [InlineData("https://identity.example/admin/realms/root/users/../clients/x")]
    [InlineData("https://identity.example/admin/realms/root/users/a%2Fb")]
    public async Task Missing_or_untrusted_created_location_is_ambiguous(string? location)
    {
        using var creator = new KeycloakAdministratorCreator(Options, "forge-admin", new Handler { Location = location });
        await creator.PrepareAsync(default);
        await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => creator.CreateAsync(Account, default));
    }
    [Fact]
    public async Task Composite_role_cannot_be_adopted()
    {
        var handler = new Handler { Composite = true };
        using var creator = new KeycloakAdministratorCreator(Options, "forge-admin", handler);
        await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => creator.PrepareAsync(default));
        Assert.Equal(0, handler.UserPosts);
    }
    [Fact]
    public async Task Unknown_provider_failure_is_redacted_and_not_reported_as_safe_to_retry()
    {
        using var creator = new KeycloakAdministratorCreator(Options, "forge-admin", new Handler { UserStatus = HttpStatusCode.InternalServerError });
        await creator.PrepareAsync(default);
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => creator.CreateAsync(Account, default));
        Assert.DoesNotContain("secret", error.ToString());
    }
    private sealed class Handler : HttpMessageHandler
    {
        public bool MissingRole, Composite;
        public HttpStatusCode UserStatus = HttpStatusCode.Created;
        public string? Location = "https://identity.example/admin/realms/root/users/new-subject";
        public string? UserBody;
        public int UserPosts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/token")) return Json(HttpStatusCode.OK, "{\"access_token\":\"issued-token\",\"token_type\":\"Bearer\"}");
            Assert.Equal("issued-token", request.Headers.Authorization?.Parameter);
            if (path.EndsWith("/roles/forge-admin"))
                return Json(MissingRole ? HttpStatusCode.NotFound : HttpStatusCode.OK, JsonSerializer.Serialize(new { name = "forge-admin", composite = Composite, clientRole = false }));
            if (path.EndsWith("/roles")) { Assert.Equal(HttpMethod.Post, request.Method); MissingRole = false; return Json(HttpStatusCode.Created, ""); }
            Assert.EndsWith("/users", path);
            Assert.Equal(HttpMethod.Post, request.Method);
            UserPosts++; UserBody = await request.Content!.ReadAsStringAsync(ct);
            var response = Json(UserStatus, "sensitive client-secret account-password");
            if (Location is not null) response.Headers.Location = new Uri(Location);
            return response;
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
