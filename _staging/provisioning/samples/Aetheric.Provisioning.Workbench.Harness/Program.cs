using System.Text.Json;
using Aetheric.Provisioning.Workbench;
using Aetheric.Provisioning.Workbench.Redis;
using StackExchange.Redis;

// Explicit standalone workspace acceptance host: does not claim Decisions or Campus readiness.
var connection = Environment.GetEnvironmentVariable("WORKBENCH_REDIS_CONNECTION");
var stage = Environment.GetEnvironmentVariable("WORKBENCH_STAGE");
var environment = Environment.GetEnvironmentVariable("WORKBENCH_ENVIRONMENT");
if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(environment))
{
    Console.Error.WriteLine("Set WORKBENCH_REDIS_CONNECTION, WORKBENCH_STAGE, and WORKBENCH_ENVIRONMENT.");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    var options = ConfigurationOptions.Parse(connection);
    options.AbortOnConnectFail = true;
    options.ConnectTimeout = 5000;
    options.AsyncTimeout = 5000;
    using var redis = await ConnectionMultiplexer.ConnectAsync(options);
    var backend = new RedisWorkbenchBackend(redis.GetDatabase());
    var result = await backend.EnsureAsync(new WorkspaceRequest(environment, "standalone-workbench", "workspace", stage), cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { Scope = "standalone-workbench", Stage = stage,
        result.AlreadyExists, StagingVerified = true, DecisionsReady = false }));
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Workbench verification cancelled."); return 130; }
catch (Exception) { Console.Error.WriteLine("Workbench verification failed. Check target access, stage ownership, and Redis availability."); return 1; }
