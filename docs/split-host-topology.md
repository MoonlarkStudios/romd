# Split Host Topology

ROMD runs as separate deployables. Application data and Hangfire storage both live in PostgreSQL 18: the shared `romd` database holds the application schema `romd` and the dedicated `hangfire` schema. The data directory keeps files only (content store, media, signing key, logs).

This is the current single-node topology. It does not introduce Redis, a SignalR backplane, horizontal workers, Kubernetes, or a Hangfire dashboard replacement.

## Process Map

| Process | Owns | Does not own |
| --- | --- | --- |
| `Romd.Admin.Host` | Admin HTTP API, admin SPA/static files, identity, management endpoints, media/storage routes, SignalR hubs, Hangfire enqueue client, admin realtime outbox publishers and dispatcher | Hangfire servers, recurring job registration, job execution, materialization dispatch, database migration/provisioning, seeders |
| `Romd.Consumer.Host` | Consumer HTTP API, consumer SPA/static files, consumer identity, browse/account/collection endpoints, signed content delivery, consumer media routes | Admin endpoints, admin SignalR hubs, admin realtime outbox publishers/dispatcher, Hangfire client/server ownership, job execution, materialization dispatch |
| `Romd.Worker.Host` | Application schema migration, grants, and version publication, Hangfire PostgreSQL schema provisioning, seeders, Hangfire servers, recurring jobs, job execution services, materialization dispatch, durable metadata reconciliation, admin realtime outbox publishers, processed-outbox cleanup job | HTTP routes, SignalR hubs, SPA/static hosting |
| `romd-player` | Dedicated static browser-player origin, runtime capability JSON, CSP and frame-parent restrictions | ROMD credentials, API access, shared data, worker dependencies, EmulatorJS asset staging |

Run exactly one `Romd.Worker.Host`. The worker alone receives the Hangfire provisioning credential. API hosts never prepare schema; both fail readiness closed until the worker publishes the expected application and Hangfire schema versions.

## Shared Data

All hosts that participate in one ROMD instance must use the same `Romd:DataDirectory`.

The data directory contains:

- `dp-keys/`: shared Data Protection keyring for authentication and encrypted
  metadata-provider credentials. Admin and worker must share these keys; losing
  them requires restoring the keyring or replacing saved provider credentials.

- `keys/openiddict-signing.pem`: persisted OpenIddict token signing key, created on first start
  by whichever host comes up first.
- `logs/`: host log files.
- content and media storage managed by the infrastructure layer.

`Romd.Worker.Host` applies EF migrations as `romd_provisioner`, lays down the runtime grants, publishes the `romd.romd_schema` version, creates the OpenIddict signing key, and runs seeders. `dotnet run --project src/Romd.Worker.Host -- --migrate-database` performs only the schema steps and exits. API hosts connect with DML-only roles and never apply migrations. The signing key is created on demand by whichever host starts first; creation is atomic (temp file + rename) so concurrent hosts converge on a single key and an API host starting before the worker does not crash.

OpenIddict application, authorization, scope, and token rows live in the application database.
The worker creates the signing key, seeds the `romd-console`, `romd-admin-spa`, and
`romd-consumer-spa` clients, and prunes expired tokens and authorizations on a daily
recurring job. **Both API hosts register the OpenIddict server**: the admin host serves
authorization-code + PKCE for `romd-admin-spa`, and the consumer host serves
authorization-code + PKCE for `romd-consumer-spa` plus the device,
verification, token, and revocation endpoints for `romd-console`. Each host is bound to
its own client IDs and stamps tokens with its own audience (`romd-admin` / `romd-consumer`),
which is the boundary between surfaces given the shared keys and database. Token encryption
key material is derived from `Romd__JwtSecret`, while the asymmetric signing key is
persisted to `keys/openiddict-signing.pem` so tokens stay valid across host restarts.

