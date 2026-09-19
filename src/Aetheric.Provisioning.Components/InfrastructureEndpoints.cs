using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Engine;
using Microsoft.AspNetCore.Antiforgery;

namespace Aetheric.Provisioning.Components;

public static class InfrastructureEndpoints
{
    public static void MapInfrastructure(this IEndpointRouteBuilder app)
    {
        app.MapPost("/setup/infrastructure/test", async (HttpContext context, IAntiforgery antiforgery, InfrastructureSetup setup) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var system = form["system"].ToString();
                var credential = await ReadAsync(setup, form, system, "", context.RequestAborted);
                var (result, receipt) = await setup.TestAsync(system, credential, context.RequestAborted);
                return Results.Json(new { success = result.Succeeded, receipt, message = Message(result.Code) });
            }
            catch (Exception ex) { return Error(ex); }
        }).RequireAuthorization(SetupAuthentication.AdminPolicy);
        app.MapPost("/setup/infrastructure/save", async (HttpContext context, IAntiforgery antiforgery, InfrastructureSetup setup) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var values = new Dictionary<string, (RootCredential, string)>();
                foreach (var system in InfrastructureConnections.Systems)
                    values[system] = (await ReadAsync(setup, form, system, system + ".", context.RequestAborted), form[system + ".receipt"].ToString());
                await setup.SaveAsync(values, context.RequestAborted);
                return Results.Json(new { success = true, redirect = "/setup/complete" });
            }
            catch (Exception ex) { return Error(ex); }
        }).RequireAuthorization(SetupAuthentication.AdminPolicy);
    }
    private static async Task<RootCredential> ReadAsync(InfrastructureSetup setup, IFormCollection form, string system, string prefix, CancellationToken ct)
    {
        string Value(string name) => form[prefix + name].ToString();
        var host = Value("host").Trim();
        _ = int.TryParse(Value("port"), out var port);
        RabbitMqRootOptions? rabbit = null;
        if (system == "rabbitmq")
        {
            if (!Uri.TryCreate(Value("url").Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Enter a management API base URL without credentials, query, or fragment.");
            host = uri.Host; port = uri.Port;
            rabbit = new(uri.Scheme, uri.AbsolutePath);
        }
        var credential = new RootCredential(host, port, Value("username"), Value("password"))
        {
            Mongo = system == "mongo" ? new(Value("authDatabase"), Value("directConnection") == "true") : null,
            Postgres = system == "postgres" ? new(Value("database")) : null, RabbitMq = rabbit
        };
        return await setup.ResolveAsync(system, credential, Value("keepPassword") == "true", ct);
    }
    private static IResult Error(Exception ex) => ex switch
    {
        AntiforgeryValidationException => Results.Json(new { success = false, message = "Your page expired. Reload and sign in again." }, statusCode: 400),
        UnauthorizedAccessException => Results.Json(new { success = false, message = "Your session expired. Sign in again to resume saved progress." }, statusCode: 401),
        ArgumentException => Results.Json(new { success = false, message = ex.Message }, statusCode: 400),
        InfrastructureSaveException => Results.Json(new { success = false, message = ex.Message }, statusCode: 503),
        InvalidOperationException => Results.Json(new { success = false, message = "Bootstrap is already complete or its state changed. Reload this page." }, statusCode: 409),
        _ => Results.Json(new { success = false, message = "The operation could not finish. Check storage and connectivity, then retry; successful saves are retained." }, statusCode: 503)
    };
    private static string Message(string code) => code switch
    {
        "verified" => "Connection and administrator access verified.",
        "dns" => "The provisioner could not resolve the service hostname. Check DNS from inside its container.",
        "tls" => "HTTPS certificate verification or the TLS handshake failed. Check the container trust store, server certificate chain, and hostname.",
        "invalid" => "Check the connection fields.",
        "authentication" => "The server rejected these credentials.",
        "permission" => "The account connected but does not have the required administrator access.",
        "timeout" => "The connection test timed out.",
        "unsupported" => "This check requires Redis 7 or later with ACL administration available.",
        "management_api" => "The RabbitMQ management API did not respond as expected. Check its URL and that the management plugin is enabled.",
        "database" => "The connection or authentication database could not be used.",
        _ => "The service could not be reached or did not return a usable response. Check its address and availability."
    };
}
