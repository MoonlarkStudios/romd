# Admin Workload Scale Baseline on PostgreSQL — 100k (1M deferred)

Status: 100k measured 2026-09-04 on PostgreSQL 18; 1M deferred by the owner on 2026-09-04 (see "1M status")

Related issues: #98 (original spike), #128 Phase 5 (this re-baseline), #125/#126 (cutover)

This report re-baselines the admin API workload of the #98 spike on the PostgreSQL
application database that #128 introduced. It uses the same harness, the same dataset
spec (v4) and seed, and the same scenarios as the SQLite baseline in
[`admin-scale-baseline-2026-08.md`](admin-scale-baseline-2026-08.md), with the
provider-specific plumbing replaced (see "Harness changes"). Numbers are transcribed
from the measurement artifact named at the end; nothing is smoothed or estimated.

The harness remains local, manual release-qualification evidence. It is absent from
PR workflows and the default `mise run test*` tasks.

## Assumptions

### Hardware and software

- Apple M2 Max, 12 cores, 32.0 GiB available to .NET, macOS 26.6.1.
- PostgreSQL runs in the Rancher Desktop Lima VM (`vz`, **2 vCPUs, 6 GiB**), the
  `compose.db.yaml` dev server (`postgres:18-bookworm`, loopback trust auth). The
  server reported `18.6 (Debian 18.6-1.pgdg12+2)`. The harness process runs on the host and
  reaches the server over loopback TCP, so every measurement includes a client/server
  round trip that the in-process SQLite baseline did not pay.
- .NET 10.0.10; Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3; EF Core 10.0.7.
- One database per scale (`romd_scale_100k`), created by the harness with the production
  baseline migration, then bulk-loaded with binary COPY and `ANALYZE`d. Identity sequences
  are reseeded past the explicit ids. The persisted search document is written for every
  title and DAT game the way the production interceptor writes it; PostgreSQL derives the
  stored `tsvector` and the GIN indexes from it.
- Dataset spec v4, seed `0x524F4D445F763421`; generation took 42.7 s for 3,266,809 rows; the database measured 958 MB.

### Harness changes for PostgreSQL

- Bulk generation uses binary COPY with catalog-driven type coercion instead of prepared
  SQLite inserts; FTS5 trigger equivalence checks are replaced by search-document, GIN-index,
  and sampled `to_tsquery` checks.
- Per-case counters come from `pg_stat_database` (blocks read/hit, tuples, temp bytes,
  transactions) and `pg_database_size` instead of SQLite page/WAL statistics; the report
  columns "WAL growth" became "Blocks read" and "Temp bytes".
