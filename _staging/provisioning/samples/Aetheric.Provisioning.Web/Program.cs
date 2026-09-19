using Microsoft.AspNetCore.HttpOverrides;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Simulation;
using Aetheric.Provisioning.Web.Components;
using Aetheric.Provisioning.Components;

var initializeBootstrap = args.Contains("--initialize-bootstrap", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(x => x != "--initialize-bootstrap").ToArray());
// Trust only the framework's default loopback proxies. Vulcan's nginx connects locally.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
var connectionConfiguration = builder.Configuration.GetSection("BootstrapConnection")
    .Get<BootstrapConnectionConfiguration>() ?? new();
if (initializeBootstrap)
{
    using var validation = new Aetheric.Provisioning.Registry.KeycloakAdministratorCreator(
        connectionConfiguration.Options(connectionConfiguration.ClientId, "configuration-validation"), connectionConfiguration.AdminRole);
    await new Aetheric.Provisioning.Persistence.FileRegistryBootstrapStore(connectionConfiguration.StateDirectory)
        .InitializeAsync(connectionConfiguration.Settings);
    Console.WriteLine("Initialized bootstrap deployment state. Existing state is never replaced.");
    return;
}
// The library only registers UI/orchestration - this sample supplies the file-based storage and
// validation backends itself, exactly the seam AddProvisioningBootstrap exists to keep open for a
// different host to fill differently.
builder.Services.AddSingleton<IRegistryBootstrapStore>(_ =>
    new Aetheric.Provisioning.Persistence.FileRegistryBootstrapStore(connectionConfiguration.StateDirectory));
builder.Services.AddSingleton<IInfrastructureStateStore>(_ =>
    new Aetheric.Provisioning.Persistence.FileInfrastructureStateStore(connectionConfiguration.StateDirectory));
builder.Services.AddSingleton<IRootCredentialStore>(_ => new Aetheric.Provisioning.Persistence.ManagedRootCredentialStore(
    builder.Configuration["RootCredentials:Directory"] ?? "data/root-credentials",
    builder.Configuration["RootCredentials:KeyDirectory"] ?? "data/root-key"));
builder.Services.AddSingleton<IRootConnectionValidator, Aetheric.Provisioning.Infrastructure.RootConnectionValidator>();
builder.Services.AddProvisioningBootstrap(connectionConfiguration);
var administratorSignIn = new AdministratorSignInConfiguration(connectionConfiguration, builder.Environment.IsDevelopment());
builder.AddSetupAuthentication(connectionConfiguration, administratorSignIn);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddProvisioningSimulation();
var app = builder.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapSetupAuthentication(administratorSignIn);
app.MapInfrastructure();
app.MapRazorComponents<App>().AddAdditionalAssemblies(typeof(ProvisioningComponentAssembly).Assembly).AddInteractiveServerRenderMode();
app.Run();
