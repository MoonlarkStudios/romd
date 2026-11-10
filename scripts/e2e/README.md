# PS2 DVD CHD end-to-end harness

`scripts/e2e/chd.py` runs the normal admin, consumer and worker hosts with
PostgreSQL, the player and the HTTPS nginx ingress from
`deploy/examples/https/compose.yaml`.

It uploads a PS2 DVD ISO, lets convergence produce the canonical CHD and kills
the worker at one named checkpoint. It then checks recovery and downloads both
delivery formats.

It uses only public APIs and the worker's `--import-redump-disc` command. It
never uses a probe entry point, never overrides a host's entry point or
command, and never reads `.env`. This page describes what the harness is
designed to check. Qualification results come only from recorded runs and
their evidence files. Each run records the exact libchdr tuple (RID,
upstream pin, native build recipe version), binary digest and build ID from every
server image. The harness requires the tuple to match current source and the
image architecture, and requires all three hosts to use the same build. A pass
for an earlier decoder recipe cannot qualify the current one.

| Task | Inputs | Use |
| --- | --- | --- |
| `mise run e2e:chd` | Real fixture discs | Local real-disc runs: Final Fantasy X, then Gran Turismo 4, one run each. |
| `mise run e2e:chd:synthetic` | Generated only | Local smoke runs, and the linux-x64 CI step in the `production-compose` job. |

The synthetic task selects `--checkpoint encoding`, exercising a live child and
container recreation. The real-disc task defaults to `publishing`; pass
`--checkpoint encoding` for the same live-child drill on a real disc. Direct
`chd.py --synthetic` retains the script's `publishing` default unless selected.
The encoding drill identifies the live child by content, not path: the worker
executes chdman from a sealed memfd, so the child's executable must be that
`chdman` image and its size and SHA-256 must match the chdman build record and
the configured binary.

For a concurrent rootless Docker run, `--https-bind 127.0.0.2` selects a
separate loopback address for ingress publication. The default is `127.0.0.1`;
non-loopback addresses are rejected. See the documented
[port collision mitigation](../../docs/development/known-issues.md#rootless-docker-published-ports-can-collide-with-host-tcp-connections).

Use `--image-defaults` when qualifying image activation. It omits native settings
from the generated overlay and requires the decoder on every server image and
the encoder on the worker image. Receipts record the image defaults, effective
configuration and exact native build. The source Compose smoke accepts the same
`--https-bind` loopback option.

Both tasks need Docker, `openssl` and `git`. The unit tests need only Python:
`python3 -B -m unittest discover -s scripts/e2e -p 'test_*.py'`. These Docker-free
checks exercise harness behavior with fake API responses; they do not qualify
container composition, real media or a retained-data decoder upgrade.

The harness reads one consistent normalized media-fact snapshot from the fresh
application baseline: logical owners and preferred outputs, manifests and stored
dependencies, captured parameters, per-decoder verifications and every measured
output, raw observations and independent creation witnesses. The backend's
`QualificationSnapshot_*` PostgreSQL tests execute the actual Python query builders
and consume a published CD graph through the real harness parser. Unit and SQL
checks establish those contracts; they do not qualify production images or media.

Decoder-upgrade runs require both image sets to implement the same fresh
application baseline. Build the baseline images with their genuine earlier native
decoder, then change only the resident decoder for the current image set. Historical
application database upgrades are unsupported. The comparison requires one complete
new decoder measurement, every earlier measurement/output unchanged, intact
ownership and creation history, and released source custody. A raw observation may
be reused when its inspector/profile is unchanged; it is independent of the decoder
build and must never be rewritten to manufacture a new observation.

Job discovery uses the canonical `/api/jobs` collection with `archive=all`, follows
every `items` page at `limit=100`, and requires coherent `hasNextPage` and
`nextCursor` fields. Upload and verification item reads follow the same paging
contract. Upgrade settling inspects every job family, including active work on
later pages.

## Real-disc runs

Export both paths explicitly in the command. The run fails closed if either
path is:

- unset or relative;
- unresolved (it contains a symlink or `..`);
- under a temporary directory or inside the checkout;
- nested in the other path.

`ROMD_E2E_ROOT/runs` must be a real directory, not a symlink.

```bash
ROMD_FIXTURES=/Users/me/RomdFixtures ROMD_E2E_ROOT=/Users/me/RomdE2E mise run e2e:chd
ROMD_FIXTURES=... ROMD_E2E_ROOT=... mise run e2e:chd -- --disc gt4 --image-tag <existing-tag>
```

Before the first real disc on a machine, run `mise run e2e:chd:synthetic` with
the same `ROMD_E2E_ROOT`. It exercises the same bind-mount, host polling and
cleanup paths in a fraction of the time.

### Fixtures

`ROMD_FIXTURES/manifest.json` (schema 1) supplies:

- each disc's size and SHA-256, SHA-1, MD5 and CRC32 (`files[]`, roles `dvd5`
  and `dvd9`);
