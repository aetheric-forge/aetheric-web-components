# Docker on Vulcan

The Dockerfile builds the web host and its pinned runtime submodule, then runs the published app as the .NET image's unprivileged `app` user. Initialize submodules before building; the Docker build does not clone repositories. Local build outputs, Git metadata, environment files, and private-key/certificate files are excluded from the build context.

The container pins SDK `10.0.401` and overrides `global.json` only inside the build stage. The local SDK pin remains `10.0.112`, whose image tag is unavailable in MCR. The runtime image follows Microsoft’s serviced `aspnet:10.0` tag.

## Start behind the existing nginx proxy

From the provisioning repository on Vulcan (Linux, Docker with Compose v2):

```sh
git submodule update --init --recursive
cp .env.example .env
# Edit .env with the Keycloak destination, client ID, public HTTPS origin, and Forge role.
docker compose build
# First deployment only: create the private durable bootstrap record.
docker compose run --rm provisioning --initialize-bootstrap
docker compose up -d
docker compose logs -f provisioning
```

The client’s service account must have the `realm-management` client role `realm-admin`, and its role scope mappings must allow that role into the issued access token. `KEYCLOAK_ADMIN_ROLE` is no longer used by connection setup. No separate human-administrator realm role is needed for the connection check. Account creation ensures the dedicated `FORGE_ADMIN_ROLE` (default `forge-admin`) exists as a non-composite realm role.

Enter the Keycloak client secret in the setup page; it is not an image argument or Compose setting. Missing Keycloak settings leave the app on its deployment-configuration screen.

Compose uses Linux host networking and binds only `127.0.0.1:5180`. This matches Vulcan's host-local nginx upstream pattern. It does not publish the app's HTTP listener to the LAN. The existing proxy must terminate HTTPS and pass WebSocket upgrades for Interactive Server Blazor. Add a location to the intended HTTPS virtual host, using its existing certificate configuration:

```nginx
location / {
    proxy_pass http://127.0.0.1:5180;
    proxy_http_version 1.1;
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_read_timeout 3600s;
}
```

Use a dedicated hostname with the app at `/`, rather than a subpath. Open that hostname over HTTPS. The app consumes forwarded scheme/address headers only from default loopback trusted proxies; it does not trust arbitrary remote proxies. This recipe assumes nginx can reach host loopback (host process or host-network container). A bridge-network proxy needs a separately configured trusted proxy address and network arrangement.

The `protection-keys` volume preserves ASP.NET Core data-protection keys. The separate `bootstrap-state` volume stores the deployment binding, creation/assignment progress, selected user ID, and completion checkpoint. Restrict and back up both volumes. Neither stores the client secret or account password. Upgrades use `docker compose up -d --build` without reinitialization. Never delete the state volume to reopen setup.

Setup proceeds from the verified client connection to a new Forge administrator account, then to that account’s own Keycloak sign-in. No platform-admin UI login is required. Set `PROVISIONER_PUBLIC_ORIGIN` to the exact public HTTPS origin, with no path, query, or fragment. Sign-in adds the exact callback and the Forge role’s client scope without replacing other settings. The standard Keycloak realm-role mapper must emit the administrator role in the access token. The simulation stays at `/simulation`. See [bootstrap behavior and recovery](registry-bootstrap.md).

## Build or run without Compose

```sh
docker build -t aetheric-provisioning:local .
docker run -d --name aetheric-provisioning \
  --restart unless-stopped --network host \
  --security-opt no-new-privileges --cap-drop ALL \
  -e ASPNETCORE_URLS=http://127.0.0.1:5180 \
  -e ASPNETCORE_HTTPS_PORT=443 \
  -e BootstrapConnection__Authority=https://sso.example.com \
  -e BootstrapConnection__Realm=root \
  -e BootstrapConnection__ClientId=provisioner \
  -e BootstrapConnection__PublicOrigin=https://provisioner.example.com \
  -e BootstrapConnection__AdminRole=forge-admin \
  -e BootstrapConnection__StateDirectory=/home/app/.local/share/aetheric/bootstrap \
  -v provisioning-bootstrap-state:/home/app/.local/share/aetheric/bootstrap \
  -v provisioning-protection-keys:/home/app/.aspnet/DataProtection-Keys \
  aetheric-provisioning:local
```

Before starting the standalone container for the first time, run the same image with the same environment and volumes using `docker run --rm` (without `-d`, `--name`, or `--restart`) and append `--initialize-bootstrap` after the image name. Initialization is an explicit one-time operation.

If Keycloak uses a private CA, the container must trust that CA before connection checks will succeed. Do not disable TLS verification. No CA certificate or live deployment secret is bundled into this image.

For background on ASP.NET Core container HTTPS configuration, see [Microsoft's container hosting guidance](https://learn.microsoft.com/en-us/aspnet/core/security/docker-compose-https?view=aspnetcore-10.0).
