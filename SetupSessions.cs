using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheric.Provisioning.Components;

// One process, bounded lifetime. Only opaque handles travel through OIDC state/cookies.
// Restarting the host invalidates setup sessions and requires a new connection check.
public sealed class SetupSessions : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    private readonly IDataProtector _protector;
    private readonly object _gate = new();
    public SetupSessions(IDataProtectionProvider protection) =>
        _protector = protection.CreateProtector("Aetheric.Provisioning.SetupCredentials.v1");

    public string CreateTicket(string secret)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _cache.Set("ticket:" + ticket, _protector.Protect(secret), new MemoryCacheEntryOptions
            { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5), Size = 1 });
        return ticket;
    }

    public string? Begin(string ticket)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue<string>("ticket:" + ticket, out var encrypted) || encrypted is null) return null;
            _cache.Remove("ticket:" + ticket);
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _cache.Set("session:" + id, encrypted, new MemoryCacheEntryOptions
                { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(20), Size = 1 });
            return id;
        }
    }

    public string? Secret(string? id) => id is not null
        && _cache.TryGetValue<string>("session:" + id, out var value) && value is not null
            ? _protector.Unprotect(value) : null;
    public bool IsActive(string? id) => id is not null && _cache.TryGetValue("session:" + id, out _);
    public void End(string? id) { if (id is not null) _cache.Remove("session:" + id); }
    public void RevokeTicket(string? ticket) { if (ticket is not null) _cache.Remove("ticket:" + ticket); }
    public void Clear() => _cache.Compact(1);
    public void Dispose() => _cache.Dispose();
}
