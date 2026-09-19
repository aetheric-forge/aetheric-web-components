using System.Security.Cryptography;
using System.Text;

namespace Aetheric.Provisioning.Persistence;

// Local filesystem only. The directory must be private and controlled by the host.
internal sealed class FileStorage
{
    private readonly string _directory;
    public FileStorage(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_directory);
        else Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string PathFor(string id, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + extension);
    }

    public static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return OpenPrivate(path, FileMode.OpenOrCreate, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
            { await Task.Delay(25, ct); }
        }
    }

    public static async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = OpenPrivate(temporary, FileMode.CreateNew, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static FileStream OpenPrivate(string path, FileMode mode, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