Application connections use Npgsql through the `Romd` connection string (`RomdProvisioning` for the worker's schema steps). Hangfire uses PostgreSQL with sliding invisibility and a dedicated schema.

### Backup

A backup is one quiesced backup set: the declarative role/schema/grant
artifact, a PostgreSQL custom-format dump of the `romd` and `hangfire`
schemas, and a tar of the data directory (CAS content, `keys/`, `dp-keys/`,
`identity/`), bound by a manifest with digests and the published schema
versions. `scripts/backup/backup.sh` captures a set from the Compose stack
(it stops the ROMD hosts, never PostgreSQL, and starts them again);
`scripts/backup/restore.sh` rebuilds one into an empty installation and
refuses incomplete, mixed-generation, or non-empty destinations. The
Testcontainers restore drill (`BackupSetRestoreDrillTests`) runs both scripts
against a real worker-provisioned installation in CI. The operator runbook,
is in
[`production-deployment.md`](production-deployment.md#backup--restore).

## Docker Compose

The root `Dockerfile` defines four production targets:

- `romd-admin`: publishes `Romd.Admin.Host` and copies the built admin SPA into `wwwroot`.
- `romd-consumer`: publishes `Romd.Consumer.Host` and copies the built consumer SPA into `wwwroot`.
- `romd-worker`: publishes `Romd.Worker.Host` with no HTTP listener.
- `romd-player`: serves `web/packages/romd-player-app/dist` from nginx on a dedicated origin and renders its runtime configuration and security policy at startup.

The root `compose.yaml` is the image-only production topology. PostgreSQL and
worker share `backend`; API hosts join `backend` and `edge`; player joins only
`edge`. No ports or container names are fixed. Production configuration, optional
ingress examples, release bundles and backup/restore procedures are documented in
[production-deployment.md](production-deployment.md).

Use `compose.dev.yaml` for daily source development. Maintainers use
`compose.build.yaml` as an overlay when building the production images from source.
`python3 scripts/deployment/smoke.py --build` exercises the actual production
images with disposable data and HTTPS ingress.

## Local Development

Recommended modes:

1. Fast backend-only work: run the hosts locally with `dotnet run`.
2. Normal app work: run Docker backends with `compose.dev.yaml` and local Vite frontends.
3. Release smoke check: run `compose.yaml` and use the static SPAs served by the admin and consumer containers.
4. CI-ish packaging check: run Compose config/build checks and hit `/health` on the API containers.

Use a shared data directory for every terminal that runs a host:

```sh
export Romd__DataDirectory="$PWD/.data/romd"
```

`dotnet run` selects the Development environment through each host's
`Properties/launchSettings.json`; without it the hosts start as Production and
`ProductionSecretValidator` refuses the default development secrets. Published
and containerised hosts do not read that file.

### Dev Database

The PostgreSQL cutover (#126) replaces the SQLite files in the data directory
with a local PostgreSQL server: #127 moved Hangfire storage there and #128
moves the application database. `compose.db.yaml` defines the development
server — the ADR baseline `postgres:18-bookworm` with loopback-only trust auth
on `127.0.0.1:${ROMD_DB_PORT:-15432}` and data persisted in the `romd-db-data`
volume. It is development tooling only; the production service, role, and
grant topology is owned by the cutover deployment work (#127).

The mise tasks are the documented happy path:

```sh
mise run db:up              # start the server, create this checkout's database
mise run db:down            # stop the server (shared by all checkouts; data persists)
mise run db:reset           # drop/recreate this checkout's database, clear the data directory
mise run db:psql            # interactive psql against this checkout's database
mise run db:snapshot <name> # snapshot via CREATE DATABASE ... TEMPLATE (no args: list)
mise run db:restore <name>  # replace this checkout's database with a snapshot
mise run db:env             # print this checkout's connection-string exports
```

The hosts reach the dev server through `ConnectionStrings__Romd`,
`ConnectionStrings__Hangfire`, and their `*Provisioning` counterparts. `.mise.toml` sources
`scripts/db/env.sh`, which derives both from this checkout's database name, so
a shell with `mise activate` has them as soon as it enters the repository, and
`mise run`/`mise exec` apply them too. In a shell without activation, run
`eval "$(mise run db:env)"` before starting a host. The dev server has
loopback trust auth and no `romd_*` roles, so every host and the worker's
schema provisioner connect as `romd`; the production owner/provisioner/runtime
role split is exercised only by `compose.yaml` and the Testcontainers tests.

One server is shared by every checkout on the machine. Each checkout or
worktree gets its own database named from the checkout path
(`romd_<dir>_<hash>`; see `scripts/db/lib.sh`), mirroring how
`Romd__DataDirectory` already varies per checkout, so parallel worktrees with
divergent migration histories never collide. `db:down` stops the server for
every checkout; `db:reset` affects only the current checkout and is the
post-cutover equivalent of deleting the SQLite data directory: it returns the
checkout to first-run state without recreating the container. Snapshots
survive `db:reset`.

Snapshots use `CREATE DATABASE ... TEMPLATE`, which is cheap relative to
fixture regeneration; use it to keep re-runnable copies of expensive states
such as the #98 100k/1M scale-harness fixtures. Do not copy database files as
a dev backup mechanism — snapshot/restore is the supported path.

Testcontainers for .NET (introduced by #127's Hangfire PostgreSQL integration
tests and extended to the application database by #128) supports container reuse so `mise run test` /
`mise run test:integration` cold-start cost stays low on developer hardware.
Unlike the Java library, reuse is not a per-machine properties-file setting:
the fixture's container builder opts in with `.WithReuse(true)` plus a stable
container name/labels, so it ships with #128's fixture code rather than local
configuration. Reused containers are exempt from Ryuk cleanup; `docker rm -f`
them when you want a truly cold start.

Start the worker first so application migrations and grants, Hangfire schema provisioning, seed data, recurring jobs, and Hangfire servers are available. For disposable dev data, `mise run db:reset` returns the checkout to first-run state:

```sh
dotnet run --project src/Romd.Worker.Host
```

Run the admin API host on `5000`:

```sh
dotnet run --project src/Romd.Admin.Host --urls http://localhost:5000
```

Run the consumer API host on `5002`:

```sh
dotnet run --project src/Romd.Consumer.Host --urls http://localhost:5002
```

Then run the admin and consumer Vite dev servers:

```sh
cd web
pnpm install
pnpm dev
```

```sh
cd web
VITE_ROMD_CONSUMER_API_ORIGIN=http://localhost:5002 pnpm dev:consumer
```

The admin app runs on `http://localhost:5137` and proxies admin API, media, health, and SignalR hub traffic to `VITE_ROMD_ADMIN_API_ORIGIN`, defaulting to `http://localhost:5000`.

The consumer app runs on `http://localhost:5174` and proxies consumer API, media, delivery, and health traffic to `VITE_ROMD_CONSUMER_API_ORIGIN`, defaulting to `http://localhost:5000`.

For one-host-at-a-time development, the defaults still work: run either `Romd.Admin.Host` or `Romd.Consumer.Host` on `http://localhost:5000` and start the matching Vite app.

For daily full-app development, run the backend split-host topology in Docker and start Vite locally. This keeps the real worker/admin/consumer process boundaries while preserving fast frontend rebuilds, HMR, and browser source maps. The dev Compose file defaults admin to `http://localhost:11337` and consumer to `http://localhost:11338` to avoid common low-port collisions.

```sh
docker compose -f compose.dev.yaml up --build romd-worker romd-admin romd-consumer romd-player
```

Then run the admin and consumer Vite dev servers:

```sh
cd web
pnpm install
VITE_ROMD_ADMIN_API_ORIGIN=http://localhost:11337 pnpm dev
```

```sh
cd web
VITE_ROMD_CONSUMER_API_ORIGIN=http://localhost:11338 pnpm dev:consumer
```

`compose.dev.yaml` keeps the admin and consumer Vite apps on the host, while `romd-player` serves the built
static player at `http://localhost:5175`. It also runs a development-only `postgres` service (trust auth on
the internal network, no published port) that the three .NET services use for Hangfire; the worker alone
receives the provisioning connection string. The admin/consumer images use Dockerfile dev targets that skip
static SPA packaging. The file sets local-only development defaults, enables Vite CORS origins for
`http://localhost:5137` and `http://localhost:5174`, and mounts a shared
`romd-dev-data:/var/lib/romd` volume only into the .NET services.

For player-app development, run its Vite server instead of the Compose `romd-player` service. Both default
to port `5175`, so do not start both at once; alternatively set `ROMD_DEV_PLAYER_PORT` and keep
`ROMD_DEV_PLAYER_URL` plus `ROMD_DEV_PLAYER_ALLOWED_PARENTS` consistent.
The Vite server provides `/player-config.json` itself, derived from the schema-v2 pin. Its optional
`VITE_ROMD_PLAYER_EMULATORJS_DATA_PATH`, `VITE_ROMD_PLAYER_CORES`, and
`VITE_ROMD_PLAYER_ALLOWED_PARENTS` overrides follow the same validation as the container; source remains
CDN-only, and an override on the official CDN host must use the pinned version.

To use a bind-mounted local data directory instead of the named dev volume:

```sh
ROMD_DEV_DATA_MOUNT=./.data/compose docker compose -f compose.dev.yaml up --build romd-worker romd-admin romd-consumer
```

If the host reserves either dev port, set `ROMD_DEV_ADMIN_PORT` or `ROMD_DEV_CONSUMER_PORT` to another free host port and use that value for the matching Vite origin.

Full frontend-in-Docker development is intentionally not the daily path. Containerized Vite adds file-watch, install, volume-performance, and port/env complexity; keep it as an optional experiment or one-off check, not the main loop. Static SPAs served by the production-like API containers are same-origin and do not need CORS.

## Production Shape

Production operators use published images and release bundles (see the production runbook).
For maintainers building those images, the root `Dockerfile` has these targets:

```sh
docker build --target romd-worker -t romd-worker:local .
docker build --target romd-admin -t romd-admin:local .
docker build --target romd-consumer -t romd-consumer:local .
docker build --target romd-player -t romd-player:local .
```

For non-container builds, publish each deployable from the same revision:

```sh
dotnet publish src/Romd.Worker.Host/Romd.Worker.Host.csproj -c Release
dotnet publish src/Romd.Admin.Host/Romd.Admin.Host.csproj -c Release -p:BuildFrontend=true
dotnet publish src/Romd.Consumer.Host/Romd.Consumer.Host.csproj -c Release -p:BuildFrontend=true
```

`-p:BuildFrontend=true` builds the relevant Vite SPA and copies it into the host `wwwroot`. Vite dev servers are only for local development.

A production deployment should expose `Romd.Admin.Host`, `Romd.Consumer.Host`, and `romd-player` on
separate origins. Keep the player and consumer cross-origin but same-site (for example,
`https://console.example.com` and `https://play.example.com`) so the player cannot read portal credentials
while its iframe storage remains persistent. Cross-site player hosting can partition or make saves
ephemeral. `Romd.Worker.Host` should run as a background service with no public port.

Configure at least:

The complete configuration reference, including JSON structure, defaults, and
environment-variable equivalents, is in [`configuration.md`](configuration.md).

- `Romd__DataDirectory`: shared durable data directory used by all hosts.
- `Romd__JwtSecret`: production secret with at least 32 characters; also derives
  OpenIddict token encryption key material. The worker creates the OpenIddict signing key
  under `Romd__DataDirectory` (`keys/openiddict-signing.pem`); keep that directory durable
  so tokens survive host restarts.
- `Romd__DefaultAdminEmail` and `Romd__DefaultAdminPassword`: initial admin bootstrap credentials.
- `Romd__AdminHost__CorsOrigins__0`: admin browser origin when hosted separately.
- `Romd__ConsumerHost__CorsOrigins__0`: consumer browser origin when hosted separately.
- `Romd__AdminHost__JwtAudience` and `Romd__ConsumerHost__JwtAudience`: optional per-surface token audiences (default `romd-admin` / `romd-consumer`).
- `Romd__Auth__AdminSpa__RedirectUris__0` and `Romd__Auth__ConsumerSpa__RedirectUris__0`: SPA auth-code callback URIs (plus matching `__PostLogoutRedirectUris__0`). Must exactly match the SPA origins; dev defaults are `http://localhost:5137/auth/callback` and `http://localhost:5174/auth/callback`.
- `Romd__ConsumerDelivery__SigningKeyId`, `Romd__ConsumerDelivery__SigningSecret`, and `Romd__ConsumerDelivery__SignedUrlTtlMinutes`: required for signed consumer delivery links.
- `Providers__Igdb__ClientId` and `Providers__Igdb__ClientSecret`: optional deployment override, identical on admin and worker. Otherwise configure IGDB at runtime in Admin Settings → Metadata Providers.

When the API hosts run behind a reverse proxy (TLS termination, different public origin), enable
forwarded-header processing with `Romd:ForwardedHeaders` and set each surface's public origin with
`Romd:AdminHost:PublicUrl` / `Romd:ConsumerHost:PublicUrl`. Do **not** set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED` — the hosts call `UseForwardedHeaders` themselves, and the built-in
env-var path trusts only loopback, which a container proxy is not. `Romd:ForwardedHeaders:Enabled=true`
must be paired with a trusted-proxy allowlist (`KnownProxies`/`KnownNetworks`) or the host refuses to start.
The portable deployment and optional HTTPS/Cloudflare ingress examples are in [`production-deployment.md`](production-deployment.md).

The default development JWT secret and default admin password are not production-safe.

## Admin Realtime Relay

Admin realtime notifications from both API mutations and worker jobs are bridged through the application database's outbox table:

1. `Romd.Admin.Host` and `Romd.Worker.Host` publish admin realtime intents into `AdminRealtimeOutboxEvents`.
2. The admin host runs `AdminRealtimeOutboxDispatcher`; the worker is publisher-only.
3. Dispatchers claim pending rows, deliver them through concrete SignalR relay helpers using the existing hub contracts, and mark rows processed.
4. The worker's recurring cleanup job deletes processed rows older than 7 days.

Current relayed events are:

- `JobUpdated`
- `TitleEnriched`
- `StorageStatsChanged`
- `LibraryUpdated`
- `CoverageStatsChanged`
- `HealthStatsChanged`

Standalone notifier-port publication failures, currently used by worker job notifications, are best-effort: the job continues and the failure is logged. Transactional mutation handlers do not use those self-persisting ports; they enlist `IAdminEventOutbox` in their owning unit of work. If an API host is down, pending rows remain in PostgreSQL until a dispatcher comes back. If a SignalR send fails, the dispatcher leaves the row unprocessed and retries after 30 seconds. The claim lease is 1 minute and dispatcher batches are 50 rows.

This relay preserves single-node admin notifications after the worker split. It is not a distributed messaging layer, does not fan out across multiple machines, and should be treated as one active admin realtime surface at a time.

## Durable Metadata Reconciliation

The worker also starts `MetadataRematerializationWorker` after schema provisioning.
Admin mutations commit required metadata work into `MetadataRematerializationRequests`
in the same transaction as their data changes. The reconciler uses fresh service
scopes and acknowledges only completed requests; failures remain for retry and a
restart can repeat completed but unacknowledged work. It requires the existing
single-worker topology and has no distributed claim protocol. See
[backend mutation conventions](backend-mutations.md) for transaction composition,
revision tokens, retry behavior, and the schema migration deployment requirement.

## Hangfire Operations

`Romd.Worker.Host` is the only process that provisions the Hangfire schema, starts Hangfire servers, and registers recurring jobs. The Hangfire and application schema provisioners are its first hosted services, so recurring-job registration, dispatchers, and servers only ever start against provisioned schemas. The admin host only enqueues work; the consumer has no Hangfire client. Runtime storage uses `PrepareSchemaIfNecessary=false` everywhere.

Queue counts are operation-bound, with PostgreSQL overlap evidence from #193:

| Queue | Workers | Evidence and remaining boundary |
| --- | --- | --- |
| `default` | `Environment.ProcessorCount` | Unchanged by #193. |
| `upload` | 1 | Two distinct replacement jobs for one source conflict at `IX_DatFiles_Pending_SourceId` when one has ingested its pending version but has not activated it. The loser's attempt fails in ingestion and needs retry; the resumable runner preserves its phase. Serialized ingest/activation lets both succeed. The winner's lifecycle changes, catalog Dirty state, library flags and outbox commit consistently. Keep one worker until source-scoped coordination spans ingestion through activation. |
| `enrichment` | 1 | A single-title job and a bulk job can load the same title before either saves new provider evidence. Their separate job fences permit both mutations; the second insert fails at `IX_TitleMetadataLayers_TitleId_SourceId` and the bulk job records the item failure. The serialized control succeeds. Title/evidence coordination is needed before increasing this queue. |
| `materialization` | 2 | Distinct jobs over a shared catalog publish independent per-library projections, including different library filters. The active-library index refuses a second active job for the same library; after cancellation permits a successor, the old execution fence rejects late publication. Reflagging during computation rejects the old compare token and a subsequent job converges. |

These are `QueueSafety_*` tests in
`tests/Romd.Infrastructure.Tests/AdminMutationOutboxAtomicityTests.QueueSafety.cs`,
`AdminMutationOutboxAtomicityTests.QueueFiles.cs` and
`AdminMutationOutboxAtomicityTests.QueueMaterialization.cs`. They use distinct
persisted jobs, real PostgreSQL repositories/transactions and execution fences;
external provider responses and DAT parsing/storage are controlled fixtures.
They exercise operation overlap directly, without transport scheduling. The
move-mode cases also verify that persisted dispositions, manifests and workspace
cleanup remain job-scoped when imports share original files. An already-removed
source retains the second successful import's manifest for the existing bounded
retry/age-out policy; rejected originals remain intact.

Materialization publishes `MaterializedLibraryTitles` and
`MaterializedLibraryReleases` with the library generation and outbox in one
transaction. It does not replace a shared manifest/CAS artifact. Keep the
active-library index, execution fence and configuration/reflag compare token;
raising the worker count does not replace any of those protections.

The 2026-09-05 local comparison used PostgreSQL 18 in Docker, eight libraries
sharing 1,000 titles/releases, and the real candidate reader, projection builder,
fenced publication, outbox and job checkpoints. After excluding one warmup
initial publication, six alternating warm rebuild trials (1, 2, 2, 1, 1, 2 workers)
measured 505.3/317.4/311.4 ms with one worker and 224.8/215.9/169.7 ms with two.
Median throughput rose from 25.21 to 37.05 jobs/s (about 1.47x). Every trial
verified all 16,000 projection rows and every library's generation/flag; no jobs
failed. PostgreSQL `pg_stat_activity` sampling at approximately 5 ms intervals
observed zero lock-blocked sessions in 261 warm-trial samples. Sampling can miss
short waits; these are local warm-rebuild results, not NAS capacity or hardware
beta acceptance. There is no timing threshold in CI.

Reproduce the operation matrix and detailed measurement output with:

```sh
dotnet test tests/Romd.Infrastructure.Tests/Romd.Infrastructure.Tests.csproj \
  --filter FullyQualifiedName~QueueSafety --logger 'console;verbosity=detailed'
```

Raise another queue count only with passing operation tests and measured
throughput/contended-write evidence. Same-job delivery recovery alone does not
establish distinct-job commutativity.

Force-materialization requests follow the same ownership boundary. The admin
host commits the library rematerialization flag, one pending
materialization job (including the requesting user), and the `LibraryUpdated`
outbox intent in one transaction. Intentionally, a force request on a library
with invalid configuration now commits an observable job whose execution
represents the invalid state in materialized projections, instead of the
previous silent no-op after the 202. Its exact-job enqueue call is only an
acceleration after that commit. `JobDispatchWorker` runs only in
`Romd.Worker.Host` and delivers all seven job families through the same durable
`romd."JobDispatches"` record. EF stages this record alongside every new Pending
job, so the caller's application transaction commits both or neither. The job
ID is the stable dispatch identity; Hangfire delivery IDs are transport details
and can differ across attempts. Upload/import acceptance responses retain the
`backgroundJobId` string field but return the stable dispatch identity, not an
as-yet-uncreated Hangfire record. Follow `statusUrl` for application job status.

The dispatcher reads at most 25 candidates every second, claims each for one
minute, and retries failures after 30 seconds. An expired execution lease also
makes a delivered intent eligible again: a shutdown redelivery that encounters
a still-live lease cannot permanently strand the application job. Normal live
and terminal executions are not redispatched. Each message has an isolated
scope, attempt count, last error, availability time, claim lease, and delivery
timestamp for read-only operator inspection. Duplicate Hangfire records are
expected after a visibility/acknowledgement crash; execution claims and fenced
checkpoints make the application job authoritative. Cancellation persists a
terminal state and clears its fence before best-effort transport deletion.
Expired-execution redispatch is throttled to once per minute per job while it
waits in a busy queue. Acceptance deduplication locks are transaction-scoped:
single enrichment uses the title key, bulk enrichment the platform key, and
export the existing global active-export key. Materialization retains its
unique active-library index. These are request-acceptance guarantees, not proof
that different jobs' underlying operations commute; the queue-specific #193
overlap evidence above determines which operations may run concurrently.
The migration backfills non-terminal pre-upgrade jobs, preserving recorded
transport identities and making expired execution leases recoverable.
Do not add a second dispatcher to an API host.

`LibraryMaterializationReconciler` retains one separate scheduling invariant:
a flagged library with no active job needs a new job once its dependent catalog
is Clean. It does not recover or enqueue existing jobs. Catalog invalidation can
legitimately precede catalog rebuilding, so this flag-to-job reconciliation is
distinct from transport recovery. Both former family-specific pending-job
recovery dispatchers have been removed.

DAT deletion follows the same durable-state boundary. The handler commits the
DAT removal, the platform's Dirty catalog projection state, the affected
libraries' rematerialization flags, and the stats outbox intents in one
transaction; no catalog rebuild or stored-file deletion happens in the request.
`CatalogProjectionRecoveryDispatcher` runs only in `Romd.Worker.Host`; it
rebuilds Dirty/Failed platform projections at least once (Clean state commits
atomically with the rebuilt rows) and flags affected libraries after each
successful rebuild. Library materialization — both dispatch and execution — is
gated while any dependent platform projection is non-Clean, so a stale catalog
is never materialized; the flagged library recovers on the next dispatch pass
after the projection returns to Clean. Orphaned stored files left behind by a
deleted DAT are reclaimed by the recurring unreferenced-file cleanup.

DAT replacement convergence is owned by the worker host (`Romd.Worker.Host`)
through the recurring `dat-replacement-convergence-sweep` job, registered
by `RecurringJobRegistrar` on the same 30-minute cadence as the orphaned-job
cleanup. Its three idempotent duties converge any interrupted replacement
without operator repair: stale non-terminal `ReplaceDatJob`s with no checkpoint
progress for 30 minutes are terminally Failed with their recorded attempt
errors preserved; abandoned PendingActivation DAT versions older than 24 hours
that no non-terminal replace job still references are deleted (the old version
stays Active — the design's known repairable state); and superseded retention
(newest one per source) is re-run as a backstop for historical states and a
false-to-true race after its read-only preflight. Normal activation applies the
same retention policy inside its lifecycle/Dirty/materialization/outbox transaction,
so a retention failure rolls activation back for retry. The sweep only releases database rows; freed stored-file
references are reclaimed by the existing daily unreferenced-file cleanup. API
hosts never run the sweep; do not register it outside
`AddRomdRecurringJobs`.

The Hangfire dashboard is intentionally absent. Use host logs, health checks, and read-only PostgreSQL inspection for queue diagnostics.

## Health And Verification

API hosts expose two anonymous health endpoints. The worker exposes neither: it stays non-HTTP, and its readiness marker file remains the container-level gate.

- `GET /health` is liveness: always 200 while the process serves HTTP, with no dependency checks.
- `GET /health/ready` is readiness: it evaluates dependency checks and returns 200 with overall status `ready` or `degraded`, or 503 with `unready`. The body is minimal (`status` plus per-check `name`/`status` as `healthy`/`degraded`/`unready`) and the endpoint is excluded from every OpenAPI document.
  - Critical checks (failure or a 2-second timeout makes the host `unready`): `database` (application PostgreSQL reachable with no pending migrations), `application-schema` (worker-published application schema version matches the host's expectation), and, on both API hosts, `hangfire-storage` (PostgreSQL reachable with the exact worker-published schema version).
  - Degraded checks (failure only degrades, still 200): `outbox` (admin realtime outbox has an unprocessed row older than 5 minutes; admin only), `worker` (newest Hangfire server heartbeat older than 3 minutes; admin only), `disk` (less than 1 GiB free on the data-directory volume), and `storage` (content-store root missing).
  - The consumer host evaluates `database`, `application-schema`, `disk`, `storage`, and `hangfire-storage`.
  - One full evaluation is bounded to 5 seconds overall (2 seconds per check within it); checks that cannot run or finish inside the budget count as failed per their criticality. Results are cached for about 5 seconds and evaluated single-flight, so probe storms do not multiply database hits. Overall-state transitions are logged once at Information.

Useful verification commands:

```sh
dotnet build
dotnet test tests/Romd.Infrastructure.Tests/Romd.Infrastructure.Tests.csproj --filter "RomdHostingBoundaryTests|Realtime|HangfireJobStateSyncFilterTests|RecurringJobRegistrarTests|JobRunnerTests"
dotnet test tests/Romd.Hosting.IntegrationTests/Romd.Hosting.IntegrationTests.csproj --filter "ConsumerBoundaryTests|OpenApiBoundaryTests|HostDeployableSmokeTests|RomdHostConfigurationTests|SignalRAdminRealtimeEventSinkTests"
dotnet test --no-build
```

The boundary tests assert that the worker owns background execution, API hosts stay enqueue-only, and the consumer host does not register admin realtime relay services.

Container verification when Docker is available:

```sh
python3 scripts/deployment/test_contract.py
python3 scripts/deployment/smoke.py --build
```

The smoke drill includes readiness, HTTPS discovery, PKCE login, restart and
Compose-mode backup/restore. See the production runbook for operator checks.

### Automatic DAT subscription checks

The worker registers `dat-subscription-checks` every 15 minutes. Each sweep selects
at most 25 due subscriptions, checks them independently through the same signed
reader and validation path as manual checks, and never activates a candidate.
Normal checks are scheduled 24 hours apart. Failures back off from 1 hour to a
maximum of 24 hours; manual retry remains available. Pending reviews, running
imports, inactive sources, and explicitly removed systems are excluded. Due state
is rechecked under the existing catalog/source locks, so overlapping sweeps and
manual checks cannot overwrite a newer result.

Standard admin and worker images both contain the pinned catalog reader and
public trust root. Custom deployments must configure `DatSubscriptions` on both
hosts, with the shared ROMD data directory. Disabling the reader on the worker
stops scheduled checks. Keep host configuration consistent so the admin can
accurately display availability. No signing private keys belong on either host.
The consumer still has no catalog reader. Migration 8 persists next-check times
and consecutive failure counts; existing subscriptions use their last check plus
24 hours until their first scheduled result is recorded.

Same-platform catalog rebuilds hold the platform row lock before reading source
state. A savepoint protects the installed projection while failure/cancellation
status commits under that same lock. This prevents an older failed attempt from
overwriting a newer successful rebuild; unrelated platforms keep separate locks.

### Administration lifecycle upgrade (schema 21)

Deploy the worker, admin host, and consumer host together for the Administration
lifecycle change. Start the worker first: it adds account activation/suspension and
last-sign-in fields, single-use account links, and administrative audit evidence.
Readiness remains closed until the expected schema is installed. Preserve existing
database and content volumes.

The shared token validator now requires a current account security stamp. Users
with tokens issued before this upgrade must sign in again. Suspension, password,
role, library, and session changes invalidate subsequent requests and refreshes
across hosts; work already accepted by a server may finish. Consumer persistence
can update LastSignedInAt but cannot access AccountLinks or AdminAuditEvents.
