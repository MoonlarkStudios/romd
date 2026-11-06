# Native-relative libchdr benchmarks

The development-only `Moonlark.Libchdr.Benchmarks` executable uses the pinned
BenchmarkDotNet 0.15.8 package. It does not change the library's dependencies or
public API. Its package reference must flow to BenchmarkDotNet’s generated
runner project; `PrivateAssets="all"` prevents that runner from compiling.
The benchmark executable is nonpackable. Run on an otherwise quiet qualified host with the exact SDK in
`global.json`, verified native assets in `artifacts/native/libchdr/<rid>`, and
the synthetic bundle in `artifacts/fixtures/libchdr-final`:

```sh
dotnet restore tests/Moonlark.Libchdr.Benchmarks --locked-mode
dotnet build tests/Moonlark.Libchdr.Benchmarks -c Release --no-restore -t:Rebuild -warnaserror
dotnet run --project tests/Moonlark.Libchdr.Benchmarks -c Release --no-build -- \
  run artifacts/benchmarks/reviewed-run-1
dotnet run --project tests/Moonlark.Libchdr.Benchmarks -c Release --no-build -- \
  gate artifacts/benchmarks/reviewed-run-1/measurements.json
```

The source checkout must be clean, including untracked files, and the output
directory must be new. `inputs.json` records the exact source commit, SDK,
runtime/RID, native binary and manifest, benchmark/library DLLs, fixture
manifest and seven CHD hashes, plus the fixed job policy and unique run ID.
The parent verifies the original HEAD and clean tracked/untracked source before
and after each child build, before/after execution, and after the run.
BenchmarkDotNet rebuilds the managed assemblies into its generated project, so
their bytes can legitimately differ from the parent DLLs. The normal out-of-process
builder is retained: after each successful build, the parent writes a new
`moonlark-child-inputs.json` into that build's binary directory. It binds the
original parent receipt, unique build ID, partition, generated source/project/script,
entry assembly/deps/runtimeconfig, and benchmark/library DLLs. The parent keeps
that receipt's digest in memory and passes it to the normal executor. Child setup
verifies this digest, the exact file inventory, paths and bytes, loaded assembly
identities, and unchanged native/fixture inputs. Linked or escaping child files
are rejected. Parent verification repeats after execution. Generated build files
and outputs are retained, with unique job names preventing reuse across runs.
`gate.json` binds the original input receipt, measurement JSON, per-launch allocation
evidence and completed child receipts by SHA-256, including failed runs; missing build evidence fails closed. Keep the
complete BenchmarkDotNet console
log, full JSON report, generated build logs, `measurements.json`,
`launch-allocations.json` and `gate.json`
together with the source revision, native/managed binary hashes and fixture
manifest. Running this command is a timing experiment; ordinary build or
focused gate tests do not establish benchmark acceptance. Linux x64 and ARM64
are development/CI evidence targets; this Mac is the local development host and
osx-arm64 qualification remains supported. Short reviewer smoke runs are diagnostic
only. Windows qualification is manual or release-gated.

The native-evidence workflow exposes a manual `benchmarks` boolean, disabled by
default. Selecting it runs this complete, unchanged policy after native qualification
on each Linux runner and the manual Windows runner. Linux uses the clean nested
qualification checkout and its container-built native assets; BenchmarkDotNet child
builds and measurements run on the matching hosted runner. Windows uses its qualified
checkout. These jobs have a 180-minute timeout. Hosted-runner noise can still make
the timing gate inconclusive; dispatch alone does not establish acceptance.

Each selected job retains the console log, all benchmark receipts and reports, and
the parent and generated child build directories for seven days, including failed
attempts. Download and audit them before expiry. Benchmark failure fails that job
and can prevent Linux attestation; inspect native qualification and timing receipts
separately. Ordinary pushes and dispatches with the option disabled do not run
benchmarks. No package publication or additional signing permission is enabled.

The matrix contains all six DVD codecs (`lzma`, `zlib`, `huff`, `flac`, `zstd`,
`none`) and the v1 `cd-cdlz` fixture. Each fixture has four paired operations:

| Operation | Raw baseline | Safe operation | Access |
| --- | --- | --- | --- |
| Hunk | Generated native `chd_read` | `ChdFile.ReadHunk` | Alternating hunks 1 and 2, whole hunk |
| Hit | Explicit raw-native one-hunk-cache adapter | `ChdFile.ReadAt` | 4,096 bytes at hunk 1 + 17 |
| Miss | Same adapter | `ChdFile.ReadAt` | 4,096 bytes alternating hunk 1/2 + 17 |
| Cross | Same adapter | `ChdFile.ReadAt` | 4,096 bytes across the hunk 1/2 boundary |

The adapter is benchmark code, not a claim that libchdr exports a positional
read API. Both paths use the production `FileChdDataSource` and
`ChdSourceContext`, independent native handles, the same verified native binary
and source file, zero compressed read-ahead, pooled one-hunk caches and equal
caller buffers. Handles, source verification, buffers, equality checks and
warmup are outside timing. Setup compares raw/safe bytes and geometry, warms
both paths, then requires an exact zero current-thread allocation delta across
1,024 operations per path. The benchmark returns an output byte to keep reads
observable. These are warm synthetic-file measurements; they do not qualify
real-disc storage throughput or large-image memory use.

Acceptance was fixed before measurements. The complete 56-row matrix must have
exactly three measured process launches per row and zero managed allocation.
For every paired case, the safe mean must be at most
`1.25 * raw mean + 250 ns`. Each mean's 95% confidence-interval half-width must
be at most 5% of that mean. Six warmup iterations precede 15–50 measurement
iterations per launch; outliers are retained. Each of the three successful launches
must have exactly one raw GC counter window. The gate uses the largest unrounded
allocated-bytes/operations ratio across those launches. A positive allocation in
any launch fails; a final zero-allocation launch cannot hide an earlier failure.
`launch-allocations.json` retains the launch indices, process success, raw GC
lines and parsed counters. Rounded display and JSON summary metrics are not
allocation evidence. Unknown or missing
allocation metrics are persisted as explicit JSON null and fail closed, so a
missing counter cannot prevent writing a failed gate receipt. Raw runtime allocation accounting may include
allocation-quantum effects; these remain recorded and cannot be waived as a
pass after the fact.

`gate` returns 0 for a complete pass, 1 for malformed/missing/duplicate cases,
invalid metrics, allocation or a precise regression, and 2 when uncertainty
makes timing inconclusive. Noise never becomes a pass. An independent invalid
metric or allocation remains a failure even when timing is noisy. Do not raise
the limits or remove cases to accommodate observed results. Investigate failed
or noisy runs and retain every attempt before repeating on a quiet host.
