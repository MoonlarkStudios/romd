# DAT catalog publication protocol

Status: implementation proposal following the accepted
[distribution decision](decisions/dat-subscription-distribution.md).
See [upstream qualification](reports/dat-upstream-qualification-2026-09.md)
for current evidence and unresolved release conditions.

## Resource model

The proposed publisher is `romd-dat-catalogs`. Publish static resources with
an open versioned contract; instances need no GitHub account or API token.
Keep scripts/configuration in Git and large DAT artifacts in release/object
storage rather than growing the source checkout on every acquisition.
Final hosting and signing implementation remain to be selected and tested.

| Resource | Contract |
| --- | --- |
| Current metadata | Small authenticated document referencing the current snapshot's exact URL, byte length, SHA-256, monotonic sequence, and expiry. |
| Snapshot | Immutable index of every supported catalog, current artifact, upstream acquisition status, and exact RSS resource reference. |
| Artifact | Immutable original DAT document, identified by SHA-256 of its exact uncompressed bytes. An optional archive has its own independent byte length/hash. |
| Feed | RSS 2.0 release events with stable GUIDs and exact artifact links, generated from the same snapshot state. |

Keep three identities separate: stable catalog ID, immutable document hash,
and publication event ID. A repeated document is a no-op acquisition; an
explicit upstream restoration of an older document can still be a new event.
Never derive order from filenames, DAT header dates, or content hashes.

Catalog IDs identify authority plus upstream system and variant, independent
of display-name changes or ROMD's local platform IDs. Maintain an explicit
mapping registry and review new/ambiguous identities before publication.
Each record declares public coverage, variant settings, upstream URL,
acquisition time, last successful check, last content change, document format,
counts, provenance, and current artifact. A failed check keeps the previous
artifact and successful-check time. It may update health and last attempted
check. A supported catalog awaiting its first success has no current artifact.
Disappearance from discovery is not authorization to remove a catalog.

## Publication and trust

1. Acquire each source independently with bounded download, timeout, archive,
   decompression, and parsing limits. XML external entities and remote DTD
   resolution must be disabled. Reject unsafe archive paths, duplicates,
   ambiguous document identities, and malformed/empty candidates.
2. Compare extracted document bytes with the accepted version. Preserve
   original DAT content, header, notices, and attribution. Structural
   validation and anomaly checks precede publisher acceptance.
3. Upload new immutable artifacts and verify their readable bytes. Construct
   the snapshot and feed using new accepted artifacts plus retained artifacts
   for failed sources. Partial acquisition failure need not fail publication.
4. Upload immutable feed and snapshot resources and verify their references.
   Advance authenticated current metadata last with a single-writer or
   compare-and-swap guard. An interrupted upload cannot expose broken current
   references. Orphaned uploads can be collected after a retention grace period.

The trust design must authenticate current metadata and transitively bind
snapshot and artifact hashes. A checksum served beside an artifact is not
authentication. Define bootstrap keys, rotation, compromise recovery, expiry,
and rollback/freeze resistance before production AutoApply. Prefer a reviewed
metadata/signing design; selecting its concrete library is a separate
implementation decision. A publisher signature attests to publisher output,
not to authenticity of an upstream response fetched over HTTP.

Cache the highest accepted publication sequence. Expired or unverifiable
metadata pauses new automatic updates while current catalogs remain usable.
Restoring an older DAT uses a new publication sequence; it must not require
accepting stale metadata. A newly installed instance still needs a trusted,
fresh bootstrap, not merely the largest sequence it has happened to see.

## RSS and catch-up

RSS includes human-readable title/summary, publication time, a stable event
GUID, and an enclosure/link to the exact DAT version. Machine metadata binds
each event to catalog ID, document hash, and publication sequence. Do not
infer identity by parsing the title. Publisher summaries cover catalog
changes; ROMD computes local collection/library impact itself.

A stable RSS URL can serve ordinary readers. ROMD treats it as a notification
hint and verifies candidate references against authenticated snapshot metadata.
An event appearing before a client's index refresh triggers reconciliation;
it cannot authorize an unlisted artifact. Feed retention and artifact retention
are distinct policies. Feed truncation never limits catch-up.

On first subscription, after downtime, and periodically during normal polling,
ROMD reconciles the complete index. Coalesce intermediate versions to the latest
eligible candidate rather than replaying every feed event as an activation.
Retain the exact history of versions actually downloaded/reviewed/activated.
Duplicate or out-of-order events do not enqueue duplicate work. Publisher
restoration events do not override a user's local rejected-version policy.

Poll daily by default with jitter, conditional HTTP requests, bounded backoff,
and Retry-After handling. Lack of an upstream ETag is not a failure: bounded
GET plus document hashing remains valid. Keep upstream acquisition health
distinct from instance connectivity and local activation/convergence health.

## Required executable fixtures

Use synthetic DAT documents for committed protocol fixtures. Real upstream
downloads stay outside source-controlled tests unless their reuse is qualified.

| Scenario | Required result |
| --- | --- |
| First subscription | Index resolves catalog and exact document without historical RSS entries. |
| Repacked ZIP | Same extracted document produces no content-change event. |
| Variant mismatch | Candidate is rejected rather than rebound to another catalog. |
| Feed truncated or out of order | Latest eligible index version is found; no duplicate activation. |
| One acquisition fails | Other catalogs advance; failed catalog retains its artifact and reports failure. |
| Publication interrupted | Current metadata references only fully available verified resources. |
| Artifact modified or metadata unverifiable | No new activation; working catalog is retained. |
| Frozen/expired metadata | Automatic update pauses with a meaningful health state. |
| Legitimate upstream restoration | New event/sequence can refer to a retained older document. |
| Local rollback/rejection | Routine polling does not immediately reapply that rejected document. |

The publisher protocol tests and the ROMD consumer tests should share these
fixtures. They do not replace application tests for exact-baseline diffs,
source curation, fenced/replayed activation, and library convergence.

The companion repository now exists at
<https://github.com/JackSkylark/romd-dat-catalogs>. Initial commit
`02d300c2593383535f9d3ab571089aa08e74d2e3` supplies a local unsigned publisher,
synthetic DAT/manifest fixtures, and 22 tests. CI passed on Python 3.11 and
3.13. It intentionally uses an experimental format and does not implement
authenticated metadata, expiry/freeze protection, remote publication, a stable
RSS alias, upstream acquisition, or ROMD integration. Those requirements above
remain pending; the prototype is not a stable production protocol.
