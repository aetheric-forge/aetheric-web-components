using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Persistence;

/// <summary>Host-supplied AES-256 key; retain that key separately across restarts. No automatic rotation.</summary>
public sealed class EncryptedFileSecretStore : ISecretStore
{
    private readonly FileStorage _files;
    private readonly byte[] _key;
    public EncryptedFileSecretStore(string directory, ReadOnlySpan<byte> encryptionKey)
    {
        if (encryptionKey.Length != 32) throw new ArgumentException("An AES-256 key is required.", nameof(encryptionKey));
        _key = encryptionKey.ToArray();
        _files = new(directory);
    }

    public async Task<SecretReference> GetOrCreateAsync(string scope, string name, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { scope, name })));
        var reference = new SecretReference(id);
        await using var lease = await FileStorage.LockAsync(_files.PathFor(id, ".lock"), ct);
        try
        {
            _ = await ReadAsync(reference, ct); // Validate existing ciphertext/key; never replace unreadable credentials.
            return reference;
        }
        catch (FileNotFoundException) { }
        var value = Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        try
        {
            var bytes = new byte[1 + 12 + 16 + value.Length];
            bytes[0] = 1;
            RandomNumberGenerator.Fill(bytes.AsSpan(1, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(bytes.AsSpan(1, 12), value, bytes.AsSpan(29), bytes.AsSpan(13, 16), Encoding.UTF8.GetBytes(id));
            await FileStorage.WriteAsync(_files.PathFor(id, ".secret"), bytes, ct);
        }
        finally { CryptographicOperations.ZeroMemory(value); }
        return reference;
    }

    public async Task<string> ReadAsync(SecretReference reference, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(_files.PathFor(reference.Id, ".secret"), ct);
        if (bytes.Length < 29 || bytes[0] != 1) throw new InvalidDataException("Invalid or unsupported secret file.");
        var value = new byte[bytes.Length - 29];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(bytes.AsSpan(1, 12), bytes.AsSpan(29), bytes.AsSpan(13, 16), value, Encoding.UTF8.GetBytes(reference.Id));
            return Encoding.UTF8.GetString(value);
        }
        finally { CryptographicOperations.ZeroMemory(value); }
    }
}
