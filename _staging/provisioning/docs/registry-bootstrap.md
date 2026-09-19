# Initial Forge administrator setup

The browser flow is **connect Keycloak → select or create the Forge administrator → sign in as that administrator**. Possession of the verified initial client secret authorizes the setup session. There is no platform-admin browser login and no password grant. The new human account receives the configured dedicated realm role (`forge-admin` by default); the initial service account retains its separately assigned `realm-management / realm-admin` permission.

## Deployment state

Configure the Keycloak server base URL, realm, client ID, public HTTPS origin, administrator role, and private bootstrap state directory. Before first use, explicitly initialize the deployment record:

```sh
dotnet run --project samples/Aetheric.Provisioning.Web -- --initialize-bootstrap
```

Supply the same `BootstrapConnection__Authority`, `BootstrapConnection__Realm`, `BootstrapConnection__ClientId`, `BootstrapConnection__AdminRole`, and `BootstrapConnection__StateDirectory` settings when initializing and running. Local state defaults to `data/bootstrap`, relative to the working directory. See [Docker deployment](docker.md) for the persistent volume and container command. No client secret is needed for initialization. Initialization refuses to overwrite existing state; normal startup never initializes or repairs it. Keep the state directory private and back it up. Missing, invalid, or mismatched state blocks setup; losing it must not silently reopen administrator creation.

## Verified connection

`/` and `/setup` display the deployment-owned Keycloak destination and accept the matching client ID and secret. `KeycloakProvisionerConnection.CheckAsync` obtains a service-account token from the HTTPS token endpoint, checks issuer, authorized client and expiry, requires `realm-management / realm-admin`, and reads the exact enabled confidential client with service accounts enabled. It makes no administrative writes. Tokens come only from the configured HTTPS endpoint and are never accepted from browser input. Requests disable redirects and use a 30-second timeout.

A successful check creates a single-use ticket valid for five minutes. Continuing exchanges it for a protected setup cookie and a server-side session lasting at most 20 minutes. The secret is encrypted in bounded process memory, not in cookies, OIDC state, or the bootstrap file. Process restart or expiry requires a fresh connection check. All setup writes require authentication and antiforgery validation. The session proves possession of the checked client credential; it does not identify a human operator.

## Select an existing administrator

The account page offers an existing-account lookup by exact Keycloak username, alongside new-account creation. Lookup reads only the configured realm, shows the account's name, username, email and immutable user ID, and requires an explicit **Use this account as Forge administrator** confirmation. Lookup itself performs no administrative writes. Missing, ambiguous, disabled and service-account identities are rejected.

Confirmation re-reads the full account by the selected immutable ID, ensures the configured Forge role is compatible, and uses the existing locked bootstrap lifecycle to persist that subject before assigning authority. It never creates another user, resets a password, or changes the account's profile. Once selected, retries remain bound to that subject. The account's own verified OIDC sign-in is still required to complete setup. Existing-account selection is available only while awaiting a principal; it does not bypass an interrupted creation or completed bootstrap.

If new-account creation reports that the username/email already exists, use the existing-account lookup rather than resubmitting credentials. Exact username queries follow the [Keycloak users API](https://www.keycloak.org/docs-api/26.7.4/rest-api/index.html#_users_resource).

## Create and assign a new administrator

The account form accepts a new username, email, first name, last name, and password with confirmation. Keycloak enforces its own user-profile and password policies. The password is submitted once with the enabled user's creation request, is not rendered back into the form, and is never saved by the provisioner. Email starts unverified; realm-required actions remain Keycloak's responsibility. Existing usernames/emails are rejected by the creation form; selecting one for administrator access requires the separate lookup and confirmation above.

Before creating a user, the provisioner ensures that the configured realm role exists and is a non-composite realm role. It creates a missing dedicated role, but rejects built-in administrative/default roles and existing composite/client roles. Deployment owners remain responsible for the meaning of an existing role.

`RegistryBootstrap.CreateAdministratorAsync` locks the deployment record and saves `CreatingPrincipal` before issuing the user POST. A successful response must contain a user location within the configured realm. The returned immutable subject ID is persisted before role assignment. The runtime-backed `KeycloakRegistryBootstrapStaff` then rechecks the enabled principal and configured role and assigns that role through `IRegistryClerk`. Assignment failure can be retried only for the saved subject, without creating another user or changing its password.

An explicit Keycloak account rejection (400/401/403/409) permits correction and retry. An ambiguous response, timeout, interrupted write, or missing/mismatched user location leaves `CreatingPrincipal` locked for operator recovery. Never automatically retry user creation or recover by username alone: Keycloak may have committed the write. The operator must inspect Keycloak's users/audit events and the private record. If creation succeeded, recover the record to `PrincipalSelected` with that exact verified subject; if it definitely did not, restore the known pre-creation record. Preserve the deployment settings, take a backup, and perform recovery while the provisioner is stopped. No browser recovery override is provided.

## Verify the administrator's own sign-in

After either creation or existing-account assignment, the sign-in action enables standard authorization-code flow for the existing client, adds only the exact `/setup/signin-oidc` callback, and adds the dedicated administrator role to its allowed realm-role scope. Existing redirects, other scopes, secrets, origins, and unrelated client settings are preserved. The standard Keycloak `roles` client scope/realm-role mapper must be available to emit `realm_access.roles` in access tokens.

The browser uses authorization code with PKCE, state, correlation and nonce. The OIDC middleware validates the ID token's signature, issuer and audience. The accompanying access token from the trusted backchannel must match that issuer, client and subject, be unexpired, and contain the configured Forge role. Setup is marked complete only in `OnTicketReceived`, after protocol/nonce validation, for the exact persisted subject. A different account, even another realm administrator, cannot complete the handoff. Failure leaves progress available for retry.

Completion is durable and closes administrator creation, including after restart. All temporary setup credentials are cleared from process memory. The completion page uses a short-lived authenticated cookie. This increment does not implement general post-bootstrap administrator login or provision the rest of Forge; the separate simulation remains at `/simulation`.

## Validation and dependencies

Tests cover existing-account lookup and confirmation reads, service-account/disabled-account rejection, independent form validation, user creation/rejection, role compatibility, exact callback and role-scope updates, concurrent attempts, ambiguous failures, assignment retries, restart persistence, and closure. Local HTTP integration tests exercise the actual cookie, antiforgery, Razor form, and OIDC middleware with a controlled signed identity provider response, including invalid signature/issuer/audience/nonce, missing role and wrong subject. These tests do not claim a live run against deployed Keycloak.

Initialize the pinned runtime submodule before restore (`git submodule update --init --recursive`). Runtime lockfiles remain in `external/locks`; the unmodified runtime emits existing `IArchiveProvider` warnings.

References: [Keycloak Admin REST API](https://www.keycloak.org/docs-api/26.7.4/rest-api/index.html), [Keycloak service accounts and role scopes](https://www.keycloak.org/docs/latest/server_admin/#_service_accounts), and [ASP.NET Core token-validation event timing](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.authentication.openidconnect.openidconnectevents.ontokenvalidated?view=aspnetcore-10.0).
