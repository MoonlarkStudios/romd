# Shared temporary staging

`LocalTempFileFactory` stages ROMs, DATs and artwork beneath
`Romd:DataDirectory/temp/staging-v1`. All hosts using that directory share one
file-count and reserved-byte budget. A process-local semaphore is insufficient
for the split admin/worker topology.

## Configuration

`Romd:MaxUploadBytes` also bounds a single factory-staged file (64 GiB fallback,
matching the upload default). `Romd:TempStorage` configures:

| Setting | Default | Meaning |
| --- | --- | --- |
| `MaximumFiles` | 64 | Live or unreclaimed staging slots across the directory |
| `MaximumBytes` | 128 GiB | Sum of reserved payload bytes across those slots |
| `MinimumFreeBytes` | 1 GiB | Volume headroom required when admitting/growing a reservation |
| `AdmissionTimeoutSeconds` | 5 | Maximum capacity/metadata-lock wait per attempt |
| `CopyTimeoutSeconds` | 3,600 | Deadline for a cooperative source copy after admission |

Configure all participating hosts consistently. The shared ledger records the
file-count, total-byte and individual-file ceilings; a conflicting host fails
closed while any owner remains live. Once every old reservation can be reclaimed,
an empty pool can adopt new ceilings. Byte budgets cannot be smaller than the
single-file limit. Changes do not require a database migration.

Known stream lengths reserve their remaining bytes before copying. Unknown
streams reserve in 8 MiB increments, capped by the single-file limit, before
writing the next bytes. Read buffers are 128 KiB per admitted copy. Completed
temporary files keep their reservations until disposed, including while later
decoding, CAS staging and database publication use them.

Every staging copy computes artifact SHA-256. ROM preparation chooses DAT hashing
from a bounded prefix and filename: ordinary ROMs compute SHA-1, MD5
and CRC32 during that same copy; encoded RVZ containers compute those hashes from
the verified logical output instead. DATs and artwork require only artifact
SHA-256. RVZ detection uses four bytes; ISO/GCM/Wii candidates extend to 32 bytes
to inspect disc magic before media admission. Prefix detection supports
non-seekable inputs and never loads a decoder.

ROM ingestion uses that same prefix to reserve RVZ inspection capacity before
reserving staging space or copying the remaining bytes. A queued file that misses
inspection admission reads only its bounded detection prefix and produces no staged
payload. Admitted files hold the reservation through their copy and complete
verification; they release it before catalog queries and database publication.
Staging failure or cancellation also releases the inspection reservation.
Ordinary ROMs do not resolve the RVZ verifier or reserve inspection capacity.
Admission waits and copy deadlines are separate; waiting for an inspection slot
does not consume the admitted copy's deadline.

Free-space checks choose the deepest matching mounted volume. They account for
reserved but unwritten staging bytes before admitting more work. Headroom is a
point-in-time guard; it cannot prevent unrelated applications or CAS writes from
consuming the same disk. Copy/write failures still trigger cleanup.

## CD descriptor staging

The experimental CD workflow additionally uses
`Romd:DataDirectory/temp/cd-cue-staging-v1` for generated tool descriptors.
chdman selects its parser by extension, so these payloads are `slot-N.cue`.
Original CUEs, BINs, sidecars, requests and CHD outputs use ordinary staging.
This small pool has its own ledger: the configured `MaximumFiles`, at most
512 KiB per descriptor (also capped by `MaxUploadBytes`), and a byte ceiling of
the smaller of configured `MaximumBytes` and descriptor limit × `MaximumFiles`.
At defaults this adds at most 32 MiB of reserved payload beyond ordinary staging.
Both pools enforce configured free-space headroom against their own outstanding
reservations; this is not a combined volume quota.

The same exclusive leases, guarded accounting, sealing, hold and recovery
protocol applies. Descriptor holds are `slot-N.cue.quarantine`, and recovery
requires that exact path under the named descriptor pool. Do not rename a normal
staging payload or create an unaccounted alias to obtain a `.cue` suffix. Hosts
that cannot recognize a new descriptor hold must leave it pinned.

## Ownership and recovery

Stable `slot-N.lease` files remain in place. An exclusive `FileShare.None` handle
owns each slot for its full lifetime; never delete or rename lease files while
hosts are running. A separate short-lived `accounting.lease` serializes budget
changes, reclamation and atomic replacement of `reservations.json`.

