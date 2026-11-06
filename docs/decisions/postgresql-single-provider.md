# PostgreSQL Single Provider

Related decisions: [host plane ownership](plane-ownership.md),
[admin use-case, transaction and domain-event boundaries](admin-use-case-transaction-event-boundaries.md)

## Context

ROMD's application data, job storage and search state must commit, recover
and restore together. A single-writer embedded database forced serialized
queues, busy-timeout and checkpoint machinery, polling to recover gaps
between persisting work and enqueuing it, and one-machine write ownership.

ROMD is pre-1.0 and compose-first. Supporting a second provider would double
migrations, query semantics, tests, troubleshooting and compatibility
promises. Ordering, search and identity must behave the same on every
installation, independent of the host's locale or installed extensions. A
backup is useful only when the database and the files restore to one
consistent point.

## Decisions

### One Provider

- PostgreSQL is ROMD's only database provider, for application data and
  Hangfire storage alike.
- There is no dual-provider runtime, and there is one migration history.

### Supported Baseline

- **PostgreSQL 18** from the official `postgres:18-bookworm` major tag. Patch
  releases within the major are applied promptly. A major upgrade is an
  explicit, tested change and never follows `latest`. Release evidence
  records the resolved image digest.
- **Deterministic locale.** The cluster and database use UTF-8, UTC and the
  `C` locale for persisted ordering. Normalizing names for users is the
  application's job, not the collation's.

### Search Is Provider-Neutral

- Full-text search uses PostgreSQL's `simple` configuration, with no language
  stemming, over a persisted, application-normalized search document.
- `SearchTextNormalizer` applies Unicode compatibility decomposition,
  invariant case folding and diacritic folding before persistence, so accent
  handling needs no `unaccent` extension.
- Why: search behaves the same for every language and installation, and
  depends on no optional extension.

### Schemas And Roles

- **Two schemas, one database.** `romd` holds application data and
  `hangfire` holds job storage.
- **Separated credentials.** A non-login owner role owns every schema object.
  Provisioning credentials are distinct from runtime credentials, and each
  host connects with its own runtime role.
- **The worker owns the schema.** It is the only schema-provisioning and EF
  migration owner. API hosts never prepare either schema, and their readiness
  fails closed until the worker publishes the expected schema version.
- **Grants allow transactional enqueue.** A runtime role that records an
  application job or outbox row can make the matching Hangfire delivery
  visible in the same transaction.

### Backups Are One Quiesced Set

- **One set per backup.** A backup is one set, not unrelated database and
  file copies. Every ROMD writer stops for the capture, and the set carries a
  unique backup-set id.
- **Capture order.** The declarative role, schema and grant definition is
  captured first and the PostgreSQL custom-format dump second. CAS,
  configuration, server identity, signing key and data-protection volumes
  follow before writers restart.
- **The manifest comes last.** It records the backup-set id, artifact digests,
  database and schema versions, and ordering. A set without a manifest is an
  aborted capture.
- **Roles are restored from their definition.** `pg_dump -Fc` is not a
  cluster-role backup, so restore recreates roles, ownership and grants from
  the declarative definition before `pg_restore`.
- **Restore targets empty volumes.** It rejects missing or mixed-generation
  artifacts, and it passes schema, ownership, identity, CAS-reference,
  authentication and application smoke checks before the installation
  serves.

### Job Delivery Is At Least Once

- Exactly-once external execution is not promised.
- ROMD guarantees one durable application job identity, a stable dispatch
  identity, idempotent or fenced execution claims, and at-least-once delivery
  attempts.
- Every job family enqueues transactionally with its durable state.

## Trade-offs

- **Self-hosting requires PostgreSQL** and a coordinated backup set; copying
  the data directory is not a backup.
- **Major upgrades are migrations.** A PostgreSQL major upgrade or a collation
  change needs explicit migration work, never an image-tag bump alone.
- **No stemming.** A search for "run" does not match "running".
- **Handlers see repeats.** At-least-once delivery means every job handler
  tolerates a repeated attempt.

## Rejected Alternatives

- **Stay on SQLite.** Rejected: single-writer contention and split-store
  recovery machinery were already architectural constraints.
- **Move only Hangfire storage.** Rejected as an end state: it leaves the
  application write bottleneck and the gap between persisting and enqueuing
  work.
- **Support SQLite and PostgreSQL indefinitely.** Rejected: two providers
  double migrations, query semantics, CI, troubleshooting and compatibility
  promises before ROMD has a stable 1.0 contract.
- **Replay the SQLite migration chain on Npgsql.** Rejected: the chain held
  SQLite-specific DDL, full-text search, table rebuilds and triggers. A
  squashed PostgreSQL baseline is safer and reviewable.
- **A tool that migrates existing SQLite data.** Rejected: no deployment held
  data worth carrying over, so installations from before PostgreSQL restart
  from an empty database and re-import their DATs and ROMs.
- **Language stemming or the `unaccent` extension.** Rejected: application
  normalization keeps search provider-neutral and the same for every
  language.

## Guardrails

- **`PersistenceBoundaryArchitectureTests`:** EF Core, provider and
  `RomdDbContext` references and raw SQL sites stay inside an exact,
  reviewed inventory.
- **`PostgreSqlRolePrivilegeTests`:** the provisioner owns the schema, and
  runtime roles hold DML grants only.
- **`HangfirePostgreSqlFreshInstallTests` and
  `HangfirePostgreSqlColdStartTests`:** a worker start provisions the schema
  before any storage use, and API processes fail closed until the worker
  publishes it, then converge.
- **`ReadinessEndpointTests`:** API readiness fails closed until the expected
  schema version is published.
- **`BackupSetRestoreDrillTests`:** a backup set restored into an empty
  installation converges, and mixed generations are rejected.
- **`SearchTextNormalizerTests`:** normalization folds case and diacritics
  provider-neutrally.
- **Review:** image tags and recorded digests are checked during
  [release qualification](../maintainers/packaged-release-qualification.md).
