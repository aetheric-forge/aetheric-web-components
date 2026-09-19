using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Persistence;
using Aetheric.Provisioning.Components;
using Microsoft.AspNetCore.DataProtection;
using Xunit;
using Microsoft.Extensions.Logging;
using Aetheric.Provisioning.Infrastructure;

namespace Aetheric.Provisioning.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public async Task Diagnostics_do_not_log_credentials_or_input_values()
    {
        var logger = new RecordingLogger();
        var validator = new RootConnectionValidator(logger);
        var result = await validator.TestAsync("rabbitmq", new("invalid-secret-host/",443,"secret-user","secret-password"),default);
        Assert.False(result.Succeeded);
        var output = string.Join("\n",logger.Messages);
        Assert.Contains("ArgumentException",output);
        Assert.Contains("invalid",output);
        Assert.DoesNotContain("secret-host",output);
        Assert.DoesNotContain("secret-user",output);
        Assert.DoesNotContain("secret-password",output);
    }
    private sealed class RecordingLogger : ILogger<RootConnectionValidator>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState,Exception?,string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state,exception));
        }
    }

    [Fact]
    public void Receipts_bind_every_option_and_session_and_can_be_consumed()
    {
        using var receipts = new InfrastructureReceipts(new EphemeralDataProtectionProvider());
        var credential = InfrastructureConnections.Normalize("mongo", new("localhost",27017,"admin","secret"));
        var receipt = receipts.Issue("session", "mongo", credential);
        Assert.True(receipts.Matches(receipt,"session","mongo",credential));
        Assert.False(receipts.Matches(receipt,"other","mongo",credential));
        Assert.False(receipts.Matches(receipt,"session","redis",credential));
        Assert.False(receipts.Matches(receipt,"session","mongo",credential with { Password="changed" }));
        Assert.False(receipts.Matches(receipt,"session","mongo",credential with { Mongo=new("admin",true) }));
        receipts.Remove(receipt);
        Assert.False(receipts.Matches(receipt,"session","mongo",credential));
        Assert.DoesNotContain("secret",credential.ToString());
    }

    [Fact]
    public async Task Managed_store_preserves_options_and_refuses_to_replace_a_lost_key()
    {
        var directory=Path.Combine(Path.GetTempPath(), "infrastructure-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var credentials=Path.Combine(directory,"credentials"); var keys=Path.Combine(directory,"keys");
            var store=new ManagedRootCredentialStore(credentials,keys);
            Assert.Null(await store.TryReadAsync("mongo",default));
            Assert.Empty(Directory.GetFiles(keys,"*.key"));
            var credential=new RootCredential("localhost",27017,"admin","never-plaintext") { Mongo=new("custom",true) };
            await store.SetAsync("mongo",credential,default);
            Assert.Equal(credential,await new ManagedRootCredentialStore(credentials,keys).TryReadAsync("mongo",default));
            Assert.DoesNotContain("never-plaintext",System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Directory.GetFiles(credentials,"*.credential").Single())));
            File.Delete(Directory.GetFiles(keys,"*.key").Single());
            await Assert.ThrowsAsync<InvalidDataException>(()=>store.SetAsync("redis",credential,default));
            Assert.Empty(Directory.GetFiles(keys,"*.key"));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Theory]
    [InlineData("redis",6379)] [InlineData("rabbitmq",15672)] [InlineData("postgres",5432)] [InlineData("mongo",27017)]
    public void Connection_validation_rejects_urls_as_hosts_and_invalid_ports(string system,int port)
    {
        Assert.Throws<ArgumentException>(()=>InfrastructureConnections.Normalize(system,new("http://localhost",port,"admin","secret")));
        Assert.Throws<ArgumentException>(()=>InfrastructureConnections.Normalize(system,new("localhost",0,"admin","secret")));
        Assert.Throws<ArgumentException>(()=>InfrastructureConnections.Normalize(system,new("localhost",port,"admin","")));
    }
}
