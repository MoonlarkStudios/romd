# SQLite Write Path Audit

Related issue: #8

Historical report: this captured the pre-split topology. For current ownership,
see `docs/split-host-topology.md`.

## Scope

This audit inventories write paths in the current single `Romd.Host` deployment
and assigns ownership for a future split into `Admin.Host`, `Consumer.Host`,
`Worker.Host`, and Hangfire infrastructure.

The split should make only one process responsible for each recurring or
high-volume write path.

## Current Write Inventory

| Area | Current trigger | Current writes | Proposed owner |
| --- | --- | --- | --- |
| Database startup | `SqliteWalModeInitializer` hosted service | Applies EF migrations and sets `PRAGMA journal_mode=WAL`. | `Admin.Host` only, or one-shot migration command. |
| Seed data | Admin/platform/taxonomy seeders | Identity roles, system user, default admin, platform rows, region/language rows. | `Admin.Host` only. |
| Recurring jobs | `RecurringJobRegistrar` | Writes recurring job definitions into Hangfire storage. | Hangfire infrastructure only. |
| Materialization dispatch | `MaterializationDispatcher` | Inserts `MaterializationJob` rows and enqueues work. | Worker/Hangfire only. |
| Auth login | `POST /api/auth/login` | Read-only today because lockout is disabled. | Admin and Consumer can expose login. |
| User admin | `/api/users` | Identity users, roles, library assignments. | `Admin.Host`. |
| Library admin | `/api/libraries` | Library configuration and materialization flags. | `Admin.Host`. |
| Collections | `/api/collections` writes | Collection and item mutations. | `Admin.Host`; consumer reads only. |
| Upload scheduling | `/api/upload`, `/dat`, `/rom` | Temp file, `UploadJob`, Hangfire enqueue. | Admin by default; optional Consumer staging only. |
| DAT/ROM ingestion | Upload/replace processors | CAS blobs, `Files`, DAT rows, ROM rows, taxonomy, titles, links. | Worker only. |
| Title curation | title endpoints | Metadata, external ids, media, merge/move, rematerialization flags. | `Admin.Host`; Worker for queued rematerialization/cleanup. |
| Enrichment scheduling | enrichment endpoints, upload auto-enrichment | Enrichment job rows and Hangfire enqueue. | Admin/manual or Worker/follow-up; never Consumer. |
| Enrichment execution | enrichment executors | Titles, metadata layers, media downloads, `Files`, materialization flags. | Worker only. |
| Materialization execution | `LibraryMaterializationService` | Materialized library rows and library status/counts. | Worker only. |
| Export title | `POST /api/export/title/{id}` | Read-only zip stream. | Consumer read path. |
| Export library scheduling | `POST /api/export/library` | `ExportJob` insert and enqueue. | Consumer for own library; Admin for any authorized library. |
| Export execution and cleanup | export executor/cleanup | Export zip files, progress/path updates, artifact deletion. | Worker only. |
| Job management | job archive/cancel/clear endpoints | Job state changes, Hangfire cancellation. | Consumer own jobs only; Admin/Worker for bulk cleanup. |
| File storage | `FileStorageService`, CAS | Temp files, CAS blobs, `Files`, cleanup. | Worker except narrow request staging. |
| Admin backfill | admin maintenance endpoints | Bulk catalog updates. | `Admin.Host`; rare maintenance. |

## Proposed Process Ownership

### Admin.Host

Admin owns configuration, curation, and maintenance writes:

- EF migrations and seeders, unless moved to a standalone migration command.
- User and role management.
- Library configuration.
- Platform, DAT, title, taxonomy, enrichment-default, media-curation, merge,
  move, and maintenance endpoints.
- Job request creation for admin-only work.

Admin can enqueue Hangfire work, but should not run Hangfire servers or
long-running executors.

### Consumer.Host

Consumer is primarily read-heavy. Direct writes are limited to user-scoped,
low-volume data:

- Library export request creation for the current user's library.
- Job archive/cancel for jobs created by the current user, after ownership
  enforcement exists.
