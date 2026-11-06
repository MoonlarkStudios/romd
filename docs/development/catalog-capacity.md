# Catalog capacity qualification

Library materialization currently retains the complete candidate graph and its
projection. Background export planning retains its selected files, grouping and
archive members. Bounded content-read batches do not bound these complete
results. The bounded library configuration preview is a separate workload.

The scale harness's `catalog-memory` scenario exercises these retained graphs
against the current media schema. It uses the same synthetic fixture as
`library-preview`: 10% candidate-free titles, two source assertions per canonical
release, two ROM requirements per source, 40% rated titles and two thirds tracked.
Thirty percent of titles own a complete first-source release. Thirty-two shared
original identities have complete published media facts; no codecs or private
ROM inventory are required. Actual child-table counts are in the manifest.

## Running the qualification

Use a task-owned disposable PostgreSQL 18 server with a distinct loopback port.
Set `capacityConnection` to that server's connection string. The harness creates
or replaces `romd_scale_30k_preview` and `romd_scale_100k_preview` there; never
point it at a shared development or production server. These database and
manifest identities are shared with the preview scenario, so run them serially.

Run from the repository root using the pinned toolchain:

```bash
umask 077
capacityEvidence="$(mktemp -d "${TMPDIR:-/tmp}/romd-capacity.XXXXXXXX")"
for scale in 30k 100k; do
  mise exec -- dotnet run -c Release --project benchmarks/Romd.ScaleHarness -- \
    --scale "$scale" --scenario catalog-memory \
    --connection-string "$capacityConnection" \
    --data-dir "$capacityEvidence/manifests" \
    --results-dir "$capacityEvidence/$scale" --preview-iterations 3
done
```

Keep the reports, connection settings, exact source revision and runner/container
identities private. Retain sanitized conclusions and the PR's validation evidence.
The harness exits nonzero when an iteration or independent completeness check
fails. Cancelled calls propagate cancellation and dispose their scopes; the
small PostgreSQL regression also verifies a subsequent run still succeeds.

## Measurement coverage

| Case | Included | Excluded |
| --- | --- | --- |
| Complete candidates and projection | Real `MaterializationDataProvider` reads; complete candidates and `MaterializedLibraryProjectionBuilder` output held together | Catalog rebuild, activation writes, invalidation/outbox work, concurrent operations |
| All-catalog export read and bind | Real `ExportRepository`, title grouping, archive member paths, `RomContentCatalog` reads and `ExportArchivePlanner.BindAsync` with production delivery policy | Library-scoped authorization, job checkpoints/progress dictionaries, ZIP writing and content I/O |

Independent expectations check every selected title and archive member identity,
ownership/completeness/playability flags, canonical-release collapse, member
paths and bound logical ROM IDs. Correct counts alone cannot pass a substituted
identity or incorrect state. The fixture has low release/source fanout and high
content reuse, so it does not represent many unique large media graphs.

Latency and allocation timing cover the actual read/build/bind call, excluding
warmup, correctness verification and forced collections. Allocation is a process
managed-allocation delta during that call. SQL command counts/plans come from
the first measured iteration. `Rows` means output title-plus-release rows for
materialization and bound member rows for export; manifests and case notes name
the input populations separately.

Retained managed heap is the absolute process heap after a full collection while
every result root remains alive through `GC.KeepAlive`. It includes surviving
EF/runtime caches and SQL capture. Reports also retain the pre-operation heap
and net deltas for diagnosis; replacing earlier graphs can make a delta small
or negative, so it is not the graph's size. Process working set is sampled every
10 ms and can miss shorter peaks. It includes earlier cases and warm caches,
and excludes the PostgreSQL server's memory. Neither number is an isolated
object-size measurement.

## Capacity conclusions

The synthetic 30,000- and 100,000-title workloads complete with exact population
checks. Complete candidate/projection and export planning remain proportional
to retained catalog membership. This establishes exercised source workloads,
not a bounded-memory guarantee, supported maximum catalog size or minimum RAM.

Representative measurements are recorded in the associated PR with the exact
source revision and environment. Native Linux amd64 release qualification,
concurrent workloads, write-side activation, high-fanout catalogs, more unique
media identities and codec resource budgets require separate evidence before
advertising a deployment capacity or latency commitment.
