using System.Security.Cryptography;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Persistence;

// Host key lifecycle. A missing key never replaces an existing credential set.
public sealed class ManagedRootCredentialStore : IRootCredentialStore
{
    private readonly string _directory;
    private readonly FileStorage _keys;
    public ManagedRootCredentialStore(string directory, string keyDirectory)
    {
        _directory = Path.GetFullPath(directory);
        if (_directory == Path.GetFullPath(keyDirectory)) throw new ArgumentException("Keep the root key in a separate directory.");
        _ = new FileStorage(directory);
        _keys = new(keyDirectory);
    }
    private async Task<byte[]?> KeyAsync(bool create, CancellationToken ct)
    {
        await using var lease = await FileStorage.LockAsync(_keys.PathFor("root-key", ".lock"), ct);
        var path = _keys.PathFor("root-key", ".key");
        if (File.Exists(path))
        {
            var key = await File.ReadAllBytesAsync(path, ct);
            if (key.Length == 32) return key;
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidDataException("Invalid root credential key.");
        }
        if (Directory.EnumerateFiles(_directory, "*.credential").Any())
            throw new InvalidDataException("Root credential key is missing. Restore the original key.");
        if (!create) return null;
        var generated = RandomNumberGenerator.GetBytes(32);
        try { await FileStorage.WriteAsync(path, generated, ct); return generated; }
        catch { CryptographicOperations.ZeroMemory(generated); throw; }
    }
    public async Task SetAsync(string system, RootCredential credential, CancellationToken ct)
    {
        var key = (await KeyAsync(true, ct))!;
        try { using var store = new FileRootCredentialStore(_directory, key); await store.SetAsync(system, credential, ct); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public async Task<RootCredential?> TryReadAsync(string system, CancellationToken ct)
    {
        var key = await KeyAsync(false, ct);
        if (key is null) return null;
        try { using var store = new FileRootCredentialStore(_directory, key); return await store.TryReadAsync(system, ct); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
