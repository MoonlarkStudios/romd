# Scale harness

This is a manual PostgreSQL workload harness. Reports and raw SQL plans stay in
its ignored `results/` directory. It does not run in the default test or workflow
gates.

## Complete catalog memory

`--scenario catalog-memory` reuses the current preview fixture at `30k` or
`100k` actual titles. It measures the complete materialization candidate graph
and its projection together, then all-catalog export reads, grouping and real
original-format binding. It records absolute post-collection managed heap with
the result roots kept alive, whole-operation allocations, sampled process
working-set peak, SQL commands and plans. Independent title/member identity and
state checks run outside measurement timing. Forced collections are also outside
latency/allocation timing; warm caches and surviving runtime state remain part
of the process memory figures.

Use `--results-dir` to retain reports in private storage outside the checkout.
See [catalog capacity qualification](../../docs/development/catalog-capacity.md)
for the measured envelope, exact exclusions, safe setup and interpretation.
The representative workload is opt-in; default infrastructure tests use only a
60-title synthetic regression fixture. `all` retains its existing scenario set.

## Library configuration preview

Use `--scenario library-preview --scale 30k` to compare bounded title batches of
256, 1,024 and 2,048 through the real `LibraryDraftEvaluator` and
`MaterializationDataProvider`. The public production constructor keeps one fixed
default; only the harness and tests can override it.

Run against a task-owned, disposable PostgreSQL 18 server on its own loopback
port. The harness creates or replaces `romd_scale_30k_preview` on the configured
server; never point it at a shared development server. For example, after
starting an isolated server and setting `previewConnection` to its connection
string:

```bash
mise exec -- dotnet run -c Release --project benchmarks/Romd.ScaleHarness -- \
  --scale 30k --scenario library-preview \
  --connection-string "$previewConnection" --prepare-only
mise exec -- dotnet run -c Release --no-build --project benchmarks/Romd.ScaleHarness -- \
  --scale 30k --scenario library-preview \
  --connection-string "$previewConnection" --preview-iterations 5
```

The preview fixture has **30,000 actual title rows**. It includes 27,000 canonical
releases, two duplicate source assertions per release, two ROM requirements per
source, 10% candidate-free titles, 40% rated titles and two thirds tracked. The
first source owns complete releases for 30% of titles; the draft excludes that
source, exercising ownership/completeness collapse from duplicate assertions.
Thirty-two shared original identities have complete media graphs published
through `MediaFactPublisher`; no native codecs or actual ROM inventory are used.
The manifest records actual child-table counts, which differ from the older
admin fixture's scale ratios.

Before each measured case, the harness checks global counts, all views, search,
late `afterId`, cursor continuation, page ordering and duplicate release semantics
against independent deterministic expectations. It warms queries outside the
measured iterations. Measurements contain the real preview call and lightweight
batch counters; the full-catalog correctness oracle is outside timing.

The 256 case is the same bounded evaluator with a smaller scope; this comparison
tunes batch size and does not measure the former unbounded implementation. Five
warmed samples describe this fixture, not a production latency percentile.

Compare command counts, latency and whole-operation allocation totals. The
working-set sample includes EF/JIT caches, SQL capture and earlier cases, so it
is not an isolated live-candidate heap measurement. Candidate scopes and page
retention are bounded, but exact global counts still require catalog-sized CPU
and I/O. A title's release/source fanout remains indivisible. Run serially on a
quiet host; the synthetic distribution and warm buffers do not establish a
production latency SLA.

The preview fixture is deliberately separate from `all` and the original
`100k`/`1m` admin fixture profiles. Their database and manifest names are retained.
