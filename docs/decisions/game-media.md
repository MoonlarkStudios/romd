# Game Media: Identity And Storage

Related decisions: [media pipeline](media-pipeline.md),
[media delivery](media-delivery.md),
[original disc sets](original-disc-sets.md),
[VFS delivery through WebDAV](vfs-webdav-connections.md).
Implementation status and gaps are tracked in
[media status](../architecture/media-status.md). CAS's physical format and
positional reads are in [CAS random access](../architecture/cas-random-access.md),
and the persisted disc-set relationship is in
[disc sets](../architecture/disc-sets.md).

## Context

Disc images are large, and the same game reaches ROMD as an original image,
a compressed container or an archive. Some formats roughly halve storage,
yet every ROM must stay deliverable as the exact bytes a DAT describes,
because preservation and emulator compatibility depend on them. Different
encodings of one disc must not split one game into several, and a claim
about a file must not outlive or exceed the evidence behind it.

Decoders and container formats are complex, and their output can be wrong
even when a tool reports success. Storage has to account for every retained
artifact, so cleanup never deletes bytes something still needs.

## Decisions

ROMD ingests game media once and preserves its logical bytes exactly. It
persists observed identities, layouts, encoding parameters and verification
results, and computes eligibility, canonical status and delivery from those
facts, registered policy and current host capabilities. A representation may
reconstruct one logical file or several; each ROM selects a preferred output
whose bytes reproduce its identity exactly.

### Identity

- A ROM is a logical byte sequence identified by its length, SHA-256 and DAT
  hashes (SHA-1, MD5, CRC32). The logical SHA-1 always hashes those exact bytes.
- A representation is an immutable stored artifact that contains or encodes a
  ROM's logical bytes, identified by the artifact's SHA-256 and length. CAS keys
  objects by artifact SHA-256 and never returns encoded bytes for a logical key.
- Different encodings, or different settings of one encoding, produce different
  artifacts with the same logical identity. An ISO and an RVZ of the same disc
  resolve to the same ROM. Encoder settings and versions are preparation facts,
  not game identity.
- One artifact can be the logical bytes of one ROM and the encoding of another,
  such as an opaque upload later identified as a disc. Delivery therefore
  addresses content by logical ROM id, never by CAS file id alone.
- Hash domains stay distinct: artifact SHA-256, logical hashes, CHD raw and
  overall SHA-1, and parent identities. A typed identity kind is required before
  MAME disk matching or before match indexes are shared across domains. The
  `DatRom`/`DatDisk` distinction survives projection and matching. File-based DAT
  matching of singleton discs does not require MAME disk semantics.

### Stored Format

PS1 CD-set conversion is Automatic in its kind registration. Imports publish
verified inputs before staging reconciliation demand; eligible complete layouts
can encode without explicit conversion intent on a qualified Linux x64 worker.
Explicit requests remain available for conversion, retry and repair. Changing
mode does not reverse retained CHDs or remove original identities, derived
descriptors or verification evidence.

