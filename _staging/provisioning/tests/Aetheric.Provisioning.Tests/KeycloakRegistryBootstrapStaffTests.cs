using System.Net;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Registry;
using AethericForge.Runtime.Providers.Identity.Keycloak;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class KeycloakRegistryBootstrapStaffTests
{
    private static RegistryBootstrapSettings Settings => new("https://identity.example/realms/root", "provisioner", "provisioner-admin");
    private static KeycloakOptions Options => new() { Authority = "https://identity.example", Realm = "root", ClientId = "provisioner", ClientSecret = "test-client-secret" };
    private static KeycloakRegistryBootstrapStaff Staff(Handler handler) => new(Settings, Options, handler);

    [Fact]
    public async Task Uses_runtime_directory_and_clerk_and_only_assigns_the_existing_role()
    {
        var handler = new Handler();
        using var staff = Staff(handler);
        Assert.True(await staff.PrincipalExistsAsync("operator", default));
        await staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, default);
        await staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, default);
        var assignments = handler.Requests.Where(x => x.Path.EndsWith("/role-mappings/realm", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, assignments.Length);
        Assert.All(assignments, x =>
        {
            Assert.Equal(HttpMethod.Post, x.Method);
            using var json = JsonDocument.Parse(x.Body);
            Assert.Equal("provisioner-admin", json.RootElement[0].GetProperty("name").GetString());
        });
        Assert.All(handler.Requests.Where(x => x.Method != HttpMethod.Get), x =>
            Assert.True(x.Path.EndsWith("/token", StringComparison.Ordinal) || x.Path.EndsWith("/role-mappings/realm", StringComparison.Ordinal)));
        Assert.All(handler.Requests.Where(x => x.Path.EndsWith("/token", StringComparison.Ordinal)), x =>
        {
            Assert.Contains("grant_type=client_credentials", x.Body);
            Assert.Contains("client_id=provisioner", x.Body);
        });
    }

    [Theory]
    [InlineData(404, "registry.role_missing")]
    [InlineData(401, "registry.role_lookup_unauthorized")]
    [InlineData(403, "registry.role_lookup_unauthorized")]
    [InlineData(500, "registry.role_lookup_failed")]
    [InlineData(200, "registry.role_mismatch", "{\"name\":\"another-role\"}")]
    [InlineData(200, "registry.role_lookup_failed", "{}")]
    [InlineData(200, "registry.role_lookup_failed", "invalid json")]
    [InlineData(200, "registry.role_lookup_failed", "null")]
    public async Task Role_inspection_errors_prevent_assignment_and_redact_provider_details(
        int status, string code, string? body = null)
    {
        var handler = new Handler { RoleStatus = (HttpStatusCode)status, RoleBody = body ?? "sensitive upstream error" };
        using var staff = Staff(handler);
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() =>
            staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, default));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("sensitive", error.ToString());
        Assert.DoesNotContain(handler.Requests, x => x.Path.EndsWith("/role-mappings/realm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retry_reinspects_role_before_repeating_assignment()
    {
        var handler = new Handler();
        using var staff = Staff(handler);
        await staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, default);
        handler.RoleStatus = HttpStatusCode.NotFound;
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() =>
            staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, default));
        Assert.Equal("registry.role_missing", error.Code);
        Assert.Single(handler.Requests, x => x.Path.EndsWith("/role-mappings/realm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_during_role_inspection_is_preserved()
    {
        using var ct = new CancellationTokenSource();
        var handler = new Handler { OnRoleLookup = ct.Cancel };
        using var staff = Staff(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, ct.Token));
        Assert.DoesNotContain(handler.Requests, x => x.Path.EndsWith("/role-mappings/realm", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(200, false)]
    public async Task Missing_or_disabled_principal_cannot_receive_authority(int status, bool enabled)
    {
        var handler = new Handler { UserStatus = (HttpStatusCode)status, Enabled = enabled };
        using var staff = Staff(handler);
        Assert.False(await staff.PrincipalExistsAsync("operator", default));
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, default));
        Assert.Equal("registry.principal_unavailable", error.Code);
        Assert.DoesNotContain(handler.Requests, x => x.Path.EndsWith("/role-mappings/realm", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Directory_failure_is_not_reported_as_absent_and_does_not_leak_responses(int status)
    {
        using var staff = Staff(new Handler { UserStatus = (HttpStatusCode)status });
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => staff.PrincipalExistsAsync("operator", default));
        Assert.Equal("registry.principal_lookup_failed", error.Code);
        Assert.DoesNotContain("sensitive", error.ToString());
    }

    [Theory]
    [InlineData(404, "registry.role_or_principal_missing")]
    [InlineData(403, "registry.assignment_unauthorized")]
    [InlineData(409, "registry.assignment_failed")]
    [InlineData(500, "registry.assignment_failed")]
    public async Task Assignment_errors_fail_closed_without_raw_provider_details(int status, string code)
    {
        using var staff = Staff(new Handler { AssignmentStatus = (HttpStatusCode)status });
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, default));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("sensitive", error.ToString());
    }

    [Fact]
    public async Task Unconfigured_role_and_mismatched_identity_cannot_be_assigned()
    {
        var handler = new Handler { Subject = "another" };
        using var staff = Staff(handler);
        await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => staff.EnsureAdminAuthorityAsync("operator", "realm-admin", default));
        Assert.Empty(handler.Requests);
        var error = await Assert.ThrowsAsync<RegistryBootstrapStaffException>(() => staff.EnsureAdminAuthorityAsync("operator", Settings.AdminRole, default));
        Assert.Equal("registry.principal_mismatch", error.Code);
        Assert.DoesNotContain(handler.Requests, x => x.Path.EndsWith("/role-mappings/realm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_is_preserved_and_no_request_is_sent()
    {
        var handler = new Handler();
        using var staff = Staff(handler);
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staff.PrincipalExistsAsync("operator", ct.Token));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("client")]
    [InlineData("http")]
    [InlineData("override")]
    public void Misbound_configuration_is_rejected_before_network_access(string change)
    {
        var settings = Settings;
        var options = Options;
        if (change == "issuer") settings = settings with { Issuer = "https://elsewhere.example/realms/root" };
        if (change == "client") options.ClientId = "another-client";
        if (change == "http") options.Authority = "http://identity.example";
        if (change == "override") options.AdminApiBaseAddress = "https://elsewhere.example/admin";
        var handler = new Handler();
        Assert.Throws<ArgumentException>(() => new KeycloakRegistryBootstrapStaff(settings, options, handler));
        Assert.Empty(handler.Requests);
    }

    private sealed record Request(HttpMethod Method, string Path, string Body);
    private sealed class Handler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode ClientStatus { get; init; } = HttpStatusCode.OK;
        public string ClientBody { get; init; } = "[{\"id\":\"client\",\"clientId\":\"provisioner\",\"enabled\":true,\"publicClient\":false}]";
        public HttpStatusCode UserStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode AssignmentStatus { get; init; } = HttpStatusCode.NoContent;
        public HttpStatusCode RoleStatus { get; set; } = HttpStatusCode.OK;
        public string RoleBody { get; init; } = "{\"id\":\"role-id\",\"name\":\"provisioner-admin\"}";
        public Action? OnRoleLookup { get; init; }
        public bool Enabled { get; init; } = true;
        public string Subject { get; init; } = "operator";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(new(request.Method, path, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            if (path == "/realms/root/protocol/openid-connect/token")
                return Json(TokenStatus, TokenStatus == HttpStatusCode.OK ? "{\"access_token\":\"test-token\",\"expires_in\":300}" : "sensitive upstream error");
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            if (path == "/admin/realms/root/clients" && request.Method == HttpMethod.Get)
                return Json(ClientStatus, ClientBody);
            if (path == "/admin/realms/root/users/operator" && request.Method == HttpMethod.Get)
                return Json(UserStatus, UserStatus == HttpStatusCode.OK ? JsonSerializer.Serialize(new { id = Subject, enabled = Enabled }) : "sensitive upstream error");
            if (path == "/admin/realms/root/roles/provisioner-admin" && request.Method == HttpMethod.Get)
            {
                OnRoleLookup?.Invoke();
                ct.ThrowIfCancellationRequested();
                return Json(RoleStatus, RoleBody);
            }
            if (path == "/admin/realms/root/users/operator/role-mappings/realm" && request.Method == HttpMethod.Post)
                return Json(AssignmentStatus, "sensitive upstream error");
            throw new InvalidOperationException("Unexpected request.");
        }
        private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
