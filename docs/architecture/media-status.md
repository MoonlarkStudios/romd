# Media implementation and qualification status

This is the maintained qualification matrix for server media and its delivery
clients. [Media architecture](game-media.md) owns facts, policy and reconciliation;
[disc-set architecture](disc-sets.md) owns grouping, layouts and snapshots.
Operators use [media processing](../self-hosting/media-processing.md) and
[decoder upgrades](../self-hosting/production-deployment.md#chd-decoder-upgrades).

Qualification covers the named build, platform and workload only. Source tests,
synthetic fixtures and an enabled image default do not qualify a downloaded
release. The [release gates](../maintainers/release-readiness.md) additionally
require the exact published bundle, security/provenance and supported recovery
path. Raw receipts, fixture identities and credentials stay in private evidence.

## Implemented scope

- One fresh guarded PostgreSQL baseline with additive PSP observations, immutable
  reconstruction graphs, retained decoder measurements and one reconciliation
  family. Unmeasured holdings do not authorize bytes.
- Whole-file GameCube/Wii RVZ and PS2 DVD/PSP UMD CHD; complete PS1 CD-track CHD.
  Current kind policy controls Automatic reconciliation. Kind, eligibility and
  canonical status are derived from facts rather than persisted decisions.
- Worker-only supervised inspection/encoding, separate verification, durable
  source pins, bounded staging and stopped/fenced atomic publication/recovery.
- Original/available stored downloads, exact format targets, scoped member reads,
  read-only VFS and metadata-only responses. Runtime claims require a unique kind;
  admitted byte delivery remains independent of emulator applicability.
- Compressed-only CD intake, DAT-exact or explicitly Derived CUEs, verified original
  CUE adoption, published disc-set snapshots and explicit partial-install consent.

Not implemented: PS2 CD compression, a second CD-track kind's per-layout recipe
routing, GDI/folder-tree reconstruction, MAME dependency media, coordinated limits
across every upload/archive/cache workspace, the delivery cache and multi-arch
release images. New manifest families need their own contracts and qualification.

## Qualification status

| Area | Recorded qualification | Limits still in force |
| --- | --- | --- |
| GameCube/Wii RVZ | macOS ARM64 real-disc managed encode/decode, convergence, original/RVZ downloads, grants, ZIP/export execution and registered FSKit reads; Linux ARM64 production-image resource enforcement and worker-kill recovery (2026-10-02–04) | Two discs; some export tests used TestServer seams. Native x64, Windows, real bzip2, Korean/dual-layer Wii, Steam Deck, WAN and gameplay remain outside the measured scope. |
| PS2 DVD CHD, current native recipe 2 | Linux x64 independently DAT-matched DVD-5/DVD-9 canonical reproducibility, active-encode container recreation, full admin/consumer original/CHD hashes, malformed isolation, retained recipe-1 re-verification, sanitizer canaries, parity and 30-minute fuzz gate (2026-10-19) | Native ARM64 recipe 2 is pending; historical ARM64 recipe-1 results do not transfer. Linux PCSX2/gameplay, Windows, WAN/load and real-disc worker export reads remain unqualified. |
| Historical PS2 ARM64 and emulator startup | Linux ARM64 real-disc encode/read/recovery and fuzz with the earlier native recipe; PCSX2 2.8.2 under Rosetta on macOS booted both resulting CHDs (2026-10-14) | Startup is not gameplay. These results do not qualify the repaired native recipe-2 build. |
| Container recovery | Stock PID-1 worker, synthetic 1-GiB PS2 image, active chdman output, SIGKILL/recreation, ordinary lease recovery, released holds and both-format hashes on rootless Docker/Linux x64 (2026-10-19) | Synthetic containment/recovery evidence does not substitute for real discs, another RID or a published-bundle installation. |
| PS1 CD storage and retained upgrades | Linux x64: three data-only and one 14-track mixed data/audio original set, exact 17-BIN reconstruction and unchanged CUEs; native recipe-2 encode/recovery and recipe-1 retained upgrade/recovery with complete hashes (2026-10-19–20) | Raw 2352-byte tracks and stored pregaps; virtual-pregap/subcode originals, native ARM64, emulator/gameplay and broader layouts remain unqualified. Later shared-BIN/correction paths have PostgreSQL/unit coverage beyond the earlier Compose drill. |
| PS1 CUE regeneration | Three single-track MODE2 controls and two mixed data/audio inputs (20/57 tracks, 19/56 stored pregaps): 5/5 regenerated CUEs matched authenticated references byte for byte | One BIN per track; no population-wide match rate or real shared-BIN/multi-track-per-file qualification. |
| PS1 read/install capacity | The scoped loopback/FIFO and full-host runs below passed with bounded decoder/cache/queue accounting and native cleanup. Automatic was accepted after the mixed-track workload (2026-11-02). | One Linux x64 machine, warm uncontrolled caches; no whole-host memory bound, preemption of active transfers, generalized starvation freedom, simultaneous conversion throughput, packaged ingress, WAN or gameplay claim. |
| PSP UMD CHD | Linux x64: five independently DAT-matched game ISOs, supervised source inspection, encoding, separate full-hash verification and exact random/boundary/sequential reads after staging disposal. Generated production-reconciliation fixtures cover re-encoding, publication and injected rollback/replay/deadlock/cancellation (2026-11-02). | Real-disc probe used isolated CAS/supervised children. Full-host PSP intake, authenticated HTTP/console delivery, native ARM64, launch and gameplay remain unqualified. No PSP native-runtime claim is registered. |
| Disc sets and console acquisition | Source contracts cover ordered published selections, original/Derived provenance, dependency closure, partial consent, scoped grants, strict parsing and durable install/repair | Production group playback remains gated on actual disc switching and shared-save acceptance. Native desktop login/secure-store behavior is not established by Flutter test-engine installer runs. |

CHD is enabled in both Linux image architectures, while published images remain
amd64 only. Read evidence binds the exact RID, libchdr pin and native build recipe.
A changed tuple requires retained-artifact re-verification and its own platform
gate; an encoding recipe change is a different concern. Follow
[platform qualification before activation](../self-hosting/production-deployment.md#platform-qualification-before-activation).

## Recipe measurements

These observations explain selected recipes, not performance guarantees. They
used the named hosts/fixtures and warm, uncontrolled OS caches; they are not
cold-storage, WAN or gameplay benchmarks.

### DVD CHD

The `chd-dvd-canonical/v2` selection used four candidates on two DAT-matched PS2
DVD-5/DVD-9 inputs (4.5/5.3 GB) on macOS ARM64 and byte-identical Linux ARM64
outputs. Candidates were within 2% of the smallest artifact. Zstd/16-KiB hunks
roughly halved random-read p95 and doubled sequential throughput to about
480–510 decimal MB/s compared with the earlier zlib recipe, at about twice the
encode CPU. Native x64 recipe-comparison and disk-cold benchmarks remain open.
The original 600-second budget projected encode failures beyond about 5.5–5.8 GB
on the measured macOS host; the current 1,200-second budgets still do not promise
completion for every disc or slower machine.

### PS1 CD

Four independently DAT-matched original sets (three data-only and one 14-track
mixed data/audio set) passed all 16 candidate comparisons. Each candidate had a
fresh whole-member verification and three read trials. Totals below are decimal
bytes and summed encode wall time, excluding verification and hashing:

| Codecs | Hunk bytes | Artifact bytes | Encode seconds |
| --- | ---: | ---: | ---: |
| `cdzs` | 19,584 | 1,523,191,217 | 434.3 |
| `cdzs,cdfl` | 19,584 | 1,426,680,819 | 487.6 |
| MAME default `cdlz,cdzl,cdfl` | 19,584 | 1,407,361,166 | 214.8 |
| **`cdzs,cdfl`** | **39,168** | **1,414,168,186** | **359.5** |

The selected recipe costs about 0.5% more bytes than MAME's default but reads the
three data discs at 432–455 MB/s instead of 45–55 MB/s. The mixed-disc median is
303 MB/s; FLAC saves about 97 MB there versus `cdzs` alone. Selected-recipe random
4 KiB p95 was 0.16–0.23 ms. Trials used 1,000 warmup and 10,000 measured random reads,
with independent oracle comparisons outside timing; OS cache state was warm and
uncontrolled. These are fixture/host measurements, not cold-storage or gameplay
benchmarks. The original qualification probe remains available for other layouts.

### PSP UMD

Five game ISOs saved 13.19–54.47%, 34.14% in aggregate. Comparing the smallest
and largest with 2-KiB hunks retained the existing 16-KiB zstd recipe: artifacts
were 5.74%/7.80% smaller and median sequential reads were 562/895 versus 350/446
decimal MB/s. Native 4-KiB random p95 was 0.0314–0.0377 versus 0.0142–0.0160 ms,
within the predeclared 1-ms criterion. Each candidate had three trials, 1,000
warmup and 10,000 measured random reads; no HTTP or gameplay was measured.

## PS1 storage and read load

The earlier 30-minute five-disc TestServer/direct load covered all 80 BIN members
at 994.54 MiB/s with authentication/library doubles. A later single-process local
Kestrel HTTPS before/after experiment used real Basic credentials, PostgreSQL
library/catalog queries and a seeded projection. FIFO delivered 957.06/955.72
MiB/s with four/six bulk readers; all 2,400 loaded interactive arrivals completed
without 503s, deadlines or misses. Completion p95 was 1.29–2.35 seconds and maximum
3.762 seconds, compared with 105.243 seconds of repeated capacity backoff before
FIFO. Conditional medians rose from 26.2–32.5 to 160.3–376.7 ms as more arrivals
waited and completed. Loopback staging was not sampled.

The subsequent full-host run used separate worker/admin/consumer hosts, normal
DAT/import/publication, actual OpenIddict authorization and Library lookup, with
no authentication/library doubles or seeded projections. It verified 24 installs,
2,880 interactive ranges and 36,041 bulk bodies with four/six bulk readers,
fast installers and a 4-MiB/s client. Worst range completion was 1.702 seconds
without refusals or retries; cancellation/recovery and terminal native cleanup
passed. The production installer ran in the Flutter test engine. Conversion
preceded timed downloads. Native desktop/UI, OS secure-store persistence/login
restoration, packaged ingress, mounts, WAN and gameplay are outside this run.

The earlier 1,801.5-second three-disc MODE2 direct/in-process test established
metadata-only reads and native cleanup at the four-active/two-idle, 64-MiB cache
defaults with auth/library doubles; it did not establish mixed-track network
performance. Keep these scopes distinct from the later tests.

For RVZ, the 1,809-second default load passed all 24 contexts with three explicit
admission deferrals; the longest original-range no-progress gap was 570 seconds.
Continuous progress and starvation freedom remain unqualified. FIFO grants the
oldest waiter but does not preempt a slow active whole-file transfer.

## Network client scope

| Client/profile | Recorded evidence and operational limit |
| --- | --- |
| Registered rclone/macOS FSKit | rclone 1.75.1, macOS 26.6.2, macFUSE 5.4.0: loopback HTTPS protocol with a synthetic 64-MiB file, real GameCube/Wii original/RVZ hashes, ranges, EOF and remounts. Use the checksum-aware exported WebDAV profile; the older `vendor = other` profile returned stale same-size replacements. Same-mount FSKit refresh remains unqualified: remount after a same-size/time replacement. |
| FSKit efficiency/isolation | A cold Dolphin trace transferred about 4.8× requested bytes; full hashing about 2.1×. `/Volumes` did not isolate other users; a private home-directory parent blocked the tested user. Real second-account isolation, Spotlight, location stability, long soak and WAN remain unqualified. |
| Native macOS WebDAV | EOF-crossing reads returned zero padding despite correct HTTP ranges. Unsupported for release 1; see [known issues](../development/known-issues.md#native-macos-webdav-eof-crossing-reads-can-return-padded-bytes). |
| Native SMB / development NFS | Samba 4.22/SMB 3.1.1 showed reads/startup but no ROMD auth integration; restart required remount after EIO. Development NFS exposed data to other local users. Neither is a supported substitute. |
| Steam Deck / Windows | Stock rclone with EmuDeck ES-DE/RetroArch and Windows WinFsp remain unqualified. No ES-DE layout is registered. |
| Parked ROMD Mount helper | Experimental bearer-based tooling, with scoped token/cache/lifecycle probes only; not supported-client qualification. See [its README](../../clients/romd_mount/README.md) for retained behavior and gaps. |

The parked helper has no signed/notarized distribution acceptance. Unidentified-
mount and FSKit recovery, legacy cache/startup reconciliation, long-soak/peak-disk
behavior, other OS stores, reboot/adverse recovery and production local-user
isolation remain outside its recorded probes.

For VFS protocol, admission bounds and measurement procedure, use
[VFS architecture](vfs.md). New qualification must name component versions,
cache state, credentials, network and workload rather than inheriting these results.