Automatic requires recorded CUE-regeneration results and read capacity for at
least one mixed-track PS1 disc with data plus CD-audio (ideally pregaps), stated
interactive range latency, and explicit owner sign-off on that evidence. The
registration alone owns the mode; endpoints, planners and clients must not add
a PS1-specific override. Qualification scope is recorded in
[media qualification](../architecture/media-status.md#qualification-status). Exact reconstruction and
aggregate capacity do not establish gameplay responsiveness. Bounded admission
does not preempt active whole-file transfers, and storage qualification makes
no native desktop, credential-persistence or emulator claim.

- Each media type has one stored format: a lossless encoding from which ROMD
  reconstructs the original bytes exactly. GameCube and Wii discs use RVZ;
  PS2 DVDs and PSP UMD game discs use standalone DVD-mode CHD. CUE/BIN discs use a shared CHD when their registered CD
  track kind and qualified capabilities support exact member reconstruction.
  Each kind declares Automatic or OnRequest conversion independently of its
  storage format. Original descriptors are retained or independently established
  by an exact DAT hash match.
  Media without a supported encoding, such as cartridge ROMs, store their
  original bytes, which CAS compresses with seekable Zstandard.
- A storage encoding names an adapter this binary implements: `Identity` (the
  exact bytes of any ordinary ROM; it does not imply an ISO), `Rvz` or `Chd`.
  Adding an encoding requires a qualified adapter and does not enable every
  subtype of its container. A media type gains a new stored format only with
  such an adapter.
- Each stored format has one versioned canonical recipe (for RVZ: codec,
  compression level and chunk size), chosen by measurement for decode cost and
  random-access performance as well as size. An artifact is canonical when its
  container parameters match the current recipe, whoever encoded it.
- Changing a media type's stored format or canonical recipe makes existing
  artifacts non-canonical; [convergence](media-pipeline.md#convergence) brings
  them in line.

### Encoding Profiles

- Every representation records an encoding profile describing how its artifact
  is encoded. Imported artifacts record parameters read from their container and
  never an invented encoder; when parameters are unknown, an explicit unknown
  marker is recorded, and it never establishes that two artifacts share a
  profile. ROMD-created artifacts record the actual encoder recipe and build.
- Container encoding, inspected layout, observation schema and runtime
  compatibility are separate. Captured parameters describe observed bytes;
  recipe ids are the only versioned policy value and are immutable once used.
  A decoder, encoder or inspector change does not by itself change a recipe.
  Desired parameters or a deliberate artifact rebuild require a new recipe id.
- RVZ decoding and encoding are ROMD-owned managed code in `Romd.Media`, using
  managed codec packages. CHD is the exception: hosts read it through a pinned
  native libchdr build, and the worker encodes with a pinned `chdman`. Each host
  verifies these binaries against their build records at startup; see
  [CHD media binaries](../self-hosting/production-deployment.md#chd-media-binaries).

### What ROMD Stores

Each kind converges to its registered stored format and canonical recipe.
A logical ROM refers to an output of a retained representation; several ROMs
may share that representation without sharing their logical identities. ROMD
fixes the stored format and canonical recipe per media type; administrators do not choose them. Changing
either is a deployment that registers a new versioned recipe, and existing
artifacts are reclassified from their captured parameters. Verified artifacts
rank:

1. canonical stored-format artifact;
2. other stored-format artifact;
3. original bytes, when they are not the stored format.

- When a verified artifact outranks the ROM's retained artifact, ROMD attaches it,
  makes it preferred and releases every lower-ranked artifact in the same
  transaction. An uploaded RVZ for a disc stored as ISO therefore replaces the
  ISO, exactly as a conversion does.
- An upload that does not outrank the retained artifact is verified, DAT-matched
  and discarded without persisted evidence, because evidence belongs to a
  retained representation. An exact re-upload establishes verification for a
  previously unverified decoder build; it never overwrites an existing row.
- Attachment requires a catalog match or permission to keep unidentified uploads.
- `Representations` owns every retained artifact. `RepresentationOutputs`
  binds a whole-file or typed manifest-member selector to a logical ROM. The
  preferred output belongs to a retained representation and reconstructs that
  ROM's exact identity. Encoding belongs to the representation; placement and
  ordinal belong to manifests and disc sets, because identical bytes can occupy
  several positions. Storage attribution and cleanup account for every owner.
- Deleting a ROM or releasing a representation removes references only. The file
  ledger deletes a CAS object once nothing references it, including a running
  job's durable source dependency.
- Provisioning starts from one fresh baseline in an empty application database.
  Workers refuse unsupported receipts or unversioned application objects before
  making schema changes. Legacy upgrades, backfills and baseline downgrades are
  unsupported.

### Eligibility

- Eligibility belongs to a file or disc, not a title or release; a release can
  contain several discs and companion files.
- Inspection of actual content decides eligibility: unsupported, supported but
  requiring preparation, or available. A platform or extension only selects a
  candidate adapter. Unknown content is inspected before a conversion is promised.
- A media kind is resolved per DAT entry from observed layout and content
  facts, with platform as a filter. A profile string alone does not establish a
  kind. One validated composition registers descriptors in Application.Common
  and their Infrastructure adapters; incomplete or ambiguous registration fails
  startup. Unclaimed content uses an explicit opaque original-only fallback.
- An inspection observes at most one platform layout family. A kind claims
  content only from its own family's observation or its own container
  evidence, and any observed layout of another family excludes it. Kinds never
  name one another, so adding a kind changes no existing kind's rule, and
  contradictory observations resolve to no candidate instead of to whichever
  kind ignores the contradiction.
- A kind policy owns recognition, conversion eligibility and admission of its
  source layout. An encoding profile owns the stored encoding, recipe,
  canonical parameters and output bound. Kinds compose shared profiles without
  depending on another kind's policy, so a shared codec does not imply shared
  platform eligibility or runtime qualification.
- Host and worker use one codec-family definition for encoder identity, bounds
  and execution selection. This definition establishes no verification proof;
  independently measured reconstruction and source equivalence remain required.
- Shared tool execution owns launch, bounded diagnostics and exact process and
  drain joins. CD and DVD encoders consume that execution boundary without
  making either encoder the other's process-lifecycle owner.
- Kind is computed on read from batched, already-loaded facts, because it
  changes with policy and reference data. It is never persisted as identity or
  authoritative evidence. A future rebuildable projection requires measured
  need and a separate decision keyed by content, inspector and reference revision.
- Recovery material required to reconstruct an artifact, such as NKit's, is part
  of eligibility.
- A GameCube or Wii ISO is eligible for RVZ when inspection confirms a supported
  disc layout, including Wii discs that use the Korean common key.

### Evidence

Each claim is independent, and none implies another. `Verifications` contains
one immutable row per representation and decoder build, including checked
container facts, provenance and per-output logical measurements. Publishing a
row for another decoder never overwrites the row used by an existing host:

| Claim | Establishes | Binds |
| --- | --- | --- |
| Complete output measurement | A complete decode of an exact artifact produced these logical bytes | Artifact SHA-256 and length, encoding, reconstruction profile, actual decoder build, verifier build, logical SHA-256, SHA-1, MD5, CRC32 and length, observation time |
| Source equivalence | A ROMD-created artifact reconstructs a known source exactly | Source identity by hash, encoder recipe and build, the independent comparison |
| Container integrity | Explicit format checks passed for a supported container profile | Artifact identity and checked profile |
| DAT match | Logical hashes match a DAT entry | The exact DAT artifact/revision and its hash semantics |

- Evidence is immutable. Verification for a new decoder build appends a row;
  repeating the same representation/build does not overwrite its existing row.
  Every observed decoder build's row stays retained for the representation's
  lifetime, preserving upgrades and rollback. Database guards reject mismatched
  records, updates and direct deletion while the representation is retained.
  Retained container parameters, ordered metadata, reconstruction manifests,
  logical identities and artifact identities remain immutable. Changes to output
  ownership and preference must preserve the exact measured logical identity.
  Releasing a representation removes
  its own evidence. Source-equivalence evidence names its source by hash, so
  releasing the source does not remove it.
- No record receives invented provenance. Artifacts without evidence remain
  unverified until measured. The first measurement of an unverified imported
  artifact may replace its unknown profile marker with the profile read from its
  container, in the same transaction. A profile with measured evidence is
  immutable.
- A compressed-only upload is supported for every registered kind. Every
  output is independently decoded and DAT-matched. A CHD alone does not prove
  original CUE text, but a deterministic regenerated descriptor whose bytes
  match the DAT hash is the original. A nonmatching descriptor is a derived
  delivery file with its own hash and does not establish preservation. A later
  original may replace that delivery choice without mutating a pinned manifest.
  Reconstruction never establishes emulator compatibility.

### Reading

- `IRomContentReader` resolves logical content through the preferred artifact
  after the caller has authorized access. Generic file storage (DATs, artwork and
  other artifacts) always reads artifact bytes.
- Positional reads compose CAS random access with the encoding's decoder. Range
  reads are not cryptographically authenticated. Full sequential reads verify
  the delivered SHA-256 at EOF.
- Decoded readers are pooled per host under bounded admission, with a shared,
  byte-budgeted block cache keyed by artifact SHA-256. Cached images and blocks
  carry no authorization decision. A failed read discards its pooled image.
- When read capacity is full, interactive reads (VFS, consumer content, admin
  downloads and single-file tickets) join a bounded FIFO, waiting up to 5 seconds
  by default. A full queue or expired admission wait returns `503` with `Retry-After`. Archive
  exports wait a bounded time per file and honor cancellation.
- Every `original` read of an encoded ROM decodes, so read capacity is sized and
  qualified for fully converged libraries. Stored-format and derived-format reads
  stream artifact bytes and need no decoder.
- Reads never convert, transcode or write a decoded copy.

### Multi-file Media And CHD

#### Standalone DVD CHD

- A standalone DVD CHD is a CHDv5 DVD-mode representation of one exact,
  sector-aligned ISO: 2,048-byte units, no parent, and explicit codec, hunk, map
  and metadata limits checked before admission. CHD is the stored format for
  PS2 DVD and PSP UMD game media; both follow the same storage rules as RVZ. RVZ remains
  the GameCube and Wii stored format.
- CHD uses the same inspection, evidence, publication and delivery boundaries as
  RVZ. Verification requires explicit successful container checks plus
  independently computed logical hashes and lengths; a tool's exit status is never
  proof. Repair (`verify --fix`) never runs on an immutable artifact; a repaired
  file is a distinct artifact with new evidence.
- PSP UMD game recognition requires bounded filesystem and executable-header
  observations, with matching UMD and SFO identifiers. A PSP catalog label or a
  DVD-shaped CHD alone does not establish a PSP layout. A positive PSP
  observation excludes PS2 runtime claims, as any foreign layout does, even
  though the storage container is shared.
- Qualification for creating a new kind may be narrower than the existing
  shared decoder qualification. An encoding-only restriction must not make
  previously admitted DVD artifacts unreadable; byte admission and runtime
  applicability keep their independent rules.
- Read admission is per decoder tuple: RID, libchdr upstream pin and native
  build recipe version. Host capability is deployment state derived from verified
  builds and the runtime qualification declared by each kind. Encode capability
  never decides readability. Native identity is verified on every assessment,
  and a failed verification invalidates readiness. A CHD and an original
  reconstructed from it are served only through evidence for the reading host's
  tuple. A host on a new tuple waits for reconciliation or an exact re-upload
  to establish its verification; a host with an admitted existing row keeps
  serving independently.
  The operator procedure is
  [CHD decoder upgrades](../self-hosting/production-deployment.md#chd-decoder-upgrades).

#### Disc Sets

`RomFile` remains an exact file identity. The single-artifact representation
association is never extended into a CD recipe by adding a track number; one BIN
can span tracks. Multi-file media use explicit manifests:

```text
ReconstructionManifest -> Member(path, role, ordinal) -> LogicalFile
Representation -> CAS artifact + captured parameters + verification evidence
RepresentationOutput -> whole-file or manifest-member selector + LogicalFile
DeliveryPlan(authorized set, requested format, runtime) -> delivered files + entry point
```

- One artifact may reconstruct several members; it is not the identity of each.
  Identical bytes may share a logical identity while occupying several positions
  in a set.
- Reconstruction manifests are typed, immutable and bounded (sector stride,
  extents, padding exclusion, subchannel packing, byte order), not a general
  transformation language. WholeFile and CdTracks carry their own completeness
  and descriptor requirements; there is no independent shape policy. Decoders
  open a selected member before multi-output artifacts are admitted.
- Original descriptors and required sidecars are immutable artifacts owned by
  the manifest. Exact DAT hash matches can establish regenerated originals;
  other regenerated or path-rewritten descriptors and playlists are derived
  delivery files with their own hashes and never claim original equivalence.
  Generated M3U bytes assert layout, not archival media equivalence.
- Sets, recipes, outputs, retained descriptors and evidence publish atomically.
  Ledger references, GC and job leases account for every dependency.
- Authorizing one member grants only that member's reconstructed bytes.
  Delivering a whole artifact requires authorization for every member it exposes.
  VFS projects each member with its own metadata, and CHD, sidecar and playlist
  names are planned together so references remain valid.
- Parent-dependent CHDs require explicit dependency ownership. NKit requires
  explicit recovery-material dependencies and independent recovery evidence.
  Unsupported cases are explicitly unavailable.

### Qualification

- Real-media probes record decoder build identities, source hashes, exact
  commands and configuration, and independent output comparisons. They extend
  `tools/Romd.Media.Probe` rather than duplicating production decoder logic.
- Qualification is per deployment architecture, platform, fixture and target
  runtime, and never extrapolates beyond what ran. Archival recovery and emulator
  compatibility are separate results.
- Read adapters are qualified separately from inspection: admission, allocation
  and cancellation bounds, malformed input, and positional and concurrent reads.
- Failure-path evidence covers publication, source retention, rollback,
  interrupted jobs, orphan cleanup, concurrent imports, GC and revoked
  authorization, exercised end to end rather than by mocking a successful
  adapter. Schema transitions refuse unsupported existing data without invented
  evidence or automatic resets.

## Trade-offs

- **Decode on every original read.** Reading `original` from an encoded ROM
  costs CPU, and read capacity must be sized for fully converged libraries.
- **One copy.** A host without a verification row for its decoder cannot serve
  the CHD or reconstructed original, because no separate original is retained.
  Other hosts can continue through their own admitted decoder evidence.
- **Evidence before service.** Unverified artifacts, and CHDs after a decoder
  change, stay unavailable until measured.
- **Fixed policy.** Administrators cannot choose a different stored format or
  recipe for their installation.

## Rejected Alternatives

- **Keeping PS1 OnRequest after mixed-track qualification and owner sign-off.**
  Rejected: Automatic uses the same verified-input boundary, eligibility,
  bounded worker and independently verified publication while avoiding a manual
  conversion request for each eligible new layout. Client runtime qualification
  remains separate from this storage policy.
- **Keeping the original alongside the encoded artifact.** Rejected: it would
  roughly double storage for the largest media. The original is always
  reconstructible from the stored artifact, which is proven by evidence.
- **Administrator-chosen stored formats or recipes.** Rejected: one fixed,
  versioned recipe per media type keeps evidence, convergence and delivery
  consistent across installations.
- **Treating an encoder's or verifier's success as proof.** Rejected: only an
  independent decode of the exact stored bytes establishes identity.
- **A ROMD-written CHD codec.** Rejected: libchdr and `chdman` are the
  ecosystem's reference implementations, so ROMD pins them instead.
- **Track numbers on the single-artifact association.** Rejected: one BIN can
  span tracks, so multi-file media need explicit manifests and recipes.
- **Repairing an artifact in place.** Rejected: artifacts are immutable, so a
  repaired file is a new artifact with its own evidence.

## Guardrails

- **`RomRepresentationTests`:**
  - a represented ROM keeps its logical identity
  - alternate representations stay referenced until their last owner releases them and collection completes
  - a preferred output without its owned representation is rejected at commit
  - an older provisioner refuses an unknown migration
- **`MediaFactSchemaTests`, `MediaContainerFactSchemaTests`,
  `MediaLogicalObservationSchemaTests`, `MediaCdObservationSchemaTests`, `MediaArchiveOwnershipSchemaTests` and
  `MediaJobPinSchemaTests`:** the fresh baseline requires complete measurements,
  preserves each retained decoder build, and binds observations, archive recovery
  and job custody to exact representations and outputs.
- **`RvzMediaProfileTests`, `RvzVerifierTests` and `ChdMediaAdapterTests`:**
  container profiles and verification of RVZ and CHD artifacts.
- **`RomContentReaderTests`, `RomReadAdmissionTests` and
  `ChdDecoderUpgradeTests`:** logical reads through the preferred artifact,
  bounded read admission, and CHD availability per decoder tuple.
- **`MediaKindResolverTests` and `MediaPolicyLiteralArchitectureTests`:** kind
  resolution uses observations and registrations; delivery surfaces contain no
  duplicated media policy literals.
- **Review:** qualification claims are checked against recorded probe evidence
  for the exact architecture, fixture and runtime. Schema changes preserve the
  ownership and immutability rules and require explicit transition approval.