Payload bytes live in `slot-N.data`. The ledger reservation is published before
creating or extending a payload. Each subsequent admission checks abandoned
reservations: only acquiring the slot's exclusive lease permits deletion and
reuse. Age alone never establishes abandonment. An abruptly killed process loses
its OS lease; another process then deletes the old payload before reclaiming its
reservation. Both completed-file and mid-copy crashes are qualified.

Before a media child starts using a staged payload, ROMD creates and flushes a
`slot-N.data.quarantine` hold. Inspection cannot start if that hold cannot be
established. The hold is removed only after the child, descendants, readers and
monitor have stopped. If cleanup or hold removal cannot be confirmed, the source
and its reservation remain in quarantine; disposal does not return that capacity.
The live process retains the lease, and guarded reclamation skips the marker even
after the owner exits. All hosts sharing staging must use this protocol version.

Encoder output uses a writable reservation from this same pool. Its complete
per-input cap is reserved before the child starts, and both input and output
receive durable holds. The inspected GameCube/Wii kind selects the canonical
output bound: Wii additionally reserves sector hash exceptions, partition splits
and tables. A cap beyond the media artifact limit is rejected before output
reservation or child launch. RVZ inputs are decoded positionally, without another
full ISO staging copy. After confirmed cleanup, measured output is sealed into
a readable artifact and its reservation shrinks under the accounting guard to
the exact artifact length. The lease remains owned through verification and
publication. Failed sealing never transfers ownership; uncertain cleanup
quarantines both paths. A writable output has no fabricated identity or logical
hashes before it is measured.

Convergence reserves its source, complete output bound and any separate
verification payload as one all-or-none aggregate before copying from CAS. A
capacity miss retains no partial aggregate. Verification currently streams the
encoded output without another staged ISO, so its additional payload charge is
zero. Source and output leases last through verification and database publication;
processing capacity is released after confirmed cleanup and before publication.
The job's separate durable CAS source dependency protects retries from GC.

