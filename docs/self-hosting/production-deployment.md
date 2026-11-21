# Production deployment

Run ROMD on one Linux server with Docker Compose: PostgreSQL 18, one worker,
admin and consumer portals, and a separate browser player. The release bundle
uses pinned images and needs no application checkout or SDK. You provide local
storage, HTTPS ingress and backups.

The initial release qualification target is **Linux amd64**. Install a qualified
full release when available; prerelease candidates are still being evaluated.
Ottercade native builds remain experimental. Before the first qualified release,
use [source development](../development/getting-started.md) to evaluate ROMD.

For a new server, start with the [quick start](#quick-start). The sections
after it explain each step and the alternatives. Existing operators can jump to
[upgrade](#upgrade), [recovery](#administrator-recovery) or
[backup and restore](#backup-and-restore).

## Quick start

This path serves ROMD on your local network. You need a Linux amd64 server with
Docker and Compose v2.25 or newer. Nothing else is installed on the host, and no
domain or account with a third party is needed.

Settle two things before you start, because sign-in depends on both:

- **One name or address that every device can reach the server by.** A `.local`
  name, a name your router's DNS serves, or a fixed IP address all work. Check
  that it resolves from another device, for example with `ping nas.local`.
  Every device must use exactly this name: ROMD refuses any other.
- **Each device must trust ROMD's certificate once.** ROMD serves HTTPS with a
  certificate it issues itself on first start, so until a device trusts ROMD's
  certificate authority its browser shows a warning and sign-in fails. Step 4
  installs it. A phone or console needs this as much as a laptop does.

1. Download the bundle and its checksum from the
   [release page](https://github.com/MoonlarkStudios/romd/releases), check it, and
   extract it. Replace `vX.Y.Z` with the release you are installing:

   ```bash
   sha256sum --check romd-vX.Y.Z-deployment.tar.gz.sha256
   tar -xzf romd-vX.Y.Z-deployment.tar.gz
   cd romd-vX.Y.Z
   ```

   To also confirm the bundle was built by this project's release workflow, see
   [release artifacts](#release-artifacts).
2. Create `.env` with the name or IP address your devices use to reach this
   server and the first administrator:

   ```bash
   cat > .env <<'EOF'
   ROMD_HOST=nas.local
   ROMD_DEFAULT_ADMIN_EMAIL=you@example.com
   ROMD_DEFAULT_ADMIN_PASSWORD=choose-a-strong-password
   EOF
   chmod 600 .env
   ```

   The password needs at least 12 characters with upper-case, lower-case and a
   digit. `.env.example` lists every optional setting.
3. Start ROMD:

   ```bash
   docker compose up -d --wait --wait-timeout 240
   ```

   On first start ROMD generates its database passwords, signing secrets and an
   HTTPS certificate for `ROMD_HOST`.
4. Trust ROMD's certificate authority on each device that will use it, before
   opening ROMD there. Copy it out of the installation, then install it as a
   trusted root certificate:

   ```bash
   docker compose cp ingress:/etc/romd/tls/ca.crt romd-ca.crt
   ```

   The authority can only vouch for the names in `ROMD_HOST`, so trusting it
   cannot be used to impersonate other sites. See
   [the built-in ingress](#built-in-ingress) for the alternatives: your own
   certificate, three host names, an existing reverse proxy or Cloudflare Tunnel.
5. Open the admin portal at `https://nas.local:7664`, sign in, and follow
   [your first library](../features/first-library.md). The consumer portal is at
   `https://nas.local:7663`; the player uses port 7665.

After you sign in, change your password in the admin portal and delete
`ROMD_DEFAULT_ADMIN_PASSWORD` from `.env`: it only creates the first account,
only the worker receives it, and editing it later has no effect.

To import an existing ROM library from this server without uploading through
the browser, add the [library overlay](configuration.md#path-import) and choose
**Import from a server folder** on the import page.

## Prerequisites and configuration

Install Docker with Compose v2.25 or newer, Bash, Python 3, OpenSSL and `shasum`.
Store PostgreSQL and ROMD data on local ext4/btrfs storage, not SMB/NFS mounts.
The host needs outbound access for catalog subscriptions and metadata providers.
Select HTTPS admin, consumer and player origins. Consumer and player must be
separate origins under the same site; sibling subdomains work well.

Download the bundle and its checksum to the same directory. Verify with
`sha256sum --check romd-<version>-deployment.tar.gz.sha256` (or
`shasum -a 256 -c`), then extract it; it unpacks into its own `romd-<version>/`
directory. Create `.env` there as in the [quick start](#quick-start), or copy
`.env.example` to `.env` and fill it in. On every start, the `romd-init` service runs first: it
[generates the database passwords and signing secrets](configuration.md#generated-secrets-and-certificates)
once and never replaces them. The database passwords live in Docker volumes
that only their own service mounts; the signing secrets live in the data
volume, so backups carry them. If an overlay replaces the `romd-data` volume
with a host directory, replace it on `romd-init` (at `/init/data`) as well;
validation rejects a mismatch. The bootstrap admin password must satisfy the
application's password policy; the worker refuses to create the first account
without one and ignores it once any account exists. Restrict `.env` permissions (`chmod 600`).
`COMPOSE_FILE` and `COMPOSE_PROJECT_NAME` in `.env` select the overlays and
project for every operational script. Keep plaintext secrets out of Git; an
encrypted secret store is appropriate.

Set each `ROMD_*_PUBLIC_URL` to an exact HTTPS origin, without a trailing slash.
The default deployment uses one canonical origin per portal. Additional origins
require an operator overlay that configures matching auth redirects, CORS and
consumer-to-player mappings; do not casually disable issuer or proxy validation.

The APIs accept the scheme, host and client address from their proxy for one
forwarded hop; [network boundaries](#network-boundaries) explains which peer that
can be. The proxy must reject unknown hosts and replace incoming forwarded
headers with authoritative values. Never publish a plaintext listener that lets
arbitrary clients claim HTTPS.

Optional IGDB credentials can be supplied to admin and worker; leave both empty
to manage them through **Integrations**. For path imports, mount the source at the
same container path on admin and worker and configure `Romd__AllowedImportPaths__*`
on both. Removing a `romd-secrets-*` volume does not rotate a database
password: the initialized PostgreSQL cluster keeps the old one and the hosts can
no longer connect. Remove those volumes only together with the database volume.

ROM uploads accept files up to `Romd__MaxUploadBytes` (default 64 GiB). While an
upload is in flight, the data volume holds the request buffer under
`/var/lib/romd/temp/requests`, the job workspace copy, and CAS staging, so allow
about three times the file size in free space. Path import avoids the request
buffer and any proxy body limit.

The admin host exclusively leases its configured `ASPNETCORE_TEMP` directory
before accepting requests and removes abandoned `ASPNETCORE_*.tmp` buffers left
by interrupted uploads. Ownership lasts through HTTP shutdown; a second admin
using the same directory fails startup without deleting live buffers. Give each
admin process a dedicated directory on a filesystem supporting exclusive file
locks. Do not delete `.romd-request-buffers.lock`, even while the host is stopped.
Unknown files and symbolic links are left alone. On the first upgrade, stop the
old admin before starting the new version: older binaries do not acquire this
lease. Without `ASPNETCORE_TEMP`, OS temporary-directory behavior is unchanged;
the admin does not sweep the shared OS temp directory.

## Release artifacts

Download a versioned deployment bundle and its `.sha256` file from
[Releases](https://github.com/MoonlarkStudios/romd/releases). The bundle contains
Linux amd64 images pinned by digest; CHD processing is enabled in that target.
ARM64 requires a source build and additional native qualification (see
[CHD media binaries](#chd-media-binaries)).

A release stays a prerelease while it is being qualified; install the latest
full release. An image receives its version tag only after its release
qualifies, and that tag is never moved. There is no `latest` tag; ignore
`sha-<commit>` staging tags. The bundle's `images.env` pins each image by
digest.

The bundle contains:

- `compose.yaml`: production topology, health checks and bounded Docker logs, with each
  image defaulting to its exact digest so plain `docker compose` works from this directory;
- `images.env`: the same immutable references for all four ROMD images and PostgreSQL;
- `manifest.json`: the release tag, source revision, and inventories of pinned images and bundle files;
- `.env.example`: required installation settings;
- `deploy/postgres/init/`: role/schema initialization for a fresh database;
- `deploy/examples/`: optional HTTPS proxy and Cloudflare Tunnel overlays;
- `scripts/backup/` and `scripts/deployment/`: compatible operational tools;
- this runbook, configuration and media-processing guides.

The release also attaches `mame0289.tar.gz`, the exact source archive of the
worker image's `chdman` binary (see [CHD media binaries](#chd-media-binaries)),
and `romd-vX.Y.Z-attestations.jsonl`, the signed provenance and SBOM
attestations. Published releases are immutable: their tag and files cannot be
changed. Check the download, and optionally its origin with the
[GitHub CLI](https://cli.github.com/) (no login needed):

```bash
sha256sum --check romd-vX.Y.Z-deployment.tar.gz.sha256
gh attestation verify romd-vX.Y.Z-deployment.tar.gz \
  --bundle romd-vX.Y.Z-attestations.jsonl --repo MoonlarkStudios/romd
```

The bundle's `images.env` pins every image by digest, so a verified bundle also
selects verified images. To check the images themselves, see
[verify a release](../maintainers/release-metadata.md#verify-a-release).

Until the first release is published, maintainers can build from source using
`compose.build.yaml`; do not assume GHCR packages already exist. Package
visibility must allow intended operators to pull the images (make packages
public for public releases, or document registry authentication for private ones).

Keep a release's files together. Keep your deployment configuration in a separate
infrastructure repository: selected release, origins, mounts, ingress, monitoring,
backup schedules and encrypted secrets. Do not run production from a mutable
application working tree or mix worker/API images from different releases,
except within the bounded [CHD decoder upgrade](#chd-decoder-upgrades) window.

## Choose ingress

No fixed container names or addresses prevent multiple isolated installations.
Keep `COMPOSE_PROJECT_NAME` stable across release directories to reuse the
intended database/data volumes.

### Network boundaries

Compose creates four project-scoped networks and Docker assigns every address;
there is nothing to configure.

| Network | Members | Carries |
| --- | --- | --- |
| `backend` | PostgreSQL, worker, admin, consumer | Database connections and outbound provider requests |
| `ingress-admin` | ingress, admin | The admin portal and API |
| `ingress-consumer` | ingress, consumer | The consumer portal and API |
| `ingress-player` | ingress, player | The player |

Each API listens for HTTP only on its address on its own ingress network, and
on loopback for its health check. It finds that address at startup from a DNS
alias Compose declares for it on that network alone, and refuses to start if
the alias does not resolve to exactly one of its own addresses. The result:

- The admin, consumer and player applications cannot open a connection to one
  another's listeners. Admin and consumer share `backend`, but neither listens
  there; the player shares no network with either.
- An API accepts forwarded headers only from its ingress network, whose one
  other member is the ingress. A request to its loopback listener cannot claim
  HTTPS or another client's address.
- The ingress resolves each application by name on a network that holds nothing
  else. When a container is recreated with a new address, the ingress follows it
  within two seconds; until then a stale answer fails with 502 and cannot reach
  another surface.
- Any container can still reach another surface the way a device does, through
  the ingress's published listener. The ingress then reports that container's
  own address and replaces any forwarded headers it sent.

This is a network boundary, not complete containment. Admin, consumer and worker
still share the data volume, including the token and delivery signing secrets in
it, so a compromised consumer host could mint tokens the admin host accepts and
read or alter stored content without connecting to the admin listener. Each
also holds its own PostgreSQL role. Separating the signing keys and storage
access is tracked as its own change.

Keep the worker's explicit `init: false`, private PID namespace and image
entrypoint when adding deployment overlays. The worker must be namespace PID 1:
it retains each media admission lease until child cleanup is confirmed, and its
death extinguishes the namespace's descendants. This permits recovery after
container recreation without querying foreign numeric PIDs. An init wrapper or
shared PID namespace breaks that proof and is rejected by deployment validation.
The worker logs fatal startup failures and returns exit status 1 through normal
teardown; it does not rely on an unhandled CLR fatal signal to terminate PID 1.
The synthetic CHD drill checks that exit path before recreating the worker during
an active encode; see [the recovery harness](../../scripts/e2e/README.md).

Changing the topology does not release existing unresolved runs recorded under
an init-wrapped worker. Those records retain their original custody facts and
still require the [operator repair procedure](media-processing.md#recovery).

Plain `docker compose`, run from the release directory, is enough: the bundled
`compose.yaml` defaults each image to its exact digest and Compose reads `.env`.
The `scripts/deployment/compose.sh` wrapper does the same from any directory,
and the operational scripts use it. `ROMD_ENV_FILE` can select an external secret file. Standard
`COMPOSE_FILE` and `COMPOSE_PROJECT_NAME` variables apply to all operational tools;
set them consistently, including for backup and restore. Record both in `.env`,
with `COMPOSE_FILE` paths relative to the release directory; the wrapper applies
them unless you export your own.

### Built-in ingress

The stack includes an nginx ingress that serves all three surfaces over HTTPS on
one host name, a port each: the consumer portal on 7663, the admin portal on 7664
and the player on 7665. Compose derives the three public origins from
`ROMD_HOST` and those ports. Change a port with `ROMD_CONSUMER_PORT`,
`ROMD_ADMIN_PORT` or `ROMD_PLAYER_PORT`, and the listening address with
`ROMD_BIND`. A plain-HTTP request to one of these ports is redirected to HTTPS.

`ROMD_HOST` is one name or address, and every device must use exactly it: the
ingress refuses other host names, and sign-in is bound to these origins. Write
it in lower case: browsers send lower-case origins, so ROMD refuses to start
with a mixed-case name and says which name to use. A
`.local` name, a name from your router's DNS or a fixed IP address all work.

On every start the `romd-init` service makes sure a certificate exists:

- **Generated (default).** It creates a private certificate authority limited to
  `ROMD_HOST` and a server certificate valid for 825 days, renewed within 30
  days of expiry when the stack next starts and served without restarting the
  ingress. Trust `ca.crt` once on each device; backups carry the authority, so a
  restore keeps that trust.
  Changing `ROMD_HOST` issues a new authority that devices must trust again.
  To include more names in the certificate, list them all, space separated, in
  `ROMD_CERTIFICATE_NAMES`.
- **Your own.** Set `ROMD_TLS_DIRECTORY` to an absolute directory containing
  `fullchain.pem` and `privkey.pem` valid for `ROMD_HOST`. ROMD never replaces
  them. With a certificate from a public authority for a domain you own, and
  that name resolving to this server on your network, devices need no setup,
  and the same origins keep working if you later route the name from outside.

The three overlays under `deploy/examples/` replace the built-in listeners. Each
needs the three `ROMD_*_PUBLIC_URL` origins set explicitly; add the overlay to
`COMPOSE_FILE` in `.env`.

| Overlay | Use it for |
| --- | --- |
| `deploy/examples/https/compose.yaml` | Three host names on one HTTPS port with your own certificate |
| `deploy/examples/cloudflare/compose.yaml` | Cloudflare Tunnel, with no published ports |
| `deploy/examples/proxy/compose.yaml` | Your own reverse proxy; removes the built-in ingress |

### Existing reverse proxy

Add `deploy/examples/proxy/compose.yaml`, which removes the built-in ingress.
Both API containers and the player listen on port 8080. PostgreSQL and worker
must remain unpublished.

**A proxy that runs as a container on this host** joins the three ingress
networks in place of the built-in ingress and proxies to `romd-admin:8080`,
`romd-consumer:8080` and `romd-player:8080`. In its own Compose project, declare
them as external networks, where `romd` is your `COMPOSE_PROJECT_NAME`:

```yaml
services:
  proxy:
    networks: [ingress-admin, ingress-consumer, ingress-player]
networks:
  ingress-admin: {external: true, name: romd_ingress-admin}
  ingress-consumer: {external: true, name: romd_ingress-consumer}
  ingress-player: {external: true, name: romd_ingress-player}
```

Start ROMD first so the networks exist. Attach nothing else to these networks:
each application accepts forwarded headers from every container on its ingress
network. Resolve the application names at request time, not only at startup,
because their addresses change when their containers are recreated.

**A proxy that cannot join those networks**, on the host itself or another
machine, needs the applications' ports published, which the network-bound
listener does not serve. Add an overlay of your own that returns each API to
listening on all its networks and names the one address it trusts:

```yaml
services:
  romd-admin:
    ports: ["127.0.0.1:8081:8080"]
    environment:
      Romd__IngressListener__NetworkAlias: ""
      ASPNETCORE_URLS: http://+:8080
      Romd__ForwardedHeaders__KnownProxies__0: 172.20.0.1
  romd-consumer:
    ports: ["127.0.0.1:8082:8080"]
    environment:
      Romd__IngressListener__NetworkAlias: ""
      ASPNETCORE_URLS: http://+:8080
      Romd__ForwardedHeaders__KnownProxies__0: 172.20.0.1
  romd-player:
    ports: ["127.0.0.1:8083:8080"]
```

Set the trusted address to the source address the container actually sees;
Docker host-port forwarding usually presents the network's gateway. In this
arrangement admin and consumer can reach each other's listeners over `backend`,
though neither is trusted to supply forwarded headers. Use host firewall rules
where LAN bindings are necessary.

Your proxy should support WebSocket upgrades on admin `/hubs/*`, stream uploads,
and set suitable request size and time limits. The ROM upload routes accept files
up to the configured upload maximum, with an additional 2 MiB request-body allowance
for multipart overhead. DAT and artwork routes accept bodies up to 34 MiB, and every
other route keeps ASP.NET Core's default of about 28.6 MiB. Serve all three origins
over HTTPS.
Verify portal login and console device authorization through your actual ingress.
For ROMD Mount, also verify authenticated `PROPFIND`, `HEAD`, and ranged `GET`
requests under `/vfs/`; preserve Authorization, Range, ETag, and Content-Range.
Stream VFS responses to the client without proxy disk buffering. Both nginx
examples disable response buffering specifically under `/vfs/`; this keeps slow
mounted reads from staging large ROM responses in nginx temporary files. Other
routes retain nginx's default response buffering. Keep the original escaped
request URI when forwarding filenames containing spaces, percent signs or `#`.
For the shipped Compose stack, set `ROMD_VFS_ENABLED=true` in `.env`. The consumer
host alone receives the VFS settings. Optional limits are `ROMD_VFS_MAX_REQUESTS`
(default 8), `ROMD_VFS_MAX_REQUESTS_PER_USER` (default 4), and
`ROMD_VFS_MAX_DIRECTORY_ENTRIES` (default 20000). Concurrency values accept 1–128
and directory entries 1–100000; the effective per-user cap cannot exceed the
global cap. Configuration validation rejects invalid values before startup.
For a directly hosted consumer process, the equivalent enable setting is
`Romd__Vfs__Enabled=true`; see the [VFS runbook](../architecture/vfs.md).

### Ingress diagnostics and credentials

Download capability tokens occur in URL paths; OAuth, device-flow and SignalR
credentials can occur in query strings. Do not retain request lines, URLs,
queries, authorization/cookie headers, or unsanitized upstream error messages
in ingress, tunnel, tracing, or external access-log collectors. Application log
redaction cannot sanitize another service's records.

Both nginx examples emit JSON `ingress_request` events with a classified surface,
a generated request ID, response status/bytes, and request/upstream timings. They
omit client-supplied request values. nginx's request error renderer includes raw
request and upstream URIs, so these listeners discard request error messages;
startup/configuration diagnostics still go to the image's global stderr logger.
Use the safe access events to monitor errors and latency, including upstream
502 responses, and use the corresponding application health/log diagnostics to
investigate failures. Do not enable nginx request debug/error logging while
credential-bearing traffic is present. Apply the same policy to an existing
reverse proxy and any Cloudflare edge or tunnel log collection.

### HTTPS nginx example

Provide a trusted certificate covering all three hosts as `fullchain.pem` and
`privkey.pem` in `ROMD_TLS_DIRECTORY`. The example runs the same nginx pin the
release qualifies; to override it, set `ROMD_NGINX_IMAGE` to an image pinned by
digest. Set the three `ROMD_*_HOST` values to match your public origins. The
example uses the same [networks](#network-boundaries) as the built-in ingress.

```bash
export COMPOSE_PROJECT_NAME=romd
export COMPOSE_FILE=compose.yaml:deploy/examples/https/compose.yaml
```

The listener binds to loopback by default; configure `ROMD_HTTPS_BIND` and
`ROMD_HTTPS_PORT` for your host. It terminates TLS itself and rejects unknown
hostnames. Certificate issuance and renewal remain operator responsibilities.
The templates forward the validated hostname without its port, so serve the
public HTTPS listener on port 443. With a non-default port, OAuth endpoint and
verification URLs can omit the port even when the issuer is configured explicitly.

### Cloudflare Tunnel example

Set the hosts as above, plus a tunnel token. The example ships reviewed nginx
and cloudflared pins; overrides (`ROMD_NGINX_IMAGE`, `ROMD_CLOUDFLARED_IMAGE`)
must be pinned by digest. The overlay adds a `tunnel` network that holds only the
connector and the ingress, and publishes no ports:

```bash
export COMPOSE_PROJECT_NAME=romd
export COMPOSE_FILE=compose.yaml:deploy/examples/cloudflare/compose.yaml
```

Route the admin, consumer and player public hostnames directly to
`http://ingress:80` in your tunnel configuration. Require HTTPS at the public
edge. The ingress listens for the tunnel only on its address on the `tunnel`
network, which it finds at startup from an alias declared there alone; it refuses
to start if that alias does not resolve to one address. The applications reach
the ingress on other networks, where nothing listens on that port, so only the
connector can connect and present a visitor's address. nginx parses
`CF-Connecting-IP` from that peer and replaces `X-Forwarded-For` with the single
visitor IP; ROMD still accepts one hop, from the ingress. Missing, invalid, chained or
port-qualified visitor addresses receive 400. nginx discards Cloudflare identity
headers before forwarding to ROMD. This preserves per-client login throttling
without trusting caller-provided forwarding chains. The address parser/trust
behavior is defined by [nginx realip](https://nginx.org/en/docs/http/ngx_http_realip_module.html).

Keep Cloudflare's Pseudo IPv4 **Off** or **Add Header**, never **Overwrite
Headers**, and disable the **Remove visitor IP headers** transform. Do not put a
same-zone Worker that can rewrite visitor identity in front of these routes:
Cloudflare documents that Worker subrequests can change `CF-Connecting-IP`, and
Pseudo IPv4 overwrite replaces true IPv6 identity with a synthetic IPv4 address.
See [Cloudflare's request-header reference](https://developers.cloudflare.com/fundamentals/reference/http-request-headers/).
Control the tunnel token, zone configuration and connector as trusted ingress
credentials. Never publish this plaintext listener to the LAN; use a separate
TLS listener for direct LAN access.

The validator rejects publishing tunnel port 80 and any connector port. Custom
ingress listeners on other ports are allowed, but their TLS termination, source
restrictions and trusted client-address handling are operator-owned. Apply the
tunnel trust rules only to its listener; separately verify which peers can
reach each additional listener and the forwarded identity it accepts. A Compose validation
pass does not qualify a custom listener's security policy.

Keep OAuth/device-flow and API requests free of interactive proxy challenges.
Bypass caching for authentication, discovery and authenticated API responses.
The player must remain accessible to its configured consumer parent. Cloudflare's
proxy rejects request bodies over 100 MB on Free and Pro plans (check your plan's
current limit), so larger ROM uploads need a direct LAN HTTPS route or path import.

## Fresh installation

Run from the extracted release directory, with the selected ingress configuration:

```bash
scripts/deployment/validate.sh
scripts/deployment/compose.sh pull
scripts/deployment/compose.sh up -d --wait --wait-timeout 240
scripts/deployment/verify.sh
```

On linux/arm64, build the ROMD images instead of pulling them; see
[CHD media binaries](#chd-media-binaries). The validator renders Compose without
displaying secrets. It checks database
connectivity, secret/origin shape, private services and player isolation. It does
not prove firewall policy, certificate validity or external DNS configuration.

PostgreSQL initializes roles on its first boot. The worker migrates, validates
its packaged [media child](media-processing.md) and seeds, then publishes its
startup marker. API startup waits for that marker; API
healthchecks query `/health/ready`. The worker marker proves startup completion,
not ongoing queue responsiveness. Monitor admin readiness details for degraded
worker heartbeat, outbox and disk checks; degraded readiness still returns 200.

Verify discovery advertises each intended HTTPS origin, both portals load, an
admin can sign in, the player configuration loads, and a small import/download
succeeds. Connect the console if it is part of your installation.

## Administrator recovery

If no administrator can sign in, an operator with local access to the Compose
installation can issue a recovery link for an **existing active administrator**:

```bash
scripts/deployment/compose.sh exec -T romd-worker dotnet /app/romd-worker.dll --recover-admin owner@example.com
```

Use the account's current email, which may differ from `ROMD_DEFAULT_ADMIN_EMAIL`.
The command writes one JSON object to the operator's terminal with `recoveryUrl`
and `expiresAt`. Open that URL in a browser and choose a new password. The link
expires after one hour, is usable once, and carries its token in the URL fragment.
Treat the output as a credential: keep it out of public issues, transcripts and
shared logs. The database stores only the token hash.

Issuance immediately revokes the account's existing sessions, OAuth tokens,
authorizations and prior pending links, rotates its security stamp, and records
system-actor audit evidence. Redemption applies the normal password policy and
revokes credentials again. A failed password-policy check leaves the link usable.
Running the command again replaces the previous recovery link.

Recovery uses the worker's runtime database credential and requires its published
schema version. The portal origin comes from
`Romd:Auth:AdminSpa:PostLogoutRedirectUris:0`, already populated by production
Compose. Production requires an HTTPS origin and the `romd_worker` database role.
The command never creates accounts, grants roles, unsuspends an account, migrates
schemas or starts worker services. It refuses suspended or pending-activation
accounts. An ordinary signed-in administrator manages those through Users.

For source installations, supply the same runtime connection and portal origin
as the installation, then run
`dotnet run --project src/Romd.Worker.Host -- --recover-admin owner@example.com`.
Development also accepts a loopback HTTP portal origin. Changing bootstrap
environment variables is not an account-recovery mechanism.

## Upgrade

The current schema starts at `20261031050000_FreshMediaBaseline` with the
additive `20261102000000_PspUmdObservations`, `20261109000000_SaturnCdObservations`,
`20261117000000_JobDispatchExpiryIndex`, `20261118000000_SystemHardwareMetadata`,
`20261118194850_SignInMessage`, `20261120162632_MediaCollection`, and
`20261121030000_ImportOutcomeReasons` migrations (readiness version 79).
The Saturn migration preserves PS1 observations and adds distinct Saturn fields.
The sign-in message starts blank. Existing title media stays title-level and
unpreferred after the media collection migration.
Deploy matching hosts worker-first so APIs observe the new readiness version.
There is no upgrade from the retired pre-baseline migration chain. Use new empty
database/data volumes for that starting point and keep the previous installation
and its matching backups for recovery. Baseline downgrades are unsupported.

The worker refuses an existing ROMD schema whose migration history is missing
or empty before applying migrations or publishing readiness. Normal worker
startup checks and provisions the application schema before Hangfire can write
its schema or readiness marker. It also refuses
migration IDs that this release does not recognize. Do not delete migration
history to force an upgrade. Recover the database and its history from the same
backup with a compatible release, or follow the release's fresh-install boundary
using a new empty database. A refused installation is left unchanged.

Before 1.0, a release may require a fresh installation. Its release notes state
whether upgrading from the previous release is supported. If they do not, keep
the previous installation and its backups for recovery, and install fresh with
new volumes.

Pre-alpha storage reset: releases from 2026-09-27 no longer read continuous
Zstandard (`.zst`) content objects, and nothing converts them. Compressed content
from earlier installations or backups becomes unreadable after an upgrade or
restore; perform a fresh installation with new volumes instead.

1. Verify and stage the new release bundle in a separate directory. Preserve the
   previous bundle, image references and protected installation configuration.
2. Use the same project name, configuration and storage mapping. Validate the new
   bundle and pull its images before interrupting the running installation
   (linux/arm64 builds them; see [CHD media binaries](#chd-media-binaries)).
3. Take a complete backup using the **currently running release's** scripts.
4. Stop both API hosts and the worker using the current configuration. The backup
   command restarts hosts after capture; stop them again before applying migrations.
5. Using the new bundle, start PostgreSQL and worker first, then APIs/player/ingress:

   ```bash
   scripts/deployment/compose.sh up -d --wait --wait-timeout 240 postgres romd-worker
   scripts/deployment/compose.sh up -d --wait --wait-timeout 240
   scripts/deployment/verify.sh
   ```

6. Verify login, discovery and application operations through ingress.
   In the admin portal, open **Settings → Server** to check what is installed
   and what is running. **Version** is the release tag of the bundle you
   started from. **Build** is the tag and source commit compiled into the
   running admin image. A stable release reuses its candidate's image, so a
   `v1.2.3` installation reports a `v1.2.3-rc.N` build. Authenticated callers
   can read both from `GET /api/server/info`. An installation without the
   bundle's `manifest.json` reports no version, and an untagged build reports
   `development`.

A release that changes the CHD decoder tuple also needs the
[CHD decoder upgrade](#chd-decoder-upgrades) procedure.

### Upgrading from a release with the `edge` network

Releases up to `v0.1.0-rc.8` put the ingress and all three applications on one
`edge` network with fixed addresses. The steps above move an installation to the
[per-surface networks](#network-boundaries) without further action: Compose
creates the new networks and recreates the containers on them. Volumes, secrets,
the certificate authority and `.env` are untouched.

- **Address settings in `.env`.** `ROMD_EDGE_SUBNET`, `ROMD_EDGE_DYNAMIC_RANGE`,
  `ROMD_TRUSTED_PROXY`, `ROMD_ADMIN_EDGE_IP`, `ROMD_CONSUMER_EDGE_IP`,
  `ROMD_PLAYER_EDGE_IP` and `ROMD_CLOUDFLARED_EDGE_IP` no longer have any effect.
  The installation starts with them still set, and `romd-init` names the ones it
  finds on every start until you delete them:

  ```bash
  scripts/deployment/compose.sh logs romd-init
  ```

- **The old network.** Compose leaves the unused `edge` network behind, still
  holding its address range. Remove it once the upgraded installation verifies,
  where `romd` is your `COMPOSE_PROJECT_NAME`:

  ```bash
  docker network rm romd_edge
  ```

- **Your own overlays.** An overlay that attaches a service to `edge`, or gives
  one a fixed address there, stops the upgrade before anything changes, with
  `service "…" refers to undefined network edge`. Attach a reverse proxy to the
  three ingress networks as [existing reverse proxy](#existing-reverse-proxy)
  describes, and drop fixed addresses. An overlay that set
  `Romd__ForwardedHeaders__KnownProxies__0` itself keeps exact-address trust and
  must also keep the settings shown there.
- **Cloudflare Tunnel.** Routes to `http://ingress:80` keep working: the
  connector resolves that name on the new `tunnel` network. Recreate the ingress
  and the connector together, which `up -d` does.

This procedure was exercised with Compose v2.25.0, v5.5.1 and v5.7.0.

Do not rely on `depends_on` to stop old running APIs during migration. Do not use
`restart` as an image upgrade command. PostgreSQL major upgrades need a separate
planned database upgrade; a ROMD image update does not authorize one.

If schema changes have run, starting older images is not a general rollback.
Restore the matching pre-upgrade database/data backup into empty volumes with
the matching previous release. Keep the failed installation stopped and preserved
for diagnosis. Never expose source and restored copies with the same server
identity simultaneously.

## Backup and restore

A backup is a quiesced set of PostgreSQL application/Hangfire schemas plus the
complete ROMD data directory, excluding temporary files and logs. It includes
content, identity, signing keys and Data Protection keys. When ROMD generated
the installation's certificate authority, the set also holds it (`ca.tar`), so
devices that trusted the original installation trust a restored one without
any change. Protect the set as secret material. Saved provider credentials need the matching Data Protection keys.

```bash
scripts/backup/backup.sh /absolute/backup/destination
```

The command stops the three ROMD hosts, captures the set, writes its manifest
last, and restarts them. PostgreSQL stays running. The one-shot data-volume
helpers run explicitly as root so they can preserve application-owned private
keys, file ownership and modes independently of the helper image's default user. Copy complete sets off-host
and schedule retention in your infrastructure automation. ROM exports are not
installation backups. Preserve the matching release bundle and a secure copy of
installation configuration separately; backup sets do not include `.env`.

Each set contains `roles.sh`, `romd.dump`, `data.tar`, `manifest.json` and, for a generated
certificate authority, `ca.tar`, with
schema versions, PostgreSQL version, artifact sizes and SHA-256 hashes. Missing
manifests, mismatched artifacts and incompatible role definitions are refused.

To restore into a fresh project with empty volumes and the matching release:

```bash
scripts/deployment/compose.sh up -d --wait postgres
# Create the stopped worker so the helpers can locate this project's data volume.
scripts/deployment/compose.sh create --no-recreate romd-worker
scripts/backup/restore.sh /absolute/path/to/backup-set
scripts/deployment/compose.sh up -d --wait --wait-timeout 240 postgres romd-worker
scripts/deployment/compose.sh up -d --wait --wait-timeout 240
scripts/deployment/verify.sh
```

Restore leaves hosts stopped until you start them. It refuses non-empty database
or data destinations. Preserve server identity for recovery; an independent clone
needs a different identity before exposure. Verify the restored instance ID,
login, known library content and download hashes. Rehearse recovery periodically.

For non-Compose installations the scripts also support `ROMD_BACKUP_MODE=local`,
`ROMD_BACKUP_PG_EXEC` and `ROMD_BACKUP_DATA_DIR`; the caller must stop all writers.
`ROMD_BACKUP_COMPOSE` overrides the Compose command for existing automation.

## CHD media binaries

The server images carry native CHD binaries built from pinned sources in ROMD's
Docker build. libchdr is at `/app/native/libchdr/` in the admin, consumer and
worker images; `chdman` is at `/app/native/chdman/` in the worker image only.
Each directory holds the binary under `native/` and its `build-manifest.json`
build record. The files are owned by root and are not writable by the
container user.

Both linux/amd64 and linux/arm64 server images turn CHD on. Admin and consumer
images (and development targets) set
`Romd__Native__LibchdrDirectory=/app/native/libchdr`; worker images set it and
`Romd__Native__ChdmanDirectory=/app/native/chdman`. The shipped `compose.yaml`
and `.env.example` contain no native keys. Activation belongs to the images.

Released images are amd64 only. A linux/arm64 installation builds the four ROMD
images on an arm64 host from a clean checkout of the release tag:

```bash
for service in admin consumer worker player; do
  docker build --platform linux/arm64 --target "romd-$service" \
    --tag "romd-$service:<version>" .
done
```

In the extracted release directory, replace `ROMD_ADMIN_IMAGE`,
`ROMD_CONSUMER_IMAGE`, `ROMD_WORKER_IMAGE` and `ROMD_PLAYER_IMAGE` in
`images.env` with these tags; `compose.sh` reads image references from that
file. Keep `ROMD_POSTGRES_IMAGE`. Build the new tags instead of pulling ROMD
images during installation and upgrades (`compose.sh pull postgres` pulls only
PostgreSQL). Use a new tag for each build; never rebuild a tag in place.

Check [media status](../architecture/media-status.md#qualification-status) for
the current RID/build/workload matrix. Image enablement is separate from platform
qualification and the exact published-candidate release gates.

There is no opt-out on these server images. A blank or relative directory, a chdman
directory without libchdr, or a worker with libchdr but without chdman refuses
startup. Before starting, each host verifies its configured builds against their
build records: RID, compiled upstream pin, binary size and SHA-256, and the loaded
libchdr's own identity. Any failure stops the host. Admin and consumer report the
result as the status of the critical `native-media` readiness check. See
[configuration](configuration.md#media-processing).

CHD encoding defaults to 1,200 s CPU and wall budgets. Configure them with `Romd:Media:ChdEncodeCpuTimeoutSeconds` and
`Romd:Media:ChdEncodeWallTimeoutSeconds`; inspection, CHD extraction and RVZ
retain the 600 s `CpuTimeoutSeconds` / `WallTimeoutSeconds` defaults. Keep the
CHD budget settings consistent across admin and worker hosts: admin selection
and worker encoding must agree on the current budget's journal key. A larger
budget holds a worker slot longer in the worst case; it does not qualify every
DVD-9 or guarantee completion on a slower host. [Recipe measurements](../architecture/media-status.md#dvd-chd) describe the
recorded budget trade-off. Budget failures keep the current representation
served and prevent automatic repeats under the same recipe, budget and preferred
representation. Jobs still offers explicit retry; see
[media processing](media-processing.md#known-limitations).

`chdman` is MAME code; MAME as a whole is distributed under GPL-2.0. Its exact
source archive, `mame0289.tar.gz` (SHA-256
`0929cc749afabcef892900e10dd90bd8b05f94a7dde8f367ac6a5d2082589f84`), is attached
to the GitHub release that publishes the images. The worker image's notices
under `/usr/share/doc/romd/native/chdman/notices/` name the archive and its
digest. If you redistribute the worker image, provide that archive with it.

### CHD decoder upgrades

A host serves a stored CHD, or an original reconstructed from one, only through
evidence measured under its own decoder tuple: RID (architecture), libchdr
upstream pin and native build recipe version (`NativePolicy.BuildRecipeVersion`).
Every read checks this, including the worker's background exports. Admin ROM
detail formats and consumer release manifests use the same admission, so a CHD
measured only under another tuple is reported unsupported rather than advertised.
Rebuilding an image with the same tuple keeps existing evidence valid. A CHD
encoding recipe change, such as `chd-dvd-canonical/v2` to a later version, does
not change read admission.

Changing any tuple field makes every stored CHD unservable under the new build
until it is re-measured. ROMD does not retain the ISO beside a measured CHD, so
both `chd` and `original` become unavailable; nothing falls back to another build.

Native build recipe 2 repairs allocator-dependent incomplete Huffman tables at
the existing upstream pin, changing the decoder tuple. Consult the
[current qualification matrix](../architecture/media-status.md#qualification-status)
and qualify the target platform before admitting retained CHDs through that build.

For PS1 production CD archives, the campaign includes retained whole-file
re-verification on Linux x64. Every original BIN is reconstructed and
compared to the immutable reconstruction manifest without requiring original BIN
files. A fresh container, CUE/sidecar and PS1 boot check appends one complete
verification for the new decoder build; creation provenance and preference stay unchanged. Member and whole-container
reads fail closed until the new tuple has that proof. Deploy the compatible
fresh-baseline release with the existing decoder before changing its tuple, and
qualify the upgrade/recovery gate with `scripts/e2e/chd_cd_upgrade.py`. Recorded
results live in [media status](../architecture/media-status.md#qualification-status).
See [PS1 compression](media-processing.md#ps1-original-disc-compression).

#### Platform qualification before activation

Decoder-tuple admission preserves evidence boundaries; it does not qualify a
platform or authorize publication. The release policy remains `linux/amd64`.
Adding another published architecture requires a packaging/release-policy change,
its native decoder qualification and its own downloaded-bundle gates.

For each target RID and native build recipe, retain private evidence for:

1. Native build verification, the decoder regression suite, and
   `mise run fuzz:libchdr:gate` on the native architecture. The full gate requires
   sanitizer canaries, zero guard-parity mismatches, and 1,800 seconds per target;
   a shorter smoke campaign does not satisfy it.
2. Both independently DAT-matched DVD-5/DVD-9 fixtures through
   `mise run e2e:chd -- --checkpoint encoding`. Verify canonical artifact
   reproducibility, all original/CHD download hashes, active-encode recovery,
   malformed-file isolation and cleanup. Receipts must identify the current
   native tuple and exact binary in every host. Generated media cannot replace
   this real-disc gate.
3. The upgrade campaign below on disposable retained real-disc CHDs measured
   under the previous tuple: unavailable formats before re-verification, bounded
   successful campaign results and zero pending items afterward, restored
   original/CHD delivery, unchanged artifact/preference, preserved earlier
   evidence, and released source pins. This is separate from fresh convergence.
4. Stock image defaults for the activated RID, using `--image-defaults` in the
   CHD drill, plus the normal source Compose smoke. Each published candidate
   still requires its separate downloaded-bundle qualification. Do not infer
   ARM64 qualification from an x64 run or emulation. Recipe-1 ARM64 results do
   not qualify recipe 2.

Before the first decoder upgrade, deploy a compatible fresh-baseline release
to worker, admin and consumer using the existing decoder tuple; see the
[current database boundary](configuration.md#database-and-installation-boundary). The
baseline records bounded campaigns, their outcomes and temporary source pins.
Only the worker provisions; API readiness stays closed until the expected schema
is published. Unsupported existing application databases are refused unchanged,
so this is not a migration from historical schemas. Within the same compatible
baseline, a decoder-only upgrade can use the campaign below.

1. Before upgrading, keep a copy of each retained CHD that must stay available,
   for example through `GET /api/roms/{romId}/download?format=chd` while the
   current hosts still serve it.
2. Upgrade the worker first. Follow [Upgrade](#upgrade) steps 1–4; the backup's
   `manifest.json` records `romdSchemaVersion` and `hangfireSchemaVersion`. Start
   only PostgreSQL and the worker from the new release directory
   (`scripts/deployment/compose.sh up -d --wait --wait-timeout 240 postgres romd-worker`),
   then read the versions it published:

   ```bash
   scripts/deployment/compose.sh exec -T postgres psql -v ON_ERROR_STOP=1 -qtA -U romd_bootstrap -d romd \
     -c 'SELECT version FROM romd.romd_schema WHERE singleton' \
     -c 'SELECT version FROM hangfire.romd_schema WHERE singleton'
   ```

   Start admin from the new release with the same decoder configuration as the
   new worker. Its Storage page captures that decoder tuple for the campaign:

   ```bash
   scripts/deployment/compose.sh up -d --wait --wait-timeout 240 --no-deps romd-admin
   ```

   Only if both published schema versions equal the previous release's manifest
   values, the previous consumer may remain running while re-verification adds
   evidence. Start it from its previous release directory without recreating
   the worker or new admin:

   ```bash
   scripts/deployment/compose.sh up -d --wait --wait-timeout 240 --no-deps romd-consumer
   ```

   Its `/health/ready` must then report healthy; the `application-schema` and
   `hangfire-storage` checks fail closed on any other published version. Equal
   versions establish schema compatibility only. If they differ, or the previous
   consumer stays unready, start the new consumer and expect CHDs to remain
   unavailable there until step 3 completes.
3. As an Admin, open **Storage → Media reconciliation**, select retained
   representations, disable conversion and choose an explicit finite limit.
   The equivalent API is `POST /api/media/reconciliation/selections` with a
   fresh `requestId`, `filter: "RetainedRepresentations"`, `limit` and
   `conversionRequested: false`. The selection captures finite high-watermarks;
   each child captures the worker's verified resident decoder and exact retained
   graph. Missing measurements append complete integrity and boot evidence.
   Verification does not re-encode, switch preference or prune copies. Earlier
   decoder evidence stays usable by compatible previous readers.
   Follow selected/completed/failed counts, enumeration completion and child
   outcomes in Jobs. A restart resumes the durable selection or captured child;
   cancellation waits for supervised inspection to stop before releasing pins.
   Failed artifacts retain earlier evidence. Retry terminal work with a fresh
   request UUID, retaining its finite bounds, and resolve failures before
   switching consumers. A changed decoder causes current-state replanning.
   A shared PS1 CD counts as one representation: its proof and attempt journal
   commit only after every member and stored dependency passes. A recreated
   worker re-verifies the pinned archive under the ordinary execution lease;
   committed measurements replay without another decode. The exact original
   CUE remains a separately retained file.
   Reference eligibility grants encoding authority. Byte delivery instead requires
   complete output verification, the exact resident decoder and capabilities;
   runtime claims separately require a uniquely resolved media kind. Exact CHD
   re-upload remains an individual repair option.
4. Start consumer from the new release and finish [Upgrade](#upgrade) steps 5–6.

Keep the window short. CHDs the new worker encodes or measures carry only its
tuple: converging a newly uploaded PS2 ISO releases that ISO, so previous readers
cannot serve the ROM. Background exports run on the worker and skip CHD-backed
PS2 members that lack evidence for its tuple. Avoid PS2 ISO uploads, convergence
runs and exports until step 4. Newly retained facts and PS2 reference arrivals
stage general reconciliation demand. PS2 encoding still requires supported
current reference authority; complete eligible PS1 layouts can reconcile
automatically on qualified Linux x64 workers. Avoid PS1 imports and conversion
work during a decoder upgrade until re-verification and host rollout finish.

If a host has no matching evidence for a CHD, roll admin and consumer back to an
image whose tuple still has evidence, or re-upload the exact CHD. A rollback
image must expect the current schema version; otherwise follow [Upgrade](#upgrade).
Re-uploading the original ISO does not help: any measured CHD outranks an ISO,
so retention keeps the CHD and discards the ISO.

Current behavior to plan for:

- Reconciliation verifies a canonical graph missing the worker's resident-build
  measurement. Decoder changes alone do not sweep the library; request an
  explicit finite retained-representation selection.
- Delivery targets bind the exact representation, output, verification and
  decoder. A target issued before a decoder change stays refused after
  re-measurement; obtain a new target from current formats or the release
  manifest. An original target also needs renewal when its preferred output
  changes.
- VFS uses the same admission, but still lists a CHD-backed member without
  evidence for the host's tuple as an original; opening its release or file fails
  with `503` and `Consumer.Vfs.InconsistentProjection`, which rclone retries.
- A failed re-verification of the resident build during a read marks
  `native-media` unready on admin and consumer until the host restarts; nothing
  re-verifies it automatically. Evidence of another tuple does not affect
  readiness.
- The worker has no readiness endpoint. A failed re-verification there disables
  its CHD reads and refuses all convergence work until the worker restarts, and
  no health check reports it.

`ChdDecoderUpgradeTests` and
`CdArchivePublicationTests.VerifyOnly_CompleteArchiveWithoutOriginalBins_RetainsEveryDecoderAndCreationWithOrWithoutLogicalOwners`
pin these rules with synthetic resident builds.

## Release validation

See [deployment smoke and packaged qualification](../maintainers/packaged-release-qualification.md)
for maintainer checks. Install and upgrade only within the qualification scope
documented in the release notes.

## Maintainer release candidates

See [versions and promotion](../maintainers/release-readiness.md#versions-and-promotion)
and the [immutable publication procedure](../maintainers/release-metadata.md#immutable-publication).
