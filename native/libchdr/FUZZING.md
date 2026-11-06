# libchdr fuzzing

`mise run fuzz:libchdr` fuzzes the pinned libchdr with libFuzzer. It is a local check, not part of CI.
The M5 gate, `mise run fuzz:libchdr:gate`, runs it for 30 minutes per target after the
[sanitizer canary](#sanitizer-canary) and the [guard parity check](#guard-parity) pass. The target, its build and its container live in
`native/libchdr/fuzz/` ([what each file does](fuzz/README.md)).

## Prerequisites

- Docker with the same architecture as the host: native Linux x64 or ARM64, or
  Apple Silicon with an ARM64 Linux VM. The runner requires a matching native Docker engine and checks the actual
  image architecture before running. The disposable container is capped at 2 CPUs and 4 GB.
  Rootless Docker maps container root to the invoking host user for its output bind.
- The .NET SDK, for the seed export and the parity check.
- The libchdr submodule checked out clean at the pin in `native/libchdr/libchdr.json`.

## Run

```sh
mise run fuzz:libchdr:gate                 # the M5 gate: canary, then parity, then 30 minutes per target
mise run fuzz:libchdr:gate 600             # the same order with another fuzzing time
mise run fuzz:libchdr:canary               # scripts/native/fuzz.sh canary: the sanitizers fire
mise run fuzz:libchdr:parity               # scripts/native/fuzz.sh parity: the harness checks match Moonlark.Libchdr's
mise run fuzz:libchdr                      # 60 s per target
FUZZ_WORKERS=2 mise run fuzz:libchdr 1800  # two libFuzzer jobs per target (the most the VM allows)
```

`fuzz:libchdr:gate` runs `scripts/native/fuzz.sh gate [seconds]`, which stops at the first failing step;
`FUZZ_WORKERS` applies to its fuzzing step.

Each task first runs `native:test-inputs`, which provides the synthetic fixtures and the host build that
the seed export loads. `scripts/native/fuzz.sh run` then:

1. Refuses a libchdr checkout that is off the pin or has local or ignored files.
2. Regenerates the seeds: every fixture `*.chd`, plus the malformed corpus. The corpus is written by
   `dotnet run --project tests/Moonlark.Libchdr.Tests -c Release -- export-malformed-corpus <dir>`, an entry
   point the test runner never calls.
3. Builds the toolchain image `romd-libchdr-fuzz:<amd64|arm64>` (cached, kept between runs). It is
   `ubuntu:24.04` pinned by digest, plus clang 18, libFuzzer, the sanitizer runtimes, CMake and Ninja
   from apt.
4. Freezes one private source snapshot for the entire invocation, then builds in a
   `--rm` container with that snapshot mounted read-only. Checkout edits cannot
   rewrite a running shell script or change sources between gate phases. The targets go into
   `artifacts/fuzz/<rid>/build`. Every object, codecs included, is compiled with
   `-fsanitize=fuzzer-no-link,address,undefined`. Undefined behaviour aborts, and asserts stay on.
   The build must be canary-free: its CMake cache sets `CHDR_FUZZ_CANARY` off, no compile command defines
   a canary macro, and the binary has no canary symbol. Every `fuzz.sh` command checks this.
5. Runs every seed once, then fuzzes each target for the given time. libFuzzer limits:
   `-rss_limit_mb=1536` (also the largest single allocation), `-timeout=10`, `-max_len=2097152`.

A nonzero exit means a finding, or a seed that failed its first replay.

## What the target covers

`chd_walk_fuzzer` opens each input from memory through `chd_open_core_file_callbacks`, the entry point
Moonlark.Libchdr uses, with the same callback semantics. It then:

- Walks the header.
- Walks the map. This is the v5 map libchdr decoded, or the v1–v4 entries read back from the input.
- Enumerates up to 64 metadata entries with `chd_get_metadata`, by wildcard and then by tag.
- Decodes up to 64 hunks, spread evenly over the image and at most 4 MiB in total, into a buffer of
  exactly `hunkbytes`. ASan therefore sees any write past the hunk.

The read-ahead budget (0, one hunk or 1 MiB, capped at the input size as `ChdFile` caps it) and the read
order (forward or reverse) depend only on the input size, so a replay repeats the run exactly.

Before libchdr reads a hunk, the input must pass Moonlark.Libchdr's own pre-read checks, mirrored in C:

- `ChdHeaderReader.ValidateEnvelope`, including the metadata-chain rules of `ChdMetadataIndex.Read`.
- The geometry check in `ChdHeaderReader.Snapshot`.
- `ChdMapValidator`.

Those checks exist because the pinned libchdr mishandles some maps. Examples are a MINI entry in a hunk
under 8 bytes, unbounded self-reference recursion, and parent entries with no parent. The harness
therefore feeds libchdr the inputs Moonlark.Libchdr would pass to it. Moonlark.Libchdr serves metadata
from its own index and calls only the open, header, cache-budget, read and close functions, so a finding
inside the harness's own `chd_get_metadata` calls needs separate reachability triage.

Harness bounds, not ROMD limits, reject a header before `chd_open` when:

- `hunkbytes` is above 1 MiB, or
- there are more than 2,621,440 hunks.

Both equal ROMD's CHD preflight limits (`ChdDvdLimits` in `src/Romd.Media/PlayStation/`), which refuse
such files before `ChdFile` opens them, so every header ROMD admits reaches libchdr; libFuzzer's
`-max_len` still caps input size.

## Output

Everything goes to `artifacts/fuzz/<linux-x64|linux-arm64>/`, which Git ignores.
The RID directory is the output root for the relative paths below; builds,
corpora and receipts from different architectures never share a directory.
Earlier ARM64 output directly under `artifacts/fuzz/` is historical evidence.
It is not reused by a current campaign:

| Path | Contents |
| --- | --- |
| `source.*/` | Frozen native and harness inputs for one invocation; retained for investigation/replay |
| `seeds/fixtures`, `seeds/malformed` | The seeds, regenerated each run |
| `build/` | The CMake build of the targets (the fuzzing build) |
| `canary/` | The canary's own build directory, `asan.log`, `ubsan.log` and `control.log`; never fuzz or replay with it |
| `canary/summary.txt` | The canary verdict, written only when it passed: clang version, exit statuses, the matched report lines, the control rejections and both binaries' SHA-256 |
| `parity/harness.log` | The harness's disposition line for every seed, from the parity replay |
| `parity/report.txt` | The managed comparison's output: counts per disposition, mismatches and codec seeds |
| `<target>/corpus/` | Inputs libFuzzer added. Kept across runs; delete it to restart from the seeds |
| `<target>/findings/` | `crash-*`, `leak-*`, `timeout-*`, `oom-*` reproducers |
| `<target>/logs/<UTC time>/` | `seeds.log` (first replay), `fuzz-<job>.log` ending with `-print_final_stats`, and `jobs.log` with two workers |

## Replaying a finding

```sh
scripts/native/fuzz.sh replay artifacts/fuzz/linux-x64/chd_walk_fuzzer/findings/crash-<sha1>
CHDR_FUZZ_VERBOSE=1 scripts/native/fuzz.sh replay artifacts/fuzz/linux-x64/seeds/fixtures/*.chd
```

The replay runs `chd_walk_fuzzer <file>...` in the same container and build. Each input runs once with
full sanitizer output; libFuzzer truncates an input longer than `-max_len` (2 MiB) to that length.
`CHDR_FUZZ_VERBOSE=1` prints one line per input: `chd_walk: reject stage=<envelope|bound|open|validation>`
with the input size and the check that rejected it, or `chd_walk: accept` with the size, version, codecs,
geometry, read-ahead budget, read order and the hunks read and decoded. The format is stable because the
parity check parses it. Inputs outside the selected RID output root are copied to its `replay/` directory first.

Every crash, hang, leak or out-of-memory finding becomes a malformed-corpus test
(`tests/Moonlark.Libchdr.Tests/MalformedCorpus.cs`) and is fixed before CHD is enabled.
A reviewed upstream repair is adopted by moving the libchdr pin. Other native
repairs or Moonlark.Libchdr guards need an owner-approved proposal; mirror any
guard in the harness.

The approved local exception is native build recipe 2's incomplete-Huffman-table
initialization. It repairs a checksum-verified build-tree copy, keeps the pinned
upstream checkout pristine, and runs an allocator-pattern/table-reuse regression
before every library build. The fuzz harness applies the same repair. This
exception does not authorize other upstream patches or establish release
qualification; see the [native build guide](BUILD.md) and
[decoder upgrade procedure](../../docs/self-hosting/production-deployment.md#chd-decoder-upgrades).

## Sanitizer canary

`scripts/native/fuzz.sh canary` shows that the sanitizers fire and that the fuzzing build has none of the
canary. It exports the seeds, then, in the same container limits:

1. Builds and checks the fuzzing build as every command does (step 4 above).
2. Deletes `artifacts/fuzz/<rid>/canary/` and configures a new build there with `-DCHDR_FUZZ_CANARY=ON`. That
   build has only `chd_walk_canary`: the harness compiled with `CHDR_FUZZ_SKIP_MAP_GUARD` (the map guard
   off) and `CHDR_FUZZ_UBSAN_CANARY`, plus `ubsan_canary.c`, an intentional signed overflow that an input
   starting with `ROMD-UBSAN-CANARY` triggers. The harness refuses to compile with only one canary macro.
3. Requires the canary to fail with an ASan `heap-buffer-overflow` in `put_bigendian_uint64_t` on the seed
   `crafted-v4-mini-hunk-of-1.chd` (the known MINI overrun), and with a UBSan `signed integer overflow` in
   `ubsan_canary.c` on the trigger.
4. Requires the fuzzing build to reject both inputs before reading a hunk: the MINI seed at its map guard
   and the trigger at its envelope check. It then prints both binaries' SHA-256 and writes
   `artifacts/fuzz/<rid>/canary/summary.txt`.

It exits 0 only when all of that holds. The canary build is never used to fuzz or replay.

The ASan half relies on the MINI overrun that the pinned libchdr still has. A pin move that fixes it makes the
canary fail; that change must then give the canary another known heap overrun, with its expected report.

## Guard parity

`scripts/native/fuzz.sh parity` checks that the harness's mirrored pre-read checks match
Moonlark.Libchdr's. It exports the seeds, replays each one whole in the container with
`CHDR_FUZZ_VERBOSE=1` and no `-max_len`, then runs
`dotnet run --project tests/Moonlark.Libchdr.Tests -c Release --no-build -v quiet -- guard-parity <harness-log> <seeds>`
(the seed export has just built it), keeping its output in `artifacts/fuzz/<rid>/parity/report.txt`.
That entry point, which the test runner never calls, opens every seed with `ChdFile.TryOpen` on the host
and separately runs `ChdHeaderReader.ValidateEnvelope` to tell rejections before `chd_open` from later
ones. It prints a count per disposition and fails on any mismatch:

| Harness | Moonlark.Libchdr | Treatment |
| --- | --- | --- |
| Rejected at the envelope check | Rejected by `ValidateEnvelope` | Agree |
| `chd_open` failed, or the map or geometry check rejected | Rejected after `ValidateEnvelope` | Agree |
| Accepted, so it reaches `chd_read` | Opened | Agree |
| Rejected by a harness-only bound, for a geometry beyond 1 MiB hunks or 2,621,440 hunks | Rejected after `ValidateEnvelope`, or opened with exactly the geometry the harness bounded | Agree, counted separately: `ChdDvdLimits` refuses these files before `ChdFile` |
| Any other pairing, a bound rejection within both limits, a seed with no harness line, or a harness line for fewer bytes than the file | | Mismatch |

The harness-only bounds are the hunk size and count above; `ChdFuzzHarnessBoundsTests` in
`tests/Romd.Media.Tests` fails when they differ from `ChdDvdLimits`. The parity replay feeds whole files, so
`-max_len` truncation cannot hide a difference; the container lists the seeds the campaign truncates
instead. The read-ahead budget never changes admission, so the managed side opens with it off.

The check also requires the fixture seeds to include a parent-free v5 DVD image, with 2,048-byte units and
one codec, whose every sampled hunk the harness decoded, for each supported DVD codec (none, zlib, LZMA,
Huffman, FLAC, zstd) at 16 KiB hunks, and for zstd at 4 KiB. These cover ROMD's current `chd-dvd-canonical/v2` recipe (zstd,
16 KiB), the earlier zlib recipe and the 4 KiB zstd candidate. `native:fixtures` generates them; the 4 KiB case
is `dvd-zstd-hs4096.chd`.