Versioned media holds identify their exact run record under
`temp/media-runs-v1`. Guarded recovery removes only a matching hold after the
media runtime proves stop or extinction of the recorded Linux namespace incarnation,
and acquires the staging slot and accounting leases. Unknown, legacy or
mismatched holds remain pinned; an expired job claim or process age is never
enough. See [media recovery](../self-hosting/media-processing.md#recovery) for the process and
original admission-lease proof.

Unresolved quarantine requires operator repair. Stop every host using the
directory and confirm its media children cannot use the payload before removing
a quarantine marker. The next guarded admission acquires the exclusive lease,
deletes the old payload and reclaims accounting. Never erase accounting or remove
the marker while a child may still use the source. Process-kill tests do not
establish power-loss durability.

Source failure, size rejection, caller cancellation and copy deadlines remove
partial payloads. Disposal is idempotent. A failed delete or accounting update
does not pretend capacity was returned: the owner lease closes, the failure is
logged, and later admission retries guarded reclamation. Missing/corrupt
accounting fails closed; do not erase accounting to force admission while data or
live owners remain. Filesystem permissions or damaged accounting require operator
repair with the hosts stopped.

This protocol requires exclusive file opens and atomic rename semantics on the
shared filesystem. It was exercised on macOS ARM64 and the Linux ARM64 runtime's
local filesystem. Do not infer NFS/SMB or power-loss durability from those results.
The probe kills processes; it does not simulate a power cut or corrupt disk.

## Scope and migration

During upload ingestion, exhausted staging slots/bytes, free-space admission,
accounting-lock admission and decoder slots raise a typed capacity signal. The
upload executor catches retryable failures within each parallel file action,
lets admitted inspections finish, and persists completed file outcomes before
requesting a durable retry. The first delay is 15–30 seconds; attempts without
progress double that delay, capped at 15–30 minutes with jitter. The job claim and dispatch
record publish that schedule atomically; workers cannot claim it before it is due.
The delay is refreshed after the final outcome flush, so progress persisted only
in that flush also gets the initial 15–30 second delay.
Capacity pressure adds neither a failed file outcome nor a permanent job error
before the retry budget is exhausted. Uploads persist a deferral count, timestamp
and last reason, also exposed as a transient job diagnostic. The count tracks
consecutive deferrals without a newly durable outcome. A successful journal flush,
or recovery of outcomes ahead of the aggregate checkpoint, resets that count.
Eight deferrals without progress are allowed; another stalled request fails the
job with its last reason after completed siblings have been flushed. With no
progress, the scheduled delays total roughly 46–92 minutes before exhaustion;
actual attempt durations add to that window. Progress-making uploads can exceed
eight total deferrals. Retrying only unfinished files prevents a queued inspection
from repeatedly cancelling the files that already hold capacity.
A previously deferred upload older than 24 hours from its first deferral also
fails on re-entry. Time spent importing before the first deferral does not
consume that window.
This is a total retry-age ceiling, including time spent making progress after
the first deferral. Large disc batches on slow hosts can reach it; completing a
file replenishes the stall budget but does not restart the 24-hour window.
Dispatch diagnostics retain the last deferral reason. Cancellation and restart
regressions cover this policy. Retry history is part of the current fresh schema;
new durable progress replenishes the stall budget. An attempt whose only durable progress is its
final outcome flush retries after 15–30 seconds; stalled attempts keep
exponential backoff.

After admission, a staging copy or RVZ inspection that exceeds its file deadline
is a permanent file failure. A dedicated media child that exits without a valid
bounded inspection result is also a file failure. The supervisor records bounded
diagnostics and joins the child before releasing capacity; see
[media processing](../self-hosting/media-processing.md). These failures
leave sibling files eligible to run. Shared admission shortages remain deferrable.
An explicit database data/constraint rejection or transaction rollback is a
known failed commit; only a missing acknowledgement requires outcome recovery.

The accepted workspace remains under job ownership across deferral and host
restart. On redelivery, ingestion skips recorded relative-path/kind pairs and
rebuilds aggregate counts and file errors from the complete outcome ledger.
Retries of an ambiguously acknowledged outcome batch reuse its IDs. A cancelled
job cannot be claimed again; terminal workspace cleanup remains the runner/orphan
cleanup protocol's responsibility. The creator's unidentified-upload permission
is reevaluated on each attempt.

Corrupt media, size violations, overlong outcome paths, inconsistent accounting
and configuration mismatches are permanent failures. Admission shortages and
admission timeouts use bounded capacity retries. Only an unacknowledged transaction commit is
classified as an uncertain publication; a pre-commit outcome/flush failure is
classified directly. Unreadable outcome journals also use an uncertain recovery
signal and the same durable scheduling
protocol. Successful upload ingestion now commits its member outcome in the
content transaction; a redelivery reads that outcome before opening the input.
Archive extraction has separate worker-owned recovery checkpoints
(`{jobId}.extraction.json` beside the workspace), with source retention until
output publication and guarded workspace cleanup. A checkpoint records the
chosen output path before extraction and the completed output, remaining quota
and nested queue before the source is unlinked; a retry rebuilds only its own
partial output. It covers worker/process interruption, not power-loss
durability, and a changed extraction policy or workspace location fails closed.
ZIP/7z support, decoder memory limits and move-source retention are described in
[archive uploads and path imports](../features/archive-imports.md).
No atomic provenance is invented for imports completed by older binaries.

This pool covers the application temporary-file factory, including managed
GameCube/Wii encoder output. ASP.NET multipart buffering, accepted upload-job
workspaces, archive extraction, CAS's own staging and the delivery cache have
separate lifetimes and must receive their own coordinated limits. This is
not a completed end-to-end temporary-storage budget.

Older `temp/*.tmp` files have no ownership lease and are deliberately not swept.
During an upgrade, stop old hosts before separately reviewing/removing confirmed
orphan legacy files. This implementation never guesses that an unleased file is
safe to delete. Long-lived job recovery inputs must be owned CAS artifacts or
explicit durable job dependencies, not these ephemeral lease-backed paths.
Convergence uses such a durable CAS dependency and releases it only with a
stopped terminal outcome; see [durable convergence](../self-hosting/media-processing.md#durable-convergence).

In particular, `AcceptUploadCommandHandler` prepares input beneath `temp/jobs`
while holding the job's execution lock through acceptance and cleanup. A
matching replay retains accepted workspace contents. An accepted job must survive
the accepting process exiting: do not apply this pool's process-death reclamation
rule to those workspaces. Their future quota/recovery integration needs durable
job ownership (or owned CAS input artifacts), not just a file lease.

## Verification

`mise run test` includes factory tests and the real-process ownership/crash probe.
The latter writes an evidence JSON in the OS temporary directory and prints its
path. To choose a report path:

```sh
dotnet run --project tools/Romd.Media.Probe -- --staging-probe /absolute/report.json
```

The real-media mode also stages each uploaded RVZ through the factory, verifies
its hash, prepares that staged file and publishes those exact bytes to its test
CAS. See the [probe instructions](../../tools/Romd.Media.Probe/README.md).

## Future CAS materialization and conversion

Convergence and re-verification staging rules are defined in the
[media pipeline decision](../decisions/media-pipeline.md#convergence).