- Captured production SQL is replayed under `EXPLAIN` with its bound parameters. A plan is
  flagged for a sequential scan only when the planner estimates 1,000 rows or more for that
  scan (lookup-table scans are the planner's correct answer), and for sort nodes.
- `wal-contention` is now `concurrency`: the same reader mix and writer, counting
  serialization failures, deadlocks, and lock timeouts (40001/40P01/55P03) where the SQLite
  run counted busy/locked errors.
- Relevance paging is a keyset over `(ts_rank desc, id)` since #128; the case names keep the
  historical `relevance-fts-offset-N` labels, but the cursor walked is a real keyset cursor.

### Topology limitation

As before, all readers and the writer run in one process against one server, with one
DbContext per scope. Production runs three processes; connection-pool and lock behaviour
across processes is not exercised here. The 2-vCPU VM also caps parallel query plans
(`Workers Planned: 2` at most).

### Dataset reuse caveat

Several scenarios mutate the dataset: materialization replaces the library projections and
advances the library's materialization generation, payload-availability disables and repairs
sources and rewrites local-payload flags. Within one run the scenario order keeps each case
on the state it expects, but a second pass on the reused dataset returned 0 rows for the
library-scoped export because its query pins the library's materialization generation, which
the previous pass had advanced. Use a freshly generated dataset (`--regenerate`) for any
artifact of record; this report's artifact is a fresh run.

## Dataset

| Table | Rows |
|---|---:|
| CatalogReleaseFileSources | 300,000 |
| CatalogReleaseFiles | 300,000 |
| CatalogReleaseLanguages | 120,000 |
| CatalogReleaseRegions | 120,000 |
| CatalogReleaseSources | 120,000 |
| CatalogReleases | 120,000 |
| CatalogSources | 27 |
| DatFiles | 27 |
| DatGameLanguages | 120,000 |
| DatGameRegions | 120,000 |
| DatGames | 120,000 |
| DatRoms | 300,000 |
| DatSources | 27 |
| Files | 130,027 |
| GameLanguages | 10 |
| JobItems | 300,000 |
| Jobs | 5 |
| Libraries | 2 |
| MaterializedLibraryReleases | 240,000 |
| MaterializedLibraryTitles | 200,000 |
| Platforms | 8 |
| Regions | 10 |
| RomFiles | 90,000 |
| SourceEntries | 120,000 |
| TitleContentRatings | 40,000 |
| TitleExternalIds | 40,000 |
| TitleMedia | 40,000 |
| TitleMetadataLayers | 40,000 |
| TitleSourceLinks | 120,000 |
| Titles | 100,000 |
| TrackedTitles | 66,666 |

Search index checks:
- Every Titles and DatGames row carries a search document (100,000 / 120,000).
- GIN indexes present on Titles.SearchVector and DatGames.SearchVector.
- Sampled prefix query 'saga:*' == generator expectation (70,000 titles).

## Measured Results — 100k

Percentiles are per-case wall time in milliseconds over the recorded iterations; Alloc is p50
managed allocations per iteration; Blocks read and Temp bytes are `pg_stat_database` deltas
across the case. A p95 that far exceeds the p50 at 5 iterations is a cold first iteration
(JIT and connection warm-up), not steady-state latency.

### title-matcher

| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | Commands | Blocks read | Temp bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| match-existing-batch-500 (platform 1) | 5 | 500 | 7.0 | 194.5 | 1.4 MiB | 1 | 0 | 0 B |

### title-search

| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | Commands | Blocks read | Temp bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| keyset-browse-offset-0 | 5 | 50 | 8.5 | 10.8 | 221.4 KiB | 1 | 0 | 0 B |
| keyset-browse-offset-2000 | 5 | 50 | 8.9 | 11.0 | 229.4 KiB | 1 | 0 | 0 B |
| keyset-browse-offset-10000 | 5 | 50 | 12.3 | 14.5 | 235.3 KiB | 1 | 0 | 0 B |
| keyset-fts-offset-0 | 5 | 50 | 8.6 | 9.2 | 221.7 KiB | 1 | 0 | 0 B |
| keyset-fts-offset-2000 | 5 | 50 | 11.1 | 12.9 | 234.6 KiB | 1 | 0 | 0 B |
| keyset-fts-offset-10000 | 5 | 50 | 17.1 | 18.7 | 234.9 KiB | 1 | 0 | 0 B |
| relevance-fts-offset-0 | 5 | 50 | 34.6 | 36.6 | 238.3 KiB | 1 | 0 | 0 B |
| relevance-fts-offset-2000 | 5 | 50 | 41.2 | 43.0 | 250.7 KiB | 1 | 0 | 0 B |
| relevance-fts-offset-10000 | 5 | 50 | 39.5 | 40.3 | 246.1 KiB | 1 | 42 | 0 B |

Plans with a sequential scan the planner expects to read 1,000+ rows:
- `keyset-browse-offset-0`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)
- `keyset-browse-offset-2000`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)
- `keyset-browse-offset-10000`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)
- `keyset-fts-offset-0`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)
- `keyset-fts-offset-2000`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)
- `keyset-fts-offset-10000`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)
- `relevance-fts-offset-0`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)
- `relevance-fts-offset-2000`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)
- `relevance-fts-offset-10000`: ->  Seq Scan on "TrackedTitles" t2  (cost=0.00..1098.66 rows=66666 width=4)

### catalog-filters

| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | Commands | Blocks read | Temp bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| whole-call | 3 | 119 | 391.9 | 411.9 | 466.1 KiB | 8 | 16,396 | 0 B |
| facet-1-sequential | 3 | 12 | 11.5 | 11.8 | 640 B | 0 | 0 | 0 B |
| facet-2-sequential | 3 | 45 | 15.1 | 15.9 | 1.3 KiB | 0 | 0 | 0 B |
| facet-3-sequential | 3 | 20 | 12.6 | 12.8 | 640 B | 0 | 0 | 0 B |
| facet-4-sequential | 3 | 10 | 78.5 | 83.5 | 1.8 KiB | 0 | 1,803 | 0 B |
| facet-5-sequential | 3 | 10 | 82.2 | 85.1 | 640 B | 0 | 227 | 0 B |
| facet-6-sequential | 3 | 12 | 5.0 | 5.1 | 672 B | 0 | 0 | 0 B |
| facet-7-sequential | 3 | 8 | 23.6 | 25.7 | 640 B | 0 | 0 | 0 B |
| facet-8-sequential | 3 | 2 | 15.0 | 15.2 | 640 B | 0 | 0 | 0 B |

Plans with a sequential scan the planner expects to read 1,000+ rows:
- `whole-call`: ->  Seq Scan on "Titles" t  (cost=0.00..5092.20 rows=40560 width=8)
- `whole-call`: ->  Seq Scan on "Titles" t  (cost=0.00..4788.00 rows=40560 width=10)
- `whole-call`: ->  Parallel Seq Scan on "DatGames" d0  (cost=0.00..5056.00 rows=50000 width=8)
- `whole-call`: ->  Parallel Seq Scan on "DatGames" d0  (cost=0.00..5056.00 rows=50000 width=8)
- `whole-call`: ->  Seq Scan on "TitleContentRatings" t  (cost=0.00..880.00 rows=40000 width=6)
- `whole-call`: ->  Parallel Seq Scan on "Titles" t  (cost=0.00..4204.67 rows=41667 width=4)
- `whole-call`: ->  Seq Scan on "Titles" t  (cost=0.00..4788.00 rows=100000 width=7)
- `facet-2-sequential`: ->  Seq Scan on "Titles" t  (cost=0.00..5092.20 rows=40560 width=8)
- `facet-3-sequential`: ->  Seq Scan on "Titles" t  (cost=0.00..4788.00 rows=40560 width=10)
- `facet-4-sequential`: ->  Parallel Seq Scan on "DatGames" d0  (cost=0.00..5056.00 rows=50000 width=8)
- `facet-5-sequential`: ->  Parallel Seq Scan on "DatGames" d0  (cost=0.00..5056.00 rows=50000 width=8)
- `facet-6-sequential`: ->  Seq Scan on "TitleContentRatings" t  (cost=0.00..880.00 rows=40000 width=6)
- `facet-7-sequential`: ->  Parallel Seq Scan on "Titles" t  (cost=0.00..4204.67 rows=41667 width=4)
- `facet-8-sequential`: ->  Seq Scan on "Titles" t  (cost=0.00..4788.00 rows=100000 width=7)

### export

| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | Commands | Blocks read | Temp bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| get-export-files-unbounded | 3 | 90,000 | 192.5 | 199.0 | 38.9 MiB | 1 | 16,280 | 14.1 MiB |
| get-export-files-library-scoped | 3 | 25,000 | 231.5 | 273.9 | 113.9 MiB | 1 | 25,337 | 21.5 MiB |

Plans with a sequential scan the planner expects to read 1,000+ rows:
- `get-export-files-unbounded`: ->  Parallel Seq Scan on "DatGames" d  (cost=0.00..5056.00 rows=50000 width=8)
- `get-export-files-library-scoped`: ->  Parallel Seq Scan on "CatalogReleaseFiles" c0  (cost=0.00..7414.00 rows=125000 width=25)

### job-items

| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | Commands | Blocks read | Temp bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| get-by-job-page-100 | 5 | 100 | 4.0 | 24.4 | 291.1 KiB | 3 | 0 | 0 B |
| get-all-by-job-100k-cap | 3 | 100,000 | 408.3 | 562.2 | 149.8 MiB | 142 | 10,291 | 10.4 MiB |

### materialization

| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | Commands | Blocks read | Temp bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| try-replace-materialized-projections | 3 | 220,000 | 987.9 | 1,272.9 | 515.5 MiB | 4 | 1,232 | 0 B |

### concurrency

| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | Commands | Blocks read | Temp bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| readers-only/reader-search-page | 728 | 728 | 17.2 | 26.2 | 0 B | 0 | 1,330 | 0 B |
| readers-only/reader-job-items-page | 726 | 726 | 3.6 | 10.8 | 0 B | 0 | 1,330 | 0 B |
| readers-only/reader-matcher-batch-500 | 726 | 726 | 5.2 | 10.9 | 0 B | 0 | 1,330 | 0 B |
| readers+writer/reader-search-page | 1252 | 1,252 | 20.2 | 32.6 | 0 B | 0 | 37,157 | 0 B |
| readers+writer/reader-job-items-page | 1252 | 1,252 | 3.9 | 11.3 | 0 B | 0 | 37,157 | 0 B |
| readers+writer/reader-matcher-batch-500 | 1252 | 1,252 | 5.8 | 11.4 | 0 B | 0 | 37,157 | 0 B |
| readers+writer/writer-batches | 1591 | 775,667 | 5.0 | 16.3 | 0 B | 0 | 37,157 | 0 B |

### payload-availability

| Case | Iter | Rows | p50 ms | p95 ms | Alloc p50 | Commands | Blocks read | Temp bytes |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| tracked-local-payload-platform-breakdown | 5 | 33,333 | 22.0 | 30.2 | 122.1 KiB | 1 | 20 | 0 B |
| source-disable-reactivate | 5 | 5,000 | 106.8 | 114.5 | 291.4 KiB | 5 | 0 | 0 B |
| full-catalog-repair-100,000-titles | 1 | 50,000 | 5,617.5 | 5,617.5 | 4.7 MiB | 26 | 152,766 | 0 B |
| link-delete-commit-100,000-titles | 1 | 100,000 | 1,520.9 | 1,520.9 | 1.3 MiB | 2 | 37,780 | 0 B |

Plans with a sequential scan the planner expects to read 1,000+ rows:
- `tracked-local-payload-platform-breakdown`: ->  Parallel Seq Scan on "Titles" t  (cost=0.00..4204.67 rows=41667 width=9)
- `source-disable-reactivate`: ->  Seq Scan on "TitleSourceLinks" t  (cost=0.00..2096.00 rows=120000 width=8)
- `source-disable-reactivate`: ->  Seq Scan on "TitleSourceLinks" t0  (cost=0.00..2096.00 rows=120000 width=14)
- `full-catalog-repair-100,000-titles`: ->  Seq Scan on "DatGames" d  (cost=0.00..5756.00 rows=120000 width=8)
- `link-delete-commit-100,000-titles`: ->  Seq Scan on "TitleSourceLinks" t  (cost=0.00..2696.00 rows=119976 width=6)
- `link-delete-commit-100,000-titles`: ->  Seq Scan on "Titles" t  (cost=0.00..5437628.64 rows=148480 width=7)

## Comparison with the SQLite 100k baseline

The SQLite artifact (`scale-harness-100k-20260818-150426`) was produced from dataset spec v1
(1,593,079 rows) and the PostgreSQL artifact from spec v4 (3,266,809 rows), so only the
title-search cases, whose populations match (100,000 titles at both spec versions in the
re-baselined harness vs 35,000 in the v1 run), are listed side by side, and even those are
indicative rather than like-for-like. The remaining scenarios were renamed or re-shaped by
#107/#109/#130 after the v1 artifact and have no v1 counterpart with the same case names.

