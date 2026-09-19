# Import statistics load investigation

During an initial catalog/ROM import, PostgreSQL consumed roughly 7.7 CPU cores
on an eight-core host, while the import worker consumed roughly half a core.
There were no configured libraries. Active expensive queries belonged to the
admin database role, not the worker role. The import continued progressing.

## Amplification path

ROM ingestion emits storage, coverage, and health invalidations in its owning
transaction. The realtime outbox previously broadcast every event separately.
The admin layout mounts statistics queries on every authenticated page, and
its socket handler immediately invalidated active queries for every event.
Statistics HTTP requests did not consume the query cancellation signal.
Together these allow repeated, overlapping aggregate reads during import.
Library rematerialization is a separate path: with no library rows, the
per-platform flagging query has no affected libraries to update.

Small ingestion transactions remain useful recovery boundaries. An archive-wide
transaction would not address the cost of these reads and would defer progress
visibility and increase rollback scope. Notification batching should be
independent of persistence transaction size.

## Query and data model findings

Coverage previously repeated correlated counts of effective DAT ROMs per tracked
title. The rewrite joins tracked titles to effective source links, DAT games,
and DAT ROMs, groups once per title, and aggregates complete/partial counts.
Titles with no effective ROMs remain in the tracked denominator but are neither
complete nor partial. Disabled/discontinued sources remain excluded.

The cataloged-ROM count now traverses effective catalog links and counts distinct
non-null ROM identities. Distinctness matters: several DAT entries can reference
the same held ROM. Inactive source references still count as identified but
unrouted, rather than unidentified.

The observed database already had indexes on source-entry/title links,
tracked title identity, DAT game source identity, DAT ROM game/ROM identity,
and held ROM file identity. No extra index, schema migration, or persisted
summary table was justified by this investigation.

## Measurements and limits

A hand-reconstructed equivalent of the original coverage SQL had planner cost
144,695; a grouped equivalent had cost 2,056. These are planner estimates, not
an observed 70-fold latency improvement. A bounded read-only EXPLAIN ANALYZE of
the grouped equivalent took 2.27 seconds under concurrent production load,
with about 103,000 DAT ROMs and 2,400 tracked titles. This was not an isolated
benchmark of the generated EF query or a deployed performance acceptance test.

The small CatalogSources table had ten rows and no ANALYZE history. The planner
estimated 477 effective source entries where execution found 50,487. That is
an additional statistics-maintenance concern, not evidence for another index.
No live ANALYZE, schema, configuration, or import-state changes were made during
the investigation. Consider targeted ANALYZE after initial catalog loading;
measure plans again before adding automatic maintenance to ingestion.

## Refresh controls

The dispatcher coalesces identical statistics signals within a claimed outbox
batch only after a successful send. Every durable event is still acknowledged
individually; failed deliveries and later batches remain eligible to notify.
The browser groups statistics invalidations into fixed five-second windows,
retains pending invalidations while a request is running, and propagates query
cancellation to HTTP. A continuous import therefore does not continually cancel
and restart the same calculation, and the final dirty state is retained.

The admin host shares one in-flight calculation per dashboard statistic with a
two-second successful-result cache. Its three entries contain global dashboard
results, never user-specific library results. Each calculation owns a separate
DI scope, a 60-second cooperative timeout, and host-shutdown cancellation;
a caller disconnect cancels that caller's wait only. Statistics signals invalidate
the corresponding entry before broadcast. A result computed across an
invalidation cannot become the cached final value, and later callers wait for
a fresh calculation. This cache is local to one admin process, not distributed
across replicas.

## Acceptance after deployment

Repeat a representative import with several admin tabs open. Check database
CPU, concurrent admin statistics queries, query duration, import throughput,
and the final displayed counts after imports stop. Verify that a continuous
stream of updates does not starve refreshes and that a final update arriving
during an in-flight read is eventually reflected. Retest empty tracked sets,
shared ROM identities, partial titles, and inactive sources.
