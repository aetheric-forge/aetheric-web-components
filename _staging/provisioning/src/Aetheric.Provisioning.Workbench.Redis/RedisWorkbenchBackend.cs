using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Workbench;
using StackExchange.Redis;

namespace Aetheric.Provisioning.Workbench.Redis;

/// <summary>Workspace registration and staging verification on an existing standalone Redis database.</summary>
public sealed class RedisWorkbenchBackend(IDatabase database) : IWorkbenchBackend
{
    // Registration is atomic across processes/plans. A different owner cannot claim the same stage.
    private const string Claim = """
        local current = redis.call('GET', KEYS[1])
        if current then
            if current ~= ARGV[1] then return -1 end
            if redis.call('PTTL', KEYS[1]) ~= -1 then return -1 end
            return 0
        end
        redis.call('SET', KEYS[1], ARGV[1], 'NX')
        return 1
        """;
    // The probe expires even if the client/process stops before cleanup. Never touches draft keys.
    private const string Probe = """
        if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
        redis.call('HSET', KEYS[1], 'content', ARGV[1], 'metadata', ARGV[2])
        redis.call('PEXPIRE', KEYS[1], 60000)
        return 1
        """;
    public static string RegistrationKey(string stage) =>
        "aetheric:workbench:v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stage)));

    public async Task<WorkspaceResult> EnsureAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Institution);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Resource);
        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Stage, @"\A[a-z0-9][a-z0-9-]{0,62}\z"))
            throw new ArgumentException("Invalid Workbench stage.");
        cancellationToken.ThrowIfCancellationRequested();
        var owner = JsonSerializer.Serialize(new { Version = 1, request.Environment, request.Institution,
            request.Resource, request.Stage, Format = "runtime-staging-hash-v1" });
        // Refuse implicit adoption of a stage populated outside this provisioning contract.
        // Provisioning and runtime writers must not concurrently initialize an unregistered stage.
        if (!await database.KeyExistsAsync(RegistrationKey(request.Stage)))
        {
            long cursor = 0;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = (RedisResult[])(await database.ExecuteAsync("SCAN", cursor,
                    "MATCH", request.Stage + ":*", "COUNT", 100))!;
                cursor = (long)page[0];
                if (((RedisResult[])page[1])!.Length != 0)
                    throw new InvalidOperationException("An unregistered Workbench stage already contains data.");
            } while (cursor != 0);
        }
        var claim = (long)await database.ScriptEvaluateAsync(Claim,
            [RegistrationKey(request.Stage)], [owner]);
        if (claim < 0) throw new InvalidOperationException("Workbench stage ownership conflicts with the requested workspace.");
        cancellationToken.ThrowIfCancellationRequested();
        var key = (RedisKey)$"{request.Stage}:data:__provisioning_probe_{Guid.NewGuid():N}";
        var payload = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var metadata = "{\"ContentType\":\"application/octet-stream\"}";
        var created = (long)await database.ScriptEvaluateAsync(Probe, [key], [payload, metadata]);
        if (created != 1) throw new InvalidOperationException("Workbench probe collision.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await database.HashGetAsync(key, [(RedisValue)"content", (RedisValue)"metadata"]);
            if (values[0] != payload || values[1] != metadata)
                throw new InvalidOperationException("Workbench staging verification failed.");
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            // Await completion even on cancellation, so a pending write cannot outlive cleanup.
            await database.KeyDeleteAsync(key);
        }
        return new(claim == 0);
    }
}
