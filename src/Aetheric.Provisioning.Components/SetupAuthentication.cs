using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Registry;

namespace Aetheric.Provisioning.Components;

// The setup session proves possession of the checked client secret, not a human login.
public static class SetupAuthentication
{
    public const string Policy = "SetupConnection";
    public const string ResumePolicy = "ResumeInfrastructure";
    public const string SignInPolicy = "SetupSignIn";
    public const string ResumeClaim = "aetheric:resume-infrastructure";
    public const string AdminSessionClaim = "aetheric:administrator-session";
    public const string AdminExpiryClaim = "aetheric:administrator-expiry";
    public const string AdminPolicy = "ForgeAdministrator";
    public const string ConnectionClaim = "aetheric:setup-connection";
    public const string IssuerClaim = "aetheric:issuer";
    public const string VerifiedClaim = "aetheric:verified-administrator";
    public const string SessionClaim = "aetheric:setup-session";
    private const string SessionProperty = "setup-session";
    private const string Scheme = "SetupOidc";

    public static void AddSetupAuthentication(this WebApplicationBuilder builder,
        BootstrapConnectionConfiguration connection, AdministratorSignInConfiguration signIn)
    {
        builder.Services.AddSingleton(signIn);
        builder.Services.AddSingleton<SetupSessions>();
        builder.Services.AddCascadingAuthenticationState();
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(Policy, policy => policy.RequireAuthenticatedUser().RequireClaim(ConnectionClaim, "true"));
            options.AddPolicy(ResumePolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(ResumeClaim, "true"));
            options.AddPolicy(SignInPolicy, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.HasClaim(ConnectionClaim, "true") || context.User.HasClaim(ResumeClaim, "true")));
            options.AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireClaim(VerifiedClaim, "true"));
        });
        var authentication = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "Aetheric.Setup";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = signIn.IsLocalHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.LoginPath = "/setup";
                options.AccessDeniedPath = "/setup/signin-error";
                options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
                options.SlidingExpiration = false;
                options.Events.OnValidatePrincipal = async context =>
                {
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<SetupSessions>();
                    var bootstrap = context.HttpContext.RequestServices.GetRequiredService<SetupBootstrap>();
                    try
                    {
                        var state = await bootstrap.StateAsync(context.HttpContext.RequestAborted);
                        var valid = context.Principal!.HasClaim(VerifiedClaim, "true")
                            ? state.Phase == RegistryBootstrapPhase.Completed && context.Principal.FindFirstValue("sub") == state.SubjectId
                                && context.Principal.FindFirstValue(IssuerClaim) == state.Settings.Issuer
                                && !string.IsNullOrEmpty(context.Principal.FindFirstValue(AdminSessionClaim))
                                && long.TryParse(context.Principal.FindFirstValue(AdminExpiryClaim), out var expiry)
                                && expiry > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            : (state.Phase != RegistryBootstrapPhase.Completed || context.Principal.HasClaim(ResumeClaim, "true"))
                                && sessions.IsActive(context.Principal.FindFirstValue(SessionClaim));
                        if (!valid) context.RejectPrincipal();
                    }
                    catch (Exception) { context.RejectPrincipal(); }
                };
            });
        if (!signIn.Enabled) return;
        authentication.AddOpenIdConnect(Scheme, options =>
        {
            options.Authority = connection.Issuer;
            options.ClientId = connection.ClientId;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.ResponseMode = OpenIdConnectResponseMode.Query;
            options.UsePkce = true;
            options.MapInboundClaims = false;
            options.SaveTokens = false;
            options.GetClaimsFromUserInfoEndpoint = false;
            options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
            options.CallbackPath = "/setup/signin-oidc";
            options.Scope.Clear(); options.Scope.Add("openid"); options.Scope.Add("profile"); options.Scope.Add("roles");
            options.TokenValidationParameters.NameClaimType = "preferred_username";
            options.TokenValidationParameters.ValidateIssuer = true;
            options.TokenValidationParameters.ValidIssuer = connection.Issuer;
            options.TokenValidationParameters.ValidateAudience = true;
            options.TokenValidationParameters.ValidAudience = connection.ClientId;
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            options.NonceCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.NonceCookie.SecurePolicy = options.CorrelationCookie.SecurePolicy =
                signIn.IsLocalHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.BackchannelHttpHandler = new HttpClientHandler { AllowAutoRedirect = false };
            options.BackchannelTimeout = TimeSpan.FromSeconds(30);
            options.Events.OnRedirectToIdentityProvider = context =>
            {
                context.ProtocolMessage.RedirectUri = signIn.Callback.AbsoluteUri;
                context.ProtocolMessage.Prompt = "login";
                return Task.CompletedTask;
            };
            options.Events.OnAuthorizationCodeReceived = context =>
            {
                var sessions = context.HttpContext.RequestServices.GetRequiredService<SetupSessions>();
                var secret = sessions.Secret(SessionId(context.Properties));
                if (secret is null || !signIn.Matches(context.Request)) context.Fail("Setup session expired or origin mismatch.");
                else
                {
                    context.TokenEndpointRequest!.ClientSecret = secret;
                    context.TokenEndpointRequest.RedirectUri = signIn.Callback.AbsoluteUri;
                }
                return Task.CompletedTask;
            };
            options.Events.OnTokenValidated = context =>
            {
                var sessions = context.HttpContext.RequestServices.GetRequiredService<SetupSessions>();
                var id = SessionId(context.Properties);
                var subject = context.Principal?.FindFirstValue("sub");
                if (!sessions.IsActive(id) || !AdministratorToken.HasAdministratorRole(context.TokenEndpointResponse?.AccessToken,
                    connection.Issuer, connection.ClientId, subject, connection.AdminRole))
                    context.Fail("Sign in as the selected Forge administrator with the configured role.");
                else
                {
                    var identity = (ClaimsIdentity)context.Principal!.Identity!;
                    identity.AddClaim(new Claim(VerifiedClaim, "true"));
                    identity.AddClaim(new Claim(IssuerClaim, connection.Issuer));
                }
                return Task.CompletedTask;
            };
            options.Events.OnTicketReceived = async context =>
            {
                var sessions = context.HttpContext.RequestServices.GetRequiredService<SetupSessions>();
                var id = SessionId(context.Properties);
                try
                {
                    if (!sessions.IsActive(id)) throw new UnauthorizedAccessException();
                    await context.HttpContext.RequestServices.GetRequiredService<SetupBootstrap>()
                        .CompleteAsync(context.Principal!, context.HttpContext.RequestAborted);
                    var identity = (ClaimsIdentity)context.Principal!.Identity!;
                    var expires = DateTimeOffset.UtcNow.AddMinutes(20);
                    identity.AddClaim(new Claim(AdminSessionClaim, Guid.NewGuid().ToString("N")));
                    identity.AddClaim(new Claim(AdminExpiryClaim, expires.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    context.Properties!.ExpiresUtc = expires;
                    sessions.Clear();
                    context.Properties.Items.Remove(SessionProperty);
                    var infrastructure = await context.HttpContext.RequestServices.GetRequiredService<IInfrastructureStateStore>()
                        .ReadAsync(context.HttpContext.RequestAborted);
                    if (infrastructure is not null && (infrastructure.Deployment != connection.Settings
                        || infrastructure.SubjectId != context.Principal.FindFirstValue("sub"))) throw new InvalidDataException();
                    context.ReturnUri = infrastructure?.Completed == true ? "/setup/complete" : "/setup/infrastructure";
                }
                catch (Exception)
                {
                    context.HandleResponse();
                    context.Response.Redirect("/setup/signin-error");
                }
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.Redirect("/setup/signin-error");
                return Task.CompletedTask;
            };
        });
    }
    private static string? SessionId(AuthenticationProperties? properties) =>
        properties is not null && properties.Items.TryGetValue(SessionProperty, out var id) ? id : null;

    public static void MapSetupAuthentication(this IEndpointRouteBuilder app, AdministratorSignInConfiguration configuration)
    {
        app.MapPost("/setup/connection/start", async (HttpContext context, IAntiforgery antiforgery,
            SetupSessions sessions, SetupBootstrap bootstrap) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (!configuration.Enabled)
            { context.Response.Redirect("/setup/connection-error?reason=configuration"); return; }
            if (!configuration.Matches(context.Request))
            { context.Response.Redirect("/setup/connection-error?reason=origin"); return; }
            string? id = null;
            try
            {
                try { await bootstrap.RequireOpenAsync(context.RequestAborted); }
                catch (Exception)
                { context.Response.Redirect("/setup/connection-error?reason=state"); return; }
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                id = sessions.Begin(form["ticket"].ToString());
                if (id is null)
                { context.Response.Redirect("/setup/connection-error?reason=ticket"); return; }
                sessions.End(context.User.FindFirstValue(SessionClaim));
                var resume = (await bootstrap.StateAsync(context.RequestAborted)).Phase == RegistryBootstrapPhase.Completed;
                var identity = new ClaimsIdentity(new[] { new Claim(resume ? ResumeClaim : ConnectionClaim, "true"), new Claim(SessionClaim, id), new Claim(ClaimTypes.NameIdentifier, id) },
                    CookieAuthenticationDefaults.AuthenticationScheme);
                await context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
                context.Response.Redirect(resume ? "/setup/resume" : "/setup/administrator");
            }
            catch (Exception)
            { sessions.End(id); context.Response.Redirect("/setup/connection-error?reason=session"); }
        });
        app.MapPost("/setup/administrator/signin", async (HttpContext context, IAntiforgery antiforgery,
            SetupBootstrap bootstrap, BootstrapConnectionConfiguration connection, SetupSessions sessions) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (!configuration.Enabled || !configuration.Matches(context.Request))
            { context.Response.Redirect("/setup/signin-error"); return; }
            try
            {
                await bootstrap.RequireOpenAsync(context.RequestAborted);
                var phase = (await bootstrap.StateAsync(context.RequestAborted)).Phase;
                var resume = context.User.HasClaim(ResumeClaim, "true");
                if (phase != (resume ? RegistryBootstrapPhase.Completed : RegistryBootstrapPhase.AuthorityAssigned))
                    throw new InvalidOperationException();
                var secret = sessions.Secret(context.User.FindFirstValue(SessionClaim)) ?? throw new UnauthorizedAccessException();
                if (!resume)
                {
                    using var client = new KeycloakProvisionerConnection(connection.Options(connection.ClientId, secret));
                    await client.PrepareAdministratorSignInAsync(configuration.Callback, connection.AdminRole, context.RequestAborted);
                }
                var properties = new AuthenticationProperties { RedirectUri = "/setup/complete" };
                properties.Items[SessionProperty] = context.User.FindFirstValue(SessionClaim);
                await context.ChallengeAsync(Scheme, properties);
            }
            catch (Exception) { context.Response.Redirect("/setup/signin-error"); }
        }).RequireAuthorization(SignInPolicy);
        app.MapPost("/setup/connection/end", async (HttpContext context, IAntiforgery antiforgery, SetupSessions sessions) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            sessions.End(context.User.FindFirstValue(SessionClaim));
            await context.SignOutAsync();
            context.Response.Redirect("/setup");
        }).RequireAuthorization();
    }
}
