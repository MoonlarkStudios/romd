# Admin Workload Scale Baseline — 100k / 1M

Status: baseline accepted 2026-08-18

Related issues: #75, #97, #98, #104, #107, #109

This report is the versioned benchmark and budget baseline for the admin
API at 100k and 1M ROM scale, produced by the #98 spike. It is the
evidence base that gates #97: every budget below is tied to dataset spec
v1 and the hardware recorded in the assumptions section, and any #97
optimization must re-run the same scenarios with the same harness and
compare against this report before and after the change. Numbers here
are transcribed exactly from the measurement artifacts listed at the end
of this report; nothing is smoothed or estimated.

The 100k/1M harness is local, manual release-qualification evidence. It is
intentionally absent from PR workflows and the default `mise run test*`
tasks: those gates retain small deterministic query-shape, command-count,
rollback, and migration tests, but never generate or execute giant fixtures.

## Assumptions

### Hardware and software

- Apple M2 Max, 12 cores, local NVMe storage (database on the internal
  SSD under the system temp directory), macOS (Darwin 25.5.0; the
  harness banner reports "macOS 26.5.2").
- .NET SDK 10.0.203; runtime .NET 10.0.7 per the artifact banners.
- Microsoft.EntityFrameworkCore.Sqlite 10.0.7, resolving
  Microsoft.Data.Sqlite.Core 10.0.7 with the bundled SQLitePCLRaw
  e_sqlite3 2.1.11 native SQLite.
- 30 s SQLite busy timeout on every connection, mirroring production
  (`SqliteConnectionConfiguration.BusyTimeoutSeconds = 30` applied via
  `SqliteBusyTimeoutInterceptor`).

### Dataset spec v1

Deterministic synthetic dataset, seed `0x524F4D445F763121` ("ROMD_v1!"),
spec version 1 (`benchmarks/Romd.ScaleHarness/DatasetSpec.cs`). The
driving axis is DatGames; every other population derives from versioned
ratios: CatalogReleases = 1.2x Titles, CatalogReleaseFiles = 3x
CatalogReleases, GameTitleMappings = DatGames, MaterializedLibraryTitles
= Titles x 2 libraries, MaterializedLibraryReleases = DatGames x 2
libraries, JobItems = DatRoms, enrichment layers on 40% of titles, and
the first synthetic job absorbing 40% of all job items. Constants: 2
libraries, 5 jobs, 10 regions, 10 languages, 5,000 games per DAT file,
8 platforms with weights 40/20/10/8/7/6/5/4.

Exact populations from the generated-dataset manifests:

| Table | 100k | 1M |
|---|---:|---:|
| CatalogReleaseFiles | 126,000 | 1,080,000 |
| CatalogReleases | 42,000 | 360,000 |
| DatFiles | 22 | 200 |
| DatGameLanguages | 100,000 | 1,000,000 |
| DatGameRegions | 100,000 | 1,000,000 |
| DatGames | 100,000 | 1,000,000 |
| DatRoms | 250,000 | 2,500,000 |
| Files | 89,022 | 870,200 |
| GameLanguages | 10 | 10 |
| GameTitleMappings | 100,000 | 1,000,000 |
| JobItems | 250,000 | 2,500,000 |
| Jobs | 5 | 5 |
| Libraries | 2 | 2 |
| MaterializedLibraryReleases | 200,000 | 2,000,000 |
| MaterializedLibraryTitles | 70,000 | 600,000 |
| Platforms | 8 | 8 |
| Regions | 10 | 10 |
| RomFiles | 75,000 | 750,000 |
| TitleContentRatings | 14,000 | 120,000 |
| TitleExternalIds | 14,000 | 120,000 |
| TitleMedia | 14,000 | 120,000 |
| TitleMetadataLayers | 14,000 | 120,000 |
| Titles | 35,000 | 300,000 |

Dataset generation took 12.8–13.2 s at 100k and 145.3 s at 1M. FTS
equivalence checks passed at both scales (Titles_fts trigger-maintained
== rebuilt; row counts match content tables; FTS5 integrity-check
passed). The generator also confirmed the #104 finding: the production
migration history destroys the DatGames FTS triggers, so the harness
rebuilds `DatGames_fts` explicitly to measure a correct index.

### Topology limitation

The harness runs all readers and the writer in a single process against
one WAL-mode database file, using multiple DbContexts and connections.
Production runs three processes (admin, consumer, worker hosts) sharing
the same SQLite file. Cross-process WAL behavior — separate page caches
and cross-process lock arbitration — is not exercised by this baseline
and is a recorded design limitation of the wal-contention numbers.

