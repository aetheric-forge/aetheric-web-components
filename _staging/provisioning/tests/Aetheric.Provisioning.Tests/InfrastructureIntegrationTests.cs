using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Infrastructure;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class InfrastructureIntegrationTests
{
    [RootIntegrationTheory]
    [InlineData("redis",16379,"default")]
    [InlineData("rabbitmq",15673,"root")]
    [InlineData("postgres",15432,"postgres")]
    [InlineData("mongo",17017,"root")]
    public async Task Real_service_accepts_admin_and_rejects_wrong_password(string system,int port,string username)
    {
        var validator=new RootConnectionValidator();
        var credential=new RootCredential("127.0.0.1",port,username,"integration-secret")
        { Mongo=system=="mongo" ? new("admin",true):null };
        var result=await validator.TestAsync(system,credential,default);
        Assert.True(result.Succeeded,system+": "+result.Code);
        Assert.False((await validator.TestAsync(system,credential with {Password="wrong-password"},default)).Succeeded);
    }
}

public sealed class RootIntegrationTheoryAttribute : TheoryAttribute
{
    public RootIntegrationTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("ROOT_CREDENTIAL_INTEGRATION") != "1")
            Skip = "Start tests/infrastructure/compose.yaml and set ROOT_CREDENTIAL_INTEGRATION=1.";
    }
}
