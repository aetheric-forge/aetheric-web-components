using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aetheric.Provisioning.Registry;
using AethericForge.Runtime.Providers.Identity.Keycloak;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class KeycloakAdministratorSignInTests
{
    [Fact]
    public async Task Registers_exact_callback_and_role_scope_without_replacing_other_client_settings()
    {
        var handler = new Handler();
        using var connection = new KeycloakProvisionerConnection(new KeycloakOptions
        { Authority = "https://identity.example", Realm = "root", ClientId = "provisioner", ClientSecret = "secret" }, handler);
        var callback = new Uri("https://provisioner.example/setup/signin-oidc");
        await connection.PrepareAdministratorSignInAsync(callback, "forge-admin");
        await connection.PrepareAdministratorSignInAsync(callback, "forge-admin");
        Assert.Equal(1, handler.ClientUpdates);
        Assert.Equal(1, handler.ScopeUpdates);
        Assert.Equal(new[] { "https://existing.example/callback", callback.AbsoluteUri }, handler.Client["redirectUris"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.False(handler.Client["fullScopeAllowed"]!.GetValue<bool>());
        Assert.Equal("existing-secret", handler.Client["secret"]!.GetValue<string>());
        Assert.True(handler.Client["standardFlowEnabled"]!.GetValue<bool>());
    }
    [Fact]
    public async Task Composite_role_cannot_be_added_to_signin_scope()
    {
        var handler = new Handler { Composite = true };
        using var connection = new KeycloakProvisionerConnection(new KeycloakOptions
        { Authority = "https://identity.example", Realm = "root", ClientId = "provisioner", ClientSecret = "secret" }, handler);
        await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => connection.PrepareAdministratorSignInAsync(new Uri("https://provisioner.example/setup/signin-oidc"), "forge-admin"));
        Assert.Equal(0, handler.ClientUpdates);
        Assert.Equal(0, handler.ScopeUpdates);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public JsonObject Client = JsonNode.Parse("""{"id":"client-id","clientId":"provisioner","enabled":true,"publicClient":false,"serviceAccountsEnabled":true,"standardFlowEnabled":false,"fullScopeAllowed":false,"secret":"existing-secret","redirectUris":["https://existing.example/callback"],"attributes":{"unrelated":"preserved"}}""")!.AsObject();
        public int ClientUpdates, ScopeUpdates;
        public bool Composite;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/token"))
            {
                var claims = new { iss = "https://identity.example/realms/root", azp = "provisioner", exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), resource_access = new Dictionary<string, object> { ["realm-management"] = new { roles = new[] { "realm-admin" } } } };
                return Json(new { token_type = "Bearer", access_token = "header." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature" });
            }
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (path.EndsWith("/clients")) return Json(new[] { Client });
            if (path.EndsWith("/roles/forge-admin")) return Json(new { id = "role-id", name = "forge-admin", composite = Composite, clientRole = false });
            if (path.EndsWith("/scope-mappings/realm"))
            {
                if (request.Method == HttpMethod.Get) return Json(ScopeUpdates == 0 ? Array.Empty<object>() : new object[] { new { id = "role-id", name = "forge-admin" } });
                Assert.Equal(HttpMethod.Post, request.Method);
                var roles = await request.Content!.ReadFromJsonAsync<JsonArray>(ct);
                Assert.Single(roles!);
                Assert.Equal("forge-admin", roles![0]!["name"]!.GetValue<string>());
                ScopeUpdates++;
                return new(HttpStatusCode.NoContent);
            }
            Assert.EndsWith("/clients/client-id", path);
            if (request.Method == HttpMethod.Get) return Json(Client);
            Assert.Equal(HttpMethod.Put, request.Method);
            var patch = await request.Content!.ReadFromJsonAsync<JsonObject>(ct);
            Assert.Equal(new[] { "redirectUris", "standardFlowEnabled" }, patch!.Select(x => x.Key).Order());
            foreach (var pair in patch!) Client[pair.Key] = pair.Value!.DeepClone();
            ClientUpdates++;
            return new(HttpStatusCode.NoContent);
        }
        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
