# Neutral Source Identity And Provider-Agnostic Title Derivation

Status: accepted

Related issues: #152 (owner), #150, #109, #104, #128, #131, #145

## Context

The catalog's title-backing relationships are keyed by DAT identity. Two
persisted bridges exist:

- `GameTitleMappings` — live curated truth, PK `DatGameId` with FK cascade
  from `DatGames`
  (`src/Romd.Infrastructure/Persistence/Entities/GameTitleMappingEntity.cs`),
  mutated by ingest, move, and merge through the catalog-owned assignment
  port (`src/Romd.Admin.Application/Catalog/ITitleSourceAssignmentStore.cs`).
- `CatalogReleaseSources` — rebuild-time projection provenance carrying
  `DatGameId` and an `AssertedTitleId` snapshot
  (`src/Romd.Infrastructure/Persistence/Entities/CatalogReleaseSourceEntity.cs`).

Because both bridges are DAT-typed, no non-DAT provider (#131 server-side
import, manual title creation, future scanners) can register source backing
for a title. `DatSources` has no status concept
(`src/Romd.Infrastructure/Persistence/Entities/DatSourceEntity.cs`), so
disabling or discontinuing an upstream authority (#145) is not
representable. #152 proposes the target architecture; this ADR records its
Phase 0 decisions. Deployment context: pre-alpha, no external deployments,
and the PostgreSQL cutover (#128) will use a freshly generated destination
baseline plus the one-time verified data migration defined in
`postgresql-single-provider-cutover.md`, rather than replaying SQLite-flavored
migrations or maintaining dual providers.

## Decisions

### Identity Parent: SourceEntry (Option A)

A catalog-owned `SourceEntry` row is the neutral identity of one claim from
one source. Both bridges re-point to it: `GameTitleMappings` is re-parented
as `TitleSourceLinks (SourceEntryId PK/FK -> TitleId FK)`, and
`CatalogReleaseSources` replaces `DatGameId` with `SourceEntryId`.

Option B (release provenance subsumes entry identity) is rejected as a
category error: `CatalogReleaseSources` rows are rebuild-time snapshots
that projection rebuilds tear down and recreate. Hanging live curated
truth off projection rows would either destroy title links on every
rebuild or force projection rows to become stable, silently rewriting the
Dirty/Clean/Failed projection model.

`DatGame` carries a 1:1 `SourceEntryId`. Only identity crosses the module
boundary; DAT payload (ROMs, hashes, clones, taxonomy) stays DAT-side.

### Entry Lifetime: Source-Scoped, Survives Replacement

`SourceEntry` is scoped to the source, not to the DAT version. Uniqueness
is `(SourceId, EntryKey)`. Replacement becomes a reconcile (upsert matched
entries, delete vanished ones) instead of today's delete-all-recreate, in
which mappings cascade away with the version's games
(`src/Romd.Admin.Application/Source/Dat/IDatRepository.cs`,
`DeleteGamesByDatFileIdAsync`) and manual curation is lost on every DAT
update. Durable curation is a prerequisite for unattended auto-apply
subscriptions (#145).

### EntryKey: Per-Kind Natural Keys, No Rename Detection

- DAT: the game name. Stable across versions; a genuine upstream rename
  reads as delete + create.
- Path import (#131): source-relative path.
- Manual: generated opaque key.

No rename-detection heuristic, now or later. Keys are normalized and
uniqueness-enforced per source by the catalog at write time.

### Platform: Nullable At Ingest

`SourceEntry.PlatformId` is nullable. Entries are created at ingest even
for unrouted sources; platform is stamped at routing (`AssignPlatform`).
Identity exists as soon as the claim exists; routing is enrichment. This
also retires the `PlatformId ?? 0` sentinel currently surfaced through
`TitleSourceAssignmentContext` — the sentinel must not re-enter the new
schema or its ports.

### BIOS Entries Get Identity, Never Links

BIOS claims produce `SourceEntry` rows (identity) but never
`TitleSourceLinks` (linkage), consistent with #150's rule that BIOS stays
outside Title creation. Parsing owns detecting BIOS; the catalog owns what
BIOS means. The claim carries an `IsBios` flag so the derivation service
can apply the rule without provider-specific knowledge. Classification
outranks curation: a claim reclassifying an existing entry as BIOS removes
its title link (amended during #159 review).

### Derivation Contract: Paired Streams, Two Verbs, Catalog Writes Entries

Providers push claims; the catalog owns all entry, title, and link writes.
Push topology: the provider remains the orchestrator (job, saga, progress,
outbox events) and calls the catalog sink. No provider-side registry or
plugin interface exists until something must enumerate providers.

The stream is bidirectional because the provider must stitch
database-assigned `SourceEntryId`s onto its own payload rows (`DatGame`).
Precedent: `IMetadataProvider.EnrichAsync` pairs
(`src/Romd.Admin.Application/Titles/Enrichment/IMetadataProvider.cs`).

```csharp
public interface ITitleDerivationService
{
    // Full-snapshot reconcile: upserts claimed entries by
    // (sourceId, entryKey), derives/links titles, deletes entries of this
    // source absent from the stream (purely stamp-based; the service holds
    // no provider-payload knowledge). Providers whose payload rows reference
    // entries must retire those payloads first — DAT instead uses UpsertAsync
    // plus its version-retention prune as the deletion arm (amended during
    // #159 review: the replacement grace policy is DAT domain knowledge and
    // lives DAT-side).
    IAsyncEnumerable<ErrorOr<ClaimAssignment>> ReconcileAsync(
        int sourceId,
        IAsyncEnumerable<TitleClaim> claims,
        CancellationToken ct = default);

    // Incremental upsert: never deletes unseen entries. Manual adds and
    // partial imports use this.
    IAsyncEnumerable<ErrorOr<ClaimAssignment>> UpsertAsync(
        int sourceId,
        IAsyncEnumerable<TitleClaim> claims,
        CancellationToken ct = default);
}

public sealed record TitleClaim(
    string EntryKey, string Name, int? PlatformId, bool IsBios);

public sealed record ClaimAssignment(
    string EntryKey, int SourceEntryId, int? TitleId);
```

Semantics:

- Pull-based lockstep: the catalog pulls claims until its internal batch
  fills, flushes entries/titles/links, yields assignments; the provider
  writes its payload rows against those ids and pulls again. Natural
  backpressure; claims are enumerated exactly once.
- Reconcile deletion uses a per-run stamp on upserted entries (delete
  where stamp differs at successful stream end), not an in-memory seen
  set. O(1) memory; the stamp doubles as audit.
- Upsert-by-EntryKey makes reconcile idempotent: with per-batch commits, a
  mid-stream failure is recovered by re-running; earlier batches converge
  and the deletion pass only fires on successful completion.
- Providers pre-filter their own parse errors (existing ingest behavior);
  the catalog only receives valid claims. Output items are `ErrorOr` for
  consistency with the parsing layer.
- The derivation service sends at most one bounded batch at a time to
  `ITitleMatcher`; both use `TitleMatchingContract.MaxBatchSize` so the
  persistence lookup cannot silently grow beyond the contract. No matcher
  state survives a batch.
- Sequencing: the contract is implementable only once `SourceEntry`
  exists. Pre-cutover the entry id is the `DatGame` PK, so ids flow
  provider-to-catalog — the inverse of this contract, in which the catalog
  mints and yields `SourceEntryId`. The facade therefore lands with or
  immediately after the cutover PR; no pre-schema stand-in is built.

### Flush Protocol On The Shared DbContext

Both sides of the paired stream write through the same scoped
`RomdDbContext`. Contract, stated here because two persistence idioms
already coexist on that context (caller-flushed staged DAT writes vs the
assignment store's immediate save):

- The derivation service flushes its writes before yielding assignments.
- The provider must not hold staged, unflushed rows across a pull.
- Unilateral `ChangeTracker.Clear()` is forbidden inside the loop. A
  coordinated clear at a batch boundary, after both sides have flushed, is
  permitted and is the expected mechanism for bounding tracker memory on
  large streams.
- The whole exchange runs within the caller's transaction scope; batch
  commit boundaries stay where the ingest saga puts them.

### Source Lifecycle: Status Gates Reads And Polling, Never Mutations

One `CatalogSource` per `DatSource` anchor (not per DAT version), carrying
`Kind` and `Status` (Active, Discontinued, Disabled).

- Non-Active status filters effective title references and pauses #145
  subscription polling. It never blocks admin mutations: a disabled source
  can still be replaced, re-routed, or deleted.
- Discontinued (upstream authority gone) and Disabled (operator choice)
  differ in label and subscription behavior only.
- Title-to-sources references are derived reads (`links -> entries ->
  DISTINCT sources`), never stored. Exactly one composable
  `EffectiveTitleSources` query helper encodes the `Status == Active`
  predicate; every read path composes it.
- Dormant (has links, all sources non-Active) titles are never
  auto-deleted; re-enabling the source restores them with no
  re-derivation. Orphaned (zero links) titles get policy: retain as
  `UserOnly` when user state exists, else delete
  (`ITitleRepository.DeleteOrRetainAsync` semantics), then projection
  rebuild. Policy runs in the deleting command's transaction with a
  convergence-sweep backstop
  (`src/Romd.Infrastructure/Jobs/DatReplacementConvergenceSweepJob.cs`
  precedent).

### Cutover Shape: Single PR, Backfill Yes, Dual-Write No, Before Postgres

Pre-alpha clean-switch collapses #152's PR1/PR2 into one cutover PR on
SQLite, landing before the #128 baseline so the Npgsql schema is generated
fresh with the new model:

- Backfill is kept (one `CatalogSource` per `DatSource`, one `SourceEntry`
  per `DatGame`, links re-parented 1:1) because it preserves existing
  curation on the development instance. It is a cheap insert-select.
- The dual-write window is dropped; it protects deployments that do not
  exist.
- SQLite landmine: adding `SourceEntryId` to `DatGames` triggers an EF
  table rebuild, which destroys the `DatGames` FTS triggers (#104). The
  cutover migration must recreate the triggers in the same migration.

## Deferred

- Public Sqid for `SourceEntry`: none initially. Titles and releases
  remain the public identities; entries are internal until the admin DAT
  browser needs to address them.
- Provider-side streaming interface (`ISourceClaimProvider` or similar):
  only if #145 scheduling or another consumer must enumerate providers.
- Rename-detection heuristics: rejected, not deferred.
