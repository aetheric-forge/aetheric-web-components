using System.Collections.Immutable;
using System.Security.Claims;
using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Registry;

namespace Aetheric.Provisioning.Components;

public sealed class SetupBootstrap(BootstrapConnectionConfiguration configuration, IRegistryBootstrapStore store,
    SetupSessions sessions, ISetupRegistryClients clients, IInfrastructureStateStore infrastructure)
{
    public async Task<RegistryBootstrapState> StateAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Settings != configuration.Settings) throw new InvalidOperationException("Bootstrap deployment settings changed.");
        return state;
    }
    public async Task RequireOpenAsync(CancellationToken ct)
    {
        var registry = await StateAsync(ct);
        if (registry.Phase != RegistryBootstrapPhase.Completed) return;
        var state = await infrastructure.ReadAsync(ct);
        if (state is not null && (state.Deployment != registry.Settings || state.SubjectId != registry.SubjectId))
            throw new InvalidDataException("Infrastructure deployment binding changed.");
        if (state?.Completed == true) throw new InvalidOperationException("Bootstrap is complete.");
    }
    public string RequireConnection(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true || !user.HasClaim(SetupAuthentication.ConnectionClaim, "true"))
            throw new UnauthorizedAccessException();
        return sessions.Secret(user.FindFirstValue(SetupAuthentication.SessionClaim)) ?? throw new UnauthorizedAccessException();
    }
    public async Task CreateAsync(ClaimsPrincipal user, NewRegistryAdministrator administrator, CancellationToken ct)
    {
        var secret = RequireConnection(user);
        if (string.IsNullOrWhiteSpace(administrator.Username) || administrator.Username.Length > 200
            || administrator.Username != administrator.Username.Trim()
            || administrator.Email.Length > 254 || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(administrator.Email)
            || string.IsNullOrWhiteSpace(administrator.FirstName) || administrator.FirstName.Length > 200
            || string.IsNullOrWhiteSpace(administrator.LastName) || administrator.LastName.Length > 200
            || string.IsNullOrEmpty(administrator.Password) || administrator.Password.Length > 4096)
            throw new ArgumentException("Enter valid administrator account details.");
        using var creator = clients.Accounts(secret);
        using var staff = clients.Staff(secret);
        await new RegistryBootstrap(configuration.Settings, store, new Access(this, user), staff)
            .CreateAdministratorAsync(administrator, creator, ct);
    }
    public async Task<ExistingKeycloakAdministrator?> FindExistingAsync(ClaimsPrincipal user, string username, CancellationToken ct)
    {
        var secret = RequireConnection(user);
        if ((await StateAsync(ct)).Phase != RegistryBootstrapPhase.AwaitingPrincipal)
            throw new InvalidOperationException("Administrator selection is already bound or closed.");
        using var accounts = clients.Accounts(secret);
        return await accounts.FindExistingAsync(username, ct);
    }
    public async Task SelectExistingAsync(ClaimsPrincipal user, string subjectId, CancellationToken ct)
    {
        var secret = RequireConnection(user);
        if ((await StateAsync(ct)).Phase != RegistryBootstrapPhase.AwaitingPrincipal)
            throw new InvalidOperationException("Administrator selection is already bound or closed.");
        using var accounts = clients.Accounts(secret);
        // Re-read by immutable ID; never re-resolve a mutable username during confirmation.
        var account = await accounts.GetExistingAsync(subjectId, ct);
        await accounts.PrepareAsync(ct);
        using var staff = clients.Staff(secret);
        await new RegistryBootstrap(configuration.Settings, store, new Access(this, user), staff)
            .AssignAdministratorAsync(account.SubjectId, ct);
    }
    public async Task ResumeAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var secret = RequireConnection(user);
        var state = await StateAsync(ct);
        if (state.SubjectId is null) throw new InvalidOperationException("No administrator selected.");
        using var creator = clients.Accounts(secret);
        await creator.PrepareAsync(ct);
        using var staff = clients.Staff(secret);
        await new RegistryBootstrap(configuration.Settings, store, new Access(this, user), staff)
            .AssignAdministratorAsync(state.SubjectId, ct);
    }
    public async Task CompleteAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        // Called only from OnTicketReceived, after OIDC nonce/protocol validation has finished.
        await new RegistryBootstrap(configuration.Settings, store, new Access(this, user), new NoAssignment())
            .CompleteAfterSignInAsync(ct);
    }
    private sealed class Access(SetupBootstrap bootstrap, ClaimsPrincipal user) : IRegistryBootstrapAccess
    {
        public Task RequireBootstrapOperatorAsync(CancellationToken ct)
        { bootstrap.RequireConnection(user); return Task.CompletedTask; }
        public Task<BootstrapSignIn> GetVerifiedSignInAsync(CancellationToken ct)
        {
            if (user.Identity?.IsAuthenticated != true || !user.HasClaim(SetupAuthentication.VerifiedClaim, "true"))
                throw new UnauthorizedAccessException();
            return Task.FromResult(new BootstrapSignIn(user.FindFirstValue(SetupAuthentication.IssuerClaim)!, user.FindFirstValue("sub")!,
                ImmutableHashSet.Create(bootstrap.AdminRole)));
        }
    }
    private string AdminRole => configuration.AdminRole;
    private sealed class NoAssignment : IRegistryBootstrapStaff
    {
        public Task<bool> PrincipalExistsAsync(string subjectId, CancellationToken ct) => throw new NotSupportedException();
        public Task EnsureAdminAuthorityAsync(string subjectId, string role, CancellationToken ct) => throw new NotSupportedException();
    }
}
