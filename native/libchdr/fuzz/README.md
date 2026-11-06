# libchdr fuzz targets

libFuzzer targets for the pinned libchdr, built from the `../upstream` sources with ROMD's feature
options and run in a disposable native-architecture Linux container. Usage, outputs and the findings policy are in
[the fuzzing how-to](../FUZZING.md).

| File | Role |
| --- | --- |
| `chd_walk_fuzzer.c` | Opens each input from memory and walks the header, map, metadata and a bounded set of hunks |
| `ubsan_canary.c` | An intentional signed overflow, linked only into the `chd_walk_canary` sanitizer canary |
| `CMakeLists.txt` | Compiles the target and every libchdr and codec source with `-fsanitize=fuzzer,address,undefined`; `-DCHDR_FUZZ_CANARY=ON` builds only the canary |
| `Dockerfile` | Toolchain only (clang 18, libFuzzer, sanitizer runtimes, CMake, Ninja) on a digest-pinned base |
| `container.sh` | Runs inside the container: build and check the fuzzing build canary-free, replay the seeds, fuzz, replay given inputs, the parity replay, or the canary |

Keep these in sync with their owners:

- The feature settings in `CMakeLists.txt` mirror `../CMakeLists.txt`.
- The checks in `chd_walk_fuzzer.c` mirror Moonlark.Libchdr's pre-read checks
  (`ChdHeaderReader.ValidateEnvelope` and `Snapshot`, `ChdMetadataIndex.Read`, `ChdMapValidator`).
  They keep the known libchdr defects listed in `ChdMapValidator` away from `chd_read`, so the harness
  feeds libchdr the inputs Moonlark.Libchdr would pass to it. Change both sides together;
  `scripts/native/fuzz.sh parity` fails when their dispositions of a seed differ.
- The `CHDR_FUZZ_VERBOSE=1` disposition lines in `chd_walk_fuzzer.c` are parsed by
  `tests/Moonlark.Libchdr.Tests/GuardParity.cs`; change the format in both.
- `MAX_HUNK_BYTES` (1 MiB) and `MAX_HUNK_COUNT` (2,621,440) in `chd_walk_fuzzer.c` equal ROMD's CHD
  preflight limits (`ChdDvdLimits` in `src/Romd.Media/PlayStation/`); raise them with those limits,
  never below.
