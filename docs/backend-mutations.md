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

Within the callback:

1. Load and validate the state required by the mutation.
2. Stage narrowly scoped writes, library invalidation, and required work intents.
3. Call `FlushAsync` if a subsequent database read must see staged changes.
4. Return the result. The helper flushes remaining changes and commits.

Pass the callback's cancellation token through database operations. Do not nest
this helper inside another transaction or hold it across provider calls, file
processing, or other external I/O. Complete that work before the short mutation
transaction, then revalidate any state on which the write depends.

Repository methods named `*StagedAsync` leave commit ownership with the caller.
Some perform immediate SQL within the active transaction; staged does not mean
that every operation waits for `SaveChanges`. Mutating queries use `AsTracking`
because the context defaults to no tracking. Avoid passing a partially loaded
aggregate to an operation that treats missing children as deletions: collection
details edits use a dedicated details-only write and obtain their item count
without replacing membership.

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

## Durable metadata work

`IRematerializationScheduler` stages a `MetadataRematerializationRequests` row in
the caller's explicit transaction. Scheduling without one throws. Title,
platform, and global requests share this durable intent mechanism; an enqueue
failure must fail the owning mutation rather than be caught as best-effort work.

Only `Romd.Worker.Host` runs `MetadataRematerializationWorker`, after schema
provisioning. It reads up to 25 due requests per pass, creates a fresh service
scope for each title request, and deletes it only after rematerialization succeeds.
Platform and global requests expand into durable per-title requests; expansion
and parent deletion share one transaction. A failed title can then retry
independently without losing work for the other titles.
Failures remain pending, increment `Attempts`, and become eligible after 30
seconds. The worker checks again on a two-second timer between passes.

Delivery is at least once: a crash or cancellation after work completes but
before acknowledgement repeats the request. Rematerialization must therefore
remain safe to repeat. Each enqueue has its own ID, so acknowledging one request
does not remove another enqueue for the same title. Run exactly one worker host;
this reconciler has no distributed claim/lease protocol. Persistent failures
remain in the table and are logged; inspect their age, attempts, and worker logs
when diagnosing stalled metadata.

## Deployment and remaining debt

The `AtomicBackendMutations` migration adds title/library GUID revisions and the
metadata request table. Deploy the matching worker first so it applies the
migration and publishes the bumped expected schema version (17 for this change).
API hosts remain unready until the expected schema versions are published. Use
the normal [split-host deployment procedure](split-host-topology.md); API hosts
must not migrate independently.

This pattern is being adopted incrementally. The frozen repository transaction
ownership ledger still records repository-owned saves and transactions; these
are not retired merely because a caller now wraps them in a transaction. Existing
collection item mutations still replace aggregate membership and own saves.
Continue migrating use cases with focused rollback and concurrency tests, and
record retirement only after removing the actual save/transaction site. Broad
aggregate writes, conflict presentation to clients, and multi-worker claiming
remain separate engineering work.
