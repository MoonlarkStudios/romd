# Game media architecture

This guide describes the current implementation. The rules and rationale live in
[game media](../decisions/game-media.md), [media pipeline](../decisions/media-pipeline.md)
and [media delivery](../decisions/media-delivery.md). Operators use
[disc compression](../features/disc-compression.md),
[media processing](../self-hosting/media-processing.md) and
[decoder upgrades](../self-hosting/production-deployment.md#chd-decoder-upgrades).
[Media status](media-status.md) owns the qualification matrix and remaining gaps.

## Policy and composition

`RomMediaPolicyComposition` validates one registration per encoded kind and
produces the descriptors, adapter registry, delivery policy and reconciliation
view together. Current registrations cover GameCube/Wii RVZ, PS2 DVD and PSP UMD
whole-file CHD, and PS1 CD-track CHD. An opaque fallback makes no conversion or
runtime claim. A filename or platform label alone establishes no media kind.

Descriptors in Application.Common name the kind, platform scope, stored
encoding, immutable recipe id, conversion mode, manifest type, profile versions
and native runtimes. The composition supplies pure fact matchers with immutable
configuration; they perform no I/O. Callers use one catalog's frozen descriptors
rather than comparing matcher delegates from separate compositions.

Infrastructure adapters supply inspection, container-parameter classification,
output bounds, encoding and decoding. Eligibility remains kind-specific: sharing
the DVD CHD codec does not give PSP the PS2 reference rules or runtime claims.
`RomDeliveryFormatCatalog` owns format labels and extensions once for all kinds.
Whole-file kinds have one platform and share the original extension of their
encoding. Startup rejects incomplete or ambiguous registrations.

`WholeFile` describes one logical output. `CdTracks` describes complete measured
tracks, BIN grouping and retained descriptors. The latter requires complete-set
encoding but permits scoped member reads. The current CD recipe composition
accepts one CD-track kind; adding a second requires layout-specific kind/recipe
resolution before another registration is sufficient.

`IMediaHostCapabilities` derives decode/encode ability from the verified deployed
builds and registered runtime qualification. Only the worker encodes. PSP has
Linux x64 encoding qualification separate from the shared DVD read path; PS1 CD
operations require their registered Linux x64 capability. Every assessment reads
current capability. CHD admission freshly verifies the resident build; a failed
check invalidates that build rather than using a cached approval.

## Facts, ownership and publication

Persist observations and byte identities; derive kind, eligibility, canonical
status and preferred delivery from current policy. Relevant persistence boundaries
are grouped below; the [MediaFacts source](../../src/Romd.Persistence/MediaFacts/)
owns the guarded DDL and current table definitions.

| Boundary | Persisted responsibility |
| --- | --- |
| `Files` | Physical SHA-256, length, storage size and captured artifact hashes |
| `Roms`, `RepresentationOutputs` | Logical identity, audited filename, preferred output and exact whole/member ownership |
| `Representations` and container parameters | Physical file/encoding, numeric RVZ/CHD observations and ordered raw CHD metadata |
| Reconstruction manifests, tracks and members | Immutable geometry, placement, BIN grouping and descriptor dependencies |
| Verifications and outputs | Complete measurements for each representation and observed decoder build |
| Logical/boot inspections | Structural observations bound to measured logical identities, including PS2, PSP and CD facts |
| Creation records and sources | Encoder/recipe provenance and independently measured source witnesses |
| CD archive owners and catalog bindings | Retained layout custody, exact catalog requirement and association revision |
| Reconciliation jobs, subjects, requests and attempts | Demand, captured sources, job pins, failures, terminal journals and finite selection items |

Reads load the complete graph in a consistent snapshot. Mutation paths fence the
catalog context, then acquire logical, representation and artifact/descriptor
file locks in order before rechecking publication authority. Complete measurement,
ownership, preference, source release and terminal job outcome commit atomically.
A transaction conflict replays the complete fenced operation with a fresh context.

Verification records are immutable and retained for the representation lifetime.
A new decoder appends a measurement instead of overwriting an older reader's
proof. Raw boot inspection and encoder creation provenance are independent facts;
successful verification never invents an encode. Source-equivalence witnesses
remain meaningful after their original source ownership is released.

Archive custody survives logical-ROM withdrawal and later layout decisions for
admin recovery. Jobs pin exact representations, outputs and descriptors through
deferral and restart. Cleanup releases an interpretation only after its logical,
archive and job owners are gone; ordinary file-ledger/CAS collection follows.
Several immutable interpretations can share physical CHD bytes without sharing
descriptor identity, ownership or decoder measurements.

An unmeasured holding grants no readable bytes or encoding authority. Logical
reconciliation settles an unmeasured identity source as `source-unverified`;
its inspector cannot bootstrap the first proof. Import preparation establishes
that proof and verified publication stages new demand. A representation-specific
verify-only request can measure retained encoded bytes. CD publication requires
complete reconstruction measurements.

## Resolution and delivery

`MediaKindResolver` consumes already-loaded content facts and per-entry platform
context. One surviving candidate is unique; multiple candidates are ambiguous;
incompatible evidence contradicts the context. No matching observations use the
opaque fallback. An empty platform collection permits evidence-only resolution;
a null platform is an explicit unclaimed context. Multiple matched platforms must
agree on one unique kind. Resolution is computed, not persisted as a projection.

Kind supplies runtime claims and stored VFS projection; it does not suppress held,
admitted bytes. CHD byte admission needs complete output evidence, the exact
resident decoder tuple and decode capability. Encode capability, a profile string
or emulator applicability alone cannot admit a read. Ambiguous/fallback outcomes
can still deliver verified CHD and decoded originals without runtime claims.

Manifests list held formats, including `Unsupported` rows without targets, grants
or runtime claims. Available entries carry server-owned display data and exact
byte targets; the client chooses a format rather than negotiating via `Accept`.
The whole-container scope requires authorization for every exposed output.

Stored VFS projection needs a uniquely applicable whole-file kind with a native
runtime. Otherwise it retains the original namespace. Portable collisions and
descriptor/playlist dependencies can also force original projection. See
[delivery contracts](consumer-delivery.md) and [VFS](vfs.md).

## Import and reconciliation

`RomPreparationService` independently measures input before catalog matching and
publication. `RomRepresentationPublisher` atomically records facts, ownership,
preference, the upload outcome and `Import` subject demand with
`conversionRequested: false`. Input is available before optional conversion.
CD completeness can emerge across uploads, including a single uploaded CHD.

The unified reconciliation family consumes desired policy, scoped holdings and
current eligibility. Its pure planner chooses verify-only, encode, release an
outranked representation, or no work. The worker captures exact logical/original-
layout/catalog-layout subjects, source identities/revisions, recipe, decoder,
encoder, capability/limit fingerprints and job-owned pins before external work.
Catalog-layout capture includes the complete current requirement, actual retained
descriptor and immutable binding revision. Finite selections retain high-watermarks
across all four subject namespaces.

Inspection and encoding run outside database transactions under supervised child
custody and shared staging admission. Publication rechecks current facts, policy,
ownership and the execution fence under ordered locks. Changed captures replan;
deterministic failure retries require a changed fingerprint. Transient capacity,
cancellation and restart preserve the owned source and use bounded recovery.
Worker readiness, imports, new facts and explicit requests schedule the same
family; startup and reads never execute its work.

Complete eligible PS1 layouts use Automatic after verified publication. Partial
or ambiguous layouts retain originals until their missing facts arrive. Explicit
conversion/retry remains available and does not sweep already settled demand.
Source pins and stopped custody remain required for both Automatic and explicit
work. Shared BINs follow their verified selected backing, including another
disc's archive; a foreign archive is not re-encoded just to move a logical owner.

## Compressed-only input and descriptors

Stored-format uploads decode into independently hashed outputs for DAT matching.
Matching canonical parameters avoid another encode. CD intake publishes the
complete measured graph, BIN owners, catalog binding and upload outcome together.

A formatter derives a CUE from measured track metadata and DAT paths. Only an
exact measured DAT descriptor hash/length match establishes an ordinary original
ROM; otherwise the generated descriptor has its own identity and `Derived`
provenance. Track matches and `chdman extractcd` text never prove original CUE
bytes. Layout completeness separately accounts for exact descriptor coverage and
all bound companions; filenames and another held descriptor are not evidence.

Catalog bindings own the graph and every decoder row until exact-revision
withdrawal or catalog removal. Reads reject catalog drift and prefer available
DAT-exact originals. A later authentic CUE can adopt retained canonical CD bytes
only after independent comparison and atomic ownership publication; that path
creates no encoder provenance and preserves other owners' generated graphs.
See [disc sets and layouts](disc-sets.md) for original/derived delivery snapshots.

## Version and extension boundaries

| Change | Required response |
| --- | --- |
| Desired codec/level/hunk/chunk parameters | New immutable recipe id; compare retained parameters and rebuild differences |
| Deliberate rebuild with identical parameters | New recipe id with the deliberate rebuild policy and reason |
| Decoder RID, upstream pin or native build recipe | Re-verify retained artifacts for that decoder; preserve earlier proofs |
| Encoder build, same desired parameters | Retain canonical artifacts; record the new encoder on new creations; changed capability can rearm failures |
| Inspector version | Reassess stale observations rather than inventing facts |

The current [fresh database boundary](../self-hosting/configuration.md#database-and-installation-boundary)
refuses unsupported existing schemas. Embedded baseline SQL is hash-pinned and
immutable; later schema work adds migrations. `RuntimeFactGrants.sql` is separate
idempotent startup grant policy. No evidence conversion or baseline downgrade is
implied by a decoder upgrade.

PS2 CD and additional CD kinds, GDI/folder-tree manifests, MAME dependencies and
broader descriptor profiles need their own implementation and qualification.
The [qualification matrix](media-status.md#qualification-status) distinguishes
implemented paths from measured workloads. Backend/media changes follow the
[validation routes](../../AGENTS.md#validation), including real custody, atomic
publication, rollback, replay and cleanup checks.
