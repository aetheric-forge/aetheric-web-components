using System.Collections.Immutable;

namespace Aetheric.Provisioning.Application;

public sealed record RegistryBootstrapSettings(string Issuer, string ClientId, string AdminRole);
public enum RegistryBootstrapPhase { AwaitingPrincipal, PrincipalSelected, AuthorityAssigned, Completed, CreatingPrincipal }
public sealed record RegistryBootstrapState(RegistryBootstrapSettings Settings, RegistryBootstrapPhase Phase, string? SubjectId);
public sealed record BootstrapSignIn(string Issuer, string SubjectId, ImmutableHashSet<string> Roles);

// Host-scoped authentication boundary. Implement from validated sessions, never form fields.
public interface IRegistryBootstrapAccess
{
    Task RequireBootstrapOperatorAsync(CancellationToken ct);
    Task<BootstrapSignIn> GetVerifiedSignInAsync(CancellationToken ct);
}

// Passwords are transient input only; never add this object to persisted bootstrap state.
public sealed class NewRegistryAdministrator
{
    public required string Username { get; init; }
    public required string Email { get; init; }
    public required string FirstName { get; init; }
    public required string LastName { get; init; }
    public required string Password { get; init; }
}

public interface IRegistryBootstrapAccountCreator
{
    Task PrepareAsync(CancellationToken ct);
    Task<string> CreateAsync(NewRegistryAdministrator administrator, CancellationToken ct);
}

// Only an explicit provider rejection proves that no account was created.
public sealed class RegistryAccountRejectedException(string code) : Exception(code);

// Adapter seam for the runtime directory and IRegistryClerk.
public interface IRegistryBootstrapStaff
{
    Task<bool> PrincipalExistsAsync(string subjectId, CancellationToken ct);
    // Must reconcile an existing compatible role/assignment and never rotate credentials.
    Task EnsureAdminAuthorityAsync(string subjectId, string role, CancellationToken ct);
}

public interface IRegistryBootstrapStore
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken ct);
    Task<RegistryBootstrapState> ReadAsync(CancellationToken ct);
    Task SaveAsync(RegistryBootstrapState state, CancellationToken ct);
}

public sealed class RegistryBootstrap(RegistryBootstrapSettings settings, IRegistryBootstrapStore store,
    IRegistryBootstrapAccess access, IRegistryBootstrapStaff staff)
{
    public async Task<RegistryBootstrapState> CreateAdministratorAsync(NewRegistryAdministrator administrator,
        IRegistryBootstrapAccountCreator creator, CancellationToken ct = default)
    {
        await access.RequireBootstrapOperatorAsync(ct);
        await using var lease = await store.AcquireAsync(ct);
        var state = await ReadAsync(ct);
        if (state.Phase != RegistryBootstrapPhase.AwaitingPrincipal)
            throw new InvalidOperationException("Account creation has already started. Resume the selected account or recover the interrupted operation.");
        await creator.PrepareAsync(ct);
        // Checkpoint before the external write. An ambiguous response must never create
        // a second administrator or adopt an existing account by mutable username.
        state = state with { Phase = RegistryBootstrapPhase.CreatingPrincipal };
        await store.SaveAsync(state, ct);
        string subject;
        try { subject = await creator.CreateAsync(administrator, ct); }
        catch (RegistryAccountRejectedException)
        {
            await store.SaveAsync(state with { Phase = RegistryBootstrapPhase.AwaitingPrincipal }, CancellationToken.None);
            throw;
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        state = state with { Phase = RegistryBootstrapPhase.PrincipalSelected, SubjectId = subject };
        await store.SaveAsync(state, CancellationToken.None);
        await staff.EnsureAdminAuthorityAsync(subject, settings.AdminRole, ct);
        state = state with { Phase = RegistryBootstrapPhase.AuthorityAssigned };
        await store.SaveAsync(state, CancellationToken.None);
        return state;
    }

    public async Task<RegistryBootstrapState> AssignAdministratorAsync(string subjectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        await access.RequireBootstrapOperatorAsync(ct);
        await using var lease = await store.AcquireAsync(ct);
        var state = await ReadAsync(ct);
        if (state.Phase is RegistryBootstrapPhase.Completed or RegistryBootstrapPhase.CreatingPrincipal)
            throw new InvalidOperationException("Bootstrap is complete or account creation needs recovery.");
        if (state.SubjectId is not null && state.SubjectId != subjectId)
            throw new InvalidOperationException("Bootstrap is already bound to another principal.");
        if (!await staff.PrincipalExistsAsync(subjectId, ct))
            throw new InvalidOperationException("The sysadmin must create this principal before bootstrap.");
        if (state.Phase == RegistryBootstrapPhase.AwaitingPrincipal)
        {
            state = state with { SubjectId = subjectId, Phase = RegistryBootstrapPhase.PrincipalSelected };
            // Commit the chosen identity before any external authority assignment.
            await store.SaveAsync(state, ct);
        }
        await staff.EnsureAdminAuthorityAsync(subjectId, settings.AdminRole, ct);
        state = state with { Phase = RegistryBootstrapPhase.AuthorityAssigned };
        await store.SaveAsync(state, CancellationToken.None);
        return state;
    }

    public async Task<RegistryBootstrapState> CompleteAfterSignInAsync(CancellationToken ct = default)
    {
        var signIn = await access.GetVerifiedSignInAsync(ct);
        await using var lease = await store.AcquireAsync(ct);
        var state = await ReadAsync(ct);
        if (state.Phase is not (RegistryBootstrapPhase.AuthorityAssigned or RegistryBootstrapPhase.Completed)
            || signIn.Issuer != settings.Issuer || signIn.SubjectId != state.SubjectId
            || !signIn.Roles.Contains(settings.AdminRole))
            throw new InvalidOperationException("Sign in as the selected provisioner administrator to complete bootstrap.");
        if (state.Phase == RegistryBootstrapPhase.Completed) return state;
        state = state with { Phase = RegistryBootstrapPhase.Completed };
        await store.SaveAsync(state, ct);
        return state;
    }

    private async Task<RegistryBootstrapState> ReadAsync(CancellationToken ct)
    {
        var state = await store.ReadAsync(ct);
        if (state.Settings != settings)
            throw new InvalidOperationException("Bootstrap configuration differs from its deployment record.");
        return state;
    }
}
