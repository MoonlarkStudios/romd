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

Library publication is explicit: the admin accepts a refresh and the worker
materializes it. Pending edits preserve the last published consumer snapshot.
See [library publication](library-publication.md) for availability, invalidation,
counts and concurrency boundaries.

The worker checks and provisions the application schema before Hangfire schema
provisioning. An incompatible or unversioned application schema therefore stops
normal worker startup before either provisioner writes.

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
The worker creates the signing key, seeds the `romd-console`, `romd-mount`, `romd-admin-spa`, and
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
[`production-deployment.md`](../self-hosting/production-deployment.md#backup-and-restore).

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
[production-deployment.md](../self-hosting/production-deployment.md).

Use `compose.dev.yaml` for daily source development. Maintainers use
`compose.build.yaml` as an overlay when building the production images from source.
`python3 scripts/deployment/smoke.py --build` exercises the actual production
images with disposable data and HTTPS ingress.

## Local Development

[Source development](../development/getting-started.md) owns initial machine setup, Docker/Podman
selection, pinned Compose, managed startup, port overrides, logs, and shutdown.
Use `mise run dev` (also `dev:source`) for worker-first local .NET hosts and Vite.
Use `mise run dev:containers` explicitly for container-backed hosts and local
portal Vite servers. Linked worktree ports and status discovery are described
in the development guide.
The same host ownership and readiness boundaries apply to both modes.
Production Compose and release checks still require Docker.

### Dev Database

`compose.db.yaml` provides the source-mode development PostgreSQL 18 server.
It binds to `127.0.0.1:${ROMD_DB_PORT:-15432}`, uses development-only loopback
trust authentication, and persists data in `romd-db-data`. The selected engine
owns this storage; changing engines does not move it. One server is shared by
checkouts using that engine, with a separate database derived from each
checkout's canonical path (`romd_<dir>_<hash>`; `scripts/db/lib.sh`).

```sh
mise run db:up              # start the shared server and ensure this checkout's database
mise run db:down            # stop the shared server; preserve data
mise run db:reset           # reset this checkout's database and data directory
mise run db:psql            # open psql against this checkout's database
mise run db:snapshot <name> # snapshot with CREATE DATABASE ... TEMPLATE; no args lists snapshots
mise run db:restore <name>  # replace this checkout's database from a snapshot
mise run db:env             # print this checkout's connection-string exports
```

`db:down` affects all checkouts using the shared server. `db:reset` affects only
this checkout; snapshots survive it. Snapshot/restore is the supported dev
backup route; do not copy PostgreSQL data files.

`.mise.toml` sources `scripts/db/env.sh` to derive `ConnectionStrings__Romd`,
`ConnectionStrings__Hangfire`, and their provisioning counterparts without
contacting an engine. `mise run` and `mise exec` receive these exports even
before PostgreSQL starts. The dev server uses the `romd` user for all hosts and
provisioning. Production role/grant boundaries are exercised by production
Compose and Testcontainers fixtures, not this development-only service.

### Manual Source Hosts

Prefer managed startup. When deliberately supervising individual hosts, first
complete [environment setup](../development/getting-started.md), then start the database and use
the same absolute data directory in every host terminal:

```sh
mise run db:up
export Romd__DataDirectory="$PWD/.data/romd"
mise exec -- dotnet run --project src/Romd.Worker.Host
```

Wait for worker provisioning/seeders before starting APIs in separate terminals:

```sh
export Romd__DataDirectory="$PWD/.data/romd"
mise exec -- dotnet run --project src/Romd.Admin.Host --urls http://localhost:5000
```

```sh
export Romd__DataDirectory="$PWD/.data/romd"
mise exec -- dotnet run --project src/Romd.Consumer.Host --urls http://localhost:5002
```

Preserve an existing durable data path when one is already configured. These
commands use each host's Development launch profile. Published/container hosts
do not read launch profiles. Run local portals/player following
[web development](../../web/docs/development.md). The admin Vite default API origin is
`http://localhost:5000`; use `VITE_ROMD_CONSUMER_API_ORIGIN=http://localhost:5002`
for the split consumer host.

Testcontainers fixtures can reuse containers when their builders explicitly
opt in with stable names/labels. This is not a Java-style per-machine setting.
Reused containers are exempt from Ryuk cleanup; inspect their ownership before
removing one through the selected engine. See the development guide for scoped
Testcontainers endpoint configuration.

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
environment-variable equivalents, is in [`configuration.md`](../self-hosting/configuration.md).

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
- `Providers__Igdb__ClientId` and `Providers__Igdb__ClientSecret`: optional deployment override, identical on admin and worker. Otherwise configure IGDB at runtime in Integrations → IGDB.

When the API hosts run behind a reverse proxy (TLS termination, different public origin), enable
forwarded-header processing with `Romd:ForwardedHeaders` and set each surface's public origin with
`Romd:AdminHost:PublicUrl` / `Romd:ConsumerHost:PublicUrl`. Do **not** set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED` — the hosts call `UseForwardedHeaders` themselves, and the built-in
env-var path trusts only loopback, which a container proxy is not. `Romd:ForwardedHeaders:Enabled=true`
must be paired with a trusted-proxy allowlist (`KnownProxies`/`KnownNetworks`) or the host refuses to start.
The portable deployment and optional HTTPS/Cloudflare ingress examples are in [`production-deployment.md`](../self-hosting/production-deployment.md).

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

`LibraryUpdated` payloads are schema version 2, which carries the library's opaque Sqid instead of its integer id. The dispatcher translates pending version-1 rows to the version-2 payload at dispatch, and a failed delivery or acknowledgement retries the same deterministic translation. Remove that translation only after a release that writes version 2 is the minimum supported direct upgrade baseline, the processed-row retention interval has elapsed, and every supported deployment reports zero unprocessed version-1 `LibraryUpdated` rows.

`JobUpdated` payloads use schema version 2 for the metadata-rematerialization
member and the artwork-completion row's persisted `mode`. Canonical job reads,
ordinary worker notifications, and the exhausted-transport filter map that mode
from the same stored scope without reading the expensive summary. The admin
job collection is the paged `/api/jobs`; its typed item feeds and expensive
`artwork-summary` projection are job sub-resources. Polling the canonical job
remains sufficient, and the realtime payload stays at version 2. Pending
version-1 rows keep their persisted version at dispatch. Artwork rows whose
version-1 payload omitted mode remain unknown, including old enrichment runs;
missing/unknown mode is nullable and omitted on serialization rather than
invented as Artwork. No version-1 translation is performed.

Standalone notifier-port publication failures, currently used by worker job notifications, are best-effort: the job continues and the failure is logged. Transactional mutation handlers do not use those self-persisting ports; they enlist `IAdminEventOutbox` in their owning unit of work. If an API host is down, pending rows remain in PostgreSQL until a dispatcher comes back. If a SignalR send fails, the dispatcher leaves the row unprocessed and retries after 30 seconds. The claim lease is 1 minute and dispatcher batches are 50 rows.

This relay preserves single-node admin notifications after the worker split. It is not a distributed messaging layer, does not fan out across multiple machines, and should be treated as one active admin realtime surface at a time.

## Durable Metadata Reconciliation

A system metadata-policy save commits a `metadata-rematerialization` Jobs row,
automatic durable dispatch, and required new-job event together with the policy.
The worker runs this resumable pass on the existing single-worker enrichment
queue. It checkpoints bounded keyset progress after each committed title or
fallback per-title intent. Exhausted transport retries rearm dispatch after 30
seconds while preserving unfinished phase and progress; live leases and stale
deliveries cannot steal ownership. Recovery failure rolls back to the previous
claim so expiry recovery remains available. Policy jobs cannot be cancelled.

The worker also starts `MetadataRematerializationWorker` after schema provisioning
for internal per-title convergence. It uses fresh scopes and acknowledges only
completed intents; failures remain for indefinite retry, and a restart can repeat
unacknowledged work. It requires the existing single-worker topology and has no
distributed claim protocol. Global/platform parent intents are migrated into
per-title intents; new policy work uses Jobs instead. See
[backend mutation conventions](backend-mutations.md) for transaction composition,
revision tokens, retry behavior, and schema deployment requirements.

## Hangfire Operations

`Romd.Worker.Host` is the only process that provisions the Hangfire schema, starts Hangfire servers, and registers recurring jobs. The Hangfire and application schema provisioners are its first hosted services, so recurring-job registration, dispatchers, and servers only ever start against provisioned schemas. The admin host only enqueues work; the consumer has no Hangfire client. Runtime storage uses `PrepareSchemaIfNecessary=false` everywhere.

`JobRunner<TJob>` owns the shared claim, fence, heartbeat, checkpoint, deferral
and notification mechanics for every job type. Family behavior is composed per
job type from four Application lifecycle capabilities in
`Romd.Admin.Application/Ingestion/Jobs`: retry budget, durable cancellation
signal, recovery/termination reconciliation and post-completion cleanup. Each
falls back to `DefaultJobLifecyclePolicy<TJob>`. Family implementations live in
`Romd.Infrastructure/Jobs/Lifecycle` and are registered only through
`AddRomdJobExecution` (`AddRomdJobLifecyclePolicies`), which admin and consumer
compositions never call; `JobLifecyclePolicyArchitectureTests`
keeps concrete job families out of the runner and the policies out of API hosts.

Queue counts are operation-bound, with PostgreSQL overlap evidence from the `QueueSafety_*` tests:

| Queue | Workers | Evidence and remaining boundary |
| --- | --- | --- |
| `default` | `Environment.ProcessorCount` | Unchanged by the overlap evidence. |
| `upload` | 1 | Two distinct replacement jobs for one source conflict at `IX_DatFiles_Pending_SourceId` when one has ingested its pending version but has not activated it. The loser's attempt fails in ingestion and needs retry; the resumable runner preserves its phase. Serialized ingest/activation lets both succeed. The winner's lifecycle changes, catalog Dirty state, library flags and outbox commit consistently. Keep one worker until source-scoped coordination spans ingestion through activation. Ingests that mint unregistered region or language keys serialize on one database lock held until each commits, and one upload worker keeps that lock uncontended. |
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
host commits a new refresh revision and update timestamp, one pending
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
and can differ across attempts. Background-work acceptance responses return only the stable application `jobId`
in a `JobReference` body and `Location: /api/jobs/{jobId}`. Poll that resource for
completion; SignalR can accelerate updates. The 202 carries no `ETag`.

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
that different jobs' underlying operations commute; the queue-specific
overlap evidence above determines which operations may run concurrently.
Do not add a second dispatcher to an API host.

Library publication is explicit. Invalidation marks affected libraries but does
not schedule a new materialization job; administrators choose **Refresh library**
after catalog projections are Clean. The durable dispatcher recovers already
accepted jobs. No `LibraryMaterializationReconciler` is registered.

DAT deletion follows the same durable-state boundary. The handler commits the
DAT removal, the platform's Dirty catalog projection state, the affected
libraries' rematerialization flags, and the stats outbox intents in one
transaction; no catalog rebuild or stored-file deletion happens in the request.
`CatalogProjectionRecoveryDispatcher` runs only in `Romd.Worker.Host`; it
rebuilds Dirty/Failed platform projections at least once (Clean state commits
atomically with the rebuilt rows) and flags affected libraries after each
successful rebuild. Library materialization execution is gated while any dependent
platform projection is non-Clean, so a stale catalog is never materialized. An admin
refreshes the flagged library after the projection returns to Clean. Orphaned stored files left behind by a
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
mise exec -- python3 scripts/validation/run.py --id deployment.contracts
python3 scripts/deployment/smoke.py --build
```

The Compose contract check also runs with Podman through the mise-pinned Compose
client; `mise run contracts:check` adds the CHD harness and OpenAPI drift checks.

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
The consumer has no catalog reader. Subscription next-check times and failure
counts are persisted in the current fresh baseline.

Same-platform catalog rebuilds hold the platform row lock before reading source
state. A savepoint protects the installed projection while failure/cancellation
status commits under that same lock. This prevents an older failed attempt from
overwriting a newer successful rebuild; unrelated platforms keep separate locks.

### Account lifecycle

The current baseline includes activation/suspension, last-sign-in fields,
single-use account links and administrative audit evidence. The shared token
validator requires a current account security stamp. Suspension, password, role,
library and session changes invalidate subsequent requests and refreshes across
hosts; already accepted work may finish. Consumer persistence can update
LastSignedInAt but cannot access AccountLinks or AdminAuditEvents.

Follow the [supported database boundary](../self-hosting/configuration.md#database-and-installation-boundary)
for worker-first deployment. Retired feature migrations are not an upgrade path.
