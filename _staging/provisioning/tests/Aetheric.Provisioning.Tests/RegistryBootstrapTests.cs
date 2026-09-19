using System.Collections.Immutable;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Persistence;
using Xunit;

namespace Aetheric.Provisioning.Tests;

public sealed class RegistryBootstrapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "registry-bootstrap-" + Guid.NewGuid().ToString("N"));
    private static readonly RegistryBootstrapSettings Settings = new("https://identity.example/realms/root", "provisioner", "provisioner-admin");
    private readonly Access _access = new();
    private readonly Staff _staff = new();
    private FileRegistryBootstrapStore Store => new(_directory);
    private RegistryBootstrap Service => new(Settings, Store, _access, _staff);

    [Fact]
    public async Task Existing_principal_can_complete_after_restart_and_bootstrap_stays_closed()
    {
        await Store.InitializeAsync(Settings);
        Assert.Equal(RegistryBootstrapPhase.AuthorityAssigned, (await Service.AssignAdministratorAsync("operator")).Phase);
        Assert.Equal(RegistryBootstrapPhase.Completed, (await Service.CompleteAfterSignInAsync()).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.AssignAdministratorAsync("operator"));
        Assert.Equal(RegistryBootstrapPhase.Completed, (await Service.CompleteAfterSignInAsync()).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.InitializeAsync(Settings));
        Assert.Equal(1, _staff.Assignments);
    }

    [Fact]
    public async Task Failed_assignment_pins_identity_and_retry_resumes_it()
    {
        await Store.InitializeAsync(Settings);
        _staff.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Service.AssignAdministratorAsync("operator"));
        Assert.Equal(RegistryBootstrapPhase.PrincipalSelected, (await Store.ReadAsync(default)).Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.AssignAdministratorAsync("another"));
        _staff.Fail = false;
        await Service.AssignAdministratorAsync("operator");
        Assert.Equal(RegistryBootstrapPhase.Completed, (await Service.CompleteAfterSignInAsync()).Phase);
    }

    [Theory]
    [InlineData("other", "https://identity.example/realms/root", true)]
    [InlineData("operator", "https://other.example/realms/root", true)]
    [InlineData("operator", "https://identity.example/realms/root", false)]
    public async Task Wrong_sso_identity_or_missing_authority_cannot_complete(string subject, string issuer, bool role)
    {
        await Store.InitializeAsync(Settings);
        await Service.AssignAdministratorAsync("operator");
        _access.SignIn = new(issuer, subject, role ? ImmutableHashSet.Create(Settings.AdminRole) : []);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CompleteAfterSignInAsync());
        Assert.Equal(RegistryBootstrapPhase.AuthorityAssigned, (await Store.ReadAsync(default)).Phase);
    }

    [Fact]
    public async Task Unauthenticated_operator_and_missing_principal_cannot_assign_authority()
    {
        await Store.InitializeAsync(Settings);
        _access.Allowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service.AssignAdministratorAsync("operator"));
        _access.Allowed = true; _staff.Exists = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.AssignAdministratorAsync("operator"));
        Assert.Equal(0, _staff.Assignments);
        Assert.Equal(RegistryBootstrapPhase.AwaitingPrincipal, (await Store.ReadAsync(default)).Phase);
    }

    [Fact]
    public async Task Missing_corrupt_or_changed_deployment_state_fails_closed()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => Service.AssignAdministratorAsync("operator"));
        await Store.InitializeAsync(Settings);
        var changed = new RegistryBootstrap(Settings with { ClientId = "other" }, Store, _access, _staff);
        await Assert.ThrowsAsync<InvalidOperationException>(() => changed.AssignAdministratorAsync("operator"));
        await File.WriteAllTextAsync(Directory.GetFiles(_directory, "*.json").Single(), "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => Service.AssignAdministratorAsync("operator"));
        Assert.Equal(0, _staff.Assignments);
    }

    [Fact]
    public async Task Concurrent_requests_cannot_select_two_administrators()
    {
        await Store.InitializeAsync(Settings);
        async Task<bool> Assign(string subject)
        {
            try { await Service.AssignAdministratorAsync(subject); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Assign("operator"), Assign("another"));
        Assert.Single(results, x => x);
        Assert.Equal(1, _staff.Assignments);
    }

    [Fact]
    public async Task Authority_granted_before_checkpoint_failure_is_reconciled_on_retry()
    {
        await Store.InitializeAsync(Settings);
        var service = new RegistryBootstrap(Settings, new FailedCheckpoint(Store), _access, _staff);
        await Assert.ThrowsAsync<IOException>(() => service.AssignAdministratorAsync("operator"));
        Assert.Equal(RegistryBootstrapPhase.PrincipalSelected, (await Store.ReadAsync(default)).Phase);
        Assert.Single(_staff.Authorities);
        await Service.AssignAdministratorAsync("operator");
        Assert.Equal(2, _staff.Assignments);
        Assert.Single(_staff.Authorities);
        await Service.CompleteAfterSignInAsync();
    }

    [Fact]
    public async Task Cancellation_before_selection_and_early_sso_leave_bootstrap_unmodified()
    {
        await Store.InitializeAsync(Settings);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.AssignAdministratorAsync("operator", cancelled.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CompleteAfterSignInAsync());
        Assert.Equal(RegistryBootstrapPhase.AwaitingPrincipal, (await Store.ReadAsync(default)).Phase);
        Assert.Equal(0, _staff.Assignments);
    }

    private sealed class FailedCheckpoint(IRegistryBootstrapStore inner) : IRegistryBootstrapStore
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken ct) => inner.AcquireAsync(ct);
        public Task<RegistryBootstrapState> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
        public Task SaveAsync(RegistryBootstrapState state, CancellationToken ct) =>
            state.Phase == RegistryBootstrapPhase.AuthorityAssigned
                ? throw new IOException("Checkpoint failure after external success.") : inner.SaveAsync(state, ct);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private sealed class Access : IRegistryBootstrapAccess
    {
        public bool Allowed { get; set; } = true;
        public BootstrapSignIn SignIn { get; set; } = new(Settings.Issuer, "operator", ImmutableHashSet.Create(Settings.AdminRole));
        public Task RequireBootstrapOperatorAsync(CancellationToken ct) => Allowed ? Task.CompletedTask : throw new UnauthorizedAccessException();
        public Task<BootstrapSignIn> GetVerifiedSignInAsync(CancellationToken ct) => Task.FromResult(SignIn);
    }
    private sealed class Staff : IRegistryBootstrapStaff
    {
        public bool Exists { get; set; } = true;
        public bool Fail { get; set; }
        public int Assignments { get; private set; }
        public HashSet<(string Subject, string Role)> Authorities { get; } = [];
        public Task<bool> PrincipalExistsAsync(string subjectId, CancellationToken ct) => Task.FromResult(Exists);
        public Task EnsureAdminAuthorityAsync(string subjectId, string role, CancellationToken ct)
        {
            if (Fail) throw new IOException("Transient registry failure.");
            Assignments++; Authorities.Add((subjectId, role)); return Task.CompletedTask;
        }
    }
}
