# Media Pipeline

Related decisions: [game media](game-media.md) (identity, stored formats,
storage ranking and evidence), [media delivery](media-delivery.md),
[host plane ownership](plane-ownership.md). The shared temporary-file budget is
described in [temporary staging](../architecture/temporary-staging.md), and the
operator view in [media processing](../self-hosting/media-processing.md).

## Context

Inspecting and encoding game media means running complex decoders and
encoders over untrusted, very large inputs. A malformed file can crash a
decoder, exhaust memory or run without end, and a single disc can take minutes
of CPU. That work must never take down the process that holds database
credentials or serves requests, and a failure must cost one file, not a batch.

Converting a library to its stored formats runs for a long time and must
survive restarts, container recreation and cancellation without losing the
bytes it started from. Temporary space is shared by uploads, archives, staging
and encoders, so every consumer of it must be bounded together.

## Decisions

### Responsibilities

- An encoder creates an artifact. Encoding success is not evidence.
- Inspection measures an artifact by decoding its complete content.
- One validated composition registers media-kind descriptors and per-kind
  adapters. Policy names stored format, immutable recipe id, conversion mode
  and runtime applicability. Adapters inspect, classify observed parameters,
  bound operations, encode and decode; callers do not copy format/platform rules.
- Pure policies rank artifacts and decide retention, preference and release from
  identities, evidence and retained representations. They return decisions; use
  cases perform the work. Process limits and temporary space are admission
  concerns, not policy inputs.
- Publication makes results durable.
- Reading requires only read capability. Composing a reader never requires
  inspection or encoding capability.

### Isolation And Resource Bounds

- Only the worker inspects and encodes media. It runs each inspection and each
  encoding in a fresh child process: a dedicated media executable that
  references only `Romd.Media` and receives no database, secret or network
  configuration.
- The child decodes and hashes, then returns a small validated result: logical
  hashes and length, encoding profile and decoder build. Decoded bytes never
  stream back to the host. Verification of encoder output always runs in a
  separate child from the one that encoded it.
- The supervisor enforces a hard memory limit (the .NET GC heap limit on every
  platform, plus an OS limit on Linux), CPU and wall-clock deadlines, and the
  output protocol. Encoding runs at reduced CPU priority. The supervisor clears
  the child's environment, bounds its diagnostics, and terminates and reaps the
  child before its capacity is reused, including after cancellation. A crash,
  limit breach or deadline fails that file, never the worker. A host that cannot
  start a valid child reports that through startup or health checks rather than
  failing individual files.
- After a Linux container recreation, an exception permits recovery on proven
  payload extinction while kernel teardown and reaping may still be finishing.
  It applies only to a recognized run whose recorded parent was PID 1 of the
  old namespace, whose parent and child share the recorded kernel boot and PID
  namespace, and whose original admission lease is independently reacquired
  with the same device and inode. The parent keeps that exact open-file-description
  lock through every unconfirmed use; the child keeps its inherited lock throughout
  payload execution. Lease files are never replaced. The current kernel boot must
  match. Namespace inode labels can be reused: matching labels identify different
  incarnations only when both recorded and current parents are PID 1 and their
  strictly parsed positive start times differ on that same boot. Ambiguous init
  facts stay pinned before any numeric PID lookup. Foreign numeric PIDs or groups
  are never queried or signalled. Missing,
  legacy or mismatched facts remain pinned. This exception does not claim an
  observed reap; normal supervision and recovery in the same namespace still
  require affirmative termination and reap before capacity reuse.
- Third-party codec packages ship with their license notices.
- Reads run in the host process. Every stored artifact has already decoded
  completely in a child under these limits, with the same code. Readers still
  validate every header, table and size before allocation, and are bounded by
  size caps, per-host admission and a shared decoded-block cache.
- Concurrency, admission waits, per-file deadlines and artifact/logical size caps
  are configurable per host. A capacity miss is retryable; it never becomes a
  permanent file failure or deletes the input needed to retry. Malformed or
  unsupported media is a permanent file failure. Once a file is admitted, its
  deadline belongs to that file and does not defer its siblings.
