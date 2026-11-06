# chdman build boundary

[mame.json](mame.json) records the exact MAME
0.289 source archive, size, digest, tag and commit. The distribution gate must
build only chdman, strip it and check dependencies on all three pinned native
RIDs. Every release needs corresponding source, an exact linked-license
inventory and build provenance. The preflight workflow publishes no binary.

The local recipe builds on its native host (macOS ARM64, Linux x64 or Linux ARM64)
into `artifacts/tools/chdman/<rid>` by default:

```sh
dotnet run --project tools/Moonlark.Native.Engineering -c Release -- chdman build \
  --archive /absolute/path/mame0289.tar.gz --jobs 4 --log /absolute/path/build.log
dotnet test tests/Moonlark.Native.Engineering.Tests -c Release --filter "FullyQualifiedName~Chdman"
```

The command verifies the archive size and SHA-256 before safely extracting it.
It builds MAME's bundled GENie, generates the tool projects and builds the
chdman target with the bundled codec archives. It records the pinned commit in
the version banner. The final link explicitly omits emulator frameworks from
MAME's global macOS settings; dead stripping and an exact allowlist leave only
libSystem and libc++. No source file is patched. Link warnings are fatal.
Apple ld's `-reproducible` preserves stable UUIDs despite incidental object
properties; builds must compare complete bytes, including their signatures.

Apple's C++ runtime is libc++ on macOS. The required Linux recipes must use
`-static-libstdc++`; neither Linux RID has run under this local recipe. Upstream
MAME and its bundled GENie/FLAC emit compiler warnings on Apple clang 21. These
remain visible in the raw log, with upstream warning flags unchanged. This
local tool is explicitly unqualified, has no attestation and must never be
committed as a native binary.

The receipt (`build-manifest.json`, schema version 2) records the binary's
`binary.sha256`, the MAME `pin` (tag and `sourceSha256`), `rid` and
`recipeVersion`, the hand-bumped `ChdmanBuild.RecipeVersion`; `fixtures generate`
and `fixtures verify` accept only the current pin and recipe version. Its other
fields, such as input paths and compiler banners, are local evidence.

The receipt's `recipeSha256` is SHA-256 over a `shasum -a 256` listing of the C#
recipe sources, `tools/Moonlark.Native.Engineering/Chdman/*.cs`, in ordinal path
order. The linked binary's LC_UUID depends on the length of the absolute build
path, so byte comparisons between rebuilds need an output path of the same
length; the C# recipe reproduced the Python recipe's final digest
`e47a873059df60e1e706c1189e89968427ed8a36750d3f665c45a598a477cda1` that way.
