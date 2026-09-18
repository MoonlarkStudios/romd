# Production deployment

ROMD supports one Docker Compose installation on a single Linux server:
PostgreSQL 18, one worker, admin and consumer API/portal hosts, and an isolated
browser player. The production Compose uses released images, publishes no ports,
and requires operator-managed HTTPS ingress. No NAS, domain, tunnel provider,
or personal network configuration is required by the application repository.

## Release artifacts

Version tags (`v<major>.<minor>.<patch>`, optionally with a prerelease suffix)
trigger the release workflow. It runs the reusable CI gates, builds and tests
Linux amd64 images, pushes them to GHCR under the repository owner's namespace,
and attaches a checksummed deployment bundle to the GitHub release. ARM64 is
not yet an advertised release target.

The bundle contains:

- `compose.yaml`: production topology, health checks and bounded Docker logs;
- `images.env`: immutable references for all four ROMD images and PostgreSQL;
- `.env.example`: required installation settings with blank secrets;
- `deploy/postgres/init/`: role/schema initialization for a fresh database;
- `deploy/examples/`: optional HTTPS proxy and Cloudflare Tunnel overlays;
- `scripts/backup/` and `scripts/deployment/`: compatible operational tools;
- this runbook.

Until the first release is published, maintainers can build from source using
`compose.build.yaml`; do not assume GHCR packages already exist. Package
visibility must allow intended operators to pull the images (make packages
public for public releases, or document registry authentication for private ones).

Keep a release's files together. Keep your deployment configuration in a separate
infrastructure repository: selected release, origins, mounts, ingress, monitoring,
backup schedules and encrypted secrets. Do not run production from a mutable
application working tree or mix worker/API images from different releases.

## Prerequisites and configuration

Install Docker with Compose v2.24 or newer, Bash, Python 3, and `shasum`.
Store PostgreSQL and ROMD data on local ext4/btrfs storage, not SMB/NFS mounts.
The host needs outbound access for catalog subscriptions and metadata providers.
Select HTTPS admin, consumer and player origins. Consumer and player must be
separate origins under the same site; sibling subdomains work well.

Extract the bundle to a release directory, verify its checksum with
`shasum -a 256 -c romd-<version>-deployment.tar.gz.sha256` before extracting,
and copy `.env.example` to `.env`. Generate independent secrets, for example
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
to manage them through Admin Settings. For path imports, mount the source at the
same container path on admin and worker and configure `Romd__AllowedImportPaths__*`
on both. Changing role password environment variables does not rotate credentials
in an already initialized PostgreSQL cluster.

## Choose ingress

The base stack has two project-scoped networks. PostgreSQL and worker use
`backend`; both APIs use `backend` and `edge`; player uses only `edge`. Your
proxy attaches only to `edge`. No fixed container names prevent multiple isolated
installations. Keep `COMPOSE_PROJECT_NAME` stable across release directories to
reuse the intended database/data volumes.

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
and set suitable request size and time limits. Serve all three origins over HTTPS.
Verify portal login and console device authorization through your actual ingress.

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

### Cloudflare Tunnel example

Select the nginx image, hosts and network settings as above, plus a reviewed
`ROMD_CLOUDFLARED_IMAGE` and a tunnel token. The overlay publishes no ports:

```bash
export COMPOSE_PROJECT_NAME=romd
export COMPOSE_FILE=compose.yaml:deploy/examples/cloudflare/compose.yaml
```

Route the admin, consumer and player public hostnames to `http://ingress:80` in
your tunnel configuration. Require HTTPS at the public edge. This private
listener assumes the tunnel has terminated HTTPS and overwrites forwarded headers;
never publish it to the LAN. Use a separate TLS listener for direct LAN access.

Keep OAuth/device-flow and API requests free of interactive proxy challenges.
Bypass caching for authentication, discovery and authenticated API responses.
The player must remain accessible to its configured consumer parent. Check your
provider's current upload limits; large ROM uploads may need a direct HTTPS route.

## Fresh installation

Run from the extracted release directory, with the selected ingress configuration:

```bash
scripts/deployment/validate.sh
scripts/deployment/compose.sh pull
scripts/deployment/compose.sh up -d --wait --wait-timeout 240
scripts/deployment/verify.sh
```

The validator renders Compose without displaying secrets. It checks database
connectivity, secret/origin shape, private services and player isolation. It does
not prove firewall policy, certificate validity or external DNS configuration.

PostgreSQL initializes roles on its first boot. The worker migrates and seeds,
then publishes its startup marker. API startup waits for that marker; API
healthchecks query `/health/ready`. The worker marker proves startup completion,
not ongoing queue responsiveness. Monitor admin readiness details for degraded
worker heartbeat, outbox and disk checks; degraded readiness still returns 200.

Verify discovery advertises each intended HTTPS origin, both portals load, an
admin can sign in, the player configuration loads, and a small import/download
succeeds. Connect the console if it is part of your installation.

## Upgrade

1. Verify and stage the new release bundle in a separate directory. Preserve the
   previous bundle, image references and protected installation configuration.
2. Use the same project name, configuration and storage mapping. Validate the new
   bundle and pull its images before interrupting the running installation.
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
last, and restarts them. PostgreSQL stays running. Copy complete sets off-host
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

## Release validation

CI validates merged Compose, builds actual production images, starts a disposable
HTTPS deployment, checks portal/discovery/player routes, rejects unknown hosts,
completes admin authorization-code + PKCE login, restarts hosts, and restores a
Compose-mode backup into a second empty project. Backend integration tests cover
application behavior and the PostgreSQL role boundaries. The smoke drill does not
exercise a real Cloudflare account, public certificates, DNS or operator firewall.

Maintainers can run:

```bash
python3 scripts/deployment/test_contract.py
python3 scripts/deployment/smoke.py --build
mise run test:integration
```

The smoke command creates uniquely named disposable projects and removes only
those projects and their volumes. It never reads the installation `.env`.
