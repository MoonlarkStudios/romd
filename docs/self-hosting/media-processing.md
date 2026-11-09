# Media processing children

> [!TIP]
> New to how ROMD compresses discs? Start with the
> [disc compression guide](../features/disc-compression.md). This page covers
> how the worker isolates, limits and recovers that work.

The worker inspects RVZ containers and recognized GameCube/Wii, PS2 and PSP ISOs in a dedicated
`Romd.Media.Worker` process. Managed GameCube and Wii encoding also use a fresh
child. The executable directly references `Romd.Media`; it receives the input
path and bounded processing options through
arguments. The supervisor clears the inherited environment before launch. The
child receives no ROMD database, identity, provider or network configuration.

The child reconstructs the original disc and computes its logical length and
SHA-256, SHA-1, MD5 and CRC32 for DAT matching. It also measures the uploaded
artifact's identity and container parameters. Its small versioned response is
validated against the staged artifact and packaged build before ROMD accepts
the measurement. It does not return decoded disc bytes through its output pipe.
RVZ measurements use `rvz-disc-supervised/v1`; Nintendo ISO measurements use
`nintendo-iso-supervised/v1`, with key-free `nintendo-iso/v1` layout inspection.
Historical measurements remain intact.

The managed encoders write the fixed `rvz-canonical/v1` recipe. Encoding takes an
explicit typed disc kind from inspected eligibility; the child checks the complete
source layout and rejects a kind mismatch. ISO and RVZ inputs use the same logical
layout checks. RVZ input is decoded positionally without a temporary ISO. Wii
encoding selects the standard or Korean common key from each partition's ticket,
handles hash-tree exceptions and packs reproducible junk. Keys stay inside the
encoder and are never logged; RVZ decoding uses embedded partition keys.
Linux encoding launches through the existing `/usr/bin/nice` before CLR threads
are created; macOS preserves an
already lowered process priority or lowers it before opening input. The child
checks the applied priority, then returns only measured artifact
SHA-256/MD5/SHA-1, length, container parameters, recipe and encoder build.
Encoding success supplies no verification or source-equivalence evidence; a
separate child must fully decode the output. The strict protocol is
`romd-media/v13`, including independently captured RVZ header parameters, joined
tool CPU accounting, the inspected disc kind for ISO and RVZ sources and separate
measured PS2 boot and PSP UMD observations. An RVZ reply requires its typed parameters;
missing facts and contradictory header/kind observations are rejected.
The CHD inspection path adds explicit native identity, DVD header and
integrity facts, and staged rejections. It requires both stored CHD hashes and
measured logical digests. Worker composition supplies it with the configured
libchdr build; without one, CHD inspection refuses.

