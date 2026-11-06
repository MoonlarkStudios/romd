# libchdr native build boundary

The reviewed source pin and header digests are in `libchdr.json`; `libchdr.props`
carries the same identity into the managed assembly. The submodule, CMake wrapper,
presets, shim and the export allowlist (`exports.txt`; CMake generates the
per-platform lists) belong to the native-wrapper gate. Build products land in the
ignored `artifacts/` tree and are never committed; see `BUILD.md`.

## Documentation

- [Build](BUILD.md): native inputs, presets and build products.
- [Fuzzing](FUZZING.md): libFuzzer targets and the local fuzz gate ([fuzz harness files](fuzz/README.md)).
- [Benchmarks](BENCHMARKS.md): native-relative managed benchmarks.
- [Generation](generation/README.md): generated binding inputs.
