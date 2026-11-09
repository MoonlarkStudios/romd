# Disc compression

Disc images are big. ROMD stores supported discs in a compressed format so your
library takes less space, and it can always give you back the exact original
file. This guide covers what gets compressed, what happens when you upload, and
how to get your games back out.

## The short version

- **Lossless, always.** ROMD reconstructs the original disc byte for byte. DAT
  matching, hashes and downloads of the original keep working.
- **Less stored data.** Once a compressed copy is verified, ROMD releases
  original source bytes when no other owner needs them.
- **Nothing to configure.** Each media type has one fixed format and recipe.
  GameCube, Wii, PS2 DVD and supported PSP UMD uploads can compress automatically. Complete eligible
  [PS1 discs](#ps1-discs) also compress automatically on qualified Linux x64 workers.
- **Verified before anything is replaced.** A separate process fully decodes
  every new compressed file and checks it against the disc's hashes before ROMD
  releases the old copy.

## What gets compressed

| Media | Stored as | Recipe | Notes |
| --- | --- | --- | --- |
| GameCube and Wii discs | RVZ | `rvz-canonical/v1`: zstd level 19, 128 KiB chunks | Includes Wii discs that use the Korean common key |
| PS2 DVDs | CHD | `chd-dvd-canonical/v2`: zstd, 16 KiB hunks | Needs a matching Redump reference; see [PS2 DVDs](#ps2-dvds) |
| PSP UMD game ISOs | CHD | `chd-dvd-canonical/v2`: zstd, 16 KiB hunks | Automatic, Linux x64 only; see [PSP games](#psp-games) |
| PS1 CUE/BIN discs | One CHD per disc | `chd-ps1-canonical/v1`: `cdzs,cdfl`, 39,168-byte hunks | Automatic, Linux x64 only; see [PS1 discs](#ps1-discs) |
| Everything else (cartridge ROMs, unsupported discs) | Original bytes | Seekable Zstandard in ROMD's content store | Compressed transparently; downloads are always the original |

Not compressed yet: PS2 CDs. ROMD stores these as their original files. Each
disc in a multi-disc set is stored in its own media type's format. Consumer
[disc-set ZIP downloads](downloads-and-exports.md#download-a-multi-disc-game)
can contain one CHD per eligible disc plus an M3U playlist. The console's
original-layout manifests and WebDAV descriptor/playlist releases still deliver
original files.

ROMD inspects the disc's contents before compressing it. The file extension or
platform only suggests what a file might be.

## What happens when you upload

ROMD ranks every verified copy of a game and keeps the best one:

1. a compressed file that matches the current recipe (*canonical*);
2. any other compressed file of the right format, such as an RVZ made with
   different settings;
3. the original, uncompressed file.

In practice:

- **You upload an ISO.** ROMD keeps it, queues a background job to compress it,
  verifies the result and then releases the ISO.
- **You upload an RVZ or CHD that already matches the recipe.** ROMD verifies it
  and keeps it as is, without re-encoding.
- **You upload a compressed file with different settings.** ROMD keeps it,
  because it ranks above an ISO, and queues it for re-encoding to the current
  recipe.
- **You upload something that doesn't rank higher than what ROMD already
  has.** ROMD checks it, matches it against your DATs, and discards it.

Compressing a disc only changes how it's stored. Its identity in the catalog
stays the same.

### PS2 DVDs

ROMD only compresses a PS2 ISO that matches a retained Redump disc reference,
because the reference is what proves it's a single-layer or dual-layer DVD. Import
the reference using the disc's Redump ID, before or after uploading the ISO:

```bash
scripts/deployment/compose.sh exec -T romd-worker \
  dotnet romd-worker.dll --import-redump-disc <redump-disc-id>
```

When a matching reference arrives after the upload, ROMD re-checks the stored
ISO and queues it for compression. Until then, the ISO is stored and served as
the original.

### PSP games

Upload a complete PSP UMD game ISO or a standalone DVD-mode CHD. ROMD checks the
ISO9660 filesystem, matching UMD and PARAM.SFO identifiers, game category and
PSP executable header. A `.iso` extension or PSP catalog label alone does not
authorize compression. Supported ISOs retain their complete original length,
including padding and update files, and reconstruct byte for byte.

PSP compression runs on Linux x64 and accepts images up to 2 GiB. UMD Video/Music,
CSO, PBP and unsupported layouts do not enter this conversion path. Original
and CHD downloads are available after verification. Ottercade runtime support
and PSP gameplay remain separate work; WebDAV currently keeps PSP original names
and serves reconstructed ISOs, including in its *stored* view.

### PS1 discs

Upload the complete original CUE and BIN files together. ROMD publishes verified
originals, then automatically reconciles eligible complete layouts on a qualified
Linux x64 worker. Use **Disc layouts → Compress to CHD** to request conversion,
retry or repair explicitly. Incomplete or ambiguous sets keep their originals.
Existing CHDs, original identities and generated descriptors remain intact.

Automatic follows owner approval of mixed-track CUE matching and read/install
capacity under bulk load, including a slow client. Those measurements qualify
storage and delivery on the recorded Linux x64 loopback setup; native desktop UI,
credential persistence and emulator/gameplay remain unqualified. See the
[measurement results and limits](../architecture/media-status.md#qualification-status).

- **What qualifies.** The disc's BINs must match exactly one current PS1 catalog
  release by SHA-1 and size, and its first track must boot as a PS1 disc. Your
  original CUE is kept byte for byte, even when it differs from the DAT's.
  Discs whose release needs other sidecar files are refused for now.
- **What's stored.** One CHD per disc, made with `chd-ps1-canonical/v1`. A
  separate process decodes every BIN from it and checks all of its hashes
  before ROMD releases the original BINs. The original CUE is stored next to it.
- **Shared tracks.** Two regions often share identical BINs, such as audio
  tracks. ROMD stores such a BIN once, so compressing one disc moves the other
  disc's copy into the same CHD. The other disc keeps working, and you can still
  compress it: ROMD reads the shared track back out of the first CHD.
- **Later decisions.** You can still confirm, detach or reject a layout after
  compressing it. Requesting compression again reuses the existing CHD when it
  already matches the layout.

## Getting your games back

Every game offers its **original** file. Compressed games also offer their
**stored format** (RVZ or CHD), served as is without decoding.

- **Admin portal.** Each file has a download button for every available
  format, such as *Download Original* and *Download CHD*. Title and release ZIP
  downloads and library exports can use either format.
- **Consumer portal and API.** Release manifests list every available format, so
  clients can pick the one they can open.
- **PS1 whole discs.** Each BIN downloads as its original file. The whole CHD
  downloads from `GET /api/original-disc-layouts/{id}/archive` for admins,
  even after the layout is detached or rejected, and from
  `GET /api/releases/{releaseId}/cd-archive` for consumers who can access every
  BIN. Add `/cue` to either route for the original CUE.
- **Ottercade.** The console installs a CHD when its PCSX2 runtime can open it.
  Otherwise it installs the original. GameCube and Wii games currently install as
  the original.
- **Network drive (WebDAV).** Each connection has a *stored* or *original*
  setting. *Stored* lists RVZ games and CHDs made with the current recipe as
  `.rvz` or `.chd` files, and everything else as originals. *Original* lists
  every game as its uncompressed original. Multi-file releases with
  playlists or descriptors are always listed as originals.

Many emulators open RVZ and CHD directly, including Dolphin for RVZ and PCSX2
for CHD. Use the original format for tools that don't.

## Running it

### Watching and managing compression

Admins can manage compression from **Storage** in the admin portal:

- **Media convergence** shows, for each media type, its stored format, how many
  discs are canonical and how many need work. *Convergence* is ROMD's term for
  bringing each disc to its stored format and current recipe.
- **CHD re-verification** re-checks stored CHDs after a decoder upgrade (see
  [After a CHD decoder upgrade](#after-a-chd-decoder-upgrade)).

Every compression job also appears under **Jobs**, where you can see progress and
retry a failed job.

### Bringing existing discs up to date

Eligible whole-file uploads and complete eligible PS1 layouts compress
automatically. When a ROMD release changes a recipe, discs
already in your library aren't re-encoded on their own; start a run to update
them.
In **Storage → Media convergence**, choose a media type and set *Examine up to*
(at most 10,000 per run). The limit counts discs that get a conversion job;
discs that need no work are skipped and don't count toward it. Each run covers
one recipe, and only one run per recipe can be active at a time.

### Settings worth knowing

The defaults suit most installations. These are the ones you're most likely to
change:

| Setting | Default | When to change it |
| --- | --- | --- |
| `Romd__Media__MaxConcurrent` | `2` | Raise it on a machine with spare CPU cores and memory to compress more discs at once |
| `Romd__Media__ChdEncodeCpuTimeoutSeconds` / `Romd__Media__ChdEncodeWallTimeoutSeconds` | `1200` | Raise both on slow hardware if large PS2 DVDs fail as "too large for the current encode budget". Set the same values on the admin and worker hosts |
| `Romd__Media__WallTimeoutSeconds` / `Romd__Media__CpuTimeoutSeconds` | `600` | Limits for inspection and RVZ encoding |
| `Romd__Media__MemoryBytes` | 512 MiB | Memory (.NET heap) limit for each media process |

All media settings are listed in [configuration](../self-hosting/configuration.md#media-processing).
If a PS2 DVD exceeds the CHD encode budget, ROMD keeps serving its current
stored copy and won't retry automatically under the same recipe and budget. You
can still retry it from **Jobs**.

### After a CHD decoder upgrade

ROMD only serves a CHD that has been verified under the decoder version it is
running: the same architecture, libchdr version and native build recipe. ROMD
doesn't keep the ISO alongside a CHD, so a release that changes the decoder
makes those PS2 games unavailable, in both CHD and original form, until they're
re-checked. After such an upgrade, start a run from **Storage →
CHD re-verification**. The [decoder upgrade runbook](../self-hosting/production-deployment.md#chd-decoder-upgrades)
has the details.

The same run also covers retained production PS1 archives on x64. A whole disc
counts as one item, and every original BIN must pass together before delivery
resumes. ROMD checks them directly from the CHD, so you don't need to upload the
original BINs again. The original CUE and creation history stay intact. Native
ARM64 qualification remains separate.

## Current limits

- PS2 CD compression isn't available yet. PS1 compression is Automatic
  for complete eligible layouts on Linux x64.
- CHD runs on Linux server images, x64 and ARM64. Released images are x64 only;
  ARM64 installs [build their own images](../self-hosting/production-deployment.md#chd-media-binaries).
  The current ARM64 CHD decoder build hasn't yet passed native qualification.
- Ottercade doesn't install RVZ yet.
- Real dual-layer Wii discs and Wii discs that use the Korean common key
  haven't been tested yet. The Korean key is covered by synthetic tests.

The [media status tracker](../architecture/media-status.md) records exactly what
has been tested, on which platforms and when.

## Learn more

- [Game media](../decisions/game-media.md) and [media delivery](../decisions/media-delivery.md):
  the rules behind storage formats, ranking, evidence and delivery.
- [Media processing](../self-hosting/media-processing.md): how the worker
  isolates, limits and recovers compression processes.
- [CHD media binaries](../self-hosting/production-deployment.md#chd-media-binaries):
  the native CHD tools in the server images and their licensing.
- [Archive imports](archive-imports.md): how ZIP and 7z uploads are unpacked
  before any of this happens.
