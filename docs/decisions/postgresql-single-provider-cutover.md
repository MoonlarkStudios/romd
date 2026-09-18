# PostgreSQL Single-Provider Cutover

Status: accepted

Related issues: #125 (decision), #126 (epic), #127, #128, #129, #173

## Context

ROMD currently runs its application data and Hangfire storage in separate
SQLite databases. The single-writer model has forced serialized queues,
busy-timeout and WAL infrastructure, polling recovery for persist/enqueue
gaps, and one-machine write ownership. ROMD is pre-1.0, first-party
deployments are compose-first, and maintaining two providers would multiply
migration, test, and operational paths.

## Decision

ROMD will replace SQLite with PostgreSQL as a single-provider cutover. There
is no dual-provider runtime or indefinite dual migration history.

The supported deployment baseline is PostgreSQL 18 using the official
`postgres:18-bookworm` major tag. Patch releases within major 18 are expected
and should be applied promptly; major upgrades are explicit, tested changes
and never follow `latest`. Production release evidence records the resolved
image digest. The cluster and database use UTF-8, UTC, and deterministic `C`
collation for persisted ordering. User-facing normalized names remain
application-owned. Full-text search uses PostgreSQL's `simple` configuration
(no language stemming) over a persisted, application-normalized search
document. The shared normalizer performs Unicode-compatible decomposition,
invariant case folding, and diacritic folding before persistence, so
multilingual/accent behavior is provider-neutral without requiring
PostgreSQL's `unaccent` extension.

One database contains separate `romd` and `hangfire` schemas. A non-login
owner owns schema objects; migration/provisioning credentials are distinct
from runtime credentials. The worker is the only schema-provisioning and EF
migration owner. API hosts never prepare either schema and fail readiness
closed until the worker has published the expected schema version. Runtime
grants must still allow the transactional enqueue design in #129 to atomically
write the application job/outbox state that makes Hangfire delivery visible.

The sequence is:

1. #127 moves Hangfire storage first while the application remains on SQLite.
2. #128 creates a squashed Npgsql application baseline, migrates existing
   first-party data, and moves integration tests to real PostgreSQL.
3. #129 makes enqueue transactional/idempotent across every job family and
   removes SQLite-era runtime machinery.

## Existing-data migration

> **Superseded on 2026-09-03 (#128 Phase 3 dropped).** No deployment held data
> worth carrying over when the application database moved to PostgreSQL, so
> the transition artifact described below was never built. Pre-cutover
> installations restart from an empty database and re-import their DATs and
> ROMs; the runbook is in `docs/production-deployment.md`. The backup-set and
> restore-drill requirements further down stand and are implemented by
> `scripts/backup/` and the Testcontainers restore drill.

"Squashed baseline" describes the destination schema history, not data loss.
Existing first-party data is migrated once by a supported transition artifact
under `tools/`, separate from runtime `src/` projects. That artifact may retain
the legacy SQLite dependency after runtime SQLite packages are removed. It is
shipped for the cutover support window and retired only through an explicit
follow-up after supported installations have migrated.

Migration is offline and source-immutable: all ROMD hosts stop, a complete
cold backup of the SQLite-era data directory is taken, and the tool reads but
never mutates that source. The destination must be empty. A failed attempt is
discarded and retried into a new empty destination; partial destinations are
never resumed or served.

The tool preserves every primary key and reseeds every PostgreSQL sequence
above the imported maximum. It validates a versioned schema manifest covering
all tables, columns, converters, primary/foreign/check/unique constraints and
indexes; streaming per-table row counts and deterministic content digests;
timestamp and hash fidelity; foreign-key validity; and CAS-reference
existence. Spot checks alone are not acceptance evidence.

Cutover occurs only after the destination passes the manifest and application
smoke tests. Before PostgreSQL accepts writes, rollback restores the complete
pre-cutover SQLite data-directory backup and prior release. After PostgreSQL
accepts writes there is no reverse synchronization: rollback requires the
PostgreSQL backup/restore path (or knowingly loses post-cutover writes by
restoring the pre-cutover snapshot).

Each backup is one quiesced backup set, not unrelated database and filesystem
copies. Stop every ROMD writer, record a unique backup-set id and manifest,
capture the declarative role/schema/grant definition first, create the
PostgreSQL custom-format dump second, then snapshot CAS, configuration, server
identity, signing key, and data-protection volumes before writers restart. The
manifest records artifact digests, database/schema versions, ordering, and the
same backup-set id. `pg_dump -Fc` is not treated as a cluster-role backup;
restore recreates roles/ownership/grants from the versioned declarative
artifact before `pg_restore`.

A restore drill into empty database and filesystem volumes must reject missing
or mixed-generation backup-set artifacts, then pass schema, ownership/grants,
identity, CAS-reference, authentication, and application smoke checks before
the cutover release is accepted.

## Full-text search

SQLite FTS5 is replaced during #128 by `tsvector` projections using the
explicit `simple` configuration over the application-normalized search
document, with GIN indexes and atomic maintenance ownership. Admin and consumer
search must preserve escaping, prefix, accent folding, filtering,
deterministic tie-breaking, and accepted relevance behavior. Migration
populates the normalized document and vector before readiness becomes true,
and the 100k/1M harness records query plans, latency, result parity, and index
maintenance cost.

## Job delivery semantics

Exactly-once external execution is not promised. ROMD guarantees one durable
application job identity, a stable dispatch identity, idempotent/fenced
execution claims, and at-least-once delivery attempts. #129 must cover upload,
path import, export, enrichment, bulk enrichment, DAT replacement, and library
materialization rather than treating one dispatcher as the whole enqueue
surface.

## Rejected alternatives

- Stay on SQLite: rejected because single-writer contention and split-store
  recovery machinery are already architectural constraints.
- Move only Hangfire: useful as the first proving step, but it leaves the
  application write bottleneck and cross-store enqueue gap.
- Support SQLite and PostgreSQL indefinitely: rejected because two providers
  double migrations, query semantics, CI, troubleshooting, and compatibility
  promises before ROMD has a stable 1.0 contract.
- Replay the SQLite migration chain on Npgsql: rejected because the chain
  contains SQLite-specific DDL, FTS, rebuild, and trigger behavior. A squashed
  PostgreSQL baseline plus verified data transfer is safer and reviewable.

## Consequences

- Self-hosting requires PostgreSQL and a coordinated database/filesystem
  backup.
- Provider-specific behavior is explicit and exercised with Testcontainers.
- The Hangfire transition utility and runtime SQLite dependencies are retired
  by #129. Pre-cutover installations restart from empty PostgreSQL and data
  volumes; no supported data migration or cutover marker remains.
- PostgreSQL major upgrades and collation changes require explicit migration
  decisions; they are not image-tag side effects.
