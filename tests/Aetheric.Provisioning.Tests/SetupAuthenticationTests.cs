using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Persistence;
using Aetheric.Provisioning.Registry;
using Aetheric.Provisioning.Components;
using Aetheric.Provisioning.Web.Components;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class SetupAuthenticationTests
{
    [Fact]
    public async Task Verified_connection_opens_account_form_without_human_oidc_login()
    {
        await using var host = await Host.Start();
        var anonymous = await host.Client.GetAsync("/setup/administrator");
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains("/setup?", anonymous.Headers.Location!.ToString());
        var ticket = await host.Ticket();
        var missingCsrf = await host.Client.PostAsync("/setup/connection/start", new FormUrlEncodedContent(new Dictionary<string, string> { ["ticket"] = ticket.Ticket }));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        var start = await host.Begin(ticket);
        Assert.Equal("/setup/administrator", start.Headers.Location!.ToString());
        var page = await host.Client.GetStringAsync("/setup/administrator");
        Assert.Contains("Create Forge administrator", page);
        Assert.Contains("Use an existing account", page);
        Assert.Contains("Find account", page);
        Assert.DoesNotContain("client-secret", page);
        Assert.DoesNotContain("platform administrator", page);
        var current = await host.Ticket();
        var reused = await host.Begin(ticket with { Antiforgery = current.Antiforgery });
        Assert.Equal("/setup/connection-error?reason=ticket", reused.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Localhost_default_origin_mismatch_explains_address_and_preserves_unused_ticket()
    {
        await using var host = await Host.Start(useDefaultOrigin: true);
        var ticket = await host.Ticket();
        var mismatch = await host.Begin(ticket);
        Assert.Equal("/setup/connection-error?reason=origin", mismatch.Headers.Location!.ToString());
        var page = await host.Client.GetStringAsync(mismatch.Headers.Location);
        Assert.Contains("http://127.0.0.1:5180/setup", page);
        Assert.Contains("different address", page);
        Assert.DoesNotContain("Sign-in could not complete", page);
        Assert.DoesNotContain("client-secret", page);
        host.Client.DefaultRequestHeaders.Host = "127.0.0.1:5180";
        var corrected = await host.Begin(ticket);
        Assert.Equal("/setup/administrator", corrected.Headers.Location!.ToString());
        Assert.Contains("Create Forge administrator", await host.Client.GetStringAsync("/setup/administrator"));
    }

    [Theory]
    [InlineData("valid", RegistryBootstrapPhase.Completed)]
    [InlineData("resume-valid", RegistryBootstrapPhase.Completed)]
    [InlineData("wrong-subject", RegistryBootstrapPhase.AuthorityAssigned)]
    [InlineData("missing-role", RegistryBootstrapPhase.AuthorityAssigned)]
    [InlineData("bad-nonce", RegistryBootstrapPhase.AuthorityAssigned)]
    [InlineData("bad-signature", RegistryBootstrapPhase.AuthorityAssigned)]
    [InlineData("bad-issuer", RegistryBootstrapPhase.AuthorityAssigned)]
    [InlineData("bad-audience", RegistryBootstrapPhase.AuthorityAssigned)]
    public async Task Real_oidc_callback_completes_only_after_protocol_checks_and_selected_identity(string scenario, RegistryBootstrapPhase expected)
    {
        await using var host = await Host.Start();
        await host.Store.SaveAsync(new(host.Configuration.Settings, scenario == "resume-valid" ? RegistryBootstrapPhase.Completed : RegistryBootstrapPhase.AuthorityAssigned, "selected-subject"), default);
        var beginning = await host.Begin(await host.Ticket());
        if (scenario == "resume-valid") Assert.Equal("/setup/resume", beginning.Headers.Location!.ToString());
        var challenge = await host.Client.GetAsync("/test/challenge");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        host.Tokens.Nonce = query["nonce"].ToString();
        host.Tokens.Scenario = scenario;
        var callback = await host.Client.GetAsync("/setup/signin-oidc?code=test-code&state=" + Uri.EscapeDataString(query["state"].ToString()));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.True(expected == (await host.Store.ReadAsync(default)).Phase, host.Tokens.Failure ?? "Completion rejected after validation");
        if (expected == RegistryBootstrapPhase.Completed)
        {
            Assert.Equal("/setup/infrastructure", callback.Headers.Location!.ToString());
            Assert.Contains("Redis", await host.Client.GetStringAsync("/setup/infrastructure"));
            var infrastructurePage = await host.Client.GetStringAsync("/setup/infrastructure");
            var csrf = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(infrastructurePage,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
            var save = new Dictionary<string,string> { ["__RequestVerificationToken"] = csrf };
            foreach (var system in InfrastructureConnections.Systems)
            {
                var fields = new Dictionary<string,string> { ["__RequestVerificationToken"] = csrf,
                    ["system"] = system, ["host"] = "localhost", ["port"] = "1234", ["username"] = "root",
                    ["password"] = "do-not-echo-root-password", ["url"] = "http://localhost:15672/",
                    ["database"] = "postgres", ["authDatabase"] = "admin", ["directConnection"] = "true" };
                var test = await host.Client.PostAsync("/setup/infrastructure/test",new FormUrlEncodedContent(fields));
                Assert.Equal(HttpStatusCode.OK,test.StatusCode);
                var json = await test.Content.ReadAsStringAsync();
                Assert.DoesNotContain("do-not-echo-root-password",json);
                using var result = JsonDocument.Parse(json);
                Assert.True(result.RootElement.GetProperty("success").GetBoolean());
                foreach (var field in fields) if (field.Key != "__RequestVerificationToken") save[system+"."+field.Key]=field.Value;
                save[system+".receipt"]=result.RootElement.GetProperty("receipt").GetString()!;
            }
            save["redis.password"]="edited";
            Assert.Equal(HttpStatusCode.BadRequest,(await host.Client.PostAsync("/setup/infrastructure/save",new FormUrlEncodedContent(save))).StatusCode);
            Assert.False((await host.App.Services.GetRequiredService<IInfrastructureStateStore>().ReadAsync(default))!.Completed);
            save["redis.password"]="do-not-echo-root-password";
            Assert.Equal(HttpStatusCode.OK,(await host.Client.PostAsync("/setup/infrastructure/save",new FormUrlEncodedContent(save))).StatusCode);
            Assert.Contains("Your Forge is ready",await host.Client.GetStringAsync("/setup/complete"));
            Assert.Equal(HttpStatusCode.Conflict,(await host.Client.PostAsync("/setup/infrastructure/save",new FormUrlEncodedContent(save))).StatusCode);
            Assert.False(host.Sessions.IsActive(host.Session));
            var closed = await host.Begin(await host.Ticket());
            Assert.Equal("/setup/connection-error?reason=state", closed.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.Redirect, (await host.Client.GetAsync("/setup/administrator")).StatusCode);
        }
        else Assert.Equal("/setup/signin-error", callback.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Account_form_validation_does_not_echo_password_or_create_account()
    {
        await using var host = await Host.Start();
        await host.Begin(await host.Ticket());
        var page = await host.Client.GetStringAsync("/setup/administrator");
        var match = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        var response = await host.Client.PostAsync("/setup/administrator", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "administrator-create", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
            ["Input.Username"] = "new-admin", ["Input.Email"] = "admin@example.com", ["Input.FirstName"] = "New", ["Input.LastName"] = "Admin",
            ["Input.Password"] = "do-not-echo-this-password", ["Input.ConfirmPassword"] = "different"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Passwords must match", body);
        Assert.DoesNotContain("do-not-echo-this-password", body);
        Assert.Equal(RegistryBootstrapPhase.AwaitingPrincipal, (await host.Store.ReadAsync(default)).Phase);
    }

    [Fact]
    public async Task Existing_account_search_form_validates_separately_from_create_password_fields()
    {
        await using var host = await Host.Start();
        await host.Begin(await host.Ticket());
        var page = await host.Client.GetStringAsync("/setup/administrator");
        var match = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        var response = await host.Client.PostAsync("/setup/administrator", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "administrator-search", ["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value),
            ["Search.Username"] = ""
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Username field is required", body);
        Assert.DoesNotContain("Password field is required", body);
        Assert.Equal(RegistryBootstrapPhase.AwaitingPrincipal, (await host.Store.ReadAsync(default)).Phase);
    }

    [Fact]
    public async Task Existing_account_can_be_found_confirmed_and_assigned_through_real_razor_forms()
    {
        await using var host = await Host.Start(existingAccounts: true);
        await host.Begin(await host.Ticket());
        var page = await host.Client.GetStringAsync("/setup/administrator");
        static string Antiforgery(string html) => WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var search = await host.Client.PostAsync("/setup/administrator", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "administrator-search", ["__RequestVerificationToken"] = Antiforgery(page), ["Search.Username"] = "existing-admin"
        }));
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var review = await search.Content.ReadAsStringAsync();
        Assert.Contains("Existing Administrator", review);
        Assert.Contains("existing@example.com", review);
        Assert.Contains("Use this account as Forge administrator", review);
        Assert.Equal(0, host.Accounts!.Assignments);
        Assert.Equal(RegistryBootstrapPhase.AwaitingPrincipal, (await host.Store.ReadAsync(default)).Phase);
        // Username may change between review and confirmation; the selected subject must not.
        host.Accounts.Username = "renamed-admin";
        var confirm = await host.Client.PostAsync("/setup/administrator", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "administrator-select", ["__RequestVerificationToken"] = Antiforgery(review), ["Selection.SubjectId"] = "selected-subject"
        }));
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Contains("Your administrator is ready", await confirm.Content.ReadAsStringAsync());
        Assert.Equal(1, host.Accounts.Assignments);
        Assert.Equal("selected-subject", (await host.Store.ReadAsync(default)).SubjectId);
        Assert.Equal(RegistryBootstrapPhase.AuthorityAssigned, (await host.Store.ReadAsync(default)).Phase);
        Assert.Equal(1, host.Accounts.Searches);
    }

    private sealed class PassingValidator : IRootConnectionValidator
    { public Task<ConnectionCheck> TestAsync(string system, RootCredential value, CancellationToken ct) => Task.FromResult(ConnectionCheck.Verified); }

    private sealed record TicketData(string Ticket, string Antiforgery);
    private sealed class Host : IAsyncDisposable
    {
        public BootstrapConnectionConfiguration Configuration { get; private set; } = new()
        { Authority = "https://identity.example", Realm = "root", ClientId = "provisioner", PublicOrigin = "http://localhost:5180" };
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "setup-auth-" + Guid.NewGuid().ToString("N"));
        public FileRegistryBootstrapStore Store = null!;
        public WebApplication App = null!;
        public HttpClient Client = null!;
        public SetupSessions Sessions => App.Services.GetRequiredService<SetupSessions>();
        public TokenHandler Tokens = new();
        public ExistingAccounts? Accounts;
        public string? Session;
        public static async Task<Host> Start(bool useDefaultOrigin = false, bool existingAccounts = false)
        {
            var host = new Host();
            if (useDefaultOrigin) host.Configuration = new BootstrapConnectionConfiguration
                { Authority = "https://identity.example", Realm = "root", ClientId = "provisioner" };
            host.Store = new(host._directory);
            await host.Store.InitializeAsync(host.Configuration.Settings);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { EnvironmentName = "Development", ApplicationName = typeof(App).Assembly.FullName });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(host.Configuration);
            builder.Services.AddSingleton<IRegistryBootstrapStore>(host.Store);
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<IInfrastructureStateStore>(new FileInfrastructureStateStore(host._directory));
            builder.Services.AddSingleton<IRootCredentialStore>(new ManagedRootCredentialStore(Path.Combine(host._directory, "credentials"), Path.Combine(host._directory, "key")));
            builder.Services.AddSingleton<IRootConnectionValidator, PassingValidator>();
            builder.Services.AddSingleton<InfrastructureReceipts>();
            builder.Services.AddScoped<InfrastructureSetup>();
            if (existingAccounts)
            {
                host.Accounts = new ExistingAccounts(host.Configuration);
                builder.Services.AddSingleton<ISetupRegistryClients>(host.Accounts);
            }
            else builder.Services.AddSingleton<ISetupRegistryClients, SetupRegistryClients>();
            builder.Services.AddScoped<SetupBootstrap>();
            builder.Services.AddScoped<BootstrapConnection>();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            var signIn = new AdministratorSignInConfiguration(host.Configuration, true);
            builder.AddSetupAuthentication(host.Configuration, signIn);
            builder.Services.PostConfigure<OpenIdConnectOptions>("SetupOidc", options =>
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = host.Configuration.Issuer,
                    AuthorizationEndpoint = "https://identity.example/authorize",
                    TokenEndpoint = "https://identity.example/token"
                };
                configuration.SigningKeys.Add(host.Tokens.Key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.Backchannel = new HttpClient(host.Tokens);
                var failed = options.Events.OnRemoteFailure;
                options.Events.OnRemoteFailure = context => { host.Tokens.Failure = context.Failure?.ToString(); return failed(context); };
            });
            host.App = builder.Build();
            host.App.UseAuthentication(); host.App.UseAuthorization(); host.App.UseAntiforgery();
            host.App.MapSetupAuthentication(signIn);
            host.App.MapInfrastructure();
            host.App.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(ProvisioningComponentAssembly).Assembly).AddInteractiveServerRenderMode();
            // Test-only fixtures: never exposed by the production host.
            host.App.MapGet("/test/ticket", (HttpContext context, IAntiforgery antiforgery, SetupSessions sessions) =>
                new TicketData(sessions.CreateTicket("client-secret"), antiforgery.GetAndStoreTokens(context).RequestToken!));
            host.App.MapGet("/test/challenge", async (HttpContext context) =>
            {
                host.Session = context.User.FindFirstValue(SetupAuthentication.SessionClaim);
                var properties = new AuthenticationProperties { RedirectUri = "/setup/complete" };
                properties.Items["setup-session"] = host.Session;
                await context.ChallengeAsync("SetupOidc", properties);
            }).RequireAuthorization(SetupAuthentication.SignInPolicy);
            await host.App.StartAsync();
            var address = host.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            host.Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
            host.Client.DefaultRequestHeaders.Host = "localhost:5180";
            return host;
        }
        public async Task<TicketData> Ticket() => JsonSerializer.Deserialize<TicketData>(await Client.GetStringAsync("/test/ticket"), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        public Task<HttpResponseMessage> Begin(TicketData ticket) => Client.PostAsync("/setup/connection/start", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["ticket"] = ticket.Ticket, ["__RequestVerificationToken"] = ticket.Antiforgery }));
        public async ValueTask DisposeAsync()
        {
            Client.Dispose(); await App.StopAsync(); await App.DisposeAsync();
            Tokens.Dispose(); Directory.Delete(_directory, true);
        }
    }
    private sealed class ExistingAccounts(BootstrapConnectionConfiguration configuration) : ISetupRegistryClients
    {
        public int Assignments, Searches;
        public string Username = "existing-admin";
        public KeycloakAdministratorCreator Accounts(string secret) => new(configuration.Options(configuration.ClientId, secret), configuration.AdminRole, new Handler(this));
        public KeycloakRegistryBootstrapStaff Staff(string secret) => new(configuration.Settings, configuration.Options(configuration.ClientId, secret), new Handler(this));
        private sealed class Handler(ExistingAccounts owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/token")) return Task.FromResult(Json(new { access_token = "service-token", token_type = "Bearer", expires_in = 300 }));
                var account = new { id = "selected-subject", username = owner.Username, firstName = "Existing", lastName = "Administrator", email = "existing@example.com", enabled = true };
                if (request.Method == HttpMethod.Get)
                {
                    if (path.EndsWith("/users")) { owner.Searches++; return Task.FromResult(Json(new[] { account })); }
                    if (path.EndsWith("/users/selected-subject")) return Task.FromResult(Json(account));
                    if (path.EndsWith("/roles/forge-admin")) return Task.FromResult(Json(new { id = "role-id", name = "forge-admin", composite = false, clientRole = false }));
                }
                // The sole permitted administrative write is adding the role mapping.
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/users/selected-subject/role-mappings/realm", path);
                owner.Assignments++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public RsaSecurityKey Key { get; }
        public string? Failure;
        public string Nonce = "";
        public string Scenario = "valid";
        public TokenHandler() { Key = new RsaSecurityKey(_rsa) { KeyId = "test-key" }; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = await request.Content!.ReadAsStringAsync(ct);
            Assert.Contains("client_secret=client-secret", form);
            Assert.Contains("code_verifier=", form);
            Assert.DoesNotContain("grant_type=password", form);
            var subject = Scenario == "wrong-subject" ? "other-user" : "selected-subject";
            var now = DateTime.UtcNow;
            using var wrongRsa = RSA.Create(2048);
            var key = Scenario == "bad-signature" ? new RsaSecurityKey(wrongRsa) { KeyId = Key.KeyId } : Key;
            var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Scenario == "bad-issuer" ? "https://other.example" : "https://identity.example/realms/root",
                Audience = Scenario == "bad-audience" ? "other-client" : "provisioner",
                Subject = new ClaimsIdentity(new[] { new Claim("sub", subject), new Claim("preferred_username", "new-admin"), new Claim("nonce", Scenario == "bad-nonce" ? "wrong" : Nonce) }),
                IssuedAt = now, NotBefore = now, Expires = now.AddMinutes(5), SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
            });
            var payload = new { iss = "https://identity.example/realms/root", azp = "provisioner", sub = subject,
                exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), realm_access = new { roles = Scenario == "missing-role" ? Array.Empty<string>() : new[] { "forge-admin" } } };
            var access = "header." + Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload)) + ".signature";
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { id_token = token, access_token = access, token_type = "Bearer", expires_in = 300 }), Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { if (disposing) _rsa.Dispose(); base.Dispose(disposing); }
    }
}
