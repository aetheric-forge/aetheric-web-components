# Infrastructure bootstrap

After the selected Forge administrator signs in, `/setup/infrastructure` collects all four root connections on one screen. Existing deployments with administrator setup completed can reconnect using the initial OIDC client secret, then sign in as the same selected administrator. Administrator creation remains closed.

- Redis: host, port (6379), optional ACL username, password. Requires Redis 7+ and ACL administration. Checks `ACL WHOAMI` and `ACL DRYRUN` for user creation/deletion without changing users.
- RabbitMQ: management API base URL (usually `http://host:15672/`), username and password. Checks `/api/whoami` for the `administrator` tag. Reverse-proxy base paths are supported.
- Postgres: host, port (5432), username, password and connection database (`postgres`). Checks the connected role's `rolsuper` flag.
- MongoDB: host, port (27017), username, password, authentication database (`admin`) and direct connection. Checks authentication and `root` on `admin` using `connectionStatus`.

Test each connection, then choose **Save credentials and finish bootstrap**. Changing inputs invalidates that connection's test. Tests expire after ten minutes and are bound to the current administrator session. The server enforces all four successful tests before saving. TLS configuration controls are deferred.

## Persistence and recovery

`IRootCredentialStore` stores encrypted credentials under the stable keys `redis`, `rabbitmq`, `postgres`, and `mongo`. Mongo, Postgres and RabbitMQ options are included in the encrypted record. Passwords are never rendered back into HTML. A saved password can be reused during a retry.

Configuration:

- `RootCredentials:Directory` (default `data/root-credentials`)
- `RootCredentials:KeyDirectory` (default `data/root-key`)

Compose supplies separate persistent `root-credentials` and `root-key` volumes. The 256-bit encryption key is generated on the first save, not during application startup. Back up both volumes and bootstrap state; a missing key is never replaced when credential files exist. The container initializes volume directories for its non-root user.

Infrastructure completion is separate from administrator completion and is written only after all four credentials have been saved and read back successfully. Partial saves remain available for retry. An expired 20-minute administrator session can resume through `/setup` with the initial client secret and the same Forge account until infrastructure bootstrap is complete. Unsaved fields must be entered again.

## Validation

Run the normal suite with `dotnet test --configuration Release`. For real service checks, start disposable fixtures:

```sh
docker compose -p aetheric-root-tests -f tests/infrastructure/compose.yaml up -d
# Wait until all four services accept connections.
ROOT_CREDENTIAL_INTEGRATION=1 dotnet test --configuration Release --filter FullyQualifiedName~InfrastructureIntegrationTests
docker compose -p aetheric-root-tests -f tests/infrastructure/compose.yaml down -v
```

Fixture ports are loopback-only and passwords are disposable test values. The integration tests check successful root authentication and rejection of incorrect passwords. HTTP tests cover the authenticated four-service save flow, rejection of edited settings, and completion closing further saves.

## Internal HTTPS trust

The build and runtime images install the public Aetheric Forge step-ca root from `docker/certs/aetheric-forge-root-ca.crt` into the operating-system trust store. RabbitMQ management URLs may use HTTPS with normal certificate-chain and hostname verification. This also enables trust for other HTTPS clients in the provisioner. No private CA key is copied into the image.

Source: infrastructure repository `platform/core/step-ca/certs/dev/root_ca.crt`.
SHA-256 fingerprint: `55:3C:11:BB:DA:E4:DC:EC:00:22:12:B2:6B:E4:03:16:9D:1E:CE:A8:D3:B2:D2:DF:D2:CF:9B:C9:C7:D9:74:7C`.

When the root changes, replace the public certificate and rebuild/recreate the provisioner container. Certificate trust is installed at build time so the application continues to run as its non-root user. Database TLS configuration controls remain deferred.

For connection-test diagnostics, run `docker compose logs --since=5m provisioning` after retrying a test. The `Aetheric.Provisioning.Infrastructure.RootConnectionValidator` category reports failure codes, exception type names, HTTP transport error categories, socket error codes, and the RabbitMQ `/api/whoami` HTTP status. It deliberately excludes raw exception messages, credentials, request headers, and response bodies. DNS and TLS failures also receive specific UI messages. A successful curl check should be compared from inside the provisioner container, against the authenticated `/api/whoami` endpoint, rather than only the management landing page.
