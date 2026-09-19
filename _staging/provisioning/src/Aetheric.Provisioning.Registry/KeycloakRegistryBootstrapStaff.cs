using Aetheric.Provisioning.Application;
using AethericForge.Runtime.Abstractions.Interfaces.Identity.Authorization;
using AethericForge.Runtime.Abstractions.Interfaces.Identity.Directory;
using AethericForge.Runtime.Abstractions.Interfaces.Identity.Services;
using AethericForge.Runtime.Models.Identity.Directory;
using AethericForge.Runtime.Providers.Identity.Keycloak;

namespace Aetheric.Provisioning.Registry;

public sealed class RegistryBootstrapStaffException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

// Uses the actual runtime staff contracts. No user creation, password changes, or role creation.
public sealed class KeycloakRegistryBootstrapStaff : IRegistryBootstrapStaff, IDisposable
{
    private readonly HttpClient _http;
    private readonly IExternalIdentityDirectory _directory;
    private readonly IRegistryClerk _clerk;
    private readonly string _adminRole;

    public KeycloakRegistryBootstrapStaff(RegistryBootstrapSettings settings, KeycloakOptions options)
        : this(settings, options, null) { }

    internal KeycloakRegistryBootstrapStaff(RegistryBootstrapSettings settings, KeycloakOptions options,
        HttpMessageHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority)
            || authority.Scheme != "https" || authority.UserInfo.Length != 0
            || authority.Query.Length != 0 || authority.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(options.Realm) || options.Realm != options.Realm.Trim()
            || options.Realm is "." or ".." || options.Realm.IndexOfAny(['/', '\\']) >= 0
            || string.IsNullOrWhiteSpace(options.ClientSecret)
            || string.IsNullOrWhiteSpace(settings.ClientId) || settings.ClientId != settings.ClientId.Trim()
            || options.ClientId != settings.ClientId || string.IsNullOrWhiteSpace(settings.AdminRole)
            || settings.AdminRole != settings.AdminRole.Trim()
            || !string.IsNullOrWhiteSpace(options.AdminApiBaseAddress))
            throw new ArgumentException("Invalid Registry bootstrap connection configuration.");
        var issuer = new Uri(new Uri(authority.AbsoluteUri.TrimEnd('/') + "/"),
            "realms/" + Uri.EscapeDataString(options.Realm)).AbsoluteUri;
        if (!string.Equals(issuer, settings.Issuer, StringComparison.Ordinal))
            throw new ArgumentException("Keycloak issuer does not match the bootstrap deployment record.");
        _adminRole = settings.AdminRole;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(30) };
        _directory = new KeycloakExternalIdentityDirectory(_http, options);
        _clerk = new KeycloakRegistryClerk(_http, options);
    }

    public async Task<bool> PrincipalExistsAsync(string subjectId, CancellationToken ct)
    {
        ValidateSubject(subjectId);
        try
        {
            var result = await _directory.GetIdentityAsync(
                new ExternalIdentityReference(_directory.Provider, _directory.Realm, subjectId), ct);
            if (result.Status == ExternalDirectoryStatus.NotFound) return false;
            if (result.Status != ExternalDirectoryStatus.Success || result.Value is null)
                throw new RegistryBootstrapStaffException("registry.principal_lookup_failed");
            var identity = result.Value;
            if (identity.Reference.Provider != _directory.Provider || identity.Reference.Realm != _directory.Realm
                || identity.Reference.SubjectId != subjectId)
                throw new RegistryBootstrapStaffException("registry.principal_mismatch");
            return identity.IsEnabled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RegistryBootstrapStaffException) { throw; }
        catch (Exception) { throw new RegistryBootstrapStaffException("registry.principal_lookup_failed"); }
    }

    public async Task EnsureAdminAuthorityAsync(string subjectId, string role, CancellationToken ct)
    {
        ValidateSubject(subjectId);
        if (!string.Equals(role, _adminRole, StringComparison.Ordinal))
            throw new RegistryBootstrapStaffException("registry.role_not_configured");
        await VerifyAdminRoleAsync(ct);
        // Recheck immediately before the write, including on a resumed bootstrap attempt.
        if (!await PrincipalExistsAsync(subjectId, ct))
            throw new RegistryBootstrapStaffException("registry.principal_unavailable");
        try
        {
            var result = await _clerk.AssignRoleToPrincipalAsync(_adminRole, subjectId, ct);
            if (result.Status != RegistryOperationStatus.Succeeded)
                throw new RegistryBootstrapStaffException(result.Status switch
                {
                    RegistryOperationStatus.NotFound => "registry.role_or_principal_missing",
                    RegistryOperationStatus.Unauthorized => "registry.assignment_unauthorized",
                    _ => "registry.assignment_failed"
                });
            // A generic 409 is not evidence of a compatible assignment. Only success advances state.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RegistryBootstrapStaffException) { throw; }
        catch (Exception) { throw new RegistryBootstrapStaffException("registry.assignment_failed"); }
    }

    private async Task VerifyAdminRoleAsync(CancellationToken ct)
    {
        try
        {
            var result = await _clerk.GetRoleAsync(_adminRole, ct);
            if (result.Status != RegistryOperationStatus.Succeeded || result.Value is null)
                throw new RegistryBootstrapStaffException(result.Status switch
                {
                    RegistryOperationStatus.NotFound => "registry.role_missing",
                    RegistryOperationStatus.Unauthorized => "registry.role_lookup_unauthorized",
                    _ => "registry.role_lookup_failed"
                });
            if (!string.Equals(result.Value.Name, _adminRole, StringComparison.Ordinal))
                throw new RegistryBootstrapStaffException("registry.role_mismatch");
            // The runtime projects only the role name; empty Permissions does not prove
            // that the deployed role has no composites or elevated privileges.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (RegistryBootstrapStaffException) { throw; }
        catch (Exception) { throw new RegistryBootstrapStaffException("registry.role_lookup_failed"); }
    }

    private static void ValidateSubject(string subjectId)
    {
        if (string.IsNullOrWhiteSpace(subjectId) || subjectId != subjectId.Trim() || subjectId is "." or ".."
            || subjectId.IndexOfAny(['/', '\\', '?', '#']) >= 0)
            throw new ArgumentException("An exact Keycloak subject ID is required.");
    }

    public void Dispose()
    {
        ((IDisposable)_directory).Dispose();
        ((IDisposable)_clerk).Dispose();
        _http.Dispose();
    }
}
