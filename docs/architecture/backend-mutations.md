# Backend mutations

Application use cases own short database transactions. A mutation, its library
invalidation, and any required metadata work intent must commit together. This
prevents a worker from consuming an invalidation before the corresponding data
is visible, or a successful edit from losing its required follow-up work.

## Composable transaction boundary

Use `IUnitOfWork.ExecuteInTransactionAsync<T>` from
`Romd.Admin.Application.Common.Persistence` around the database portion of a
command. The callback returns `ErrorOr<T>`. The helper commits successful results,
rolls back business errors, exceptions, and cancellation, and logs disposal
failures after a successful commit without reporting that durable write as a
failure. Persistence exceptions still propagate to the caller's error handling.
If mutation or commit fails, secondary transaction cleanup failures are logged
without replacing the original exception or cancellation token. A failed commit
acknowledgement remains uncertain; cleanup does not establish that it rolled back.

Keep persistence-error translation and any permitted whole-transaction retry outside
transaction settlement. Cleanup must not change which primary failure reaches those
classifiers. Taxonomy merge retains one retry only for a confirmed aborted deadlock;
its lock timeout and alias writes retain their existing conflict policy. A lost
commit acknowledgement does not create another retry opportunity.

Some application decisions intentionally succeed without committing. Force
materialization can return an existing active job without flushing unrelated
tracked state. Keep its explicit begin, commit, convergence and enqueue sequence;
use `DisposePreservingOutcomeAsync` only for shared technical cleanup. Record a
primary failure before settlement and mark a commit acknowledged only after
`CommitAsync` returns. Dispose exactly once. Successful ordinary mutations still
commit even when their result reports zero changed rows.
A successful replay does not by itself acknowledge a new commit: queue acceptance
and other explicit no-write branches retain their existing no-flush behavior and
cleanup policy. Preserve primary failures before persistence-error translation;
secondary rollback or disposal cannot change a cancellation into a database error.


Within the callback:

1. Load and validate the state required by the mutation.
2. Stage narrowly scoped writes, library invalidation, and required work intents.
3. Call `FlushAsync` if a subsequent database read must see staged changes.
4. Return the result. The helper flushes remaining changes and commits.