- the Redump DAT's path and SHA-256, and each disc's DAT game name (`dats[]`,
  role `ps2-redump-dat`).

Before a stack starts, the harness checks the DAT hash and the DAT entry
against the manifest, and rehashes the ISO.

The discs use Redump references 210 (FFX, DVD-5) and 955 (GT4 v1.01, DVD-9).

Fixtures are mounted read-only into a test client container, which streams
them to the API. They are never copied into the Docker VM.

### Run folder

Each disc runs in `$ROMD_E2E_ROOT/runs/<run-id>/`, under Compose project
`<run-id>`.

ROMD's data directory is bind-mounted from `<run-id>/data`, created with mode
`0707`. The image's app user (UID 1654) is "other" to that host-owned
directory, on Linux and through Rancher's virtiofs alike. It needs read, write
and search there; group bits are unnecessary. PostgreSQL uses a named volume.

A generated overlay lives only in the run folder:

- It sets `Romd__Native__LibchdrDirectory` on admin, consumer and worker, and
  `Romd__Native__ChdmanDirectory` on the worker only.
- It mounts a generated copy of the HTTPS example's ingress template with
  server-level `proxy_buffering off;` added for API downloads. The committed
  `/vfs/` location already streams and is preserved verbatim. All harness
  downloads therefore stream instead of spooling to the ingress container disk.
  Committed templates are unchanged. Evidence records this deviation as
  `ingress`; runs do not exercise normal routes' committed buffering behavior.

### Free space

Each run needs the larger of two values, plus 10 GiB of headroom:

- the assumed 30 GiB peak;
- the largest `peakBytes` that a *passed* run of that disc recorded in the
  evidence directory.

All units are binary GiB. Failed or aborted runs never count, because they can
stop early with a low peak.

### Images