| Case | PostgreSQL p50 / p95 ms | SQLite v1 p50 / p95 ms |
|---|---:|---:|
| keyset-browse-offset-0 | 8.5 / 10.8 | 1.1 / 21.7 |
| keyset-browse-offset-2000 | 8.9 / 11.0 | 1.1 / 1.5 |
| keyset-browse-offset-10000 | 12.3 / 14.5 | 1.1 / 1.4 |
| keyset-fts-offset-0 | 8.6 / 9.2 | 6.7 / 7.5 |
| keyset-fts-offset-2000 | 11.1 / 12.9 | 7.8 / 8.2 |
| keyset-fts-offset-10000 | 17.1 / 18.7 | 7.1 / 7.6 |
| relevance-fts-offset-0 | 34.6 / 36.6 | 24.2 / 31.0 |
| relevance-fts-offset-2000 | 41.2 / 43.0 | 24.1 / 24.6 |
| relevance-fts-offset-10000 | 39.5 / 40.3 | 25.5 / 26.0 |

Reading: the in-process SQLite baseline answered a 50-row keyset page in about 1 ms;
PostgreSQL over loopback answers the same page in 9–15 ms, of which the round trip and
Npgsql command overhead are the fixed part. Relevance paging costs 35–43 ms on PostgreSQL
versus 24–31 ms on SQLite and is now a true keyset (no offset re-scan), so its cost does not
grow with page depth.

## Budgets at 100k

The accepted budgets in the SQLite report apply at 1M; they are listed here against the 100k
PostgreSQL numbers for orientation only. No budget is amended by this report (D7: budgets
change only with recorded 1M evidence).

| Case | 1M budget (p95) | 100k PostgreSQL p95 |
|---|---|---:|
| title-search keyset-browse | ≤ 50 ms | 14.5 ms (worst depth) |
| title-search keyset-FTS | ≤ 100 ms | 18.7 ms (worst depth) |
| title-search relevance-FTS | ≤ 250 ms | 43.0 ms (worst depth, keyset) |
| job-items page(100), uncontended | ≤ 100 ms | 24.4 ms |
| job-items page(100), reader-writer mix | ≤ 400 ms | 11.3 ms |
| catalog-filters whole-call | ≤ 1.5 s | 411.9 ms |
| export unbounded | ≤ 7 s | 199.0 ms |
| materialization replace | ≤ 6 s | 1,272.9 ms |
| writer batch under reader mix | ≤ 3 s | 16.3 ms |
| contention errors (was SQLITE_BUSY) | zero | zero in every phase |

## 1M status

Deferred (owner decision, 2026-09-04). The 100k database occupies 958 MB, so a 1M dataset needs roughly 10 GiB of
PostgreSQL space plus WAL and temp headroom inside the Rancher Desktop VM disk, which is a
sparse file on the host volume. The host volume sat at 95% used (about 24 GiB free) when the
100k run finished, and an earlier attempt during this work filled it completely (see
`docs/known-issues.md`, "Rancher Desktop guest disk I/O errors after the host volume filled
up"). The 1M run should wait for at least 30 GiB of free host space; until it is recorded,
the 1M budgets in the SQLite report remain the accepted budgets and #128 closes with this 100k
re-baseline as its Phase 5 evidence.

## Reproduction

Run inside a mise-activated shell after `mise run db:up` (the harness reads
`ConnectionStrings__Romd` and creates `romd_scale_<scale>` next to the checkout database),
or pass `--connection-string` for another PostgreSQL 18 server:

```bash
dotnet run --project benchmarks/Romd.ScaleHarness -c Release -- --scale 100k --regenerate
dotnet run --project benchmarks/Romd.ScaleHarness -c Release -- --scale 1m --regenerate
dotnet run --project benchmarks/Romd.ScaleHarness -c Release -- --scale 1m --scenario export
```

`--scenario` accepts one of `title-matcher`, `title-search`, `catalog-filters`, `export`,
`job-items`, `materialization`, `concurrency`, `payload-availability`, or `all` (default).
`--data-dir` holds the dataset manifests (default: system temp `/romd-scale-harness`);
`--regenerate` drops and recreates the scale database. Reports (JSON + Markdown) land in
`benchmarks/Romd.ScaleHarness/results/` (gitignored).

Source artifact for this report (local, gitignored):

- `scale-harness-100k-20260904-180547.{json,md}` — fresh spec-v4 100k dataset, all
  scenarios, report schema v5.
- `scale-harness-100k-20260904-180821.{json,md}` — second pass on the reused dataset;
  not used for numbers (see the reuse caveat), kept as the record of that behaviour.