Pass the callback's cancellation token through database operations. Do not nest
this helper inside another transaction or hold it across provider calls, file
processing, or unrelated external I/O. Complete preparation before the mutation
transaction, then revalidate any state on which the write depends. Required
[locked CAS publication](#locked-cas-publication) follows its storage protocol.

Repository methods named `*StagedAsync` leave commit ownership with the caller.
Some perform immediate SQL within the active transaction; staged does not mean
that every operation waits for `SaveChanges`. Mutating queries use `AsTracking`
because the context defaults to no tracking. Avoid passing a partially loaded
aggregate to an operation that treats missing children as deletions: collection
details edits use a dedicated details-only write and obtain their item count
without replacing membership.

## Locked CAS publication

Download, decode, hash and stage file content before opening the transaction.
Publishing that prepared content uses the existing title/catalog lock order,
then globally sorted file locks, held through reference commit or rollback.
This required publication step may remain inside the transaction helper: moving
it outside those locks would race reference creation against collection. It
does not permit provider calls or further file processing inside the boundary.

Use `PublishStagedAsync`; when its receipt requires a flush, the application
owner calls `FlushAsync` before `ReadAfterFlush` and dependent reference writes.
The receipt belongs to its original active transaction. Database rollback does
not undo CAS publication: leave unreferenced objects to the locked collector,
including after an uncertain commit acknowledgement. Do not retry the external
effect automatically or delete potentially shared content as compensation.

File creation exposes only `AddStagedAsync`; file publication exposes only the
staged single-file and batch receipts. Publication never flushes a generated
identity implicitly. Each actual command or worker owner performs a required
flush, reads its receipt inside the same active transaction, and stages dependent
references before commit. Empty and already-recorded batches retain the same
transaction-bound receipt guards. Repeated pending hashes share one tracked
insertion; guarded checksum enrichment preserves known facts without flushing
unrelated tracked writes. The legacy immediate file Add, Store and publication
conveniences are removed.

The worker's `RomdCatalogImporter` retains its existing bootstrap exception:
catalog validation and alias pruning happen under catalog exclusion before
reading, hashing and staging reviewed embedded icons. This bounded offline
catalog installation keeps those operations in its existing transaction; it
does not establish a pattern for interactive command transactions. The blob
bridge publishes every icon in one ordered batch without saving. The importer
conditionally saves file identities, reads them and disposes local staging
before `CatalogArtworkImport.ImportAsync` records asset reservations and makes
its existing dependency save. Typed system/rating imports follow that save in
the same atomic catalog transaction. Primary failures and cancellation survive
secondary cleanup faults; rollback clears the import's scoped DbContext staging.

## System DAT import preparation

`ImportSystemDatCommandHandler` owns platform enablement and its upload job plus
transactional dispatch. It retains one deliberate long-copy transaction: after
read-only inspection and the exact hash pin, it enables/locks the platform,
checks the document hash globally, and only then copies the reviewed input.
This preserves missing-system and already-installed errors before filesystem
errors. This exception does not extend the generic short-mutation helper.

The workspace lease is the worker/orphan exclusion and stays alive through the
owner's explicit flush, commit or rollback, and transaction disposal. Completed
input is retained on failed or uncertain acceptance; a later successful rollback
attempt cannot prove that an earlier commit failed. Known committed acceptance
still returns its job ID if transaction or lease cleanup subsequently fails.

## Library publication

Materialization computes its candidate before the short publication transaction.
`StageMaterializedProjectionsAndActivateAsync` requires that caller-owned boundary
and returns an `IStagedLibraryPublication` receipt reporting whether the revision
check accepted the candidate. The owner retains the receipt through outbox staging,
`FlushAsync`, and commit or rollback. Disposing it releases only tracking acquired
by that publication; it never flushes or commits. Invalidating a candidate likewise
stages its SQL changes inside the caller's transaction.
Publication and transaction cleanup preserve an acknowledged result or the
original failure, including cancellation and lost acknowledgement. Rollback or
disposal failure cannot turn cancellation into an ordinary processing error.

## Disc-set decisions and reads

The application owns the short disc-set decision transaction. It validates the
request, acquires the topology lock, reads the protected target, and checks the
revision before decoding definitions or evaluating member facts. A pure Domain
policy decides acceptance, correction, membership and eligibility from owned
snapshots and source claims. The handler then stages the decision, invalidates
configured or already-published libraries, and constructs its response before
committing those effects together.

The shared transaction helper preserves a primary failure or cancellation through
cleanup and preserves success after an acknowledged commit. A failed commit
acknowledgement remains uncertain; cleanup does not prove rejection or authorize
replaying the decision.

List and options mapping performs no database reads. Each bounded 100-row frame
loads exact platform/source/member claims and system keys once, preserving absent
and ambiguous members. Claims remain partitioned by platform so a stale set cannot
borrow another platform's current member. Options scans discard each frame's facts
before loading the next; they do not retain a catalog-wide cache. Query-count
regressions establish bounded statement growth, not request-latency claims.

## Collection creation

`CreateCollectionCommandHandler` calls `ICollectionRepository.AddStagedAsync`
inside `ExecuteInTransactionAsync`. The repository requires a new, identity-free
collection and the caller's active transaction, and returns an
`IStagedCollectionCreation` receipt. The handler calls `FlushAsync`, then reads
the created aggregate and generated ID through `ReadAfterFlush()` and builds
the response before the transaction helper commits. Reading the receipt does
not flush or commit creation.

## Collection item mutations

Collection writes validate normalized names (200 characters), descriptions
(2,000 characters), and item notes (500 characters) before changing state;
invalid input returns a field-specific 400 error. Domain operations retain their
own guards. Create and details-update commands validate a selected cover inside
their transaction through `ICollectionCoverMediaReader`. Its key-share lock
prevents a concurrent media deletion from invalidating the accepted foreign key
before commit. Missing media is a field error; deletion after commit keeps the
existing foreign-key behavior of clearing the cover.

Add, remove, reorder and item-note commands run in one
`ExecuteInTransactionAsync` callback each. The callback first calls
`LockForItemMutationStagedAsync`, which takes `SELECT ... FOR UPDATE` on the
collection row and only then reads details and membership. Item mutations on one
collection therefore serialize, and each decides from committed state. The
callback then stages one purpose-specific write: insert one item, delete one
item and compact the remaining order, set one note, or apply an ordering. None
of these writes deletes or recreates items absent from a snapshot, and none
writes collection details. Concurrent semantics:

| Overlap | Outcome |
| --- | --- |
| Two adds of different titles | Both persist; the later one is appended after the earlier one. |
| Two adds of the same title | One succeeds; the other returns `Collections.DuplicateTitle` (409), as for a sequential duplicate. |
| Add and remove | Both apply. Removal compacts order over current membership, including a committed addition. |
| Remove of an already-removed title | `Collections.TitleNotInCollection` (404); nothing is written. |
| Note on a removed title | `Collections.TitleNotInCollection` (404); nothing is written. |
| Reorder whose title set differs from current membership | Rejected with `Collections.ReorderMismatch` (400) and nothing is written. Orderings are not merged; the client reloads and reorders. |
| Note and reorder | Both persist: a note write changes only `Note`, an ordering only `SortOrder`. |
| Item mutation and details edit | Both persist. The row lock serializes them, and neither writes the other's columns. |

Item IDs, `AddedAt` and unrelated notes are preserved. Business errors,
cancellation and failed commits roll back the whole callback. The details-only
edit does not take the lock, so its response `ItemCount` can predate an item
mutation that commits while the edit waits on the row.

## Title writes

Title writes go through use-case ports in `Romd.Admin.Application.Titles`, not
one broad repository. No title port has an immediate update or a whole-aggregate
update. Every snapshot write is a staged method that requires the caller's
transaction, verifies and locks the snapshot revision before staging anything,
rotates the revision, and leaves flush and commit to the use case. Each write
names the state it owns:

| Write | Port | Callers | Writes | Never touches |
| --- | --- | --- | --- | --- |
| `UpdatePrimaryMediaStagedAsync` | `ITitleMediaStore` | Set primary media | `IsPrimary` for one media type | Other media, links, layers, ratings, metadata |
| `RemoveMediaStagedAsync` | `ITitleMediaStore` | Delete title media | Deletes one media row and persists the snapshot's recalculated `IsPrimary` flags for the remaining rows | Links, layers, ratings, metadata fields, persistence-owned title columns |
| `ApplyEnrichmentStagedAsync` | `ITitleMetadataStore` | Single and bulk enrichment | Upserted links and layers, appended media, rematerialized state | Children absent from the snapshot; persistence-owned title columns |
| `ApplyMergeStagedAsync` | `ITitleMergeStore` | Merge titles | Revalidates source and target in ID order, then the enrichment write for the target | Source deletion, which stays a separate step (`DeleteAbsorbedSourceAsync`) |

`ITitleMetadataStore` also owns `UpdateMaterializedMetadataStagedAsync` (field
overrides, requeue, rematerialization) and `UpdateUserMetadataStagedAsync` (user
metadata and content ratings).

Provider links change in three places. Curator matches go through the
provider-match service, which takes the title lock, fences pending artwork
imports for a changed game and queues the follow-up enrichment. Automatic
identity publication only adds a missing link the curator has not suppressed.
A merge's staged write adds the source's links for providers the target lacks,
but never for a provider the curator unlinked on the target: that provider's
link, metadata layers and enrichment-sourced media (a source id that is exactly
the provider) are not absorbed. An unlink keeps the provider's curator-collected
gallery images (`gallery:<providerId>`) and artwork-library assets, so a merge
carries the source's to the target like any other.

Only full aggregates reach snapshot writes. `ITitleAggregateReader.GetAggregateAsync`
is the only application member that returns a `Title`, and it always loads
external IDs, media, metadata layers, and content ratings. Lookups, lists, detail
responses, media rows, enrichment selection, and tracking reads use read ports
(`ITitleReader`, `ITitleDetailReader`, `ITitleMediaReader`,
`ITitleEnrichmentStatusReader`, `ITrackedTitleReader`) whose read models cannot be
passed to a write. A write therefore never receives an under-loaded snapshot that
would treat missing children as deletions; the only other `Title` a store accepts
is a new `Title.CreateNew` for creation. Architecture tests pin each port's
members and reject a broad title repository or a port that both loads and writes
aggregates.

Aggregate reads load sibling collections separately to avoid multiplying media,
provider links, layers and ratings in one joined result. A final revision read
validates the complete detached snapshot. A revision change restarts the load,
with a conflict after three unsuccessful attempts. The enrichment-state query
returns 409 when that read budget is exhausted. Media and merge commands also
handle exhaustion during their initial reads, separately from write-side
precondition errors. The reader preserves the
caller's transaction and isolation level and takes no write lock. Mutations that
change several parts of the aggregate must continue to rotate its revision.
Domain collection views do not expose mutable backing collections; primary-media
selection and metadata-layer updates go through the aggregate.
Titles copy media children when constructing, appending or replacing a snapshot,
and reject foreign title identities before changing retained media. Attribution
and source-page provenance are immutable creation or rehydration facts. A retained
child cannot be shared with a caller's input or another title snapshot.

Disc playlist layouts capture their own immutable entry points and derive the
playlist text and SHA-256 from that captured order. Those facts cannot be edited
independently after construction.

Gallery and user media appends (`ITitleMediaStore`) rotate the revision without
a snapshot. Media and artwork writers take `ITitleRowLock` before publishing
files. Deleting title media commits the row deletion, primary recalculation, and
storage-stats outbox event together, then releases the file only after commit
when nothing else references it.
User uploads and gallery imports likewise prepare their complete response before
commit. Their staging and temporary inputs stay owned through settlement;
releasing preparation cannot replace a rejection, original fault or acknowledged
response. Published CAS content retains the locked collector's recovery policy.
 Orphan cleanup (`ITitleOrphanCleanup`) keeps
its enforced caller-transaction precondition.

Child rows are updated in place from tracked entities, so audit columns and
fields the domain does not model survive. Provider calls finish before the
transaction opens; the revision check then rejects a title that changed during
them. Interactive commands read their response inside the transaction after an
explicit `FlushAsync`, so a failed read rolls back instead of misreporting a
committed write. Merge refreshes that response after a best-effort catalog
rebuild and falls back to the committed read if the refresh fails. Single-title
creation uses `ITitleCreationStore.AddStagedAsync` in the caller's transaction.
Its `IStagedTitleCreation` receipt exposes that insert's generated identity
through `ReadIdAfterFlush()` after the command explicitly flushes. Reading the
receipt does not flush or commit. Batch ingest uses `AddBatchStagedAsync` and
`MatchOrStageBatchAsync`; the existing derivation batch owner conditionally
flushes new identities before reading ordered matches or writing dependent
links. It enlists in a caller transaction when present and preserves standalone
per-flush durability otherwise. Its two coordinated batch flushes and tracker
clear remain a separately reviewed protocol. A batch receipt for staged
insertions cannot survive rollback or clearing its tracked staging.

Provider matching is an application workflow over detached match reads and
staged persistence operations. Provider availability and remote resolution run
before the short transaction. Match state, artwork fencing, follow-up work and
the response revision commit together; an optional live preview cannot turn a
durable success into an apparent failure. Enrichment discovers identities once
per title operation, then checks current trust before fetching and applying a
provider result.

Bulk enrichment keeps provider streaming separate from title persistence.
Publication and artwork processing use a fresh scope per title, with that
scope's execution guard and the job's audit actor. Ending the scope releases
tracked rows without clearing unrelated pending state. Provider authentication
tokens are shared independently of these database scopes and bound to the exact
credentials used to acquire them.

Each title advances exactly one aggregate outcome after its final owned mutation
acknowledges. An unacknowledged write records one processing failure without
inventing an enriched or not-found success. Optional notification and later scope
cleanup cannot count an acknowledged outcome again as a failure.

The bulk metadata stream retains its provider and platform map for the job.
Per-title artwork and identity operations read current runtime settings in
their own scopes. An IGDB 401 invalidates only the rejected token lease, reloads
runtime credentials and retries the request once with a fresh token and request
body. A second 401 ends the operation; a rejected batch does not fan out into
individual retries.

## Consumer account writes

VFS create, rotate and revoke commands own their short mutation boundary through
`IConsumerSecurityTransaction`. Its adapter delegates to the shared unit-of-work
helper. The store requires that transaction and holds the per-account lock through
the security checks and commit. New credentials are returned only after commit;
business rejection, faults and cancellation roll back staged credentials.

Consumer password changes verify the current password before opening a short
transaction. The update compares the loaded password hash, security stamp and
concurrency stamp, and requires an active account. A concurrent reset or account
change rejects the stale request. The new credentials and session revocation
commit together; a revocation failure rolls both back.

`ConsumerUserSettingsStore` in Persistence uses one PostgreSQL upsert per complete
consumer settings snapshot.
The application prepares response keys and the complete DTO before invoking that
upsert, then returns it only after acknowledgement. Response preparation failure
therefore cannot leave newly saved settings behind an error response.

Concurrent first saves cannot race into duplicate rows. Later saves preserve
`CreatedAt`; raw SQL supplies timestamps in the same Unix-millisecond format as
the EF converters.

Play-session upserts use Read Committed under their per-user transaction-scoped
advisory lock. The lookup after a lock wait sees the preceding owner's commit;
concurrent first deliveries reconcile the same client-generated session identity.
The persistence adapter compares replayed timestamp identities at Unix-millisecond
precision while the domain and newly supplied facts retain their exact precision.
The response is prepared before commit and
returned only after acknowledgement. Secondary cleanup failures preserve that
acknowledged result or the original failure and cancellation token. An uncertain
acknowledgement propagates; a compatible client retry reconciles the same session
identity against durable state rather than creating a second session.

## Subscription checks

Subscription create/check commands own candidate publication, explicit identity
flushes, response reads and commit. Expected check failures persist their status
and backoff; unexpected scheduled failures use the existing fresh recovery scope.
Written response receipts require their original context, transaction and a
successful owner flush.

These checks retain a legacy transaction spanning signed discovery, download,
review and staging. The catalog/source try-locks preserve the existing `Busy`
response without fetching under contention. Shortening this scope requires a
separately qualified exclusion protocol; this long-I/O boundary remains debt.
The general transaction helper continues to exclude external I/O.

`ApplyDatSubscriptionCommandHandler` owns subscription approval, job acceptance
and dispatch in one transaction. It retains catalog146 before source145 exclusion
through server-held review and workspace copying. Pins are checked before replay;
an accepted job replays before the subscriptions-enabled check. Contention still
answers `Busy` while subscriptions are disabled. Initial import retains the current
actor, stored platform and `subscribed-catalog.dat`; replacement retains the
reviewed private bytes, `reviewed-catalog.dat` and null actor/platform override.
The second preview/reopen/pinned replacement review remains part of that protocol.

The owner stages only the accepted source binding, job, Applying state and cleared
message. Commit flushes those fields, the GUID job and its dispatch together.
Keep the physical worker/orphan workspace lease through transaction disposal and
owned document/receipt cleanup. Completed inputs remain after failed or uncertain
acceptance; rollback attempts and later database reads do not prove safe deletion.
Cleanup preserves the primary fault or cancellation token, and an acknowledged
commit or already accepted replay still returns its durable job identity.

## Catalog and reference settlement

Reference-data mutation callbacks own their business changes and snapshot
publication through the reference transaction. Provider settlement preserves the
original fault or cancellation and an acknowledged result without changing the
catalog lock, isolation or dependency-conflict policy.

Catalog rebuilds retain their savepoint protocol: failed work restores the old
graph and persists the existing Dirty or Failed decision. Once any commit has
been attempted, a lost acknowledgement cannot enter savepoint repair or attempt
a second commit. If repair itself fails, retain the original work cause and
report the secondary failure. An acknowledged rebuild result survives disposal.
Media-fact and CD-layout savepoints preserve cancellation if restoring their
savepoint also fails. For a failed constraint repair, the recovery failure still
fails closed: it does not become `SourceChanged` or authorize continuing in the
same transaction. Successful savepoint repair keeps its existing reuse policy.


An unidentified-ROM purge commits deletion and its event intents before attempting
age-guarded CAS reclamation. After acknowledged deletion, failed reclamation is
logged for recurring cleanup and the response reports only confirmed reclamation.
A purge with no deletions retains its no-commit path and ordinary reclamation
failure behavior.

## Revisions and timestamps

Keep `UpdatedAt` for audit and diagnostics. It is not a reliable concurrency
token: timestamp storage precision and clock behavior can allow distinct writes
to share a value. GUID tokens express equality with a previously read version
without relying on elapsed time or chronological ordering.

Titles carry `Revision`. Repository writes compare the original domain
snapshot's revision and rotate it for the next write. A stale snapshot fails
optimistic concurrency instead of silently overwriting a concurrent edit. Do not
retry by resaving the same stale aggregate; reload and recompute. Bulk SQL and
new mutation paths must respect the revision protocol and preserve fields owned
by catalog projections.

Libraries carry `MaterializationRevision`. Invalidation rotates it atomically
with the relevant write. Materialization reads that token before building its
projection, then activates only if the token still matches. A new invalidation
makes the previous materialization ineligible to activate even when both writes
have the same `UpdatedAt`.

## Background-work acceptance

Background actions return 202 through the host's `JobAccepted` helper: a
`JobReference` body with `jobId`, `Location: /api/jobs/{jobId}`, and no `ETag`.
Application services return job identities; the host owns HTTP locations. A
conditional action keeps its required `If-Match` and 412/428 errors. Polling the
job resource is sufficient to observe completion; realtime events accelerate it.
Bulk-enrichment deduplication returns the existing pending or active job identity.
An overlapping library export returns 409 `Export.AlreadyInProgress`.

Multipart upload acceptance belongs to `AcceptUploadCommandHandler`. Generic,
DAT and ROM input keep their distinct validation and classification order; DAT
and ROM classification read a separate stream from the one copied into the
workspace. Generic request replay samples the acceptance clock before looking
up any job family, then repeats the authoritative lookup under the existing
cross-process worker/orphan workspace lock. Matching replay retains accepted
workspace contents. The owner finishes copying before opening its transaction,
stages the job and commits its dispatch atomically. It holds the workspace lease
through transaction and owned-stream disposal. Complete input survives failed
or uncertain acceptance; an acknowledged commit remains accepted despite later
cleanup failure. Upload and replacement acceptance use application owners and
staged family ports. Their legacy creation facades and immediate repository
creation saves are removed.

Job checkpoint ports require a caller-owned transaction. `UpdateStagedAsync` may
execute SQL immediately; the checkpoint coordinator owns flush and commit. A
claimed checkpoint flushes phase state while holding the execution row lock,
restores the fence and lease, then commits. Its fresh scope releases tracked state
on success or failure. The unclaimed runner fallback also owns a short transaction
and notifies observers after commit. Recovery and artifact-cleanup backstops
recheck current state and apply conditional, purpose-specific writes.
Claim, checkpoint and deferral protocols distinguish an acknowledged commit
from lost acknowledgement. Provider cleanup preserves the original recovery
signal or acknowledged result; cleanup cannot establish that an uncertain commit
failed. Deferral still rearms the dispatch and releases the execution fence in
one commit.
Checkpoint persistence errors remain visible. Optional notification after an
acknowledged checkpoint is best effort; requested checkpoint cancellation still
propagates through its existing shutdown and redelivery policy.
Upload finalization still attempts to flush completed siblings after work fails.
A second journal failure retains the original work exception and cancellation or
ownership-loss signal, while uncertain durability still retains the workspace and
requests the existing recoverable handoff. Recovery reads may confirm a recorded
outcome; a failed recovery read cannot establish rollback or replace that signal.


## Server-local path import acceptance

`ImportFromPathCommandHandler` checks tracked-title eligibility before resolving an
optional body system key, captures the current actor, and inspects the allowlisted
source before sampling acceptance time. Path import retains its existing concurrency
values; the multipart upload range check does not apply to it.

Inspection fixes the selected files. Preparation revalidates each source and copies
originals under the worker/orphan workspace lease, before the database transaction.
Only copies whose owned streams finish successfully enter the workspace manifest.
A failed copy is removed before another file can be accepted; inability to prove
that removal aborts preparation and preserves the original failure. Workers never
receive a partial failed copy alongside successful files.
Move mode writes its fingerprint manifest beside the workspace; source originals
stay untouched until the successful worker cleanup protocol permits removal.
The owner stages the upload and commits its dispatch in one transaction. GUID
acceptance requires no intermediate identity flush.

Keep the same lease through transaction disposal and any discard. Before any commit
attempt, completed preparation can be discarded after transaction disposal. After
any attempted commit, retain both workspace and manifest, including when a later
rollback or database read suggests no acceptance. Preserve the primary fault or
cancellation token through stream, transaction and lease cleanup. An acknowledged
commit still returns its accepted job ID if cleanup subsequently fails.

## Search continuations

Game and title search continuations use the shared strict `KeysetCursor` envelope.
They bind the route family, effective normalized query and filters, resolved library
scope, sort and direction. Changing only the page size keeps a continuation valid.
Malformed, unbound, or incompatible tokens return 400 `Search.InvalidCursor` with
`errors.cursor`; they never restart the search silently. Earlier unbound search
tokens are invalid, so clients restart by omitting the cursor. Tokens are opaque
continuations, not durable bookmarks.

Search positions validate the sort value and positive tie-breaker identity before
query execution. Text positions carry canonical UTF-8 Base64URL inside the shared
envelope so maximum-length Unicode names fit the HTTP request-line budget without
changing stored names or their ordering. Numeric positions use finite canonical
invariant values. The larger search-only token limit leaves other cursor limits
unchanged.

## Job resources and archive state

The common `Job` base owns shared state and start behavior. `DirectLifecycleJob`
adds completion and failure; `CancellableJob` adds cancellation. Required metadata
rematerialization supports direct completion and failure but exposes no cancellation.
Media reconciliation exposes none of these direct terminal transitions: its durable
journal owns completion, failure and cancellation.

After the executor returns, the runner first recovers committed family outcomes.
Attributed cancellation and permanent failure reconcile termination and custody
through the existing family policies before any direct transition. Only an
unsettled direct lifecycle uses a supported terminal capability. A journal-owned family missing a durable
outcome fails closed and retains unfinished work for redelivery. These type boundaries
do not replace execution fences, journal recovery, custody checks or durable
cancellation intent.

`GET /api/jobs` returns `Page<JobDto>` ordered by creation time and GUID descending.
Its `query`, `jobType`, `outcome`, `archive`, `correlationId`, `from`, and `before`
filters share one collection; `archive` defaults to active. Page limits range
from 1 to 100. Cursors bind the endpoint, sort, normalized filters and effective
visibility, so a caller cannot reuse a position after changing those semantics.
`KeysetCursor` in Application.Common shares canonical decoding and fingerprint
checks across these feeds; changing the limit is allowed. Each page reapplies visibility: users see their
own jobs, managers see operational jobs, and only admins see the media
reconciliation family. A missing or empty actor sees no jobs.

`GET /api/jobs/{jobId}` is the cheap polymorphic job representation. Typed feeds
are `items` for upload provenance and `artwork-items` for artwork completion.
They return the common page envelope and reject incompatible families with 404
`Jobs.FeedUnavailable`. Media reconciliation exposes subject/selection progress,
origin selection, outcome and outcome code on the job representation. Its
finite selection journal and complete measurements remain worker-owned. Expensive
artwork aggregates live at `artwork-summary`. Invalid GUIDs, closed filters,
limits and cursors fail with 400; unknown or invisible jobs share 404
`Jobs.NotFound`.

`PATCH /api/jobs/{jobId}` requires the sole field `isArchived` and returns the
updated job. Setting or clearing archive state is idempotent for terminal jobs;
nonterminal jobs conflict with 409. The transaction updates only archive state
and timestamps, preserving execution phase, progress and fences, and resolves
the response before commit. Terminal artwork transitions own pending-selection
cleanup; archiving does not replay execution checkpoints. Cancel also conflicts
for terminal jobs. DELETE requires an archived job (409 otherwise) and retains
501 for permanent deletion until that operation is implemented.

The upload `item-export` artifact is distinct from the paged feed. It streams a
complete JSON array or CSV using bounded keyset batches within one repeatable-read
snapshot held through name resolution and response flushing. A failed or
cancelled response disposes the snapshot without completing it; failure after
headers aborts the response so truncated data cannot appear successful.
Snapshot cleanup preserves the original read, write or completion failure. An
acknowledged full export survives later cleanup without aborting its complete
body; writer cleanup before completion can still prevent completion. Ordinary
taxonomy reads share a read-only snapshot helper that keeps validation outside
the snapshot and the ordered business reads inside it. Nullable results still
complete their read snapshot; the helper adds no retry.


## Durable metadata work

System metadata-policy PATCH commits its defaults, a real
`MetadataRematerializationJob`, its automatic `JobDispatches` row, and the new
job's required `JobUpdated` outbox event in one transaction. The response is 200
with the updated policy, its `ETag`, and nullable `pendingJobId`; GET exposes the
same active job identity. The validator includes sorted defaults and ordered
global source priority, but excludes title count and pending work, so GET always
returns 200 even for a matching `If-None-Match`. Writes use the existing required
`If-Match` precondition seam. A no-op stages no work.

The defaults advisory lock serializes policy writes. Pending-job reuse also takes
`FOR UPDATE` and requires Pending phase, no start time, and no execution fence.
If a worker claims first, a subsequent change creates a successor; if the policy
write locks first, the claim reads the new committed defaults. Reuse publishes
no creation event. Post-commit enqueue is an acceleration: failure leaves the
committed job and dispatch recoverable.

The worker runs the job on the single-worker enrichment queue. It reads bounded
keyset pages of 500 title IDs, resolves each title in a fresh scope, and persists
its cursor only after successful title work or a separately committed fallback
title intent. Count, enumeration, checkpoint, and fallback persistence failures
preserve unfinished work for retry. Cancellation and lost ownership are not
per-title failures. Deferred titles make the completed pass
`CompletedWithErrors`; their internal intents continue retrying independently.
The policy job cannot be cancelled or manually retried through retry-failed.

After the two Hangfire retries exhaust, the sync filter uses its existing durable
recovery transaction: lock the job row, reject terminal, stale-delivery, and
live-lease cases, rearm exactly one dispatch for 30 seconds later, and clear the
execution/transport claims without changing phase, cursor, or counts. Failure
rolls back the whole recovery, leaving the expired claim available to the usual
expiry scan. This recovery emits no terminal event. The family cancellation
policy treats an unattributed cancellation as required work awaiting redelivery.

`IRematerializationScheduler` stages only per-title
`MetadataRematerializationRequests` in the caller's explicit transaction;
scheduling without one throws. These internal convergence intents from title
mutations do not create Jobs rows or `pendingJobId`. The current baseline stores
per-title intents directly, preserving each intent's own creation/due timestamps
and retry state.
There is no title foreign key: deletion before processing is a successful no-op.

Only `Romd.Worker.Host` runs `MetadataRematerializationWorker`, after provisioning.
It reads up to 25 due intents per pass, uses a fresh service scope for each title,
and acknowledges only successful work. Failures increment `Attempts` and become
eligible after 30 seconds; the worker checks every two seconds. Delivery is at
least once, so a crash after title work but before acknowledgement repeats it.
Each intent has its own ID, so acknowledging one leaves later work for that title
intact. Run exactly one worker host; this internal reconciler has no distributed
claim protocol. Inspect intent age, attempts, and worker logs for stalled work.
Global source-priority configuration changes at restart rotate the validator but
do not automatically schedule a global pass.

## Deployment and ownership guardrails

Title/library GUID revisions and metadata requests belong to the current fresh
baseline. Deploy matching hosts worker-first and follow the supported
[database boundary](../self-hosting/configuration.md#database-and-installation-boundary).
API hosts stay unready until expected application/Hangfire versions are published
and must never migrate independently.

The transaction ownership ledger keeps its frozen initial baseline of 52 keys
and 54 operations and additional baseline of 103 keys and 115 operations.
Explicit retirements account for removed migration-debt capabilities; wrapping a
repository save or transaction in a caller's transaction does not retire it.
The architecture guard requires zero remaining measured migration debt and
rejects resurrected capabilities. Intentional unit-of-work, read-snapshot,
worker, bootstrap, batch-flush, publication and savepoint protocols retain their
reviewed classifications and exact sites.

Title-port aggregate writes must be staged, and the architecture checks pin each
port's members. New mutation owners need focused rollback and concurrency tests.
Broad aggregate writes, conflict presentation to clients, and multi-worker
claiming remain separate engineering work.