The shared POSIX launcher can also start a tool within the admitted child's
existing process group. It inherits the same admission lease at fd3, without the
parent worker's ready channel at fd4. The launcher owns one direct-child wait;
failed stream setup terminates and joins that child before closing its pipes.
The isolated Linux DVD encoder uses this launcher with the canonical
`chd-dvd-canonical/v2` recipe: 2048-byte unit, 16384-byte hunk, zstd and one
processor. Recipe selection and its measured scope live in
[media status](../architecture/media-status.md#dvd-chd).
The encoder verifies the
tool candidate, then copies chdman and `/usr/bin/prlimit` into sealed Linux
memfds. The sealed chdman bytes must match the pinned size and SHA-256 before
any process starts, and both programs execute from those descriptors. The
executed image is known by construction, so a tool that exits immediately is
accepted on the same evidence as a long one. The tool runs under direct-child
ownership, inherits the worker's group, lowered priority and limits, and
receives the reserved output cap through RLIMIT_FSIZE during writes. Regular source and output handles reject
links and special files. It checks source identity again after the tool stops.
Both bounded diagnostic drains and the direct-child wait finish before return.
A `ToolImageUnavailable` failure from the
retired post-exec observer means a launch path bypassed `ChdmanToolLauncher`;
use sealed-image launch rather than reintroducing timing-based observation.

The existing outer supervisor supplies the wall deadline and descendant cleanup;
a pipe-holding descendant can keep the encoder pending until that cleanup.
Measured raw output and coherent DVD header facts supply no decoded integrity:
separate CHD inspection remains mandatory. The paired `encode-chd` operation runs
through the concrete supervisor with an explicit tool candidate and reserved
output. Its strict response binds source identity, tool identity, output cap and
joined tool execution; diagnostic tails stay inside the child. It shares the
existing admission, durable permit, source/output custody and complete-stop
requirements. Worker composition wires CHD convergence only when the libchdr and
chdman builds are configured and verified; unverified builds refuse before
staging or process capacity is used. Worker images built for linux/amd64 and
linux/arm64 configure both; API images configure only the decoder. Platform
qualification is recorded separately for each RID; see
[CHD media binaries](production-deployment.md#chd-media-binaries). Activation does
not change the [known limitations](#known-limitations) below.

The worker reads its chdman candidate from the build record in
`Romd:Native:ChdmanDirectory` and verifies it at startup, before any launch. It
measures the binary and record bytes and checks the record's schema, RID and MAME
pin and the binary's size and SHA-256; verification neither spawns nor loads code.
Engineering source provenance and the upstream MAME pin remain separate facts.
This verification supplies no release qualification or proof of a later process
image; the caller retains immutable tool custody through launch.

## PSP UMD compression

The `psp-umd` whole-file kind uses the existing standalone DVD CHD encoder and
`chd-dvd-canonical/v2` recipe. New PSP encoding is qualified on Linux x64. Its
Automatic policy uses the common verified-input publication and reconciliation
path; PS2 DVD reference requirements do not apply to PSP.

The bounded `psp-iso-umd-game/v1` inspector requires a single-volume 2048-byte
ISO9660 filesystem with the `PSP GAME` system identifier, file-structure version
1 or 2, matching UMD_DATA.BIN and PARAM.SFO disc IDs, category `UG`, BOOTABLE=1,
and an EBOOT.BIN PSP header. Both unversioned filenames and `;1` names are
recognized, with ambiguous aliases refused. Descriptor scans, directories and
SFO structures have fixed bounds; paths, extents, endian pairs, duplicate keys,
overlaps and allocation sizes are checked before reads. Every directory record
must be well framed, but only the six selected entries must be conforming
single-extent, single-volume records with printable names; other entries are
skipped, and a selected name recorded more than once, such as a multi-extent
file, is refused. The 2-GiB image bound is
an admission limit, not a claim that every image under it is supported. Header
recognition establishes no signature authenticity or emulator compatibility.

Original and CHD inspection record the same raw PSP facts. CHD observations are
admitted only after the full decode confirms the hunks used by recognition and
all logical and container hashes. Schema 73 adds nullable immutable PSP
observations to the existing inspection resource and extends the captured disc
kind; the schema-72 baseline resources are unchanged. Existing observations are
not backfilled or relabeled. Hosts fail readiness until the worker provisions
the additive migration.

PSP encoder qualification does not narrow shared DVD read admission: retained
bytes still require the existing verified decoder tuple and decode capability.
This adds no ARM64 qualification claim. PSP advertises no qualified emulator
runtime. Admin/consumer original and CHD
delivery use existing admitted-byte paths; WebDAV retains original ISO projection
until a stored-format runtime is qualified. Native ARM64, packaged deployment,
PPSSPP/Ottercade startup and gameplay qualification remain separate gates.

The [probe guide](../../tools/Romd.Media.Probe/README.md#psp-umd-qualification)
documents independent DAT binding and supervised real-disc measurements.

## Read capacity

Read delivery uses the separate `Romd:MediaRead` capacity and cache settings.
Decoded-reader admission uses one bounded FIFO across encodings, with four active
readers, eight waiting artifact snapshots and a five-second default wait. Explicit
caller timeouts still apply, including zero for fail-fast admission. Queue exhaustion
and timeout refuse without opening a decoder; cancellation, failed opens and shutdown
release queued source custody. Returning a slot grants it to the oldest waiter before
new work can claim it. The shared cache remains 64 MiB and idle-reader limit remains two.
The VFS HTTP admission queue remains separate; no priority depends on platform,
encoding or requested range. FIFO does not preempt an already active whole-file stream,
so long or slow transfers can still exhaust the bounded wait. See the
[read settings](configuration.md#media-processing) for limits and overrides.

Imported Wii RVZ groups larger than 2 MiB share their unpacked payload and hash
exceptions across reconstructed regions and concurrent readers. These source
groups use the same byte-budgeted cache as reconstructed blocks; eviction or an
insufficient budget permits decoding again. The canonical 128 KiB recipe keeps
its direct group decoding path.
The admin and consumer hosts do not launch inspection or encoding children.
Every read of a CHD, or of an original reconstructed from one, requires retained
evidence measured under the reading host's decoder tuple (`RomChdDecoderBuild`:
RID, libchdr upstream pin and native build recipe version). The host re-verifies
its resident build for each admission and caches no decision; a failed
re-verification disables CHD reads until restart, which admin and consumer
report through `native-media`. Changing a tuple
field needs the [CHD decoder upgrade](production-deployment.md#chd-decoder-upgrades)
procedure.

## PS1 original-disc compression

The Linux x64 worker uses the registered Automatic policy for complete eligible
PS1 layouts after verified input publication. Upload the complete original
CUE/BIN set together first. Verified imports and layout changes queue durable
reconciliation demand atomically; eligible sets can encode without an explicit
conversion request. To request conversion, retry or repair, use **Disc layouts →
Compress to CHD**, or `POST /api/media/reconciliation/subjects/OriginalLayout/{id}`
with a fresh `requestId` UUID and `conversionRequested: true`. The API stages
intent; the worker captures current sources and policy, then copies, encodes,
independently verifies and publishes. Reads and host startup never encode.

DAT-matched CUEs retain normal ROM ownership and an original descriptor attachment;
unreadable retained CUE bytes become a `DescriptorUnavailable` layout issue.
Incomplete or ambiguous sets keep their originals. Mode changes do not reverse
existing CHDs or remove original identities, derived descriptors or verification
history. Proof-bound ownership and the worker's existing processing/staging
limits remain required. This mode change introduces no sweep of settled demand.

The [qualification matrix](../architecture/media-status.md#qualification-status)
and [PS1 read/install workload](../architecture/media-status.md#ps1-storage-and-read-load)
record the basis and limits for Automatic. They do not extend native desktop,
credential-store, gameplay or deployed-ingress support.

Admission requires a pending or accepted layout. Its complete raw BIN set must match
one unambiguous current PS1 catalog release by SHA-1 and size; an accepted layout must
match the current release it confirmed. Catalog CUE text is a reference: a renamed
original CUE is retained exactly and is never declared equivalent to a different DAT
descriptor.
Required sidecars currently fail closed; direct CHD uploads do not establish original
file boundaries or CUE bytes. A BIN that another disc's archive already reconstructs,
such as an audio track shared by two regions, still counts as an exact original: the
worker stages it through verified selected-member reconstruction.

The fixed recipe is `chd-ps1-canonical/v1`: pinned chdman `createcd`, `cdzs,cdfl`,
39,168-byte hunks (16 CD frames), one processor. The encoder and a fresh verifier
independently recognize the bounded PS1 root PS-X EXE profile. Every original BIN
must reconstruct with the same length, SHA-256, SHA-1, MD5 and CRC32. Original CUE
bytes remain separate. This establishes storage equivalence, not emulator/gameplay
compatibility. PS2 CD, cooked tracks, subcode, split-track file boundaries and other
boot/layout profiles remain outside admission.

The reconciliation attempt pins **all** physical inputs before external work.
One processing admission and a complete staging reservation cover originals, request
and output, with the generated tool CUE in its own bounded pool. The ordinary staging
file/byte limits apply: a set needs one slot per original plus two, so large multi-BIN
sets may need more configured slots. An attempt that cannot fit fails closed; raise `Romd:TempStorage:MaximumFiles` or
`Romd:MaxUploadBytes` (see [temporary staging](../architecture/temporary-staging.md))
on every host before requesting again. Source
pins survive deferral and worker loss. Cancellation or terminal failure releases them
only after child custody is confirmed. Publication rechecks the layout, catalog,
recipe and decoder under the job fence and ordered ROM/file locks, then commits all
member associations, preference changes, the terminal journal and source release in
one transaction. Layout paths retain their original logical hashes after their
physical backing becomes the shared CHD. A logical BIN has one backing: every layout
that binds it, including another release's, follows the newest archive without a new
decision or revision. A changed captured source causes replanning; a foreign
archive that already supplies a BIN remains verified backing rather than being
re-encoded merely to move that logical owner.

Layout decisions made after publication keep the result. Confirm binds the archived
originals, and a repeated request adopts an archive that already renders the current
decision instead of encoding again. A request arriving during active subject
work records newer demand; the worker rechecks that decision before publication.

Per-file admin downloads, consumer grants and VFS reconstruct only the selected
original BIN through the shared decoder pool/cache. They cannot expose a sibling or
the raw container. Whole-container downloads use separate authenticated routes:
`GET /api/original-disc-layouts/{id}/archive` for admins and
`GET /api/releases/{releaseId}/cd-archive` for consumers. The admin route serves the
layout's newest qualified archive after any later decision, including detach and
reject, as the recovery lane. The consumer route requires a live layout (pending, or
accepted for that release) and the captured catalog identity, then checks every BIN
against the current published library in one consistent snapshot. A missing member,
revoked release or unavailable decoder closes that lane before CAS open. Append
`/cue` to either whole-disc route to recover the retained original CUE, with its
original hash and filename; it is never replaced by a DAT template.
These routes carry no emulator applicability claim and do not alter console
installation or disc-set/playlists behavior.

### Retained decoder upgrades

Recipe comparisons and read-load results are maintained in
[media status](../architecture/media-status.md#ps1-cd).

The bounded [CHD re-verification campaign](#chd-decoder-re-verification)
includes retained production PS1 and Saturn archives on Linux x64. It reconstructs every
whole BIN from the CHD, checks all recorded hashes, container geometry/integrity,
retained CUE/sidecars and a fresh observation of the disc's boot profile, then appends a proof bound
to the exact immutable creation manifest and current decoder. Original BINs are
not required. Member and whole-container reads remain closed on a new tuple until
that proof commits. Native ARM64 qualification and runtime launch/gameplay
remain separate work. Automatic scheduling retains these same per-build checks.

Recorded retained-upgrade/recovery results and their native-platform limits are
in [media status](../architecture/media-status.md#qualification-status). Re-run
the gate for changed decoder builds; the procedure above does not transfer an
old qualification to new inputs.

## Saturn original-disc compression

Complete Saturn CUE/BIN layouts use the same fenced publication, whole-member
verification and source-custody protocol as [PS1](#ps1-original-disc-compression),
with explicit conversion through **Disc layouts → Compress to CHD**. Catalog
matching resolves the layout's own platform before choosing its recipe. Pending
layouts must match exactly one complete current release across the registered CD
platforms; accepted layouts must still match their pinned release.

The separate `chd-saturn-canonical/v1` recipe uses `cdzs,cdfl`, 39,168-byte hunks
and one processor. Its encoder and independent verifier require
`saturn-cd-ip/v1`: a raw MODE1 first track with the Saturn hardware identifier,
bounded initial-program size, product code and area symbols. The header digest
and extents are retained as Saturn observations, never PS1 executable facts.
Required sidecars and unsupported track layouts retain their originals.

Saturn is OnRequest pending real-disc recipe/load qualification. The shared
multi-disc ZIP path can select one verified CHD for each eligible disc and retain
original layouts for the rest, with an ordered M3U. Original CUEs remain exact.
No Saturn native-runtime or gameplay claim is registered. Qualification and
activation limits live in [media status](../architecture/media-status.md).

## CD inspection and compressed-only uploads

The `chd-cd-inspection/v2` measurement also attempts bounded PS1 root-executable
or Saturn initial-program
recognition directly from the first raw data track, using observed track type
and stored pregap. No CUE, DAT or original BIN is required. A positive observation
caches at most 512 sectors and checks them against the same full hunk pass whose
integrity hashes are verified; contradictory native reads refuse the measurement.
Boot facts have a 1 KiB wire bound. PS1 filesystem extents must fit both the
declared volume and the first track, and executable entry points must be aligned.
Saturn initial-program extents must fit the first MODE1 track. Both positive
families in one measurement are refused.
A missing observation means this bounded recognizer did not establish a supported
root boot layout. It supplies neither a catalog match nor a gameplay claim.

The separate `inspect-chd-cd` operation measures CD track projections inside the
same supervised child envelope. It is available through
`IMediaWorkerSupervisor.InspectChdCdAsync` and the
[CD inspection probe](../../tools/Romd.Media.Probe/README.md#cd-track-inspection).
It remains separate from the singleton DVD inspector. Original-set PS1 production
compression uses this CD protocol through the whole-disc path above. Normal CHD upload
admission reads a bounded standalone v5 header: 2,048-byte units select DVD inspection,
and 2,448-byte units select raw CD inspection. Inspection finishes and its child stops
before catalog association or a content transaction begins.

CD inspection accepts standalone v5 geometry with 2,448-byte units, at most
450,000 padded frames, 256 frames per hunk and a 2 GiB artifact. It requires
one through 99 contiguous tracks in checksummed CHT2 metadata. Its initial
projection supports MODE1_RAW, MODE2_RAW and AUDIO without subcode. Cooked
tracks, other metadata families, non-checksummed metadata and embedded subcode
are rejected explicitly. Parent-dependent and earlier-version CHDs remain
unsupported. These bounds describe inspection, not a CD encoding recipe.

One full hunk pass measures the raw CHD SHA-1 and each track's SHA-256, SHA-1,
MD5, CRC32 and length. The track projection takes 2,352 bytes per stored frame,
includes stored pregaps, excludes CHD track alignment, and reverses 16-bit audio
samples into BIN byte order. Raw data sectors remain opaque: no ECC repair,
sector synthesis or XA normalization occurs. Generated gaps are recorded and
are not synthesized into a projection. Both stored raw and overall hashes must
be present and match. The exact metadata payloads are bound to both the parsed
track table and the verified overall hash, so a changing source cannot substitute
a different layout between metadata reads. The input artifact is rehashed after
measurement.

The CD response is a distinct protocol variant with no singleton `inspection`,
DVD evidence or PS2 observation. The parent validates its track geometry and
binds its artifact hash and length to the staged input. Protocol v9 permits at
most 192 KiB of stdout for CD inspection, including all 99 track measurements
and exact metadata payloads;
other operations retain their 8 KiB limit. It retains strict field/depth checks
and carries no reconstructed bytes.

A measured track is not automatically an original logical file. A source BIN
may span several tracks, and an uploaded CHD cannot establish exact original
CUE bytes or source file boundaries. Authorization, qualified CD encoding and runtime delivery must
meet the [multi-file representation design](../decisions/game-media.md#multi-file-media-and-chd)
before activation. The [representation roadmap](../architecture/media-status.md) tracks
those gates.

### Compressed-only CD intake

A normal upload job can ingest a standalone CD CHD without its original files. Every
measured track must match the length, SHA-1, MD5 and CRC32 of one complete, unambiguous
registered catalog entry. The current projection requires one BIN per measured track;
unsupported grouping, required sidecars and ambiguous matches are refused.

The worker formats a deterministic CUE from measured geometry and catalog filenames in
its separately bounded CUE pool. A full expected DAT-identity match is independently
verified and published as an ordinary original ROM. A mismatch retains the actual CUE
identity as a derived descriptor and leaves the original descriptor requirement unmet.
Release and disc-set manifests deliver those retained derived CUE bytes under their own
signed descriptor capabilities. Required per-file provenance distinguishes `Derived` from
`Original`; `isDeliveryComplete` describes the obtainable file set while `isComplete` still
describes original inventory. New manifests prefer a full DAT-exact original. An already
issued descriptor capability keeps its captured identity, and redemption checks that exact
retained scope before opening and verifying its bounded CAS bytes. Web downloads label
generated CUEs explicitly. Console installs persist actual identities and provenance
without grants, and refuse changed generated bytes or provenance during grant renewal.
These guarantees establish exact file delivery, not emulator gameplay qualification.

Artifact staging precedes the caller's transaction. Publication fences the complete
catalog scope, then commits actual BIN roots and outputs, archive facts, CUE ownership,
preferences, demand and the upload outcome together. A stale scope or lost upload fence
publishes none of them. Same-artifact re-imports append a measurement for a newly observed
decoder build without removing older measurements, including after catalog projection
replaces its file rows and bindings. Reuse requires the complete current reconstruction
and actual logical owners under fresh catalog authority. Different compressed bytes with
the same reconstruction cannot contribute a proof to retained canonical bytes. Canonical
imports require no encoder or fabricated creation history; noncanonical catalog layouts
reconcile under the registered Automatic policy and current qualified capabilities. Unknown child-stop state retains and
quarantines its staging custody instead of releasing capacity prematurely.

### Internal CD archive foundation

`RomReconstructionManifest.CdTracks` describes the immutable reconstruction
layout independently of decoder measurements. Its normalized tracks, members and
selectors retain one raw standalone CHD, complete original file identities,
BIN-to-track placement, and stored CUE/sidecar dependencies. A BIN can concatenate
consecutive whole tracks; every track occurs exactly once across BINs. The
initial recipe supports the inspection profile above: 2,352 bytes per 2,448-byte
frame, stored pregaps included, four-frame alignment excluded, and 16-bit audio
samples swapped into BIN order. It does not synthesize sectors, subcode, gaps,
CUE text or protection data. Boundaries that split a track require another recipe.

Each complete BIN reconstruction is independently measured and must equal the
original size, SHA-256, SHA-1, MD5 and CRC32. Per-track digests cannot establish a
concatenated BIN's identity. Every observed decoder build has its own immutable
verification covering all outputs and the exact captured CHD parameters. Original
CUE/sidecars must be supplied; generated text is a different file.

CAS staging precedes the database publication transaction. The fact publisher
retains the artifact, complete manifest, measured outputs and descriptor
identities under ordered logical-ROM, representation and file locks. The fenced
CD publisher commits every output association, preferred output, independent
creation witness, archive owner, terminal journal and source release together.
Deferred guards reject incomplete or contradictory graphs. Rollback may leave
reclaimable blobs, never a committed partial graph.

Original-layout custody references an actual native original-comparison verification
and the authentic retained CUE file. Encoding creation rows remain independent history;
custody does not require or invent an encoder run. Live archive reads recheck the current
descriptor identity. Metadata-only confirmation can retain delivery, while a changed
descriptor disables live delivery and leaves the older archive available for admin recovery.

When authentic original CUE bytes arrive after a canonical catalog import, reconciliation
captures the complete original BIN/CUE set and the exact current catalog binding as an
explicit adoption attempt. A supervised decoder compares every pinned original against
the retained CHD outside a database transaction. This needs decode capability and no
encoder or conversion request. Comparison reserves the artifact, originals and request
as one bounded aggregate, plus the separate CUE pool; it reserves no encoded output.
Unknown child-stop state retains every source and custody charge.

After confirmed stop, publication rechecks topology/catalog, logical, graph and file
authority in order. It retains a measured graph with the authentic original manifest on
the same physical CHD, its genuine comparison verification, original-layout owner and
stopped attempt journal atomically. It reuses an identical graph only when the fresh
comparison can be retained with its actual verification purpose; otherwise it creates
a graph without changing an existing measurement. It adds no encoder creation. The
catalog's generated CUE graph, foreign preferences and every owned decoder measurement
remain intact. A
changed binding, cancellation or lost execution fence publishes no adoption; database
replay reuses the stopped comparison. Live archive delivery serves the authentic retained
CUE from the adopted graph.

A layout's archive owner survives logical-ROM withdrawal and catalog changes for
admin recovery. Several layouts can own interpretations of the same physical
CHD or descriptor. Active jobs pin exact representations and outputs, including
stored dependencies. Bounded cleanup removes a representation only after all
logical, layout and job owners release it; ordinary ledger/CAS collection follows.
The fresh baseline has no legacy archive upgrade or downgrade path.

Production selected-member reads use `RomContentReader`, global decoder admission
and the shared native pool/cache with explicit representation, output, verification
and decoder bindings. Descriptor reads use their retained artifacts. BIN reads
recheck the resident native build, header and exact selector. Arbitrary ranges
expose only the selected logical file; sequential EOF reads verify its SHA-256.
Ranges alone do not authenticate the entire file. A complete-container grant
requires authorization for every BIN. Disc groups and M3Us remain independent;
see [PS1 production](#ps1-original-disc-compression).

Real original-file qualification uses the explicit
[`--chd-cd-qualification-probe`](../../tools/Romd.Media.Probe/README.md#original-cd-fixture-qualification)
mode with a SHA256-pinned independent DAT and the complete CUE/BIN file set.
It records per-fixture observed layouts and complete original-file comparisons.
Explicit BIN name bindings support renamed originals; a differently named catalog
CUE template is recorded as a reference, never as a match for the retained original.
Synthetic fixtures cannot satisfy the real-disc gate. The
[qualification matrix](../architecture/media-status.md#qualification-status)
records the separately measured original layouts and platform limits.

### Supervised CD encoding and whole-file verification

`ChdCdArchiveWorkflow` is an unregistered internal workflow over that archive
model. It takes a complete original CUE/BIN set and explicit sidecars. The bounded
CUE parser accepts UTF-8, named BINARY files, consecutive MODE1/2352, MODE2/2352
or AUDIO tracks, INDEX 00/01, PREGAP/POSTGAP and REM. Names are portable basenames;
absolute paths, traversal, cooked sectors, WAVE and unrecognized directives are
rejected. Limits are 64 KiB of original CUE, 99 tracks/BINs, 450,000 original
frames and 16 sidecars of at most 16 MiB each. Unsupported input cannot establish
source equivalence. All originals must fit simultaneously within configured
staging slots/bytes, along with the request and reserved CHD output; the format's
99-file ceiling is not an additional staging-capacity grant.

ROMD retains the exact original descriptor. A separate generated `.cue` names
only the held staged BINs, using paths relative to its own directory. The pinned
chdman prefixes this directory even for absolute FILE references and selects
the descriptor parser from its extension. Generated text is never substituted
for the original CUE. The descriptor uses the separately bounded
[CD descriptor staging pool](../architecture/temporary-staging.md#cd-descriptor-staging).

The `encode-chd-cd` child runs the verified pinned chdman with fixed experimental
`chd-cd-candidate/v1` arguments: `createcd`, 19,584-byte hunks, `cdzs` and one
processor. It shares the DVD encoder's sealed-image launch,
lowered priority, inherited CPU/memory/core limits, output file-size cap, bounded
diagnostic drains and exact kill/reap ownership. Its response contains raw
artifact facts and tool provenance. This candidate has no production canonical
status. A binary that changed after verification is refused before launch.

After the encoder stops and output is sealed, a fresh `verify-chd-cd` child
checks both CHD integrity hashes and the decoder tuple. It hashes each complete
original BIN and its whole-file projection with size, SHA-256, SHA-1, MD5 and
CRC32, checks declared track modes, and rehashes held and named sources after
the comparison. A single original BIN can span several tracks. Every BIN must
match in full before any archive manifest can be constructed. Original CUE and
sidecars receive their own measured identities and remain retained artifacts.
The encoder cannot produce this verification proof.

These jobs use a distinct strict `romd-media-cd/v5` envelope, at most 512 KiB,
with mutually exclusive encoding, verification or closed failure variants.
CD inspection includes separate nullable PS1 and Saturn boot observations. A required
original boot comparison must agree with that native observation. Retained-file
verification uses `chd-cd-retained-files/v2`: its boot field is required but nullable.
Every prior positive observation must match; an absent observation can gain a new
positive fact. The retained reply must agree with the native observation even when
both are null. Whole-member, stored-descriptor and container integrity checks are
mandatory regardless of boot recognition. Only positive observations are persisted.
Older protocol envelopes are refused. Existing RVZ/DVD measurement fields and
byte limits are unchanged.
Parent validation binds every returned member to the exact request and staged
source. The request, every original, generated descriptor and output receive
durable holds; CD run records allow at most 128 bindings/1 MiB. Other run records
retain their existing three-binding/16 KiB limits. Cancellation joins the process
tree before releasing any hold; uncertain stop quarantines all dependencies.

The [synthetic Linux probe](../../tools/Romd.Media.Probe/README.md#cd-encoding-round-trips)
qualifies these boundaries without a catalog or database. The PS1 production path
above adds admission, convergence and delivery. Runtime launch, disc grouping and
playlists remain separate work.

The probe validates isolated encoding/verification, output bounds, cancellation
and cleanup. Real-disc and production-path qualification is recorded separately
in [media status](../architecture/media-status.md#qualification-status).

## PS2 observations and retained references

Original ISO preparation identifies media inside the existing supervised admission
lease. PS2 boot facts remain bound to that exact original measurement. After the
child has stopped and its lease has been released, preparation resolves DVD
eligibility against retained singleton Redump references. Missing, contradictory
or unavailable references leave eligibility absent; recognition alone does not
establish DVD5/DVD9 physical media or permission to encode.

The worker can admit a reference with `dotnet romd-worker.dll --import-redump-disc
<positive-disc-id>`. It uses canonical HTTPS with standard platform TLS validation,
rejects redirects and oversized responses, and completes retrieval before opening
the caller-owned database transaction. In Production it requires the
`romd_worker` runtime credential and the current worker-published schema version.
It builds its limited command composition without starting hosted services or
provisioning schemas. Successful stdout is one JSON result; diagnostics use stderr.
Use `ConnectionStrings:Romd`, never the provisioning credential, for this command.

The fresh baseline retains exact reference bodies and hashes, exact retrieval
and measurement timestamps, raw logical/boot observations and separate encoding
provenance. Existing application audit timestamps retain millisecond precision.
Database guards reject incomplete or contradictory measurements and mutation of
retained observations. Raw reference rows are worker-only; API catalogs read bounded assertion metadata
through guarded authority and cannot read raw bodies or fabricate references. An imported CHD measurement does not invent encoding provenance.

The current DVD recipe is `chd-dvd-canonical/v2`. Changing the canonical recipe
reclassifies earlier parameter sets without changing readability through an
admitted decoder. A noncanonical CHD lists in VFS as its original until
reconciliation replaces it. Finite selections, explicit/retry requests, PS2
reference arrivals and newly retained facts all use the same subject-demand path. Legacy
database upgrades and baseline downgrades are unsupported.

`mise run e2e:chd` is a disposable end-to-end harness for real PS2 DVDs that
runs through the normal hosts. It is designed to converge each disc and kill
the worker at the named `publishing` checkpoint. It then checks re-executed
recovery, both delivery formats and malformed-CHD isolation. Results come only
from recorded runs. `mise run e2e:chd:synthetic` is its generated-input CI
step; a synthetic pass shows composition only. See the
[harness runbook](../../scripts/e2e/README.md).

## Installation and startup

Build and publish `src/Romd.Worker.Host` normally. Its project copies the child
DLL, runtime configuration, dependency manifest and dependencies into
`media-worker/` beside `romd-worker.dll`. Deploy that directory with the worker.
The production worker image includes it; the API images exclude it. The release
bundle includes this guide alongside the production deployment runbook.

The worker checks the child before starting dispatch, Hangfire processing and
its readiness marker. A missing executable, invalid protocol or incompatible
build stops startup. Restore the complete worker release and inspect startup
logs before restarting. Copying only the child DLL is insufficient.

The worker uses absolute paths for the .NET host and child executable. Linux
also requires `/usr/bin/prlimit`, already present in the production runtime
image. The Docker build checks that executable instead of installing a tool.
chdman operations also need executable memfds: `/proc` mounted, and the
`vm.memfd_noexec` sysctl at 0 or 1 (the kernel default is 0). At 2, every CHD
encode and extract fails closed with `ToolLaunch/LaunchFailure` before chdman
starts.
See [configuration](configuration.md#media-processing) for override names,
defaults and ranges.

## Capacity and failures

ROMD reserves an inspection slot before copying recognized media into shared
temporary staging. Inspection and encoding share one host-local capacity limit
across worker processes using the same data directory. Stable admission leases
and bounded run records live in `temp/media-runs-v1`; keep that directory on the
same durable volume as staging. Conflicting concurrency settings fail closed
while a live or unresolved run exists. An admission shortage defers the file
through the durable upload job.
Once admitted, malformed media, child crashes, invalid output, memory exhaustion
and inspection deadlines fail that file. Sibling files and later jobs remain
eligible to run. Caller cancellation retains its cancellation semantics.

Every child receives a GC heap hard limit. Linux additionally receives DATA,
address-space and CPU limits. The GC budget limits managed heap commitment;
the Linux DATA budget also covers writable native allocations. Address space
includes CLR reservations and is a separate guard. These settings do not claim
an exact resident-memory cap. macOS does not gain the Linux address-space
protection merely because a resource-limit API is present. Budget room for the worker, two default concurrent
children, read delivery, staging and the operating system.

On cancellation, deadline or failure, the supervisor kills owned descendants
and joins the output readers before releasing inspection capacity. It bounds
diagnostics and rejects excess output. Staging ownership follows the existing
[temporary staging policy](../architecture/temporary-staging.md). Redelivery uses durable
per-file outcomes rather than repeating completed files.
The cleanup boundary begins immediately after a successful launch, including
partial console/wait/monitor setup. Cancellation releases admission only with
affirmative unstarted or complete-stop proof. An unexpected supervision throw
without that proof quarantines the source and output and retains its media slot.

The staged source receives a durable hold before a child may open it. A failed
hold prevents launch and is a permanent source-custody file failure. A failure
to remove a hold retains the source reservation for operator repair. If the
supervisor cannot prove that the process tree stopped, it also retains the
affected media slot, records unavailable status and reports a permanent
containment failure. Marked staging slots survive owner exit. Recognized run
records permit guarded recovery; unknown custody remains charged. See
[recovery](#recovery) before repairing retained capacity.

Encoding reserves its complete per-input output cap in the same staging pool as
the source before launch. GameCube's bound includes worst-case payload,
alignment and compressed tables. Wii's bound also covers sector hash exceptions,
partition/raw-area splits and partition tables. Both paths have durable child
holds. Only after
confirmed process-tree cleanup and complete output measurement can the output
be sealed, its reservation reduced to actual length and the same lease transferred
to a readable artifact. Cancellation and failures discard partial output after
cleanup; uncertain containment quarantines both paths and retains capacity.

## Recovery

A run records its parent, child, job, packaged build, admission lease and staging
bindings. The supervisor flushes `launching` before native spawn and requires the
child's matching identity and launch permission before payload access. A
`prepared` record proves that launch has not begun; an unidentified `launching`
record does not prove that a child is absent and stays pinned.

Recovery waits for the shared accounting lock for up to
`Romd:Media:AdmissionTimeoutSeconds`, honoring caller cancellation. A busy lock
at the deadline still blocks recovery. Once the lock is acquired, the same
process-stop and custody checks apply before any capacity is released.

Normal recovery checks process birth and, on Linux, boot and PID namespace before
terminating an owned orphan group. Capacity returns only after recognized
descendants are joined and reaped and the original admission lease is independently
acquired. Recovery removes only matching versioned staging holds. A stopped
certificate also requires that independent lease acquisition. Child PID reuse,
unreadable records and legacy holds do not release capacity.

Local Linux recovery can also join a known zombie leader that is a waitable
child of the recovering process. A single process-stat snapshot must match the
recorded PID, birth and process group, report zombie state, and identify the
recovering process as its parent; the recorded boot and PID namespace must also
match. This path still requires independent acquisition of the exact original
admission inode, the local chronology checks below, and the existing group join,
reap and extinction checks. Zombie state alone does not release capacity.
Unknown, live, foreign or mismatched identities keep custody pinned.

Before querying a retained Linux parent or child PID, local recovery reads the
current namespace init and binds it to the current boot and PID namespace.
When the init's namespace link is unreadable, as with a host init owned by
another user, the init is bound to this process's own PID namespace only when
this process's procfs `NSpid` entry is single and equals its PID: procfs then
belongs to this namespace, so its PID 1 is this namespace's init. Any other shape
keeps custody pinned. This applies to a worker run outside a container on a Linux
host. The shipped Compose worker uses `init: false` and runs the image's worker
entrypoint as PID 1 in a private namespace, as the
[deployment runbook](production-deployment.md#choose-ingress) requires.
Retained non-init parent and child start ticks must both be strictly later than
that init's start ticks. Unknown, malformed, older or equal ticks keep custody
pinned; an observed init start of zero can be a valid baseline. Equal ticks are
ambiguous at the kernel's clock-tick resolution, so this can conservatively pin
a legitimate launch. This is a denial check before the existing identity,
process-group and reap checks, not an additional extinction exception. The
qualified managed hosts do not change their time namespace during a run.

After a Linux container recreation, the approved exception permits recovery
when payload extinction is proven even if kernel reaping is still finishing.
The recorded parent must have been PID 1 of the old namespace incarnation;
parent and child must share its recorded boot and PID namespace, and the current
kernel boot must match. Different namespace inode labels establish different
incarnations. Labels can be reused: when they match, the recorded parent and
current process must both be PID 1 with valid, positive, unequal start times on
that same boot. Equal or unknown start times do not establish a new incarnation.
Recovery must acquire a new, independent open-file description of the
exact original lease, with the same device and inode. The parent retains that
lock through every unconfirmed child use, and the child retains its inherited
descriptor throughout payload execution. Lease files are never replaced.
Foreign numeric PIDs and groups are never queried or signalled. Missing, legacy
or mismatched facts stay pinned. This exception proves extinction, not an
observed reap; recovery within the same incarnation keeps the normal termination
and reap rule.

The synthetic [CHD end-to-end harness](../../scripts/e2e/README.md) exercises this
exception through the shipped Compose topology: it observes a permitted child,
the actual live chdman and growing output, kills the worker and force-recreates
its container, then requires a new encode, completed publication and released
media/staging holds. The harness also checks that an invalid native build makes
the PID-1 worker exit with status 1 within 30 seconds.

For unresolved custody, stop all hosts sharing the data directory and establish
that the affected children cannot use it before operator repair. Never delete
lease files or reset media/staging accounting to force admission. The
[staging guide](../architecture/temporary-staging.md#ownership-and-recovery) owns payload and
quarantine-marker repair guidance.

## Durable convergence

One `media-reconciliation` job family handles an exact logical ROM, original
layout, catalog CD layout or retained representation, or a finite selection of
those subjects.
The worker compares current immutable media facts with the registered kind's
recipe, conversion mode and available capabilities. Kind is computed from
facts; catalog platform labels do not establish physical media or encoding
authority. GameCube/Wii use `rvz-canonical/v1`, PS2 DVD uses
`chd-dvd-canonical/v2`, PS1 CD uses `chd-ps1-canonical/v1`, and Saturn CD uses
`chd-saturn-canonical/v1`.
An invalid kind or encoding registration fails startup.

Import publication, newly retained observations and PS2 reference arrivals stage
general subject demand in the same transaction as their facts. Acceptance
records intent; it does not choose a source, capture policy, pin files or run a
codec. Read requests and API startup do not execute media work. The worker
resolves the current facts and capabilities when processing demand. It verifies
a retained graph missing the resident decoder's complete measurement, encodes an
eligible noncanonical source when conversion is permitted, and releases weaker
holdings only after the preferred canonical graph is verified. Matching
parameters remain canonical across encoder-build changes unless the immutable
recipe explicitly requires a rebuild.

Before external work, the fenced worker captures exact sources, all logical
outputs, the decoder, recipe, capability and limits fingerprints, and durable
source pins. It reserves processing capacity and all operation-specific staging
charges before materialization. Sources are checked against captured SHA-256 and
length; encoded output is decoded by a separate child and every output identity
is compared independently. Source pins retain the complete physical graph and
stored descriptors across deferral, preferred-output changes and worker loss.
Temporary staging alone is not a GC reference.

Publication holds the execution fence and ordered subject, logical-owner,
representation and file locks. It rechecks captured authority and current
ownership, records measurements and source-equivalence evidence, updates
preferences, releases eligible holdings and records the attempt atomically.
All decoder-build verifications on retained graphs survive. Changed facts cause
replanning or supersession rather than stale publication. A lost commit
acknowledgement replays the committed journal before opening input. Durable
dispatch and execution leases recover interrupted work; capacity shortage and
unresolved native custody defer it. Cancellation records stop intent. Pins and
staging are released only after independent stopped-custody evidence; host
shutdown does not invent a user cancellation.

**Storage → Media reconciliation** shows registered recipes, conversion modes
and this API host's read/runtime capabilities through
`GET /api/media/reconciliation/policies`. The worker owns encoding capability.
Administrators submit a nonempty `requestId` UUID and explicit
`conversionRequested` to
`POST /api/media/reconciliation/subjects/{subjectKind}/{subjectId}`.
Subject kinds are `LogicalRom`, `OriginalLayout`, `Representation` and
`CatalogCdLayout`; IDs are public Sqids. A catalog layout uses its catalog file ID,
so the subject survives replacement of its immutable graph association.
Representations support verification only and require `conversionRequested: false`.
Both layout subjects request their current decision. The worker captures and
rechecks the exact layout or catalog binding revision under publication locks.

A catalog association retains a complete measured graph and its actual generated
CUE. It does not invent an original layout or encoder creation history. Matching
canonical parameters settle without encoding; noncanonical layouts honor the
registered conversion mode. A verified replacement atomically publishes genuine
creation facts, a fresh association revision, preferences and the attempt journal.
Other catalog/layout owners keep the old graph and every decoder measurement.
Withdrawing a DAT-exact CUE's verified ordinary original supersedes its catalog
reconciliation scope; restoring the original makes that scope eligible again.
The catalog binding and retained CD measurements remain owned. Publication treats
concurrent loss of that original as a source change and leaves the caller's
transaction usable, so it is not cached as a deterministic codec failure.

`POST /api/media/reconciliation/selections` accepts an explicit UUID, `filter`,
positive finite `limit` and `conversionRequested`. `AllSubjects` selects all
subject kinds; `KindSubjects` requires `kindId` and optionally `systemKey`;
`RetainedRepresentations` supports verification only. Selection captures finite
high-watermarks and persists its cursor. Created plus adopted work consumes
the requested limit; selected counts every examined subject, including skips.
Captured bounds across the four Int32 subject namespaces keep the entire scan
finite, including the catalog file high-watermark. The selection records which children it owns. Cancellation stops owned children, while adopted work keeps
its original ownership. The job exposes selected, created, adopted, skipped,
completed and failed counts plus enumeration completion; its final outcome
comes from the committed journal.

Replaying the same UUID and exact request returns the accepted work; conflicting
reuse is rejected. Concurrent requests deduplicate active subject work and
record newer demand. Retry terminal work through
`POST /api/media/reconciliation/{jobId}/retry` with a fresh UUID. This retains
the original subject intent or finite selection bounds. The ordinary bulk
retry-failed action does not retry media work. Deterministic failures are
reused only for the complete operation fingerprint. Capacity, start, I/O, CPU
and wall deadline failures are transient and do not poison that cache.

See [media status](../architecture/media-status.md#qualification-status) for
GameCube/Wii platform and workload qualification. Admission and cache limits
alone establish neither a whole-host memory cap nor external-network latency.

## CHD decoder re-verification

Use **Storage → Media reconciliation** with the retained-representation filter
and conversion disabled, or `POST /api/media/reconciliation/selections` with
`requestId`, `filter: "RetainedRepresentations"`, a positive finite `limit` and
`conversionRequested: false`. A shared CD is one representation regardless of
BIN count. A layout-owned archive remains selectable after every logical ROM
disappears. The worker resolves its verified resident decoder; the API does not
capture a decoder tuple on acceptance. Keep worker and reading-host decoder
configuration consistent. A current complete proof settles without decoding.

Each selected representation becomes current subject work. The attempt pins the
exact graph, artifact, outputs and stored descriptors before verification. DVD
verification uses source-only staging and the normal `inspect-chd` child; it
never invokes chdman or reserves an encoded output. Production CD verification
uses `reverify-chd-cd` and one atomic source-only reservation for the CHD, exact
stored CUE/sidecars and bounded request. It reconstructs every complete original
BIN, including stored pregaps and audio byte order, and compares size, SHA-256,
SHA-1, MD5 and CRC32. Raw/overall CHD integrity, exact track geometry/selectors
and a fresh root PS-X EXE observation are checked independently. It never stages
original BINs or synthesizes CUE text. Unowned graphs are outside the selection.

One complete `Verifications` row and all its `VerificationOutputs` append for
the captured decoder. Separate raw logical/boot observations are retained;
verification creates no encoding provenance. The measurement, attempt result
and stopped source release commit atomically under the execution fence and
ordered locks. Deferred guards reject partial, wrong-layout and wrong-decoder
proofs. Every earlier build's proof, preference, creation history and retained
file remains intact. Layout ownership suffices without a live logical ROM.
Facts requiring logical-owner reconciliation stage demand through the same
family; PS2 reference eligibility controls encoding authority independently of
byte delivery and runtime claims.

`GET /api/jobs/{jobId}` exposes the finite selection's progress, outcome and
bounded outcome code. Owned child jobs identify their origin selection and
exact public subject. Recovery resumes the captured attempt or replays its
committed journal without repeating a complete measurement. A failed child is
counted while the finite selection continues. Cancellation and retry exhaustion
retain dependencies until native custody is affirmatively stopped. Historical
attempt and selection journals do not pin Files or representations.

The single fresh baseline installs these guards. Unsupported existing databases
are refused before writes; no legacy campaign history is migrated or invented.
See [decoder upgrades](production-deployment.md#chd-decoder-upgrades) for
worker/admin/consumer ordering within a compatible baseline and the retained
previous-reader window.

## Known limitations

- **CHD encode budgets remain finite.** CHD encodes use 1,200 s CPU and wall
  budgets, approved by the owner on 2026-10-14. Inspection, extraction and RVZ
  keep their 600 s defaults. A chdman CPU-limit kill returns `LimitExceeded` with
  `CpuBudgetExceeded`; the sole Linux child wait owner samples the kernel's
  `CPUCLOCK_PROF` limit counter after exit with `waitid(WNOWAIT)`, then joins with
  `wait4`. Adjusted `wait4` usage can differ from the counter enforcing the limit;
  it is a fallback if the final limit clock is unavailable.
  Equal soft/hard CPU limits deliver SIGKILL, so a signal alone
  never establishes a limit. Attribution requires consumed CPU to reach the
  inherited soft limit, allowing 20 ms for Linux CPU accounting granularity.
  The supervisor's wall deadline also produces an encode-budget outcome.
  External SIGKILL/SIGXCPU without that CPU evidence and ordinary corrupt-media/tool
  failures remain `Malformed`.
  Reconciliation records `Rom.Media.CpuDeadline` or `Rom.Media.WallDeadline`
  and defers transient failures with bounded retry/backoff. These outcomes are
  never cached as deterministic media defects. The original stays served;
  stopped attempts release staging and capacity. Terminal source dependencies
  are removed only with independently stopped custody, so historical failure
  journals do not pin Files against garbage collection.
- **Earlier init-wrapped runs remain conservative.** A live run recorded under
  an earlier `init: true` deployment cannot acquire new extinction evidence from
  the corrected topology. Its job, media slot and staging stay pinned until the
  operator repair in [Recovery](#recovery). The
  `init-wrapped-namespace-change` regression rows continue to reject that shape;
  changing deployment configuration does not rewrite historical custody facts.
