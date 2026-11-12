# Production deployment

Run ROMD on one Linux server with Docker Compose: PostgreSQL 18, one worker,
admin and consumer portals, and a separate browser player. The release bundle
uses pinned images and needs no application checkout or SDK. You provide local
storage, HTTPS ingress and backups.

The initial release qualification target is **Linux amd64**. Install a qualified
full release when available; prerelease candidates are still being evaluated.
Ottercade native builds remain experimental. Before the first qualified release,
use [source development](../development/getting-started.md) to evaluate ROMD.

For a new server, follow [prerequisites](#prerequisites-and-configuration),
[release artifacts](#release-artifacts), [ingress](#choose-ingress) and
[fresh installation](#fresh-installation), in that order. Existing operators can
jump to [upgrade](#upgrade), [recovery](#administrator-recovery) or
[backup and restore](#backup-and-restore).

## Prerequisites and configuration

Install Docker with Compose v2.24 or newer, Bash, Python 3, OpenSSL and `shasum`.
Store PostgreSQL and ROMD data on local ext4/btrfs storage, not SMB/NFS mounts.
The host needs outbound access for catalog subscriptions and metadata providers.
Select HTTPS admin, consumer and player origins. Consumer and player must be
separate origins under the same site; sibling subdomains work well.

Download the bundle and its checksum to the same directory. Verify with
`shasum -a 256 -c romd-<version>-deployment.tar.gz.sha256`, then extract into
a release directory and copy `.env.example` to `.env`. Generate independent secrets, for example
with `openssl rand -hex 32`; PostgreSQL passwords must be hex because Compose
embeds them in connection strings. The bootstrap admin password must satisfy
the application's password policy. Restrict `.env` permissions (`chmod 600`).
Keep plaintext secrets out of Git; an encrypted secret store is appropriate.

Set each `ROMD_*_PUBLIC_URL` to an exact HTTPS origin, without a trailing slash.
The default deployment uses one canonical origin per portal. Additional origins
require an operator overlay that configures matching auth redirects, CORS and
consumer-to-player mappings; do not casually disable issuer or proxy validation.

`ROMD_TRUSTED_PROXY` is the source IP of the final proxy as the APIs see it.
ROMD trusts exactly that peer for one forwarded hop. The proxy must reject
unknown hosts and replace incoming forwarded headers with authoritative values.
Never publish a plaintext listener that lets arbitrary clients claim HTTPS.

Optional IGDB credentials can be supplied to admin and worker; leave both empty
to manage them through **Integrations**. For path imports, mount the source at the
same container path on admin and worker and configure `Romd__AllowedImportPaths__*`
on both. Changing role password environment variables does not rotate credentials
in an already initialized PostgreSQL cluster.

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
full release. Images are published under immutable version tags only, with no
`latest` tag, and the bundle's `images.env` pins each image by digest.

The bundle contains:

- `compose.yaml`: production topology, health checks and bounded Docker logs;
- `images.env`: immutable references for all four ROMD images and PostgreSQL;
- `.env.example`: required installation settings with blank secrets;
- `deploy/postgres/init/`: role/schema initialization for a fresh database;
- `deploy/examples/`: optional HTTPS proxy and Cloudflare Tunnel overlays;
- `scripts/backup/` and `scripts/deployment/`: compatible operational tools;
- this runbook, configuration and media-processing guides.

The release also attaches `mame0289.tar.gz`, the exact source archive of the
worker image's `chdman` binary (see [CHD media binaries](#chd-media-binaries)).

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

The base stack has two project-scoped networks. PostgreSQL and worker use
`backend`; both APIs use `backend` and `edge`; player uses only `edge`. Your
proxy attaches only to `edge`. No fixed container names prevent multiple isolated
installations. Keep `COMPOSE_PROJECT_NAME` stable across release directories to
reuse the intended database/data volumes.

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

The `scripts/deployment/compose.sh` wrapper consistently loads `images.env` and
`.env`. `ROMD_ENV_FILE` can select an external secret file. Standard
`COMPOSE_FILE` and `COMPOSE_PROJECT_NAME` variables apply to all operational tools;
set them consistently, including for backup and restore.

### Existing reverse proxy

Create an operator overlay attaching your proxy to `edge`, or publish the three
HTTP container ports only to the interface needed by an external proxy. Both API
containers and player listen on port 8080. Configure the actual trusted peer IP;
Docker host-port forwarding can change the source address seen by a container.
Use host firewall rules where LAN bindings are necessary. PostgreSQL and worker
must remain unpublished.

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
`privkey.pem` in `ROMD_TLS_DIRECTORY`. Set `ROMD_NGINX_IMAGE` to a reviewed
release/digest, set the three `ROMD_*_HOST` values to match your public origins,
and select a non-conflicting `ROMD_EDGE_SUBNET` and `ROMD_EDGE_DYNAMIC_RANGE`.
Set `ROMD_TRUSTED_PROXY` to a free address inside the subnet and outside the dynamic
allocation range (the example uses `172.28.0.2` with `172.28.0.128/25`).
The examples also reserve `.3`, `.4` and `.5` for admin, consumer and player.
If changing subnets, update `ROMD_ADMIN_EDGE_IP`, `ROMD_CONSUMER_EDGE_IP` and
`ROMD_PLAYER_EDGE_IP` too. Stable service addresses prevent cached proxy DNS
from sending traffic to a different surface after container restarts.

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

Select the nginx image, hosts and network settings as above, plus a reviewed
`ROMD_CLOUDFLARED_IMAGE` and a tunnel token. Reserve `ROMD_CLOUDFLARED_EDGE_IP`
(default `172.28.0.6`) inside the edge subnet, outside its dynamic range, distinct
from ingress, the gateway and the API/player addresses. The overlay pins the
connector to this address and publishes no ports:

```bash
export COMPOSE_PROJECT_NAME=romd
export COMPOSE_FILE=compose.yaml:deploy/examples/cloudflare/compose.yaml
```

Route the admin, consumer and player public hostnames directly to
`http://ingress:80` in your tunnel configuration. Require HTTPS at the public
edge. The private listener accepts only the pinned connector's original socket
address. nginx parses `CF-Connecting-IP` from that peer and replaces
`X-Forwarded-For` with the single visitor IP; ROMD still trusts only ingress for
one hop. Direct edge-network peers receive 403, and missing, invalid, chained or
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
TLS listener for direct LAN access. When upgrading the example, validate the
reserved address and recreate ingress and cloudflared together.

The validator rejects publishing tunnel port 80 and any connector port. Custom
ingress listeners on other ports are allowed, but their TLS termination, source
restrictions and trusted client-address handling are operator-owned. Apply the
tunnel trust rules only to its listener; separately verify the original socket
peer and forwarded identity for each additional proxy path. A Compose validation
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
additive `20261102000000_PspUmdObservations` and
`20261109000000_SaturnCdObservations` migrations (readiness version 74).
The latter preserves PS1 observations and adds distinct Saturn fields; deploy
matching hosts worker-first so APIs observe the new readiness version.
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

A release that changes the CHD decoder tuple also needs the
[CHD decoder upgrade](#chd-decoder-upgrades) procedure.

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
content, identity, signing keys and Data Protection keys. Protect it as secret
material. Saved provider credentials need the matching Data Protection keys.

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

Each set contains `roles.sh`, `romd.dump`, `data.tar` and `manifest.json`, with
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

CI validates merged Compose, builds actual production images, starts a disposable
HTTPS deployment, checks portal/discovery/player routes, rejects unknown hosts,
completes admin authorization-code + PKCE login, restarts hosts, and restores a
Compose-mode backup into a second empty project. Backend integration tests cover
application behavior and the PostgreSQL role boundaries. The smoke drill does not
exercise a real Cloudflare account, public certificates, DNS or operator firewall.
The `--vfs` check uploads a synthetic 4 MiB ROM and matching DAT through the admin
API, then enables an assigned published library with an explicit policy allowing
the unrated fixture. It signs in through the consumer PKCE client, confirms that
bearer tokens cannot read `/vfs`, and issues a registered connection whose export
uses the configured HTTPS origin and checksum profile. With that app password it
verifies directory traversal, exact ranges (including EOF clipping), full-file
integrity, conditional reads, anonymous rejection, and denial after connection
revocation at startup, after restart, and after restore. The fixture is imported only once; restart/restore checks read
the persisted content. This is a correctness check, not a performance measurement.
Its isolated self-signed TLS certificate is trusted explicitly by the test client,
without modifying system trust. The drill also requires certificate failure for
an untrusted issuer and a mismatched hostname. Public certificate issuance/renewal
and outside-LAN connectivity still require separate validation.

Maintainers can run:

```bash
mise exec -- python3 scripts/validation/run.py --id deployment.contracts
python3 scripts/deployment/smoke.py --build --vfs
mise run test:integration
```

The registered contract check runs under the mise environment so Podman uses the
pinned Compose client; `mise run contracts:check` runs it with the CHD harness and
OpenAPI drift contracts. Its Docker BuildKit context-export case skips with a
reason on other engines.

The real smoke drill requires Docker and Compose. It creates uniquely named
disposable projects and removes only those projects and their volumes. It never
reads the installation `.env`. A continuous lifecycle observer starts before the
first service container and remains active through both projects' cleanup. Every
started container must have a matching termination with exit code zero; an OOM,
forced kill, unexpected exit or restart, missing event coverage or cleanup
failure fails the drill, even when Compose succeeds. Create-only metadata probes
acknowledge each project observer's readiness and final drain; an end-of-run
event-history query is insufficient.

The sanitized lifecycle receipt stays in a new directory under
`.artifacts/deployment-smoke/`, or the new path supplied with `--evidence-dir`.
Bounded container logs and state captured before removal stay private under
`.artifacts/container-lifecycle-private/`; do not publish those diagnostics.
For a local large-disc check, add `--vfs-file /absolute/path/to/image.iso` with
`--vfs`. The source is read-only, hashing and download verification are streamed,
and the temporary catalog names it `vfs-smoke.iso`. Allow disk capacity for upload
staging, CAS, an uncompressed backup, and a restored CAS copy. This opt-in uses the
same startup/restart/restore checks; CI continues using the small synthetic file.

## Maintainer release candidates

The release workflow checks a strict `v<SemVer>` tag against the clean current
`main` tip before running reusable CI. It retains a second build and recovery
drill, then publishes those exact source/version-labelled images. The worker's
verified GPL source archive is uploaded before any image push. The candidate
remains a prerelease throughout automated qualification; production promotion
requires a separate maintainer decision (see
[versions and promotion](../maintainers/release-readiness.md#versions-and-promotion)).

Source packaging records every bundle file, the full source commit, tag,
Linux amd64 platform and image digest map in `manifest.json`:

```bash
scripts/deployment/bundle.sh images.env release-output/deployment.tar.gz FULL_SOURCE_COMMIT v1.2.3-rc.1
```

A separate runner downloads the actual candidate bundle and compares it with
the publisher's independently retained bundle hash and image map before
executing the installation. It verifies running image digests, worker-first
provisioning, browser callbacks and authenticated views in both portals, real
isolated-player startup, restart and restore. Broken required portal assets and
player startup failures must fail qualification and leave sanitized diagnostics.
See [packaged release qualification](../maintainers/packaged-release-qualification.md).

Publishing does not overwrite version tags or release assets. A partial draft
with assets, an exposed candidate, an existing image version, or an uncertain
registry/API response blocks another publishing attempt. Preserve that state
for maintainer review and use a new version for a new publish. Failed
qualification jobs may be rerun against the original immutable candidate;
rerunning the publisher cannot silently rebuild or replace it. No workflow
command turns an unqualified candidate into a final release.
