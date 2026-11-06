# Media pipeline probe

## CD track inspection

`--chd-cd-inspection-probe <absolute configuration.json> <absolute new report.json>`
measures standalone v5 CD containers through the concrete supervisor and its
packaged child. It does not mutate or import the input CHDs. Each input is copied
to managed staging in the fresh `DataDirectory`, then independently rehashed
after the child stops. Budget enough temporary space for the largest input;
inputs run sequentially and release their staging after each measurement.

Example configuration (all properties are required, including explicit null
when there is no independent track oracle):

```json
{
  "NativeDirectory": "/absolute/build/libchdr/osx-arm64",
  "DataDirectory": "/absolute/new-cd-probe-data",
  "SourceDescription": "Describe the independent oracle and retain its catalog/source digest",
  "Inputs": [
    {
      "ChdPath": "/absolute/disc.chd",
      "ExpectedTracks": null
    }
  ]
}
```

`ExpectedTracks`, when supplied, is a complete ordered array with `Number`,
`BinBytes`, `Sha1`, `Md5` and `Crc32` for each track-local BIN projection. All
hashes use lowercase hex. Supply an independent BIN source or DAT entry whose
file layout is known to match those track boundaries; a multi-track source BIN
cannot be compared to one projected track. Do not include a CUE entry among BIN
projections. The report retains the supplied expectations and provenance and
states precisely what was compared. A matching track digest does not establish
original CUE bytes or a general source reconstruction recipe.

Configuration is bounded to 1 MiB, 16 inputs and 99 expected tracks per input.
Unknown/duplicate properties, invalid oracles, path aliases, symbolic links and
existing report destinations are refused. The native build is explicit and
verified in every child. The worker must remain beside the probe in
`media-worker/`. Only `Status: passed` confirms completion; `in-progress` is a
checkpoint. Inspection uses the normal 600-second wall/CPU limits. No native
code is loaded into the parent.

```bash
dotnet build tools/Romd.Media.Probe
dotnet tools/Romd.Media.Probe/bin/Debug/net10.0/Romd.Media.Probe.dll \
  --chd-cd-inspection-probe /absolute/configuration.json /absolute/new-report.json
```

