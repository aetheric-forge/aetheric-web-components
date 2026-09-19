using System.Collections.Immutable;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Application;

public static class InfrastructureConnections
{
    public static ImmutableArray<string> Systems { get; } = ["redis", "rabbitmq", "postgres", "mongo"];
    public static RootCredential Normalize(string system, RootCredential value)
    {
        if (!Systems.Contains(system) || string.IsNullOrWhiteSpace(value.Host) || value.Host.Length > 253
            || value.Host != value.Host.Trim() || Uri.CheckHostName(value.Host.Trim('[', ']')) == UriHostNameType.Unknown
            || value.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(value.Password) || value.Password.Length > 4096
            || value.Username?.Length > 200 || (system != "redis" && string.IsNullOrWhiteSpace(value.Username))
            || (system != "mongo" && value.Mongo is not null) || (system != "postgres" && value.Postgres is not null)
            || (system != "rabbitmq" && value.RabbitMq is not null))
            throw new ArgumentException("Check the host, port, username, password, and service options.");
        if (system == "mongo")
        {
            var options = value.Mongo ?? new();
            ValidateDatabase(options.AuthDatabase);
            return value with { Mongo = options };
        }
        if (system == "postgres")
        {
            var options = value.Postgres ?? new();
            ValidateDatabase(options.Database);
            return value with { Postgres = options };
        }
        if (system == "rabbitmq")
        {
            var options = value.RabbitMq ?? new();
            if (options.Scheme is not ("http" or "https") || options.BasePath.Length > 500
                || !options.BasePath.StartsWith('/') || options.BasePath.Contains('\\')
                || options.BasePath.IndexOfAny(['?', '#']) >= 0
                || options.BasePath.Split('/').Any(x => Uri.UnescapeDataString(x) is "." or ".."))
                throw new ArgumentException("Enter a management API base URL without credentials, query, or fragment.");
            return value with { RabbitMq = options with { BasePath = options.BasePath.TrimEnd('/') + "/" } };
        }
        return value with { Username = string.IsNullOrWhiteSpace(value.Username) ? null : value.Username };
    }
    private static void ValidateDatabase(string database)
    {
        if (string.IsNullOrWhiteSpace(database) || database.Length > 128 || database.Any(char.IsControl))
            throw new ArgumentException("Enter a valid connection or authentication database.");
    }
}

public sealed record ConnectionCheck(bool Succeeded, string Code)
{
    public static ConnectionCheck Verified { get; } = new(true, "verified");
}
public interface IRootConnectionValidator
{
    Task<ConnectionCheck> TestAsync(string system, RootCredential credential, CancellationToken ct);
}
public sealed record InfrastructureState(RegistryBootstrapSettings Deployment, string SubjectId,
    bool Completed, DateTimeOffset? CompletedAt);
public interface IInfrastructureStateStore
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken ct);
    Task<InfrastructureState?> ReadAsync(CancellationToken ct);
    Task SaveAsync(InfrastructureState state, CancellationToken ct);
}