### Report schema v2 fail-loud semantics

The initial report schema (v1) recorded failing cases as rows=0 with
0.0 ms percentiles, indistinguishable from a fast empty result. Report
schema v2 records `FAILED` rows with per-iteration exception notes and
a nonzero process exit. The export and job-items scenarios below use
the v2 fail-loud re-run artifacts; the v2 100k re-runs confirmed the
baseline row counts (75,000 / 15,738 / 100,000) unchanged.

## Measured Results

Percentiles are per-case wall time in milliseconds; Alloc is p50
managed allocations per iteration. All rows are transcribed exactly
from the named artifacts.

### 100k scale

Source: `scale-harness-100k-20260818-032306` (full run); export and
job-items row counts confirmed by v2 re-runs
`scale-harness-100k-20260818-132635` / `-132659`.

| Scenario / case | Rows | p50 ms | p95 ms | Alloc p50 |
|---|---:|---:|---:|---:|
| title-matcher / get-by-platform-load-all (platform 1) | 14,000 | 115.1 | 246.8 | 21.1 MiB |
| title-search / keyset-browse-offset-0 | 50 | 2.0 | 3.2 | 173.4 KiB |
| title-search / keyset-browse-offset-2000 | 50 | 1.1 | 2.6 | 181.2 KiB |
| title-search / keyset-browse-offset-10000 | 50 | 1.1 | 3.1 | 180.7 KiB |
| title-search / keyset-fts-offset-0 | 50 | 8.0 | 8.2 | 207.4 KiB |
| title-search / keyset-fts-offset-2000 | 50 | 8.1 | 8.6 | 217.5 KiB |
| title-search / keyset-fts-offset-10000 | 50 | 8.3 | 8.9 | 217.6 KiB |
| title-search / relevance-fts-offset-0 | 50 | 28.9 | 33.9 | 205.1 KiB |
| title-search / relevance-fts-offset-2000 | 50 | 24.6 | 25.7 | 205.6 KiB |
| title-search / relevance-fts-offset-10000 | 50 | 25.8 | 25.9 | 205.6 KiB |
| catalog-filters / whole-call | 119 | 155.6 | 172.0 | 303.2 KiB |
| catalog-filters / facet-4-sequential (regions) | 10 | 57.1 | 57.9 | 1.1 KiB |
| catalog-filters / facet-5-sequential (languages) | 10 | 56.1 | 56.8 | 1.1 KiB |
| export / get-export-files-unbounded | 75,000 | 151.8 | 189.9 | 32.6 MiB |
| export / get-export-files-library-scoped | 15,738 | 867.3 | 954.6 | 50.7 MiB |
| job-items / get-by-job-page-100 | 100 | 1.0 | 17.0 | 413.2 KiB |
| job-items / get-all-by-job-100k-cap | 100,000 | 4,202.8 | 4,388.6 | 183.6 MiB |
| materialization / try-replace-materialized-projections | 135,000 | 548.2 | 1,203.7 | 284.5 MiB |

The remaining catalog facets at 100k measured p95 0.6–7.9 ms
(facet-1/2/3/6/7/8). The job-items 100k-cap case peaked at 324.9 MiB
working set. WAL contention at 100k (per-op allocations not measured
under concurrency, per the artifact):

| Phase / case | Ops | p50 ms | p95 ms |
|---|---:|---:|---:|
| readers-only / reader-search-page | 63 | 16.5 | 56.9 |
| readers-only / reader-job-items-page | 64 | 3.4 | 50.2 |
| readers-only / reader-platform-load-all | 61 | 275.7 | 435.0 |
| readers+writer / reader-search-page | 150 | 23.4 | 64.5 |
| readers+writer / reader-job-items-page | 151 | 2.1 | 38.2 |
| readers+writer / reader-platform-load-all | 148 | 224.8 | 278.9 |
| readers+writer / writer-batches (500 titles/txn) | 10 | 768.5 | 2,870.4 |

Zero reader or writer busy/locked errors in every phase.

### 1M scale

Sources: `scale-harness-1m-20260818-131713` (full run) for
title-matcher, title-search, catalog-filters, materialization, and
wal-contention; v2 fail-loud re-runs `scale-harness-1m-20260818-132745`
(export) and `-132807` (job-items).

