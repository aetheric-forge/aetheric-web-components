using System.Security.Cryptography;
using System.Text.Json;
using Aetheric.Provisioning.Engine;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Aetheric.Provisioning.Components;

public sealed class InfrastructureReceipts(IDataProtectionProvider protection) : IDisposable
{
    private sealed record Receipt(string Session, string System, RootCredential Credential);
    private readonly IDataProtector _protector = protection.CreateProtector("Aetheric.Infrastructure.TestReceipt.v1");
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 256 });
    public string Issue(string session, string system, RootCredential credential)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _cache.Set(id, _protector.Protect(JsonSerializer.Serialize(new Receipt(session, system, credential))),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
        return id;
    }
    public bool Matches(string id, string session, string system, RootCredential credential)
    {
        if (!_cache.TryGetValue<string>(id, out var encrypted) || encrypted is null) return false;
        var receipt = JsonSerializer.Deserialize<Receipt>(_protector.Unprotect(encrypted));
        return receipt is not null && receipt.Session == session && receipt.System == system && receipt.Credential == credential;
    }
    public void Remove(string id) => _cache.Remove(id);
    public void Dispose() => _cache.Dispose();
}
