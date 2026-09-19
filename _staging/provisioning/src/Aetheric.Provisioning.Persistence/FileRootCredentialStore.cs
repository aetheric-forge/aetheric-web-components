using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheric.Provisioning.Engine;

namespace Aetheric.Provisioning.Persistence;

/// <summary>
/// Host-supplied AES-256 key, dedicated to this store and kept separate from
/// <see cref="EncryptedFileSecretStore"/>'s own key - root infrastructure credentials are more
/// sensitive than the engine's own generated child-resource secrets, so a compromise of one key
/// should not expose the other. Retain the key separately across restarts; no automatic rotation.
/// </summary>
public sealed class FileRootCredentialStore : IRootCredentialStore, IDisposable
{
    private readonly FileStorage _files;
    private readonly byte[] _key;
    private sealed record Envelope(int Version, RootCredential Credential);

    public FileRootCredentialStore(string directory, ReadOnlySpan<byte> encryptionKey)
    {
        if (encryptionKey.Length != 32) throw new ArgumentException("An AES-256 key is required.", nameof(encryptionKey));
        _key = encryptionKey.ToArray();
        _files = new(directory);
    }

    public async Task SetAsync(string system, RootCredential credential, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        Validate(credential);

        var path = _files.PathFor(system, ".credential");
        await using var lease = await FileStorage.LockAsync(_files.PathFor(system, ".lock"), ct);

        var value = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, credential));
        try
        {
            var bytes = new byte[1 + 12 + 16 + value.Length];
            bytes[0] = 1;
            RandomNumberGenerator.Fill(bytes.AsSpan(1, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(bytes.AsSpan(1, 12), value, bytes.AsSpan(29), bytes.AsSpan(13, 16), Encoding.UTF8.GetBytes(system));
            await FileStorage.WriteAsync(path, bytes, ct);
        }
        finally { CryptographicOperations.ZeroMemory(value); }
    }

    public async Task<RootCredential?> TryReadAsync(string system, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(system);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(_files.PathFor(system, ".credential"), ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (bytes.Length < 29 || bytes[0] != 1) throw new InvalidDataException("Invalid or unsupported root credential file.");
        var value = new byte[bytes.Length - 29];
        Envelope? envelope;
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(bytes.AsSpan(1, 12), bytes.AsSpan(29), bytes.AsSpan(13, 16), value, Encoding.UTF8.GetBytes(system));
            envelope = JsonSerializer.Deserialize<Envelope>(value);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Invalid root credential payload.");
        }
        finally { CryptographicOperations.ZeroMemory(value); }

        if (envelope is null || envelope.Version != 1) throw new InvalidDataException("Invalid root credential version.");
        Validate(envelope.Credential);
        return envelope.Credential;
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    private static void Validate(RootCredential credential)
    {
        if (credential is null
            || string.IsNullOrWhiteSpace(credential.Host)
            || credential.Port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(credential.Password))
            throw new InvalidDataException("Invalid root credential: Host and Password are required, Port must be 1-65535.");
    }
}
