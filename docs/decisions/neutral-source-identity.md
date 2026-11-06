# Neutral Source Identity And Provider-Agnostic Title Derivation

Related decisions: [PostgreSQL single provider](postgresql-single-provider.md),
[DAT subscriptions](dat-subscriptions.md)

## Context

Titles are backed by claims from sources: DAT entries, server-side path
imports, manual creation and future scanners. If title backing is keyed by
one provider's identity, no other provider can back a title. Replacing that
provider's payload, such as a new DAT version, then destroys the curation
attached to it. Unattended subscriptions make DAT replacement routine, so
curation must survive replacement.

Sources have lifecycles. An upstream authority can disappear, or an operator
can disable a source, and either must hide the source's contribution without
destroying it. Derivation runs over very large streams on one scoped
`RomdDbContext` that the provider also writes through, so memory and flush
behavior must be bounded and agreed on both sides.

## Terms

- **Source:** a `CatalogSource`, the anchor of one provider's claims, with a
  `Kind` (`Dat`, `Import` or `Manual`) and a `Status`.
- **Entry:** a `SourceEntry`, the neutral identity of one claim from one
  source.
- **Link:** a `TitleSourceLinks` row, the curated fact that an entry backs a
  title.
- **Effective:** visible through `Active` sources only.

## Decisions

### Identity Parent: SourceEntry

- **Entries are the identity.** A catalog-owned `SourceEntry` is the neutral
  identity of one claim from one source. Title links and release provenance
  (`CatalogReleaseSources`) both reference it by `SourceEntryId`.
- **Payload stays with the provider.** Provider payload rows carry a 1:1
  `SourceEntryId`, as `DatGame` does. Only identity crosses the module
  boundary; ROMs, hashes, clones and taxonomy stay provider-side.
- **Entries are internal.** Titles and releases are the public identities. An
  entry gets a public id only when an admin surface must address entries
  directly.

### Entry Lifetime: Source-Scoped, Survives Replacement

- An entry belongs to its source, not to a payload version, and
  `(CatalogSourceId, EntryKey)` is unique.
- Replacing a payload reconciles entries: matched entries stay and vanished
  ones are removed, so links and curation survive a DAT update.

### EntryKey: Per-Kind Natural Keys, No Rename Detection

- **DAT:** the game name. A genuine upstream rename reads as a delete plus a
  create.
- **Import:** the source-relative path.
- **Manual:** a generated opaque key.
- The catalog normalizes keys and enforces their uniqueness per source when
  it writes them.

### Platform: Nullable At Ingest

- `SourceEntry.PlatformId` is nullable. An entry exists as soon as its claim
  does, and routing stamps the platform later: routing is enrichment, not
  identity.
- An established platform never moves. A claim that disagrees with it is a
  validation error.
- No platform sentinel, such as `0`, enters the schema or its ports.

### BIOS Entries Get Identity, Never Links

- BIOS claims create entries but never title links, because BIOS stays
  outside title creation. Entries without a platform get no link either.
- Parsing detects BIOS and marks the claim `IsBios`. The catalog decides what
  BIOS means.
- Classification outranks curation: reclassifying an entry as BIOS removes
  its link.

### Derivation Contract: Paired Streams, Two Verbs, Catalog Writes Entries

- **The catalog writes identity.** Providers push claims, and the catalog
  owns every entry, title and link write through `ITitleDerivationService`.
  The provider remains the orchestrator of its job, saga, progress and
  events.
- **Two verbs.**
  - `ReconcileAsync` reconciles a full snapshot. It upserts claimed entries,
    derives and links titles, and on successful completion deletes the
    source's entries that were absent from the stream.
  - `UpsertAsync` never deletes unseen entries. DAT ingest, routing and
    partial imports use it, and DAT prunes through its own version-retention
    policy.
- **Paired streams.** `TitleClaim` values go in and `ClaimAssignment` values
  come out, exactly one per claim and in claim order, so the provider can
  stamp each database-assigned `SourceEntryId` onto its payload.
- **Lockstep batches.** The catalog pulls claims until its batch fills,
  flushes, then yields assignments. The provider writes its payload rows and
  pulls again. Backpressure is natural, and claims are enumerated once. The
  title matcher receives at most one batch at a time, bounded by
  `TitleMatchingContract.MaxBatchSize`, and keeps no state across batches.
