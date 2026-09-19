using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Persistence;
using System.Collections.Immutable;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class RegistryAdministratorCreationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private static readonly RegistryBootstrapSettings Settings = new("https://identity.example/realms/root", "provisioner", "forge-admin");
    private readonly Access _access = new();
    private readonly Staff _staff = new();
    private readonly Creator _creator = new();
    private FileRegistryBootstrapStore Store => new(_path);
    private RegistryBootstrap Service => new(Settings, Store, _access, _staff);
    private static NewRegistryAdministrator Account => new() { Username = "new-admin", Email = "admin@example.com", FirstName = "New", LastName = "Admin", Password = "not-persisted-secret" };

    [Fact]
    public async Task New_account_is_pinned_assigned_and_requires_its_own_sign_in()
    {
        await Store.InitializeAsync(Settings);
        Assert.Equal(RegistryBootstrapPhase.AuthorityAssigned, (await Service.CreateAdministratorAsync(Account, _creator)).Phase);
        Assert.Equal("new-subject", (await Store.ReadAsync(default)).SubjectId);
        foreach (var file in Directory.GetFiles(_path, "*.json"))
            Assert.DoesNotContain(Account.Password, await File.ReadAllTextAsync(file));
        _access.Subject = "another-user";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CompleteAfterSignInAsync());
        _access.Subject = "new-subject";
        Assert.Equal(RegistryBootstrapPhase.Completed, (await Service.CompleteAfterSignInAsync()).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateAdministratorAsync(Account, _creator));
        Assert.Equal(1, _creator.Calls);
    }
    [Fact]
    public async Task Assignment_failure_resumes_same_subject_without_creating_or_resetting_password()
    {
        await Store.InitializeAsync(Settings);
        _staff.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Service.CreateAdministratorAsync(Account, _creator));
        Assert.Equal(RegistryBootstrapPhase.PrincipalSelected, (await Store.ReadAsync(default)).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateAdministratorAsync(Account, _creator));
        _staff.Fail = false;
        await Service.AssignAdministratorAsync("new-subject");
        Assert.Equal(1, _creator.Calls);
    }
    [Fact]
    public async Task Explicit_provider_rejection_allows_corrected_form()
    {
        await Store.InitializeAsync(Settings);
        _creator.Error = new RegistryAccountRejectedException("registry.account_exists");
        await Assert.ThrowsAsync<RegistryAccountRejectedException>(() => Service.CreateAdministratorAsync(Account, _creator));
        Assert.Equal(RegistryBootstrapPhase.AwaitingPrincipal, (await Store.ReadAsync(default)).Phase);
        _creator.Error = null;
        await Service.CreateAdministratorAsync(Account, _creator);
    }
    [Fact]
    public async Task Rejected_creation_can_switch_to_existing_account_and_requires_its_own_login()
    {
        await Store.InitializeAsync(Settings);
        _creator.Error = new RegistryAccountRejectedException("registry.account_exists");
        await Assert.ThrowsAsync<RegistryAccountRejectedException>(() => Service.CreateAdministratorAsync(Account, _creator));
        await Service.AssignAdministratorAsync("existing-subject");
        Assert.Equal("existing-subject", (await Store.ReadAsync(default)).SubjectId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.AssignAdministratorAsync("other-subject"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CompleteAfterSignInAsync());
        _access.Subject = "existing-subject";
        Assert.Equal(RegistryBootstrapPhase.Completed, (await Service.CompleteAfterSignInAsync()).Phase);
        Assert.Equal(1, _creator.Calls);
    }
    [Fact]
    public async Task Lost_response_blocks_second_creation_and_existing_user_adoption()
    {
        await Store.InitializeAsync(Settings);
        _creator.Error = new IOException("lost response");
        await Assert.ThrowsAsync<IOException>(() => Service.CreateAdministratorAsync(Account, _creator));
        Assert.Equal(RegistryBootstrapPhase.CreatingPrincipal, (await Store.ReadAsync(default)).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateAdministratorAsync(Account, _creator));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.AssignAdministratorAsync("someone-else"));
        Assert.Equal(1, _creator.Calls);
    }
    [Fact]
    public async Task Only_one_concurrent_creation_can_win()
    {
        await Store.InitializeAsync(Settings);
        async Task<bool> Create()
        {
            try { await Service.CreateAdministratorAsync(Account, _creator); return true; }
            catch (InvalidOperationException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Create(), Create()), x => x);
        Assert.Equal(1, _creator.Calls);
    }
    [Fact]
    public async Task Missing_connection_or_missing_state_cannot_create()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Service.CreateAdministratorAsync(Account, _creator));
        await Store.InitializeAsync(Settings);
        _access.Allowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.CreateAdministratorAsync(Account, _creator));
        Assert.Equal(0, _creator.Calls);
    }
    public void Dispose() { if (Directory.Exists(_path)) Directory.Delete(_path, true); }
    private sealed class Access : IRegistryBootstrapAccess
    {
        public bool Allowed = true;
        public string Subject = "new-subject";
        public Task RequireBootstrapOperatorAsync(CancellationToken ct) => Allowed ? Task.CompletedTask : throw new UnauthorizedAccessException();
        public Task<BootstrapSignIn> GetVerifiedSignInAsync(CancellationToken ct) => Task.FromResult(new BootstrapSignIn(Settings.Issuer, Subject, ImmutableHashSet.Create(Settings.AdminRole)));
    }
    private sealed class Staff : IRegistryBootstrapStaff
    {
        public bool Fail;
        public Task<bool> PrincipalExistsAsync(string subjectId, CancellationToken ct) => Task.FromResult(true);
        public Task EnsureAdminAuthorityAsync(string subjectId, string role, CancellationToken ct) => Fail ? throw new IOException() : Task.CompletedTask;
    }
    private sealed class Creator : IRegistryBootstrapAccountCreator
    {
        public int Calls;
        public Exception? Error;
        public Task PrepareAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateAsync(NewRegistryAdministrator administrator, CancellationToken ct)
        { Calls++; return Error is null ? Task.FromResult("new-subject") : throw Error; }
    }
}