- Optional upload request staging only if consumer uploads are a product
  requirement.

Consumer does not run migrations, seeders, materialization dispatch, recurring
registration, Hangfire servers, or catalog mutation commands.

### Worker.Host

Worker owns high-volume and long-running business writes:

- Upload, DAT replacement, DAT ingestion, ROM ingestion, title matching,
  taxonomy auto-creation during ingestion, and ROM/DAT link updates.
- Enrichment, media downloads, media cleanup, and library rematerialization.
- Export artifact creation and export job progress updates.
- File/CAS writes after initial request staging.
- Recurring cleanup jobs and job purging.

Worker should be the primary ROMD database writer in SQLite deployments.

### Hangfire

Hangfire owns queue and recurring-job state in its own SQLite database today
(`hangfire.db`). Keep Hangfire storage separate from the ROMD SQLite database
and make exactly one process responsible for:

- `AddHangfireServer`;
- recurring job registration;
- Hangfire dashboard, if enabled;
- Hangfire state filters that update ROMD job rows.

Admin and Consumer may use `IBackgroundJobClient` to enqueue, but should not
host queue workers.

## Consumer Write Allow-List

The explicit Consumer-host direct database write allow-list is:

1. `ExportJobs` insertion for the current user's library or accessible
   collection export request.
2. `Jobs` archive/cancel state changes for jobs owned by the current user.
3. Optional, product-dependent upload request staging: temp workspace file
   creation, `UploadJobs` insertion, and Hangfire enqueue only.

Everything else is denied on Consumer:

- no `AspNetUsers`, `AspNetRoles`, or `AspNetUserRoles` mutation;
- no `Libraries` mutation;
- no `Collections` or `CollectionItems` mutation;
- no DAT/ROM/title/platform/taxonomy/catalog mutation;
- no `Files` or CAS writes except optional upload staging handoff;
- no enrichment, materialization, recurring cleanup, seed, or migration writes;
- no Hangfire server or recurring registration.

Login remains outside the write allow-list while lockout is disabled and no
persistent session/refresh token table exists.

## SQLite Mitigations

### WAL

WAL is enabled for the ROMD database by `SqliteWalModeInitializer` at startup.
That hosted service is the current single-host owner for startup database
initialization and runs in this order:

1. apply EF migrations;
2. set `PRAGMA journal_mode=WAL`;
3. set `PRAGMA wal_autocheckpoint=1000`.

Future split hosts must keep that ownership single-process: Admin.Host or a
one-shot migration command should complete migration/WAL initialization before
Admin, Consumer, Worker, or Hangfire processes serve traffic. Do not let every
host run migration and WAL initialization concurrently.

### Busy Timeout

Every process and every SQLite connection should set a non-zero busy timeout.
ROMD EF connections use a connection interceptor that runs
`PRAGMA busy_timeout=5000` when a SQLite connection opens. Hangfire uses a
separate `hangfire.db` file and configures its sqlite-net connection factory
with `BusyTimeout=5s`.

### Checkpointing

ROMD sets `wal_autocheckpoint=1000` during startup initialization so automatic
checkpoint pressure is predictable instead of inherited from provider defaults.

Only one process should own any scheduled manual checkpointing. If WAL growth
requires active maintenance later, add a low-frequency Worker/Hangfire-owned
`PRAGMA wal_checkpoint(PASSIVE);` job. Use `TRUNCATE` only during quiet
maintenance windows, and monitor `db-wal` growth and checkpoint duration.

## Postgres Tripwire

SQLite is acceptable only for a single-node deployment where the ROMD database
file is local to one machine and Worker is the primary writer.

Before running more than one instance of any write-capable host, before running
multiple Worker/Hangfire servers against the same database, before placing the
SQLite file on NFS/SMB/cloud-synced storage, or before requiring horizontal
scale or high write concurrency, migrate ROMD persistence and Hangfire storage
to Postgres.

Treat that as a required architecture change, not a tuning exercise.