The accepted inspection bounds and unsupported layouts are described in
[media processing](../../docs/self-hosting/media-processing.md#cd-inspection-and-compressed-only-uploads).
This mode measures containers and track bytes only; it does not qualify PS1
identity, encoding, archival substitution, import, storage, delivery or emulator
compatibility.

## CUE regeneration measurement

Run `--chd-cd-cue-regeneration-probe <absolute configuration.json> <absolute new report.json>`
after the CD inspection command above has produced a completed `passed` receipt.
This offline command consumes that pinned receipt and independent raw DAT/CUE
metadata; it does not load native code or publish application facts.

All configuration properties are required:

```json
{
  "CorpusDirectory": "/absolute/read-only-corpus",
  "SourceDescription": "Describe the official metadata source and capture its acquisition receipts",
  "InspectionPath": "/absolute/passed-inspection.json",
  "InspectionSha256": "<64 lowercase nonzero hex characters>",
  "DatPath": "/absolute/standard-playstation.dat",
  "DatSha256": "<64 lowercase nonzero hex characters>",
  "CueArchivePath": "/absolute/original-cues.zip",
  "CueArchiveSha256": "<64 lowercase nonzero hex characters>"
}
```

Configuration and output must be outside the corpus. The receipt must contain
one successful stopped native handshake and 1–16 successful stopped CD rows from
the same captured decoder, with every input under the explicit corpus directory.
The DAT is bounded to 32 MiB and 20,000 entries; the CUE ZIP to 16 MiB compressed,
32 MiB declared uncompressed and 20,000 members. Chosen CUEs are read with a fixed
64 KiB bound and must match the DAT's complete size/CRC32/MD5/SHA1 identity.

The formatter uses measured raw track facts and exact DAT BIN names, with UTF-8
without BOM, quoted filenames, CRLF and a terminal newline. Stored pregaps use
INDEX 00/01; virtual pregaps and postgaps remain explicit. This initial profile
requires one uniquely hash-matched BIN per track. It does not infer a multi-track
BIN's original boundaries, additional indexes, REM text, flags or sidecars.
Unsupported and ambiguous cases remain in the overall denominator. The report
records exact matches, mismatches, failure classes, line endings, BOM and quoting.

Only exact descriptor bytes authenticate an original. A matching BIN is not a
CUE preservation claim. Raw inputs, inventory and reports remain private. The
command measures candidate descriptor bytes; import/publication, gameplay,
scheduling and capacity qualification are separate work.

## CD encoding round trips

On Linux, `--chd-cd-roundtrip-probe <fixtures> <native> <tool> <new data> <new report>`
runs the concrete CD archive workflow against synthetic source sets. All five
paths must be absolute and free of symlink aliases; data/report destinations must
be fresh and separate from inputs. Supply the generated `libchdr-final` fixture
directory and verified build directories for the host's libchdr and chdman. Run
as the image's ordinary user, with `/usr/bin/nice` and `/usr/bin/prlimit` available.
The packaged `media-worker/` must remain beside the probe.

```bash
dotnet publish tools/Romd.Media.Probe -c Release --no-self-contained -o /absolute/probe
dotnet /absolute/probe/Romd.Media.Probe.dll --chd-cd-roundtrip-probe \
  /absolute/fixtures /absolute/libchdr /absolute/chdman \
  /absolute/new-data /absolute/new-report.json
```

The probe builds bounded larger originals from the tiny synthetic templates so
the executed-image observer can sample genuine encoding. It measures single-BIN,
multiple-BIN and two-track partial-hunk layouts with MODE2/audio, stored/virtual
pregaps and unaligned track lengths, plus a separate raw MODE1 single-track case.
It records separate joined encoder and fresh verifier
receipts, tool/native tuples and complete archive manifests. Verification compares
all whole original BIN hashes; the exact CUE and synthetic sidecar are retained.
It also rejects a 124-byte output cap, cancels an active genuine encode after
observing output, checks input retention/hold release, and verifies a fresh sibling
to establish processing-slot reuse. Tiny exits without tool image observation
are rejected; the probe does not relax the observer.

Only `Status: passed` establishes completion; checkpoints retain `in-progress`
or `failed`. Allow roughly 1 GiB of writable temporary space; cases run serially
and release staged payloads. The report retains metadata only. This is synthetic
qualification of a fixed experimental recipe, not real-disc, PS1 identity,
production recipe, import, delivery, gameplay or multi-disc qualification.

## Original CD fixture qualification

On native Linux, `--chd-cd-qualification-probe <configuration.json> <new report.json>`
measures operator-supplied original CUE/BIN sets through the same supervised CD
archive workflow. This is the first PS1 compression qualification gate; it does
not register production PS1 import or certify a console family from CD geometry.

```json
{
  "SourceDescription": "Trusted DAT publisher/revision and fixture provenance",
  "FixtureKind": "real",
  "NativeDirectory": "/absolute/libchdr/linux-x64",
  "ToolDirectory": "/absolute/chdman/linux-x64",
  "DataDirectory": "/absolute/new-data",
  "Inputs": [
    {
      "Name": "data-only",
      "FixtureDirectory": "/absolute/readonly-disc",
      "CueName": "original.cue",
      "Sidecars": [],
      "DatPath": "/absolute/trusted-ps1.dat",
      "DatSha256": "<64 lowercase hex digest of the independently obtained DAT>",
      "DatEntryName": "<exact unique DAT game name>",
      "MaximumOutputBytes": 1073741824
    }
  ]
}
```

Every field shown above is required. `FixtureKind` is explicitly `real` or `synthetic` and
records the operator's declaration; generated media and self-generated DATs must
use `synthetic`. All paths are normalized absolute paths without symlink aliases.
The fresh data/report paths must be separate and outside every input, tool and
probe directory. Existing reports, including links to original files, are refused.
Configuration is limited to 1 MiB, 16 named cases and 16 explicit sidecars per case.

The SHA256-pinned XML DAT is limited to 64 MiB. Its exact unique game entry must
contain the CUE's complete set of BIN filenames, with a complete matching
size/CRC32/MD5/SHA1 tuple for each whole original file. Named CUE and sidecar
entries, when present in the DAT, must also match exactly; every DAT entry must
belong to the supplied original set, except for the explicit catalog descriptor
reference described below. A source BIN containing several tracks is compared as
one file. Descriptors and sidecars are never treated as track sectors.
Duplicate/case-colliding names, partial matches, path references,
missing digests, non-sector lengths and unsupported CUE layouts fail closed.
Public DOCTYPE declarations are ignored with no resolver; entity references fail.
The caller remains responsible for choosing a trusted PS1 DAT and provenance.

Renamed original BINs can supply optional `DatBinNames`, a complete one-to-one
object mapping each original CUE-referenced BIN name to its exact DAT member name.
No name guessing or hash-only fallback is performed. For example:

```json
"DatBinNames": { "original.bin": "Catalog title.bin" },
"CatalogCueName": "Catalog title.cue"
```

`CatalogCueName` is optional and must identify one bounded `.cue` DAT entry with a
name distinct from the original CUE. It accounts for a catalog descriptor template
whose filename references differ from the retained original. Its expected identity
is recorded separately as `CatalogCueReference`; it is **never** included in
`DatMatchedMembers` or claimed as a byte match for the original descriptor. The
original CUE remains unchanged and independently hashed/retained in the manifest.
A descriptor with the same name as the original still requires an exact DAT match.
Missing/extra BIN bindings, duplicate targets, unaccounted DAT entries and attempts
to classify a BIN as a catalog descriptor are rejected. These tool-only bindings
establish original BIN identity and reconstruction; they do not establish PS1
admission or runtime compatibility of a renamed descriptor.

```bash
dotnet build tools/Romd.Media.Probe -c Release
dotnet /absolute/repo/tools/Romd.Media.Probe/bin/Release/net10.0/Romd.Media.Probe.dll \
  --chd-cd-qualification-probe /absolute/configuration.json /absolute/new-report.json
```

Run as an ordinary user with `/usr/bin/nice`, `/usr/bin/prlimit`, the packaged
`media-worker/`, and verified native/tool builds for the actual host architecture.
Mount originals, the DAT and executables read-only when using a qualification
container. Budget simultaneous staged originals, the generated request/CUE and
the explicit maximum output, plus the normal staging headroom. The normal
64-file staging limit still applies; a 99-track parser limit grants no extra slots.
The encoder's existing executed-image observer and resource limits are unchanged.

A passing row records full source identities, independent whole-file decoder
comparisons, retained exact CUE/sidecar identities, both stopped-child receipts,
CHD integrity, native/tool/probe build identities, input/configuration digests,
output size, observed layout coverage and combined encoding/verification elapsed
seconds. Held and named originals and the named DAT are checked again before a
row passes. Generated CHDs are temporary and removed after measurement; originals
are never replaced. Reports and run data remain private and caller-owned.
`in-progress` and `failed` reports never establish completion. Cancellation joins
supervised work through the normal custody rules before owned staged files dispose.

The initial real fixture gate needs data-only and mixed data/audio cases, including
multiple BIN files and pregap layouts; a passing row qualifies only its observed
layout. Synthetic coverage does not satisfy that gate. Compression recipe
approval and production qualification are separate from this probe; the fixed
x64 production path now has its own admission, publication and delivery boundaries.
Emulator startup remains a separate gate. Multi-disc
automation and playlist generation are outside this first compression slice.

## PS1 VFS decode capacity

`--chd-cd-vfs-load-probe <absolute qualification.json> <absolute new qualification-report.json> <absolute new load-report.json> <seconds:1800..86400>`
uses the original-fixture configuration above and requires a configured local
PostgreSQL Testcontainers engine. It independently encodes and verifies the
current CD recipe, retains bounded exact archive copies in the new data directory,
and publishes their measured graphs into a fresh disposable PostgreSQL database
and raw CAS. Logical BINs have only CHD backing; original-file oracles stay outside
CAS and remain read only.

The common converged-read workload uses the production reader, native admission,
4-active/2-idle/64-MiB decoder defaults, VFS resolver/admission and HTTP endpoint.
It measures metadata, direct and HTTP full/range reads for independent clients,
controlled decoder saturation, queued-content timeout/cancellation/recovery,
resource bounds and cleanup for at least 30 minutes. Every member must complete
full and range coverage. Authentication and the flat VFS lookup are explicit
probe doubles; the report does not qualify TCP/TLS, a real mount, deployment
containers, gameplay or another native RID. Keep configurations, oracle names,
archive copies and raw receipts private. The measurement-only CD qualification
mode deletes temporary archives; explicit read-load retention returns the actual
verified graph and retained path without inventing encoding provenance.

## Mixed-track interactive HTTPS reads

`--chd-cd-interactive-read-probe <absolute qualification.json> <absolute new qualification-report.json> <absolute new interactive-report.json> <seconds-per-load-phase:300..1800>`
uses the original-fixture configuration and the same independent encoding,
verification, private retained-archive and disposable PostgreSQL/raw-CAS setup.
At least one input must contain both data and audio. Supply original raw2352
track files with the independently pinned DAT and authentic CUE metadata; this
probe does not replace the separate offline CUE-regeneration measurement.

The loopback Kestrel HTTPS host uses an ephemeral certificate pinned by the
clients, real VFS credential issuance/authentication and post-admission recheck,
the production PostgreSQL live-library/catalog repository and default layout,
and the production reader/native decoder. It explicitly seeds published catalog
rows from the independently qualified sets; subscription import and library
materialization jobs are outside this measurement. All BINs have only CHD backing.
Exact retained descriptors receive separately rehashed original-byte graphs.
Credentials remain in memory and never appear in the report.

For each mixed disc, one data and one audio track of at least 1 MiB supply
4-KiB, 64-KiB and 1-MiB range requests at the start, a captured hunk boundary,
sector 150 and clipped EOF. Oracle bytes are loaded before timing. Fresh-decoder
observations also have fresh TLS clients; warm observations reuse both. OS page
cache is uncontrolled. The sustained capacity probe above covers every member;
these interactive observations cover only the stated selected tracks/ranges.

After isolated observations, fixed 500-ms arrivals run for 60 seconds idle,
the requested duration with four bulk readers, the requested duration with six,
and 60 seconds recovery. At most eight interactive requests are outstanding.
Missed arrivals, 120-second operation deadlines and transport failures stay in
the denominator. Successful first-byte/completion percentiles include admission
attempts and retry waits; failures have separate elapsed times. Scheduled
completion also includes dispatch delay. Exact nearest-rank percentiles always
include counts; a single fresh-decoder observation is not a distribution.
Defaults remain four active/two idle decoders, a 64-MiB cache and unchanged HTTP
admission bounds. No deployed-container, WAN, mount or gameplay claim follows.
Keep all reports/configurations private. This command neither changes PS1
OnRequest policy nor grants approval to enable Automatic.

## PS1 recipe comparison and boot diagnostics

`--chd-cd-recipe-probe <absolute configuration.json> <absolute new report.json>`
uses the original-fixture configuration above. It compares a closed four-candidate
matrix (`cdzs` or `cdzs,cdfl` at 8 frames, MAME's default CD triple at 8 frames,
and `cdzs,cdfl` at 16 frames), retaining candidate artifacts and manifests in the
new data directory. Each original set is checked against the pinned DAT before
encoding; every candidate passes a fresh supervised whole-file verifier. The
read trials use the production native pool/cache and whole-BIN projection, with
three sequential and 10,000 random 4 KiB trials after 1,000 warmups. Oracle reads
and comparisons are outside timing; cache state is recorded as warm/uncontrolled.
The bounded external encoder records its exact arguments, tool/native/probe
identities, output identity and sampled RSS. Report checkpoints never convert a
failed candidate into passed evidence.

`--ps1-cd-identification-probe <absolute configuration.json> <absolute new report.json>`
performs only DAT checks and bounded managed PS1 boot recognition. It neither
encodes nor activates production. Production repeats recognition in both supervised
children. Root-relative `cdrom:` boot paths, a trailing DOS EOF terminator and a
mixed disc's whole-disc PVD size are supported; boot reads stay within the data BIN.

The selected production recipe and the scope of the measured x64 evidence are
maintained in [media processing](../../docs/self-hosting/media-processing.md#ps1-original-disc-compression).
Use the [PS1 Compose drill](../../scripts/e2e/README.md#ps1-original-disc-recovery)
for active-encode recovery and full host delivery. Private inputs, candidates and
raw evidence must remain outside Git.

## DVD inspection and encoding

The isolated PlayStation CHD inspection mode is
`--chd-inspection-probe <absolute configuration.json> <absolute new report.json>`.
Its configuration contains `NativeDirectory`, `InputChd`, `OracleIso` and a new
`DataDirectory`. `NativeDirectory` is a libchdr build directory: its own
`build-manifest.json` and `native/<file>`, verified against that record in every
child. Paths must be distinct, absolute and free of symbolic-link aliases. Supply a synthetic CHD of at most
1 MiB and an independent DVD oracle of at most 16 MiB. The compiled worker must
remain beside the Probe in `media-worker/`.

This mode exercises verified native handshake, four logical digests, both CHD
integrity hashes, precise rejection controls, readiness restoration and joins.
It does not qualify native packages or activate CHD in host composition. On
2026-10-09 it ran on Linux ARM64 (Ubuntu 24.04, .NET 10, 1 CPU, 1 GiB, no
network, read-only inputs): thirteen supervised children completed with joined
custody, every hash and native-identity rejection control returned a typed
failure without a measurement, and readiness was restored afterward.

`--chd-encoding-probe <absolute configuration.json> <absolute new report.json>`
exercises the isolated Linux DVD encoder over the concrete supervisor. Supply
`Mode`, `SourceIso`, `OriginalHostPath`, `ExpectedSource` (size and four digests),
`ToolDirectory`, `NativeDirectory`, `WorkerPath` and a fresh `DataDirectory`. Each
directory holds the build record its own build wrote; both must be built for this
Linux RID. `synthetic` mode permits an independent 16 or 64 MiB oracle with null
`OriginalHostPath`; `ffx` and `gt4` require `ROMD_FIXTURES`, the fixture root
holding `manifest.json` and `ps2/`, and accept only the recorded Final Fantasy X or
Gran Turismo 4 image beneath it. No fixture growth or retry is automatic.

The probe reserves source plus full output capacity together, rejects a tool
identity that disagrees with its record before artifact creation, then encodes
with the recorded candidate.
It independently measures and seals the raw output after stopped proof, and
uses a separate child to compare all logical digests with the original and check
both stored/computed CHD integrity hash pairs. The original is rehashed after
staging cleanup. Mount
all inputs and compiled tools read-only with no writable aliases and keep report
and staging separate. Only `Status: passed` confirms the complete sequence.
This mode does not publish media, establish PlayStation eligibility or qualify
the native release or production worker.

The existing `--custody-probe` also accepts `DiscKind: dvd` with explicit `Chd`
inputs: `Mode`, `OriginalHostPath`, `ExpectedSource`, `MaximumOutputBytes`,
`ToolDirectory` and `NativeDirectory`. Keep
the existing `Operation`, `DataDirectory`, `InputIso`, `ProductionWorker` and
`JobId` fields. Input authorization has the same synthetic/FFX bounds as the
encoding probe. CHD `same-namespace` qualification is separately scoped.

For the Linux `nonempty` checkpoint, the probe requires a live genuine tool,
nonempty output, the original admission inode and inherited fd3 lock, exact
image/arguments, and matching process identity and limits. Readiness is a
point-in-time observation. The controller must freshly reconfirm the exact live
tool before killing its owned container, join the original attached process and
confirm removal before starting recovery. Use direct non-root Linux PID1 for
this narrowly guarded foreign-container recovery case; an init or shell wrapper
changes the parent identity. A recycled namespace label alone does not identify
an incarnation; the existing recovery predicate also checks PID1 birth ticks.

`assert-held` checks shared-pool denial while the first container lives.
`recover` retains the existing ten recovery guards, independently acquires the
original admission inode, then encodes and decodes a new sibling through the
same pool. It proves foreign payload extinction rather than an observed old
process reap. It was exercised on 2026-10-09 in a Linux ARM64 container. This is
an opt-in experiment in a
representative Linux container; production-image and full-worker qualification
remain open.

Opt-in qualification harness for operator-supplied GameCube/Wii images. This is
an opt-in test harness for the production representation adapter, not an automatic test suite. It
does not import games into ROMD or change the application database. Source files
are opened read-only. A unique temporary raw-policy CAS is deleted on exit.
Allow space for the uploaded and normalized RVZ artifacts from every fixture,
plus one additional staged RVZ at a time and the staging pool's disk headroom.

## Inputs

Build with `dotnet build tools/Romd.Media.Probe -c Release`.

The probe exercises the managed `Romd.Media` reader and supervised `RvzVerifier`.
The build copies the dedicated child into `media-worker/` beside the tool; Linux
also requires `/usr/bin/prlimit` and `/usr/bin/nice`. Commercial game data is not checked into this
repository. Each result records measured-output provenance and checks the
supervised profile and exact decoder/child builds.

Prepare an ISO oracle with an independent tool, then verify it against a trusted
DAT before using it. For the read-pipeline mode, supply a *different* RVZ encoding
from a separately qualified RVZ writer (no scrubbing). The 2026-09-30 run used
`encounter/nod` `nodtool`
**v2.0.0-alpha.12** (commit `ebd80cac99b48a323200d84365e07a58cc27412d`) as that
external ISO oracle. Its `convert` command writes ISO; `-c` selects compression
for that output and does not make it an RVZ producer:

```sh
nodtool convert --md5 -d /path/to/redump.dat /path/to/input.rvz /path/to/oracle.iso
```

Historical RVZ candidates used the external RVZSharp writer
`1.0.0+2745298d` (SHA-256
`05750d4fa74d1ccf788b4aab362165d2c4dd0ceb547ba3e8e23f99903aba00e1`).
That writer tested GameCube magic at ISO `0x18` instead of `0x1c`, writing
RVZ disc type 0. The recipe probe corrected only scratch GameCube output to type
1 at `0x48`, then recalculated structure SHA-1 (`0x10`, over `[0x48, 0x124)`)
and header SHA-1 (`0x34`, over `[0, 0x34)`). Independently decoding each candidate
with nodtool had to reproduce the full DAT-matched ISO hash. Keep this workaround
specific to that historical oracle: never patch retained CAS bytes, normalize Wii
outputs or assume another writer version has the defect. Each candidate was also
decoded through the production codec before its measurements were accepted.
Encoding mode below creates fresh RVZ outputs with ROMD's managed encoder;
it does not need a separately normalized RVZ.

Write a fixture array with absolute paths:

```json
[
  {
    "Name": "GameCube",
    "UploadedRvz": "/path/to/input.rvz",
    "NormalizedRvz": "/path/to/normalized.rvz",
    "Iso": "/path/to/oracle.iso",
    "Dat": "/path/to/redump.dat",
    "DatId": "697"
  }
]
```

Then run the compiled tool from a scratch directory:

```sh
dotnet /absolute/repo/tools/Romd.Media.Probe/bin/Release/net10.0/Romd.Media.Probe.dll \
  /absolute/path/fixtures.json /absolute/path/results.json
```

The fixture and results paths need to be visible inside any test container;
on macOS/Rancher Desktop use `/private/tmp` for host temporary-directory binds.

The process exits nonzero on the first failed assertion. A results file can
contain an `in-progress` checkpoint; only `Status: passed` confirms completion.
Capture console output separately to preserve the failing assertion if any.

## Evidence

- Independent CRC32/MD5/SHA-1/SHA-256 over the ISO oracle; size and DAT checksums.
- Production `RvzVerifier`, complete streamed reconstruction in a child, the
  `rvz-disc-supervised/v1` profile and exact decoder/child build identities.
- Production shared temporary staging before preparation, exact staged artifact
  hashes, and CAS publication from that staged file. Each artifact records
  `BoundedStaging: true`.
- Real raw-policy CAS publication, duplicate identity, whole-object verification.
- Full decoding of both RVZ encodings through CAS positional callbacks; all
  reconstructed digests must match the ISO oracle.
- Deterministic random, block-boundary, partition-region and EOF reads compared
  byte-for-byte with the ISO. Includes reads above 4 GiB for the Wii sample.
- Shared-reader and independent-reader concurrency, pre-cancellation, cancellation
  during opening and artifact reads, a blocked artifact read not stalling other
  reads of the same image, disposal, malformed headers and truncated backing streams. These structural probes do
  not establish that every damaged RVZ block has an authenticated checksum.
- Production `ConsumerVfsEndpoints`, `ConsumerVfsService` and range handling,
  hosted in ASP.NET TestServer with the production content reader and fake catalog
  and authentication. Checks ISO and RVZ bytes, metadata, HTTP ranges, validators,
  concurrent transfers, and denial before content opening. HEAD, bodyless
  conditional responses and 416 responses assert that no content reader opens.
- Changing RVZ encoding preserves the ISO ETag. RVZ ETags change; a stale
  `If-Range` receives a complete replacement whose hashes must match.

## Limits

Warm/uncontrolled OS caches; timings are exploratory, not performance gates.
This is not TCP/TLS, rclone, mount-cache, WAN, emulator/gameplay, production
authorization, database migration, durable publication, or process-crash testing.
No Linux/Windows or Wii dual-layer qualification is implied by a macOS run.

One image serves concurrent reads; cancellation reaches CAS reads during opening
and reading. Read delivery uses explicit size caps and host admission; decoding
a single read chunk is not interruptible. Upload inspection uses a supervised
child with separate limits. The reader reports no per-range cryptographic integrity.

The production WebDAV endpoint uses authorized metadata for HEAD, PROPFIND,
bodyless conditional responses and unsatisfiable ranges. No test path materializes
an ISO while opening or serving a request.

All fixture content is registered in the fake catalog solely for testing. Persistence,
migration and ownership are covered separately by the infrastructure tests.
This harness does not qualify normalization jobs or a public format-selection API.

The separate `--inspection-probe <absolute fixtures.json> <absolute report.json>`
mode needs only `UploadedRvz`, `Iso`, `Dat`, `DatId` and `Name` from each fixture.

The report destination must be new; existing paths and source aliases are rejected.
It hashes both inputs read-only, matches the ISO to the local DAT, reserves
inspection before staging, and compares the fresh child's full logical hashes
and exact build identities with those oracles. It deletes its unique staging directory after confirmed
child cleanup; held or quarantined staging is preserved, with its recovery path
in the report and console output. This
mode does not qualify database ingestion, delivery or actual memory-allocation
rejection; those require separate tests and container probes.

`--iso-inspection-probe` uses the same fixture file and inspects the ISO itself
through the production child and preparation adapter. Add independently
specified `NintendoKind` (`gamecube` or `wii`), `StandardKeyPartitions` and
`KoreanKeyPartitions` to each fixture. It checks those structural facts, the
`nintendo-iso-supervised/v1` measurement and `nintendo-iso/v1` eligibility profile,
and every full-file hash against the independent ISO/DAT oracle. It uses the
same admission, report protection and cleanup rules. It does not qualify ticket
authenticity, encoding, database ingestion or delivery.

`--encoding-probe <absolute configuration.json> <absolute new report.json>`
qualifies GameCube or Wii encoding through the production staging pool and
supervisor.
Configuration contains `Fixtures` (the array above), an absolute
`OracleExecutable`, its pinned `OracleSha256`, `InputFormats` (`original`, `rvz`)
and `Repetitions` (default 2, maximum 4). `InputFormats` must begin with `original`;
`rvz` may follow. Each fixture's independently inspected eligibility supplies the
typed disc kind. Optional `NintendoKind`, `StandardKeyPartitions` and
`KoreanKeyPartitions` assert expected layout facts. Each output must have
deterministic bytes, canonical
parameters and independently measured artifact hashes. A separate fresh child
verifies every logical hash against the ISO/DAT; the independent nodtool then
fully decodes it for another whole-ISO comparison. For Wii, independently extract
the original ISO and fresh RVZ with nodtool and compare plaintext file hashes too:
an encrypted round trip alone can hide wrong-key selection. `nodtool extract`
defaults to `-p data`, so that comparison covers the game/data partition; use
explicit partition selection for any broader plaintext claim. Output reservations
and both child custody holds use
the production pool. Existing report paths and fixture aliases are rejected.
The independent oracle writes through an owned `.iso` symlink to its reserved
payload because pinned nodtool rejects a `.data` destination; this creates
no second disc copy. Oracle exit/output diagnostics remain in a failed report.

Set optional `RetainArtifactsDirectory` to an absolute scratch directory to keep
the qualified raw RVZs after staging cleanup. Copies have an exact byte bound and
are rehashed; an existing artifact must match every recorded hash and length.
Failed copies remove only their owned incomplete destination. Retained artifacts
outlive the probe and need separately budgeted disk space and explicit cleanup.
Encoding returns raw metadata; a separate child supplies the decode measurement.
This mode does not publish source-equivalence records or convergence outcomes.

For Linux production-image qualification without a locally available Linux
oracle, replace `OracleExecutable` with `QualifiedReport`, containing `Path` and
`Sha256` for a passed macOS encoding report. Every Linux artifact must equal that
report's independently decoded artifact. This mode still inspects sources and
verifies outputs in separate current production children against the ISO/DAT.
Set `RecoveryIso` to an absolute, supported synthetic GameCube or Wii ISO of at
most 8 MiB; independent inspection supplies its kind. Export the existing
GameCube fixture by running
`RvzEncodingServiceTests.EncodeAsync_GameCube_CreatesRawArtifactMetadataAndSeparateChildEstablishesEvidence`
with `ROMD_MEDIA_GC_RECOVERY_FIXTURE_EXPORT` set to a new absolute file.
The Linux probe rejects an undersized output reservation and cancels after output
bytes exist. Its shortened wall deadline is five seconds for GameCube and thirty
seconds for Wii, allowing Wii source hashing and encoder startup before the
required output observation; the production default remains 600 seconds.
It also injects one setup failure immediately after launch and one after the
console drains start, each after observing real output within a 60-second probe
wait. These use the current supervisor's internal test seam; the child and its
launch environment stay the production ones.
After each case it checks
process-group extinction, both custody holds, partial-output deletion and all
admission slots, then encodes and verifies the small sibling through that same
supervisor/admission instance. Its final real-disc sibling uses the default pool
and the same staging factory; the report labels both scopes separately.
It samples the exact encoding child's `/proc` high-water RSS and every visible
thread's nice value at 100 ms intervals. The report records transient read races;
sampling is not continuous tracing or an RSS enforcement limit. It requires more
than one observed runtime thread, all with nice 10–19. It makes no Linux nodtool
claim. Use the image's non-root user, disable network and mount fixtures read-only.
Use disk-backed shared staging when fixture/output reservations exceed the
container's memory budget; a full Wii ISO does not fit in a 4 GiB cgroup-backed
tmpfs. A macOS wrapper's sampled RSS/nice after output creation excludes startup
and is not a continuous peak or Linux runtime-thread measurement.
Neither encoding mode publishes representations or runs convergence jobs.

`--supervisor-probe <absolute configuration.json> <absolute new report.json>`
qualifies the production supervisor inside the final worker image. It requires
Linux PID 1 and the image's non-root user. Configuration contains absolute
`ProductionWorker`, `FaultWorker`, `InputsDirectory` and `OracleIso` paths.
Optional `ExitedLeaderRepetitions` defaults to 12 and accepts 12 through 4096
for bounded diagnostic repetitions.
Export synthetic fault inputs explicitly from the infrastructure fixture, then
clone the image's `media-worker/` directory and replace only its worker DLL with
that emitted fault DLL. Overlay the image's root DLLs onto the published probe;
the probe checks loaded Infrastructure, Media and Storage against `/app`.

Launch dotnet directly as the container entrypoint, with no init or shell parent.
Keep network disabled, the root and fixture/runtime mounts read-only, and provide
a writable temporary filesystem and new report directory. The probe checks
subreaper status, actual native allocation rejection at DATA 1 GiB versus
acceptance at DATA 2 GiB with the same GC budget, a fixed managed allocation
rejected at GC 512 MiB versus accepted at GC 1 GiB under identical OS budgets,
deadline/caller cancellation, repeated orphan cleanup and single-slot inspection
recovery. Every successful reconstruction matches the independent synthetic ISO.
Failed scratch remains beside the report on its writable evidence mount;
completed runs remove their owned scratch after containment is confirmed.
This proves allocation enforcement and process ownership; it does not claim an
exact RSS cap, database recovery or encoding. Exact image/assembly identities,
commands and results belong in the dated qualification report.

The separate `--staging-probe [report.json]` mode needs no game media.
It starts another copy of this process, proves a live owner blocks competing
admission, kills the owner, and checks reclamation for completed files and
interrupted copies. It is part of `mise run test` and backend CI. See the
[staging runbook](../../docs/architecture/temporary-staging.md) for guarantees and limits.

The separate `--extraction-probe [report.json]` mode also needs no game media. It kills child processes during a partial ZIP write, after decoding
but before publication, and after completed extraction before classification.
Fresh production extractors must recover the exact synthetic bytes without
duplicate output paths. It runs in `mise run test`; an explicitly supplied report
path should be absolute because `dotnet run` uses the project's working directory.
The recovery contract is described in
[temporary staging](../../docs/architecture/temporary-staging.md).

## Recipe measurements

`--recipe-probe` measures one already-encoded candidate against an independent,
DAT-matched ISO. It does not encode media or select the canonical recipe. Write
an absolute-path specification:

```json
{
  "Name": "GameCube",
  "Artifact": "/path/to/candidate.rvz",
  "Iso": "/path/to/independent.iso",
  "Dat": "/path/to/redump.dat",
  "DatId": "697"
}
```

Build as above, then run the compiled tool from an empty scratch directory,
using a new report path. Existing files (including source aliases) are rejected;
checkpoints use the same exclusively created output handle throughout the run:

```sh
dotnet /absolute/repo/tools/Romd.Media.Probe/bin/Release/net10.0/Romd.Media.Probe.dll \
  --recipe-probe /absolute/path/spec.json /absolute/path/result.json
```

Only `Status: passed` confirms completion. The probe hashes the artifact and ISO,
checks the DAT, publishes to disposable raw CAS, and performs three complete
decoded size/CRC32/MD5/SHA-1/SHA-256 comparisons. It then checks 1,000 first/read
repeat pairs per workload (4 KiB, 64 KiB, 1 MiB, actual chunk boundaries and EOF)
through both the production decoder and VFS endpoint. Every read is compared
with the ISO; Wii boundaries include actual partition-relative chunks and reads
above 4 GiB where logical size permits. The 20,000 comparisons include empty
EOF reads. HEAD, PROPFIND, 304 and 416 assert no CAS or decoder opens.

Each workload starts with an empty decoder pool/cache. Later first requests use
normal pooling/cache reuse; each immediate repeat follows its matching request.
These are warm/uncontrolled OS-cache measurements, not disk-cold measurements.
Full-decode times include hashing. Direct range timers include opening, reading
and disposal; VFS timers include the streamed HTTP body. Oracle reads and byte
assertions are outside range timers.

Run one candidate per fresh process when measuring external peak RSS. That RSS
covers the entire probe, including DAT parsing, CAS publication, hashing and
TestServer; it is not decoder-only memory. Authentication, layout and catalog
remain test doubles, with no TCP/TLS, mount or production authorization claim.
On the restricted macOS agent host, see the
[file-watcher startup issue](../../docs/development/known-issues.md#media-recipe-probe-can-stall-during-host-startup-in-the-macos-sandbox)
before retrying a matching startup stall.

The 2026-10-01 measurement (macOS ARM64, .NET 10, one GameCube and one Wii
DAT-matched disc) covered zstd levels 1–22 with 64 KiB–2 MiB chunks plus an LZMA
reference. zstd 19 / 128 KiB was approved as `rvz-canonical/v1`: level 22 saved
only a few kilobytes at roughly three times the encode time; 512 KiB chunks saved
1.6% but about doubled small-read p95; 2 MiB saved 4.8% with clearly higher
random-read cost; LZMA was 1.4–3.4% smaller but decoded three to four times
slower. Sustained emulator and mount load were not qualified by that run.

Verify report-destination protection with disposable symlink/hard-link inputs:

```sh
python3 tools/Romd.Media.Probe/test-recipe-reports.py \
  /absolute/repo/tools/Romd.Media.Probe/bin/Release/net10.0/Romd.Media.Probe.dll \
  /absolute/path/new-safety-evidence
```

The evidence directory must be new. This checks all four source types, existing
reports and case aliases when the filesystem is case-insensitive. It does not
qualify decoding; the input files are deliberately invalid media.

## PSP UMD qualification

`--chd-encoding-probe <absolute configuration.json> <absolute new report.json>`
accepts `Mode: "psp"` for operator-supplied complete PSP UMD ISOs. Supply
`SourceIso`, `OriginalHostPath: null`, the independently computed complete
`ExpectedSource` size/CRC32/MD5/SHA1/SHA256 tuple, `ToolDirectory`,
`NativeDirectory`, packaged `WorkerPath`, and a fresh `DataDirectory`.
Also supply `Dat`, its independent `DatSha256`, and the unique complete
`DatEntryName`; the pinned DAT entry must contain exactly one matching ISO.
All ordinary path, output-isolation and source-custody protections still apply.

Set `RecipeId: "chd-dvd-canonical/v2"` to include the existing ISO CAS baseline,
retained CHD and raw-CAS read campaign. An independently supervised original
inspection must recognize PSP UMD facts before encoding. A separate verifier
child fully reconstructs the output, compares every logical digest and checks
that PSP observations agree. PSP uses the production 600-second inspection and
1200-second CHD encode CPU/wall budgets. The wrong-tool-pin control and stopped
custody checks remain mandatory.

The receipt records the DAT binding, original before/after identities, source
and output observations, exact tool/native builds, resource limits and read
results. Only `Status: passed` establishes completion. This qualifies the named
storage/codec/read workload; it does not qualify normal-host ingestion,
publication/recovery, HTTP/VFS delivery, packaged deployment or PSP gameplay.
Keep the corpus, retained artifacts, configurations and raw receipts private.

Copy the completed probe and its `media-worker/` directory to a separate private
qualification directory before long runs. Concurrent builds must not replace
the parent or child assemblies during an encode; mismatched protocol/build
identities are refused even when the codec output is valid.

## DVD CHD recipe measurements

`--chd-recipe-probe` measures one externally encoded candidate without copying it
into CAS or changing the canonical recipe. Build the probe in Release and run:

```sh
dotnet /absolute/repo/tools/Romd.Media.Probe/bin/Release/net10.0/Romd.Media.Probe.dll \
  --chd-recipe-probe /absolute/spec.json /absolute/new-report.json
```

The specification uses these required fields (the digest values below are
placeholders; supply the independent fixture values and observed CHD header):

```json
{
  "Name": "dvd-default-ffx",
  "SourceRevision": "<40 lowercase hex source revision>",
  "Hardware": "Apple M2 Max; 12 CPU; 32 GiB; macOS version/build",
  "Artifact": "/scratch/candidate.chd",
  "Iso": "/readonly/independent.iso",
  "Dat": "/readonly/redump.dat",
  "DatEntryName": "Final Fantasy X (USA, Canada)",
  "ExpectedSource": { "Size": 4508221440, "Crc": "<8 hex>", "Md5": "<32 hex>", "Sha1": "<40 hex>", "Sha256": "<64 hex>" },
  "NativeDirectory": "/repo/artifacts/native/libchdr/osx-arm64",
  "ToolDirectory": "/repo/artifacts/tools/chdman/osx-arm64",
  "EncodeReceipt": "/scratch/encode-receipt.json",
  "EncodeReceiptSha256": "<64 lowercase hex receipt digest>",
  "ExpectedHeader": { "ArtifactBytes": 12345, "LogicalBytes": 4508221440, "HunkBytes": 16384, "UnitBytes": 2048, "HunkCount": 275160, "Codec0": 2053925218, "Codec1": 0, "Codec2": 0, "Codec3": 0 },
  "SequentialRequestBytes": 1048576
}
```

The external encoder must retain a separate JSON receipt with exactly these
fields: `SourcePath`, `CandidatePath`, `ToolPath`, `Source` (the same complete
`Size/Crc/Md5/Sha1/Sha256` tuple), `ArtifactBytes`, `ArtifactSha256`, `ToolIdentity`,
`Arguments`, `ElapsedSeconds`, `StartedUtc`, `EndedUtc`, and `ExitCode`.
`ToolIdentity` is the verified `ChdmanToolIdentity` object with `Rid`, `MameTag`,
`MameSourceSha256`, `RecipeVersion`, `BinaryBytes`, and `BinarySha256`.
`Arguments` is a JSON array excluding the executable, for example
`["createdvd", "-i", "/readonly/independent.iso", "-o", "/scratch/candidate.chd", "-np", "1", "-c", "zlib", "-hs", "16384"]`.
Only paired `-i`, `-o`, `-np`, optional `-c` and `-hs` are admitted, each once.
Record elapsed seconds from a monotonic clock around the joined encoder process;
record start/end as UTC timestamps with zero offset, and require exit code zero.
Hash the artifact separately after encoding, then hash the retained receipt.
The receipt binds owner measurements; it is not production publication evidence.
The probe neither launches the encoder nor invents argv or encoding time.

The bounded DAT parser ignores public DOCTYPE declarations with a null XML
resolver. Entity references fail; no external DTD/entity fetch is permitted.

All JSON is bounded to 64 KiB and depth 12. Missing, null, unknown and recursively
duplicated fields fail closed. Input/runtime paths must be normalized absolute
paths without symlink ancestors; the new report must be outside inputs and native,
tool and probe directories. The exclusively created report handle remains held
for every checkpoint. Only `Status: passed` confirms completion. Failure or
cancellation records the current phase and completed rows after owned decoders drain.
The CLI handles Ctrl-C and SIGTERM through the same cancellation path.

The probe verifies tool/native records and resident native identity, all four
codec slots and DVD geometry using `ChdDvdLimits.Preflight`, the fixture length
and full hash quartet, the selected unique DAT game name with a single ROM, and a separate full reconstructed
quartet. It retains readonly descriptors for the candidate and oracle throughout
measurement and rechecks held and named bytes before completion.

Each trial owns a private cache namespace; artifact hashes remain independent
evidence rather than namespace identifiers. Each of three random trials starts
a fresh 64 MiB decoded cache and native pool
(cap four, idle cap two), reads 1,000 warm-up then 10,000 measured 4 KiB ranges
at 4 KiB-aligned offsets using seed 123456, and records p50/p95, standard deviation,
and after-warm-up retained-hit/flight-join/new-factory and raw callback deltas.
Every random range is compared with the independent ISO outside its timer.
Three sequential trials each start another fresh cache/pool and read the complete
logical image with managed adjacent-range read-ahead enabled. The sequential
loop excludes hashing and oracle I/O; it includes normal loop bookkeeping and
byte-count checks. The report gives median seconds and decimal MB/s
(`logical bytes / seconds / 1,000,000`). Native read-ahead is zero.

OS cache conditions are warm and uncontrolled. Opening, independent hashing and
oracle comparisons are excluded from performance timers. A concurrent 250 ms
resource sampler observes process RSS and managed heap; cgroup and process
lifetime peaks include prior phases. Retained cache bytes, live/peak rounded
rentals, raw callbacks, flights and active/idle/live/peak handles are separate
observations. Unused ArrayPool retention is unmeasured: CHD-only returned arrays
have a conservative upper bound below 32 MiB and the generic full pool below
1 GiB. Native raw maps require 12 bytes per hunk per handle; codec and compressed
map allocations are not individually attributed. These numbers do not establish
a whole-process RSS cap, gameplay, production-image qualification or recipe choice.