| Scenario / case | Rows | p50 ms | p95 ms | Alloc p50 |
|---|---:|---:|---:|---:|
| title-matcher / get-by-platform-load-all (platform 1) | 120,000 | 544.9 | 769.8 | 180.2 MiB |
| title-search / keyset-browse-offset-0 | 50 | 1.3 | 2.5 | 169.7 KiB |
| title-search / keyset-browse-offset-2000 | 50 | 1.2 | 1.4 | 179.4 KiB |
| title-search / keyset-browse-offset-10000 | 50 | 1.4 | 2.3 | 178.3 KiB |
| title-search / keyset-fts-offset-0 | 50 | 36.3 | 36.8 | 200.2 KiB |
| title-search / keyset-fts-offset-2000 | 50 | 40.5 | 41.3 | 210.9 KiB |
| title-search / keyset-fts-offset-10000 | 50 | 41.6 | 43.3 | 210.0 KiB |
| title-search / relevance-fts-offset-0 | 50 | 194.9 | 211.0 | 197.8 KiB |
| title-search / relevance-fts-offset-2000 | 50 | 202.8 | 204.6 | 198.2 KiB |
| title-search / relevance-fts-offset-10000 | 50 | 203.1 | 228.5 | 198.2 KiB |
| catalog-filters / whole-call | 119 | 1,399.3 | 1,996.7 | 307.8 KiB |
| catalog-filters / facet-4-sequential (regions) | 10 | 553.9 | 580.9 | 1.1 KiB |
| catalog-filters / facet-5-sequential (languages) | 10 | 546.6 | 564.4 | 1.1 KiB |
| export / get-export-files-unbounded | 750,000 | 1,819.2 | 6,200.7 | 321.2 MiB |
| export / get-export-files-library-scoped | FAILED — `SqliteException: too many SQL variables` (0/3 iterations; 4,061.3–5,139.7 ms per failing iteration; #107) | — | — | — |
| job-items / get-by-job-page-100 | 100 | 8.6 | 636.7 | 421.7 KiB |
| job-items / get-all-by-job-100k-cap | FAILED — `SqliteException: too many SQL variables` (0/3 iterations; 3,647.6–3,765.8 ms per failing iteration; #107) | — | — | — |
| materialization / try-replace-materialized-projections | 1,300,000 | 4,780.5 | 5,563.1 | 2.66 GiB |

The remaining catalog facets at 1M measured p95 4.3–64.8 ms
(facet-1/2/3/6/7/8). Materialization peaked at 1.18 GiB working set.
The earlier same-day full run (`-131713`, schema v1) measured the
non-failing export and job-items cases warmer: unbounded export p95
1,556.4 ms and page-100 p95 14.6 ms; the v2 re-run p95s above carry a
cold first iteration in a freshly started process. Both measurements
are retained here because both artifacts exist; the re-run values are
the fail-loud record of reference.

WAL contention at 1M (per-op allocations not measured under
concurrency, per the artifact):

| Phase / case | Ops | p50 ms | p95 ms |
|---|---:|---:|---:|
| readers-only / reader-search-page | 11 | 140.9 | 356.4 |
| readers-only / reader-job-items-page | 12 | 1.8 | 357.4 |
| readers-only / reader-platform-load-all | 9 | 1,470.3 | 1,913.0 |
| readers+writer / reader-search-page | 22 | 235.2 | 360.4 |
| readers+writer / reader-job-items-page | 23 | 98.9 | 197.7 |
| readers+writer / reader-platform-load-all | 20 | 1,506.3 | 1,740.2 |
| readers+writer / writer-batches (500 titles/txn) | 172 | 2.6 | 15.1 |

Zero reader or writer busy/locked errors in every phase at both scales.

### Query-plan highlights

Captured `EXPLAIN QUERY PLAN` flags from the artifacts, common to both
scales:

- Temp b-trees for ORDER BY and DISTINCT appear across every scenario:
  title-matcher load-all sorts via temp b-tree; every title-search
  variant builds temp b-trees for DISTINCT (and ORDER BY on the FTS
  paths); both export cases build a temp b-tree for DISTINCT.
- Correlated per-row scalar subqueries dominate the title-search
  projection: per returned title, a mapping-count subquery, two
  co-routine ROM-aggregation subqueries with their own temp-b-tree
  DISTINCT, and a primary-media lookup all execute per row.
- Facet junction scans: the region and language facets scan the full
  `DatGameRegions` / `DatGameLanguages` junction tables with a
  correlated per-row subquery into `DatGames`, which is why facet-4 and
  facet-5 cost ~554/547 ms each at 1M (vs 57/56 ms at 100k). The
  year, publisher, and rating facets full-scan `Titles` with temp
  b-trees for GROUP BY and ORDER BY.
- FTS co-routine materialization: the relevance path materializes the
  entire bm25-ranked match set in a co-routine with a temp-b-tree ORDER
  BY before paging, so cost scales with total match cardinality
  (210,000 matched titles at 1M), explaining the flat ~200 ms relevance
  numbers regardless of offset. The keyset-FTS path scans the FTS
  virtual table with a temp-b-tree ORDER BY (~36–43 ms at 1M).
- The unbounded export scans the full `IX_DatRoms_DatGameId_RomFileId`
  covering index with five rowid lookups per row plus a temp-b-tree
  DISTINCT.

## Accepted Budgets

All budgets apply at 1M scale on the hardware above. A #97 change is
accepted only if the re-run of the same scenario stays within budget.

| Case | Budget (p95) | Measured (p95, 1M) | Status |
|---|---|---|---|
| title-search keyset-browse | ≤ 50 ms | 2.5 ms (worst offset) | Within budget |
| title-search keyset-FTS | ≤ 100 ms | 43.3 ms (worst offset) | Within budget |
| title-search relevance-FTS (offset paging, exploration depth ≤ 10,000 as measured) | ≤ 250 ms | 228.5 ms (offset 10,000) | Within budget; nears budget — 9% headroom |
| job-items page(100), uncontended (warm process) | ≤ 100 ms | 14.6 ms warm (v2 cold-process re-run p95 636.7 ms; p50 8.6 ms) | Within budget warm — see methodology note below |
| job-items page(100), reader-writer mix | ≤ 400 ms | 197.7 ms | Within budget |
| catalog-filters whole-call (sequential per the #89 single-flight rule) | ≤ 1.5 s | 1,996.7 ms (p50 1,399.3 ms) | FAILING at 1M — budget retained; #97 must close the gap |
| export unbounded (provisional until #97 replaces it with streaming) | ≤ 7 s | 6,200.7 ms | Within budget |
| materialization replace (background job path) | ≤ 6 s | 5,563.1 ms | Within budget; nears budget — 7% headroom |
| WAL writer-batch under standard reader mix | ≤ 3 s | 15.1 ms at 1M (2,870.4 ms at 100k) | Within budget; the 100k measurement nears it |
| WAL SQLITE_BUSY at 30 s busy-timeout | zero | zero at both scales, all phases | Within budget |

Two measurement tensions were arbitrated rather than silently adjusted:

- catalog-filters whole-call is recorded as FAILING its retained 1.5 s
  budget at 1M. The facet junction scans (facet-4/5, ~1.1 s combined)
  are the measured bottleneck; #97 optimization must bring the
  whole-call p95 under budget, and accommodation by raising the budget
  was rejected.
- job-items page(100) uncontended is a warm-process budget. The v2
  re-run p95 of 636.7 ms carries a cold first iteration in a freshly
  started process at only three iterations (p50 8.6 ms; the warm
  same-day run measured p95 14.6 ms). Re-measurement under #97 must
  state warm/cold conditions explicitly.

## Explicit Failing Baselines (No Budget; Blocked)

- Library-scoped export at 1M: crashes every iteration with
  `SqliteException: SQLite Error 1: 'too many SQL variables'`
  (4,061.3–5,139.7 ms per failing iteration). Cause and fix are #107:
  the repository expands ~90,000 release ids into SQL parameters past
  the 32,766 variable limit. Works at 100k (15,738 rows, 954.6 ms
  p95).
- Job-items 100k-cap export at 1M: crashes identically
  (3,647.6–3,765.8 ms per failing iteration; ~70,000 distinct title
  ids expanded). Cause and fix are #107. Works at 100k (100,000 rows,
  4,388.6 ms p95).
- Title-matcher load-all: 120,000 titles for the heaviest platform at
  1M, p50 544.9 ms uncontended and ~1.5 s p50 under contention
  (1,470.3 ms readers-only, 1,506.3 ms readers+writer), with 180.2 MiB
  allocated per call — proportional to platform title count.
  Provisionally accepted for the background DAT-import path only; it
  is a named follow-up rework candidate and gets no interactive
  budget.
- Job-items 100k-cap memory profile at 100k: 183.6 MiB allocated and
  324.9 MiB peak working set to materialize 100,000 rows. No budget;
  #97 replaces this path with streaming plus a structured partial
  indicator per the contract ADR.

## SQLite Reconsideration Triggers

SQLite remains the accepted store. These measurable triggers force a
reconsideration decision if met:

- Sustained interactive-read p95 > 500 ms at production cardinality
  after #97 and #107 land.
- Writer-transaction p95 > 3 s under normal admin load.
- Any required read exceeding SQLite parameter/variable limits after
  #107's rework.
- FTS full-rebuild time exceeding the worker's acceptable migration
  window at production cardinality (ties to #104's trigger fix, which
  rebuilds `DatGames_fts` during migration).
- Single-node WAL contention producing SQLITE_BUSY despite the 30 s
  busy timeout.

The original run met the variable-limit trigger captured by #107. The
remediation evidence appended below shows both formerly failing 1M reads now
complete under the shared 500-id command ceiling; the remaining triggers were
not met on this hardware.

## Follow-Up Implementation Issues

- #107 (remediated below): bounded id batches remove the two variable-limit
  failures while #97 retains ownership of streaming and pagination.
- #104 (filed): DatGames FTS triggers destroyed by table rebuild;
  search index silently unmaintained. Confirmed independently by the
  harness generator at both scales.
- #97: keyset/filter-bound cursors and streaming exports per the
  contract ADR. This report is its gating baseline; #97 re-runs these
  scenarios and compares against the tables above.
- #109 (remediated below): the matcher now performs indexed 500-name batch
  lookups with allocation independent of platform title cardinality.

## #109 Remediation Evidence — 2026-08-26

The accepted spec-v1 baseline above is retained unchanged. Issue #109
replaced the stream-lived platform preload with one normalized-name
lookup per 500-item derivation batch. The obsolete
`ITitleRepository.GetByPlatformAsync` matcher seam was removed, so the
load-all regression is no longer expressible through the matcher.

The harness dataset is now spec v3, seed `0x524F4D445F763321`
(`"ROMD_v3!"`). This revision repairs the #98 generator for the neutral
source schema: it emits `CatalogSources`, `DatSources`, `SourceEntries`,
and `TitleSourceLinks`; stamps `DatFiles.DatSourceId` / lifecycle and
`DatGames.SourceEntryId`; and derives stored normalized title names with
the production `TitleNormalizer`. It also emits release-source and
release-file provenance and release taxonomy with truthful
`CatalogReleases.SourceCount`, file aggregates, and partial-tail `DatFiles`
child counts. DAT requirements, canonical files, stored ROM metadata, and
CAS file sizes now derive from one byte identity; SHA-backed file
fingerprints use the production `sha1:<hex>` shape. Fresh-generation
verification checks those identities, aggregates, taxonomy, provenance
keys, and foreign keys before writing a reusable manifest. Spec v2 was
discarded after re-review found mismatched source/canonical sizes and empty
release taxonomy; the v3 manifest and seed prevent those databases from
being reused. Fresh generation took 13.5 s at 100k and 184.7 s at 1M on
the hardware below.

Sources: `scale-harness-100k-20260826-220833` and
`scale-harness-1m-20260826-221156`, both fresh spec-v3 datasets under
.NET 10.0.11 on macOS 26.6.1.

| Scale / case | Rows | p50 ms | p95 ms | Alloc p50 | Iterations (ms) |
|---|---:|---:|---:|---:|---|
| 100k / match-existing-batch-500 | 500 | 7.6 | 149.6 | 1.6 MiB | 149.636, 27.319, 7.586, 5.167, 7.346 |
| 1M / match-existing-batch-500 | 500 | 7.3 | 152.8 | 1.6 MiB | 152.803, 32.162, 7.282, 4.810, 4.998 |

The first iteration carries process/EF query warm-up at both scales;
the remaining four iterations are 5.2–27.3 ms at 100k and 4.8–32.2 ms
at 1M. More importantly for the issue's memory contract, p50 allocation
is byte-stable within measurement noise (1,717,072 bytes at 100k;
1,721,544 bytes at 1M) even though the heaviest platform grows from
14,000 to 120,000 titles. Both runs returned the 500 expected persisted
ids in input order, created no titles, and produced zero WAL growth.

The captured production query plan at both scales is:

```text
SEARCH t USING INDEX IX_Titles_PlatformId_NormalizedName
    (PlatformId=? AND NormalizedName=?)
```

There is no full scan, name sort, or temp b-tree. The WAL reader mix now
exercises the same 500-name matcher operation instead of the deleted
platform-load-all path; contention was not re-measured in this focused
remediation run.

## Reproduction

Run these commands manually on release-qualification hardware; they are not
PR CI gates and their generated databases/reports remain local and gitignored.

```bash
dotnet run --project benchmarks/Romd.ScaleHarness -c Release -- --scale 100k
dotnet run --project benchmarks/Romd.ScaleHarness -c Release -- --scale 1m
dotnet run --project benchmarks/Romd.ScaleHarness -c Release -- --scale 1m --scenario export
```

`--scenario` accepts one of `title-matcher`, `title-search`,
`catalog-filters`, `export`, `job-items`, `materialization`,
`wal-contention`, `payload-availability`, or `all` (default). `--data-dir` overrides the
database location (default: system temp `/romd-scale-harness`);
`--regenerate` forces dataset regeneration.

Determinism: current generation uses the fixed seed `0x524F4D445F763421`
and dataset spec v4; an existing database is reused only when its embedded
manifest matches the requested scale and spec version, otherwise the
harness regenerates. Reports (JSON + Markdown) always land in
`benchmarks/Romd.ScaleHarness/results/` (gitignored) regardless of the
invocation directory.

Source artifacts for this report (local, gitignored):

- `scale-harness-100k-20260818-032306.{json,md}` — 100k full run
  (report schema v1).
- `scale-harness-1m-20260818-131713.{json,md}` — 1M full run (report
  schema v1; failing cases recorded as zero-row rows with notes).
- `scale-harness-100k-20260818-132635` / `-132659` — 100k fail-loud
  re-runs (schema v2) of export and job-items; row counts confirmed.
- `scale-harness-1m-20260818-132745` / `-132807` — 1M fail-loud
  re-runs (schema v2) recording the FAILED export and job-items cases.
- `scale-harness-100k-20260826-220833` — fresh spec-v3 100k dataset and
  bounded matcher remediation measurement.
- `scale-harness-1m-20260826-221156` — fresh spec-v3 1M dataset and
  bounded matcher remediation measurement.

## #107 Initial Bounded-Query Evidence — 2026-08-26

The accepted spec-v1 failing baselines above are retained unchanged. Issue
#107's initial implementation routed every client-resolved id collection through the shared
`BoundedIdQuery.MaxIdsPerBatch` ceiling of 500. `JobItemRepository` resolves
platform and title names in distinct, first-seen batches; `ExportRepository`
queries the selected release ids in the same bounded batches, de-duplicates
across them, and returns a deterministic title/file order. The full
pagination and streaming redesign remains #97.

The harness report schema is v3 for these runs and records the maximum total
parameter count of any captured command. The scoped export maximum is 501:
500 release-id parameters plus the library-scope parameter. Job-item title
resolution tops out at exactly 500. Focused command-interceptor tests separately
count the collection parameters by their generated names and fail if either
repository puts more than 500 ids in one command.

Fresh spec-v3 generation took 19.6 s at 100k and 243.2 s at 1M. Sources:
`scale-harness-100k-20260826-230118` (export),
`scale-harness-100k-20260826-230130` (job items),
`scale-harness-1m-20260826-230558` (export), and
`scale-harness-1m-20260826-230611` (job items), under .NET 10.0.11 on macOS
26.6.1.

| Scale / case | Rows | p50 ms | p95 ms | Alloc p50 | Max total params | WAL growth |
|---|---:|---:|---:|---:|---:|---:|
| 100k / library-scoped export | 9,800 | 248.7 | 293.7 | 52.9 MiB | 501 | 0 B |
| 1M / library-scoped export | 75,000 | 3,000.2 | 3,083.0 | 466.2 MiB | 501 | 0 B |
| 100k / job-items 100k cap | 100,000 | 579.4 | 623.2 | 191.0 MiB | 500 | 0 B |
| 1M / job-items 100k cap | 100,000 | 1,441.7 | 2,070.9 | 265.9 MiB | 500 | 0 B |

All iterations completed; the two 1M hard failures are closed. The 100k
library-scoped count differs from the historical spec-v1 value of 15,738
because the #109 remediation replaced that obsolete generator with the
truthful neutral-source/release-provenance spec v3 topology. On the current
deterministic dataset, 9,800 at 100k and 75,000 at 1M are the complete
owned/exposed selected-file populations. The job-item cap remains exactly
100,000 at both scales.

The bounded job-item title lookups use primary-key searches. Each bounded
export command uses the existing materialized-release and file/hash indexes:

```text
SEARCH m USING INDEX IX_MLR_LibraryId_CatalogReleaseId
    (LibraryId=? AND CatalogReleaseId=?)
SEARCH c USING INDEX IX_CatalogReleaseFiles_CatalogReleaseId_FileFingerprint
    (CatalogReleaseId=?)
SEARCH r USING INDEX IX_RomFiles_Sha1 (Sha1=?)
```

`DISTINCT` continues to use a temp b-tree in the export query, and the
100,000-row materialization still allocates hundreds of MiB. Those are the
known #97 streaming/pagination concerns, not variable-limit regressions.

### Snapshot-consistency corrective pass

Independent review found that the initial multi-command implementation could
observe different autocommit snapshots between batches. The final #107 shape
keeps the shared 500-id ceiling for job-item title and platform resolution, but
the admin query handlers now own an explicit repeatable read snapshot spanning
the initial item read and every bounded lookup. This is a dedicated application
contract, not the generic mutation unit of work: SQLite opens a deferred
Serializable transaction, PostgreSQL requests RepeatableRead, and unknown
providers fail closed. Repository transaction ownership is unchanged. Page and
export tests mutate the 501st title from a second SQLite connection at the batch
seam and prove both responses remain one coherent generation; cancellation
between title batches and more than 500 distinct platform ids are also pinned.
The provider-policy test pins the Npgsql provider name to
`IsolationLevel.RepeatableRead` rather than its ReadCommitted default. The real
PostgreSQL proof that a writer can commit between batches while the reader stays
on its original generation is an explicit #128 provider-integration acceptance
case; this SQLite prerequisite does not add Npgsql or Testcontainers early.

Library-scoped export now resolves valid owned/exposed candidate releases and
their optional files in one provider-neutral left-joined statement. Fileless
candidates remain part of release selection, so a preferred fileless release
does not incorrectly fall back to another release. A shared-cache/WAL test
rematerializes the library at the former query seam and proves the response is
never a torn generation. The statement streams rows ordered by
`MaterializedLibraryReleases.TitleId`, retaining one title group at a time; the
captured plan uses `IX_MLR_LibraryId_TitleId` and the release-file/SHA indexes
without a full scan or temp b-tree.

The harness report schema is v4 and adds total command count. The final focused
runs reuse the freshly generated spec-v3 databases above:

| Scale / case | Rows | p50 ms | p95 ms | Alloc p50 | WS peak | Commands | Max total params | WAL growth |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 100k / library-scoped export | 9,800 | 233.2 | 272.0 | 65.4 MiB | 138.7 MiB | 1 | 1 | 0 B |
| 1M / library-scoped export | 75,000 | 2,059.5 | 2,271.5 | 614.6 MiB | 169.1 MiB | 1 | 1 | 0 B |
| 100k / job-items 100k cap | 100,000 | 624.8 | 657.1 | 190.7 MiB | 207.5 MiB | 51 | 500 | 0 B |
| 1M / job-items 100k cap | 100,000 | 873.2 | 1,063.3 | 268.3 MiB | 232.8 MiB | 142 | 500 | 0 B |

The 100k row count was checked against an identical-dataset control rather than
inferred from the old spec-v1 number. Exact pre-#107 main `feafb422`, built from
an archive and run against the same spec-v3 database/manifest, also returned
9,800 library-scoped rows (`scale-harness-100k-20260826-234925`). The historical
15,738 count belongs to spec v1; the current-main spec-v3 topology changed the
complete population, while #107 preserves its exact result.

The first snapshot-correct single-statement export measurement materialized all
candidate rows and allocated 69.4 MiB at 100k and 649.8 MiB at 1M. Streaming
title groups reduced that to 65.4 MiB and 614.6 MiB, kept command/parameter
counts at 1/1, and removed the transient sort by ordering on the existing
materialized-release title index. This is bounded in-process grouping, not the
public streaming/pagination redesign still owned by #97.

Corrective artifacts (local, gitignored):

- `scale-harness-100k-20260826-234708` — final export query and plan.
- `scale-harness-1m-20260826-234748` — final export query and plan.
- `scale-harness-100k-20260826-234206` — transactional job-item read.
- `scale-harness-1m-20260826-234240` — transactional job-item read.
- `scale-harness-100k-20260826-234925` — exact pre-#107 main control against
  the identical spec-v3 database (stored in the temporary baseline archive).

## #130 Local-payload projection evidence — 2026-08-27

Issue #130 advances the deterministic fixture to spec v4 and makes Titles the
driving scale axis: the large fixture contains an actual 1,000,000 `Titles`
rows (not an extrapolation), 1,200,000 source entries/links, 1,200,000 DAT
games, and 3,000,000 DAT ROMs. It also uses the normalized provider claim keys
from #175. Fresh fixture generation on the hardware above took 24.5 s at 100k
and 555.1 s at 1M; the 1M database contains 32,667,669 total rows. All FTS,
foreign-key, normalized-provenance, and deterministic population checks passed.

The title-local-payload read uses `IX_Titles_PlatformId_HasLocalPayload` (also
pinned by a migration query-plan test). Projection writes are mismatch-only.
Status/topology changes roll up from persisted source-entry truth without
calling providers; source assertions are refreshed only for payload mutations
and recovery. Platform, source, and keyset-range work remains relational, so
command and parameter counts do not grow with title count.

Final exact-tree Release reports: `scale-harness-100k-20260827-181819` and
`scale-harness-1m-20260827-182027`, measured at commit `25510bca` (report
schema v4, dataset spec v4). The
repair preparation corrupts both `SourceEntries.HasLocalPayload` and
`Titles.HasLocalPayload`, then checkpoints that deliberate fault injection
before timing. The measured path is production's provider assertion refresh
plus title rollup, with one transaction per platform just like recovery. The
scenario derives the expected 600,000/500,000 one-million-row truths from the
deterministic spec and fails unless both counts and both normalized layers have
zero mismatches. Working-set high-water is sampled every 10 ms while each case
runs, rather than sampled only after it returns.

| Scale / case | Rows | p95 ms | Alloc p50 | WS peak | Commands | Max params | WAL growth |
|---|---:|---:|---:|---:|---:|---:|---:|
| 100k / tracked platform breakdown | 33,333 | 137.0 | 128.9 KiB | 128.2 MiB | 1 | 0 | 0 B |
| 1M / tracked platform breakdown | 333,333 | 1,409.9 | 205.8 KiB | 187.4 MiB | 1 | 0 | 0 B |
| 100k / source disable-reactivate | 5,000 | 70.3 | 244.0 KiB | 130.1 MiB | 5 | 1 | 20.1 KiB |
| 1M / source disable-reactivate | 5,000 | 111.3 | 242.2 KiB | 191.2 MiB | 5 | 1 | 20.1 KiB |
| 100k / full catalog repair | 50,000 available | 1,149.5 | 2.1 MiB | 139.7 MiB | 26 | 1 | 16.1 MiB |
| 1M / full catalog repair | 500,000 available | 16,233.9 | 7.7 MiB | 203.0 MiB | 26 | 1 | 156.4 MiB |
| 100k / delete + refresh + commit | 100,000 | 503.9 | 363.7 KiB | 144.1 MiB | 2 | 2 | 26.1 MiB |
| 1M / delete + refresh + commit | 100,000 | 736.7 | 702.3 KiB | 215.3 MiB | 2 | 2 | 33.7 MiB |

The accepted #130 ceilings are met: the million-title repair completes below
30 s, every measured projection mutation stays below 256 MiB managed
allocation and peak working set, and no captured command exceeds two
parameters. Source lifecycle and high-fanout command/parameter counts are
identical at 100k and 1M. Per-platform commits bound the million-title repair's
WAL high-water to 156.4 MiB instead of accumulating the whole catalog in one
journal. Both set-based layers are mismatch-only, and a focused
`total_changes()` test proves a clean audit rewrites zero rows. No speculative
index was added: captured plans already use the source and title identity
indexes for scoped work, while the full repair is intentionally an all-title
reconciliation.

The platform-breakdown row measures only the normalized grouped aggregate, not
the surrounding `GetStatsAsync` queries. The lifecycle row deliberately times
the status-write plus source-scoped rollup core in isolation; the real
`SetSourceStatusCommandHandler` composition and its rollback behavior are
pinned separately with SQLite. The high-fanout row commits the destructive
link deletion and rollup so its WAL value is writer evidence; the harness
restores the deterministic fixture after measurement for repeatable runs.
Recovery is non-cancelable and idempotent for committed and rolled-back
operations; startup repairs a durable backup before any scenario reads or
replaces it. The focused 100k qualification was deliberately started with a
pre-existing backup while the original links still existed and restored the
120,000-link/50,000-available-title fixture without collision.

These giant fixtures are local release-qualification evidence, never a PR/CI
gate. A lightweight architecture test rejects any `Romd.ScaleHarness`
reference from default mise test tasks or GitHub workflows; small deterministic
query-shape and command-count proofs remain in CI. Reproduce the focused manual
qualification from commit `25510bca` with:

```bash
dotnet run --project benchmarks/Romd.ScaleHarness -c Release -- --scale 100k --scenario payload-availability
dotnet run --project benchmarks/Romd.ScaleHarness -c Release -- --scale 1m --scenario payload-availability
```