Images are built one target at a time from the working tree
(`compose.yaml` + `compose.build.yaml`), with a unique `e2e-<invocation>` tag.
Serial builds avoid the shared-VM exhaustion in
[known issues](../../docs/development/known-issues.md#whole-linux-validation-exhausted-the-shared-rancher-vm).

Each tag is recorded as soon as it is built, so even a partial build is
removed afterwards. `--image-tag` or `--keep-images` skips that removal. The
evidence records the revision and whether the tree was dirty.

### Cleanup

Cleanup always runs. SIGTERM and SIGHUP take the same path as Ctrl-C unless
already ignored (as under `nohup`), and none of the three interrupts cleanup
once it has started. In order:

1. Save bounded logs next to the evidence (a 120-second limit).
2. Run `docker compose -p <run-id> down -v --remove-orphans`.
3. Delete the run folder, but only if this run created it, it is a run-id
   directory directly under a real `runs/`, and the stack is gone.
   - Where the host user cannot delete files the app user wrote (Linux), a
     throwaway worker-image container first runs
     `find /cleanup -mindepth 1 -delete`. That container is not a ROMD host.
4. On macOS, after deleting the run folder, drop the guest's dentry and inode
   caches with `rdctl shell sudo sh -c 'sync && echo 2 > /proc/sys/vm/drop_caches'`.
   Until the guest releases their inodes, the deleted run folder's files stay
   allocated on macOS (see the
   [virtiofs entry](../../docs/development/known-issues.md#deleted-rancher-desktop-bind-mount-files-stay-allocated-on-macos)).
   Synthetic runs do this too. The drop is VM-wide, so other containers briefly
   re-read cached metadata, and `sync` waits for their pending writes. Without
   `rdctl` the step is skipped and recorded.
5. On real macOS runs, run `rdctl shell sudo fstrim -v` on the guest mount that
   `rdctl shell df -P /var/lib/docker` reports (see the
   [trim entries](../../docs/development/known-issues.md#rancher-desktop-cache-cleanup-may-need-filesystem-trim)).

A failed release or trim is a warning with the manual commands, not a run
failure: each only returns disk to macOS, and the next disc's space preflight
still applies.

If any other step fails, the harness prints the exact commands to finish
cleanup by hand.

## What each run is designed to check

### 1. Activation, before any upload

- A one-off worker from the same Compose service and unmodified image entrypoint
  receives an invalid native directory. It must log the startup failure and exit
  with status 1 within 30 seconds as PID 1, without an init wrapper.
- `docker inspect` shows the native keys above, and admin and consumer lack
  the chdman key.
- Each host runs its image's own entry point and command, and binds its data
  directory from the run folder.
- All three hosts are healthy.
- The worker uses `init: false`, a private PID namespace and `dotnet` as PID 1.

The native-activation evidence is the effective environment plus a healthy
startup; the hosts' startup asset checks fail closed on unverifiable native
builds. `/health/ready` `native-media` must also be healthy on admin and
consumer. That check alone is weak evidence, because it is also healthy when
CHD is off.

### 2. Catalog

- `POST /api/upload/dat?systemKey=ps2` imports the DAT.
- `docker compose exec -T romd-worker dotnet romd-worker.dll --import-redump-disc <id>`
  prints JSON. Its system, media, file count, logical size, SHA-1, MD5 and
  CRC32 must match the manifest and the DAT entry.
- References are imported before the ISO.

### 3. Eligibility

After `POST /api/upload/rom`, a `media-reconciliation` subject job is staged for
the exact public logical-ROM ID. Acceptance does not establish eligibility or
capture a recipe. The worker resolves current facts and reference authority;
the committed creation must use the `Id` in
`src/Romd.Media/Chd/ChdDvdCanonicalRecipe.cs`.

### 4. Crash recovery

At checkpoint `encoding`:

- the job is in phase `Encoding`, is not terminal and has a null outcome;
- its bounded durable record names a permitted `encode-chd` child with a PID-1
  parent, the original admission inode and the reserved output;
- the same output grows across consecutive 25 ms observations;
- `/proc` in the worker reports the actual live chdman as the media child's
  direct child before the kill.

The harness kills the worker container with SIGKILL, then requires that the same
job, run, parent, child, admission and output remain recorded. It force-recreates
only the worker (`up --force-recreate --no-deps`) and requires a different
container ID. The original PID-1 parent retained the admission lock until death;
namespace death extinguishes descendants. The normal runtime can then reclaim
the exact original lease and recover custody without looking up foreign PIDs.

At checkpoint `publishing`:

- the job is in phase `Publishing`, is not terminal and has a null outcome;
- a file under `data/content/.staging/` is growing;
- no `run-*` record exists under `data/temp/media-runs-v1/`.

The host polls the bind-mounted data directory every 25 ms. When it sees
growth, it runs `docker kill -s KILL` on the worker container, whose ID it
resolved before arming.

After the kill, the API must still report the checkpoint, and the file that
grew must still be staged. That shows the staged file was interrupted before
publication, though not necessarily mid-copy. The evidence's
`copyInterrupted` field records whether it was also shorter than the final
CHD. Otherwise the run fails as `MISSED checkpoint publishing`, as it also
does when the job turns terminal first.

For `publishing`, the worker restarts with `up -d --wait romd-worker`.
After its 5-minute execution lease expires, either checkpoint must recover to
`Completed` with a committed `Completed` outcome:

- re-executing the encode. For `encoding`, a new permitted encode run with a
  different durable run ID and positive output must be observed after recreation;
  the old persisted `Encoding` phase during the lease wait does not count. For
  `publishing`, a later observation attempt must reach `Encoding` or `Verifying`.
  Each poll counts toward the observation attempt it was issued in. A replay of
  the outcome journal fails the run. Encode duration excludes a stale `Encoding`
  interval followed by renewed inspection.
- with no deferral after the kill;
- with exactly one retained, preferred CHD representation, read with
  read-only `psql` in the disposable PostgreSQL;
- with every hold released: no `run-*` record, no `content/.staging` entry,
  no staging reservation or `slot-*.data`, and no `*.quarantine`. Persistent
  `*.lease`, `capacity.json` and accounting files are not holds.

Earlier runs recorded under an init-wrapped worker remain pinned: this drill
does not rewrite historical custody or qualify that unsupported topology.

### 5. Serving admission on every reading host, before downloads

- Admin `GET /api/roms/{id}` lists the CHD as available.
- A library is published, and the consumer release manifest grants the same
  CHD.
- The worker reads CHDs only for export jobs, so it is not exercised. The
  evidence records this limitation.

### 6. Downloads

The original and the CHD are each downloaded twice: from admin, and through
consumer content grants. Admin downloads first create a fresh ticket through
`POST /api/download-tickets` with `target: Rom` and the requested format, require
201, then redeem the returned capability URL without a bearer token. Consumer
downloads continue to use fresh content grants. Capability addresses are
redacted from errors and excluded from recorded evidence. Each download is
hashed as it streams and never saved.

- The original must match the DAT (size, SHA-1, MD5, CRC32) and the manifest
  (SHA-256).
- The CHD must match the published size and SHA-256.

### 7. Malformed-file isolation

A ZIP holds a poison CHD and a small sibling. The poison's v5 header passes
ROMD's header preflight, but libchdr refuses to open the file because its map
is random. It is uploaded through `POST /api/upload` with
`allowUnidentified=true&archiveOnly=true`; without `allowUnidentified`, the
unmatched sibling would be rejected.

The run passes only if:

- the poison fails with exactly the Malformed description, "The media is
  malformed or its complete output could not be verified.";
- the sibling is ingested;
- the worker neither restarts nor becomes unhealthy;
- no run record or hold remains.

This exercises open-time rejection in a real worker. Decode-time corruption of
an admissible CHD is covered by the deterministic inspection and protocol unit
tests (`ChdInspectionServiceTests`, `MediaWorkerProtocolTests`). The upload
integration analogue, `MediaWorkerUploadRecoveryTests`, uses RVZ.

## Evidence

Pass `--evidence /absolute/path.json`; the default is
`$ROMD_E2E_ROOT/evidence/<invocation>.json`. The path must be outside the run
folders, the fixtures and the checkout.

The JSON records:

- the source revision and dirty flag, image IDs and the expected recipe;
- the Docker server's version, OS, kernel, architecture, CPU count and memory,
  and the host's system, release and machine;
- for each disc:
  - the fixture path, the `manifest.json` and DAT SHA-256s, the DAT game name,
    and the fixture and DAT identities the run compared;
  - the reference import's logical size, SHA-1, MD5 and CRC32;
  - the converged CHD's SHA-256 and size, and whether they equal the
    recipe-measurement artifacts (real v2 discs only);
  - the poll-granular phase timeline and the encode wall seconds per attempt;
  - checkpoint details, including whether the staging file that grew was
    shorter than the final CHD;
  - recovery facts;
  - each download's size, SHA-256, SHA-1, MD5, CRC32 and throughput;
  - peak disk use, both as the host free-space drop and as run-folder
    allocation;
  - cleanup results.

The file is rewritten after each disc passes, so a later disc's hard failure
keeps the earlier disc's record.

Real-mode logs (`<run-id>.log`, the last 3,000 lines per service) stay beside
the evidence. Never attach or upload real-disc logs or evidence. Errors never
echo command lines, so tokens and login forms stay out of output, and content
grant tokens are masked.

## Synthetic mode

`--synthetic` generates every input:

- a PS2 DVD ISO with the `SyntheticPs2Iso` boot layout, padded to
  `--synthetic-mib` (default 1024) with seeded incompressible bytes;
- a matching DAT, the poison and the sibling;
- a mock `redump.info`:
  - The page uses the importer tests' layout.
  - nginx serves it over TLS, with a CA generated for the run, on an internal
    network.
  - Only the worker reaches it, through `extra_hosts` and `SSL_CERT_FILE` in
    the generated overlay, so the unchanged importer runs.

The padding keeps the CHD close to the ISO's size. Staging copies and hashes
the whole CHD before the publication transaction, so the Publishing window
should last seconds on a CI runner. Zero padding would compress the CHD to
almost nothing and close the window.

The working directory is temporary unless `ROMD_E2E_ROOT` is set. On macOS,
set `ROMD_E2E_ROOT` under your home directory; Rancher Desktop may not share
temporary paths with containers
([known issue](../../docs/development/known-issues.md#rancher-desktop-temporary-bind-mounts-need-the-resolved-host-path)).
Cleanup is `down -v` plus the temporary directory, and logs are kept only on
failure.

In CI the step:

- reuses the `romd-*:test` images that `smoke.py --build` produced;
- runs with `--convergence-timeout 15 --upload-timeout 10` under a 45-minute
  step limit;
- uploads only the diagnostics directory, and only on failure or
  cancellation.

A synthetic pass is designed to show composition and regression behavior:

- the normal hosts and importers;
- explicit native activation;
- PS2 eligibility and convergence;
- the named crash checkpoint with a re-executed recovery;
- for the synthetic task, live-child custody recovery after container recreation;
- both delivery formats;
- open-time malformed-input isolation.

It does not show:

- real-disc behavior, timing or memory;
- that the recipe reproduces the measured artifacts;
- linux-arm64 behavior, when it runs on x64 CI;
- worker export reads;
- PCSX2 playback;
- the other crash checkpoints.

## Expected cost and risks

- **Duration:**
  - A real disc should take roughly 45–90 minutes on the shared 2-CPU/6 GB
    Rancher VM. That covers the upload, ingestion, two encodes because of the
    crash, verification, the 5-minute lease wait and four multi-GB downloads.
  - The image build adds about 20 minutes or more.
  - The synthetic run should take roughly 10–15 minutes once the images exist.
- **Disk:** run data stays on the host through the bind mount. The VM disk
  still grows with PostgreSQL and container logs.
- **Unverified until the first Docker run:**
  - bind-mounted data ownership and permissions (UID 1654 against the host
    user, mode `0707`);
  - incremental growth being visible to host polling, and file leases, over
    virtiofs;
  - .NET honouring `SSL_CERT_FILE` for the mock CA.

## Decoder recipe upgrade

`mise run e2e:chd:upgrade` qualifies retained-data re-verification separately from
fresh convergence. Supply existing image sets: the current stock managed hosts
with a genuine recipe-1 decoder packaged into the baseline images, and the
current stock images with recipe 2. Build the earlier decoder from its committed
source with its own verified manifest. Never edit a manifest's tuple or seed
invented database evidence. Keep both image sets on the same native RID.
The baseline's managed host version must include the current worker exit and
reconciliation fixes; this isolates the decoder transition being tested.

The recipe-1 source reference for this transition is
`0c71e61b6f57b95bb1e16c9de772779451888deb`. Build its `libchdr-artifacts`
Dockerfile target on the native architecture using the
[native build guide](../../native/libchdr/BUILD.md). In the private baseline
image context, copy only its verified `build-manifest.json` and
`native/libmoonlark_chdr.so` into the corresponding `/app/native/libchdr/`
paths of each current server image. Preserve the image entrypoint and file modes.
When deriving from local image tags, use `docker build --builder default` so the
builder can resolve the daemon's local image store. Do not publish these
qualification-only baseline images or commit native assets.

```bash
ROMD_FIXTURES=/absolute/fixtures ROMD_E2E_ROOT=/absolute/execution \
  mise run e2e:chd:upgrade -- --disc ffx \
  --baseline-image-tag <recipe-1-baseline> --image-tag <current-stock> \
  --https-bind 127.0.0.2 --evidence /absolute/evidence/ffx-upgrade.json
```

Run `--synthetic --synthetic-mib 64` first with the same image tags. This smaller
case needs no active-encode checkpoint. It generates all media and uses the
same mocked Redump reference as the normal synthetic harness. Real qualification
still requires both independently matched DVD-5 and DVD-9 fixtures.

The qualifier ingests and converges under recipe 1, checks both delivery formats,
and waits for ingestion, reference reconciliation and convergence to settle.
It recreates worker, admin and consumer over the same data with recipe 2,
requires both formats to be unsupported without grants, then starts a bounded
one-item verify-only `RetainedRepresentations` selection through the normal
admin API. It requires exact selected/owned/completed counters and the stopped
child attempt's captured decoder and complete persisted verification. Restored
full download hashes, unchanged artifact/preference/source equivalence/encoding
provenance, append-only measurement/integrity/boot evidence, and released source
pins are checked from current job resources and read-only graph snapshots.
Database queries are read-only snapshots; all evidence is written by normal
production code. Every run removes only its own Compose project and run folder.
Image tags are caller-owned and retained. Evidence and host logs remain private.

## PS1 original-disc recovery

`python3 scripts/e2e/chd_cd.py --config /absolute/private/qualification.json
--fixture NAME --root /tmp/romd-ps1-drill --evidence /absolute/private/run.json`
uses the same strict real-fixture configuration as the original CD probe. Keep
this shell command on one line. Supply existing stock images as
`romd-ps1-production:{worker,admin,consumer,player}`, or set `--image-prefix`.
The configured native/tool directories are probe inputs; they are never mounted
into the production hosts. Server images use their shipped native defaults.

The harness checks the pinned DAT and exact source hashes, uploads its selected
DAT entry and original CUE/BIN ZIP through normal intake, explicitly requests
compression, and observes a permitted CD child of namespace PID 1, a live pinned
chdman and growing output before SIGKILL. It recreates only its worker, waits for
ordinary lease expiry without SQL mutation, and requires a fresh encode attempt.
It checks one complete archive/journal, no active source pins or custody residue,
full original BIN and retained CUE downloads on both hosts, the separately authorized
whole CHD, unchanged supplied originals and malformed-input sibling isolation. It inherits
the HTTPS example's streaming-overlay deviation described above. There is no
emulator/gameplay claim and no native ARM64 result.

Use separate runs for a data-only and a mixed data/audio fixture. The root and
all run folders are private and disposable; cleanup is restricted to the run's
own Compose project and run-id directory. Evidence includes private fixture
names and logs: do not commit it. The recipe-comparison probe is independent of
this production drill.

### Retained PS1 CD decoder upgrade and recovery

`python3 scripts/e2e/chd_cd_upgrade.py --config /absolute/private/qualification.json
--fixture ridge-racer --root /tmp/romd-cd-upgrade --evidence /absolute/private/result.json
--baseline-image-prefix romd-cd-recipe1 --image-prefix romd-cd-current` uses the same
real-fixture configuration as `chd_cd.py`. Supply genuine x64 recipe-1 and current
recipe-2 images built from the respective native sources. The drill does not edit
stored manifests, invent evidence or relabel a current binary as an old decoder.

It creates an archive through normal intake/production compression, checks full
baseline downloads, recreates worker/admin/consumer with the new decoder, and
asserts fail-closed BIN/container delivery. A finite verify-only selection's
owned representation child is interrupted only after its permitted run maps the
native library and accumulates CPU time. The
worker is killed with SIGKILL and recreated using shipped Compose with stock image
entrypoints, ordinary execution leases and no native mounts or injected delays.
The drill requires a new permitted verification run, exactly one appended whole-CD
proof/journal, immutable creation evidence/preferences, released source custody,
full admin/consumer BIN/CHD/CUE body hashes and malformed-input isolation. It uses
read-only SQL for evidence. Raw receipts and fixture inventories remain private;
no native ARM64 or emulator/gameplay qualification is implied.

Serialize qualifier startup when sharing one Docker daemon: subnet selection is
read-only until Compose creates the networks, so two simultaneous prepare/start
phases can select the same free subnet. Wait for one stack's networks to exist
before starting another, or run fixtures sequentially. Keep project-scoped cleanup.
