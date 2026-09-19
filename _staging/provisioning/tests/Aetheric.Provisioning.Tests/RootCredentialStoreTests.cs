using System.Security.Cryptography;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Persistence;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class RootCredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "provisioning-tests-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private FileRootCredentialStore Store() => new(_root, _key);

    private static RootCredential Mongo() => new("dev-mongo.internal", 27017, "root", "correct horse battery staple");

    [Fact]
    public async Task SetAsync_ThenTryReadAsync_RoundTrips()
    {
        var store = Store();
        var credential = Mongo();

        await store.SetAsync("mongo", credential, default);
        var loaded = await store.TryReadAsync("mongo", default);

        Assert.Equal(credential, loaded);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsNullForAnUnconfiguredSystem()
    {
        var loaded = await Store().TryReadAsync("postgres", default);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task SetAsync_OverwritesAPreviousValue()
    {
        var store = Store();
        await store.SetAsync("redis", new RootCredential("redis-a", 6379, null, "first-password"), default);

        await store.SetAsync("redis", new RootCredential("redis-b", 6380, "admin", "second-password"), default);
        var loaded = await store.TryReadAsync("redis", default);

        Assert.Equal(new RootCredential("redis-b", 6380, "admin", "second-password"), loaded);
    }

    [Fact]
    public async Task TryReadAsync_ThrowsOnTheWrongKey()
    {
        var store = Store();
        await store.SetAsync("rabbitmq", new RootCredential("mq.internal", 5672, "admin", "hunter2"), default);
        var wrongKeyStore = new FileRootCredentialStore(_root, RandomNumberGenerator.GetBytes(32));

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => wrongKeyStore.TryReadAsync("rabbitmq", default));
    }

    [Fact]
    public async Task TryReadAsync_ThrowsOnCorruptedContent()
    {
        var store = Store();
        await store.SetAsync("postgres", new RootCredential("pg.internal", 5432, "postgres", "s3cret"), default);
        var files = Directory.GetFiles(_root, "*.credential");
        var file = Assert.Single(files);
        await File.WriteAllBytesAsync(file, [0]);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.TryReadAsync("postgres", default));
    }

    [Theory]
    [InlineData("", 5432, "postgres", "secret")]
    [InlineData("pg.internal", 0, "postgres", "secret")]
    [InlineData("pg.internal", 70000, "postgres", "secret")]
    [InlineData("pg.internal", 5432, "postgres", "")]
    public async Task SetAsync_RejectsAnInvalidCredential(string host, int port, string? username, string password)
    {
        await Assert.ThrowsAsync<InvalidDataException>(
            () => Store().SetAsync("postgres", new RootCredential(host, port, username, password), default));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