- Temporary bytes come from the shared [staging budget](../architecture/temporary-staging.md).
  HTTP multipart buffering, upload-job workspaces, archive extraction, CAS
  staging, encoder output and the delivery cache each have coordinated limits.

### Ingestion

- Detection reads a bounded prefix and never loads a decoder. An encoded upload
  reserves inspection capacity before staging copies the remaining bytes, holds
  it through complete verification, and releases it before catalog queries and
  publication. Ordinary ROMs compute DAT hashes during the staging copy.
- Inspection completes before DAT matching and publication. Encoded uploads are
  matched on their reconstructed logical hashes. Verification streams the
  decode; it never writes a decoded copy.
- Compressed-only inputs are decoded into independently hashed outputs and
  matched by logical DAT identity. Canonical container parameters avoid an
  unnecessary encode. A missing CUE is regenerated from measured track facts
  and DAT paths; its exact DAT hash establishes an original, otherwise it is a
  derived delivery file and preservation remains incomplete.
- Publication applies the ranking in
  [What ROMD Stores](game-media.md#what-romd-stores). Reconciliation evaluates
  retained facts against the kind's conversion mode and host capabilities.
  Imports publish independently verified inputs before conversion. The same
  transaction commits facts, ownership, preference, the upload outcome and
  subject demand. Only the reconciliation worker captures exact sources and
  job-owned pins, binds the attempt fingerprint, and executes conversion outside
  database transactions. Stopped custody and fenced atomic publication protect
  replacement and cleanup. This publish-then-reconcile boundary applies to every
  kind; CdTracks always use it, including a single compressed CD container,
  because logical completeness and shared ownership can emerge across uploads.

### Convergence

- One durable reconciliation family compares actual facts with desired state
  per logical ROM or reconstruction set. It verifies retained WholeFile or
  CdTracks representations when the resident decoder lacks evidence, encodes
  when parameters differ or a registered recipe explicitly requires a deliberate
  rebuild, subject to conversion mode/capability,
  releases outranked artifacts after verification, or does nothing.
- Worker readiness, explicit admin requests, imports and new facts schedule the
  same family, deduplicated per subject. Reference arrivals and incremental set
  completion re-evaluate the same facts. Startup and read requests never execute
  reconciliation work.
- An `Automatic` `CdTracks` kind reconciles eligible inputs after verified
  publication without inventing an explicit conversion request. Changed original-layout bindings and
  curation decisions stage all affected layout subjects in subject order inside
  the source transaction, because durable demand and its source facts must commit
  or roll back together. Shared members retain independent job pins and layout
  ownership; cancellation releases only its own stopped work. The registration's
  qualified runtime and current decoder proof still gate every CD operation.
- Deterministic attempt results are keyed by source representation, operation,
  recipe, capability fingerprint and limits fingerprint. They retry only when
  the fingerprint changes. Transient capacity, cancellation and restart outcomes
  preserve custody and retry with backoff.
- Re-encoding follows parameter differences or an explicit deliberate rebuild
  in a new immutable recipe. Encoder-build changes alone do not make an artifact
  noncanonical. The retained artifact keeps serving until its replacement is
  verified and published.
- Jobs are idempotent, cancellable, bounded and recoverable, and never run inside
  read requests or database transactions.
- The job owns a durable dependency on its source artifact before staging and
  keeps it across retries; an ephemeral staging lease is not a GC reference.
- Convergence and re-verification materialize CAS input through the shared
  staging factory, never an unaccounted temporary file, and check it against the
  owned SHA-256 and length before decoding or encoding.
- Source, encoded-output and verification reservations share an aggregate
  ceiling. Bounded output is reserved before the encoder starts and grows only
  after admission; reservations last through verification and publication. A
  full pool defers within the job's budget and preserves the owned source.
  Cancellation terminates and reaps the child before releasing leases. A restart
  removes abandoned ephemeral payloads and repeats the deterministic step from
  owned input.
- Verification decodes the exact retained artifact in a separate child and
  compares every output's logical hashes and length with its identity. Results
  are immutable per representation and decoder build; verifying another build
  never overwrites the evidence used by an older host. Encoding provenance and
  source-equivalence claims remain distinct from measured decode identity.
- Publication attaches the new artifact, records its evidence, makes it
  preferred and releases lower-ranked artifacts in one transaction. A failed
  publication leaves the prior preferred representation and the source
  dependency in place until recovery resolves the outcome. The source dependency
  is released only at a terminal job state.
- Wii encoding decrypts each partition's title key with the Wii common key
  selected by its ticket. ROMD ships the standard and Korean common keys for the
  encoder only. Decoding needs no key, because RVZ stores each partition's key.

### Publication

- Artifact bytes are staged before the database transaction begins.
- One transaction locks the affected logical identities and representation set
  in a stable order, re-evaluates the ranking under those locks, publishes the
  artifact under the file ledger lock, attaches the representation, records evidence, releases outranked artifacts, applies
  the use case's own changes (catalog links, tracking, outcome events) and
  commits the job outcome. If the decision under the lock needs bytes that were
  not staged, the attempt fails and retries.
- Publication holds no inspection or encoding capacity. Typed reconstruction
  manifests, outputs, descriptor ownership and complete verification publish
  atomically with ledger dependencies; no member is published with an invented
  identity or a broader authorization scope.

## Trade-offs

- **Process overhead.** Every inspection and encoding starts a fresh child, and
  verification needs a second one, so each file pays process start-up costs.
- **Worker-only throughput.** Media work runs only on the single worker, so
  conversion throughput is bounded by that one host's capacity.
- **Long conversions.** Converging a large library takes a long time, and its
  bytes stay reserved in the staging budget while a job runs.
- **Platform-specific limits.** The OS memory limit and the container-recreation
  recovery exist only on Linux; other platforms rely on the GC heap limit and
  normal supervision.

## Rejected Alternatives

- **Conversion before first publication.** Rejected: it delays verified-input
  availability and requires an import-scoped subject and staged-source custody
  extension, plus separate staging, single-worker recovery and capacity
  qualification. Published-subject reconciliation already owns those guarantees.
- **Inspecting or encoding in the host process.** Rejected: one malformed file
  could crash or exhaust the process that holds credentials and serves
  requests.
- **Verifying output in the child that encoded it.** Rejected: an encoder's
  own view of its output is not independent evidence.
- **Streaming decoded bytes back to the host.** Rejected: the host needs only
  hashes and lengths, and streaming would move bulk decoded data across the
  isolation boundary.
- **Writing a decoded copy to verify or convert.** Rejected: it doubles
  temporary space for the largest media, and streaming the decode is
  sufficient.
- **Treating a capacity miss as a file failure.** Rejected: a full pool is
  temporary, and failing the file would discard input that a retry needs.

## Guardrails

- **`MediaWorkerSupervisorTests`, `ChdMediaWorkerSupervisorTests`,
  `ChdCdMediaWorkerSupervisorTests` and
  `PlayStationMediaWorkerSupervisorTests`:**
  - supervised children
  - operation-specific replies
  - invalid replies fail only their file
  - CPU, stop and reap observation
- **`MediaChildCustodyReadTests`:** the child reads only the record it
  originally opened, and rejects input that grew past its bound.
- **`MediaReconciliationEncodingTests`, `MediaReconciliationJobExecutorTests`
  and `CdArchivePublicationTests`:**
  - capacity misses open no CAS input and release unstarted slots
  - CAS bytes are checked against the owned snapshot before any child starts
  - encoding and verification use separate children
  - blocked publication keeps its reservation
- **`RvzEncodingServiceTests`:** canonical RVZ encoding.
- **Review:** the reconciliation family preserves durable source custody,
  immutable per-build verification, typed output confinement and the operation
  fingerprint's deterministic/transient distinction. Automatic CD conversion
  requires verify-only support, qualified scheduling and measured decode capacity.
- **Review:** changes to the container-recreation exception need a review of
  every pinned-fact rule. A prose rewording counts as a behavior change.