- **Stamp-based deletion.** Reconcile stamps every upserted entry with its
  run and deletes unstamped entries only at successful stream end. Memory
  stays constant, a re-run converges, and the stamp doubles as audit.
- **Curation is durable.** Derivation never overwrites an existing link. A
  claim whose entry is already linked reports the existing title.
- **Errors are per claim.** Providers filter their own parse errors, and each
  output is an `ErrorOr`.
- The interface's documentation carries the complete contract, including
  duplicate-key handling.

### Flush Protocol On The Shared DbContext

Both sides of the paired stream write through the same scoped
`RomdDbContext`, which mixes caller-flushed staged writes with immediate
saves.

- The derivation service flushes its writes before yielding assignments.
- The provider never holds staged, unflushed rows across a pull.
- Neither side calls `ChangeTracker.Clear()` on its own inside the loop. A
  coordinated clear at a batch boundary, after both sides have flushed, is
  the mechanism that bounds tracker memory on large streams.
- The exchange runs inside the caller's transaction scope, and batch commit
  boundaries stay where the provider's workflow puts them.

### Source Lifecycle: Status Gates Reads And Polling, Never Mutations

- **One source per anchor.** There is one `CatalogSource` per provider anchor,
  such as one per DAT source rather than per DAT version. Its `Status` is
  `Active`, `Discontinued` or `Disabled`.
- **Status gates reads, not mutations.** A non-Active source is hidden from
  effective reads and its subscription polling pauses. Admin mutations stay
  open: a disabled source can still be replaced, routed or deleted.
- **Two reasons, one behavior.** `Discontinued` (the upstream authority is
  gone) and `Disabled` (an operator choice) differ only in label and
  subscription behavior.
- **References are derived.** Title-to-source references are read through
  links, entries and distinct sources, never stored. One composable
  effective-read helper (`EffectiveTitleSourceQueries`) encodes the `Active`
  predicate, and every effective read composes it.
- **Dormant titles are kept.** A title whose links all lead to non-Active
  sources is never deleted automatically. Re-enabling the source restores it
  with no re-derivation.
- **Orphaned titles follow policy.** A title with no links is deleted, or kept
  as `UserOnly` when it carries user or curated state, through
  `ITitleOrphanCleanup`. The policy runs in the deleting command's
  transaction and is followed by a projection rebuild. A recurring
  convergence sweep is the backstop, with a creation-age grace window that
  protects titles still being derived.

## Trade-offs

- **Renames cost curation.** An upstream rename reads as a delete plus a
  create, so curation on the renamed entry is lost.
- **One write path.** Every provider writes titles only through the
  derivation service and its flush protocol.
- **Effective reads pay a join** against source status.
- **Dormant titles persist** until their source is deleted or re-enabled.

## Rejected Alternatives

- **Release provenance as the identity parent.** Rejected as a category
  error: `CatalogReleaseSources` rows are rebuild-time snapshots that
  projection rebuilds tear down and recreate. Curated truth attached to them
  would be destroyed on every rebuild, or would force projection rows to
  become stable.
- **Version-scoped entries, deleted and recreated on replacement.** Rejected:
  links cascaded away with each version's games, so manual curation was lost
  on every DAT update.
- **Rename detection.** Rejected now and later: no heuristic can tell a
  rename from a delete plus a create reliably.
- **Stored title-to-source references.** Rejected: a derived read cannot
  drift from the links it reflects.
- **A provider registry or plugin interface.** Rejected until something must
  enumerate providers, such as subscription scheduling across provider kinds.

## Guardrails

- **`TitleDerivationServiceTests`:**
  - one assignment per claim, in claim order, with stable entry ids
  - curation is preserved
  - established platforms are enforced
  - BIOS reclassification removes the link
  - duplicate keys are canonicalized across batch boundaries
- **`EffectiveTitleSourceReadsTests`:** ownership, enrichment evidence,
  exports, materialization and projections exclude non-Active sources, and
  re-enabling restores without re-derivation.
- **`SourceLifecycleStoreTests` and `SourceLifecycleTests`:** status changes
  are idempotent and authorized, truth-level reads ignore status, and a
  disabled source can still be restored and deleted.
- **`DeleteDatOrphanPolicyTests`:** the orphan policy runs inside the deleting
  transaction on real PostgreSQL.
- **`RomdHostingBoundaryTests`:** derivation, source-reference and lifecycle
  ports are registered in curation compositions, never in the consumer.
