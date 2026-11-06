# Synthetic libchdr fixtures

All data is deterministic and contains no game content. Generate into ignored
artifacts with the exact pinned, locally built chdman:

```sh
dotnet run --project tools/Moonlark.Native.Engineering -c Release -- fixtures generate \
  --chdman artifacts/tools/chdman/<rid>/native/chdman \
  --manifest artifacts/tools/chdman/<rid>/build-manifest.json \
  --log /absolute/path/fixtures.log
dotnet run --project tools/Moonlark.Native.Engineering -c Release -- fixtures verify \
  --directory artifacts/fixtures/libchdr-final
dotnet test tests/Moonlark.Native.Engineering.Tests -c Release --filter "FullyQualifiedName~Fixtures"
```

The 1 MiB DVD source is a logical 2,048-byte sector stream with repeated
pattern and zero hunks. The six DVD cases exercise LZMA, zlib, Huffman, FLAC,
zstd and uncompressed v5. The CD source has 16 MODE1/2352 data frames and 16
stereo audio frames; four audio frames form a stored pregap. Its deliberately
synthetic data-sector EDC/ECC fields are preserved, rather than presented as a
standards-compliant mastered disc. Audio in the source BIN is little-endian.
CHD logical audio is stored with the native CD byte order, while `extractcd`
recovers the exact source BIN. CD cases exercise cdlz, cdzl, cdfl and cdzs.
A separate 16-frame CDRDAO `RW_RAW` fixture preserves deterministic nonzero
96-byte subcode for each raw frame.

The generator validates the tool pin and binary digest, requires both raw and
overall success messages from `chdman verify`, independently recomputes SHA-1
from the extracted logical bytes and checksummed metadata, and compares the
DVD/BIN extractions byte for byte with the original sources. A zero tool exit
status alone does not establish verification. Uncompressed v5 has no stored
hashes and chdman declines verification; its manifest explicitly records this
alongside independently computed hashes and exact source recovery.
Three negative header-hash copies exercise raw mismatch, overall mismatch and
missing raw SHA-1. The pinned chdman returns exit zero for all three, while the
fixture verification parser rejects their diagnostics.

`fixtures-manifest.json` records sources, recipes, CHD and logical hashes,
headers, metadata and recovery results. Source identities for v1 are:

| Source | Bytes | SHA-256 |
| --- | ---: | --- |
| DVD logical source | 1048576 | `28fdd300e672af165872ba82ed0bb5e054949dd75cdccc57f017ce13ddf7b73b` |
| CD BIN | 75264 | `94fcd21e5ed2483fdad9bd02227ee158d807e9b8b1ca3b5c9016512ea051288a` |
| CD CUE | 133 | `d7560171224893384d0aff08e74fdcacfb25a18d14821fe2ec69b5edb9146b03` |

Generated binaries and source data remain local ignored artifacts. These small
synthetic cases establish no real-disc, DVD-9 memory, emulator compatibility or
platform qualification.

Generator `libchdr-synthetic/v2` retains all eleven v1 fixture records and all
five v1 source identities unchanged. It adds four `cdlz` cases with eight-frame
hunks and explicit source-derived logical projections:

| Fixture | Source geometry | Stored frame starts | Logical frames |
| --- | --- | --- | ---: |
| `cd-edge-multi` | Separate 17-frame MODE2/2352, 13-frame AUDIO, 9-frame AUDIO files | 0, 20, 36 | 48 |
| `cd-edge-single` | The same three tracks in one concatenated BIN | 0, 20, 36 | 48 |
| `cd-edge-partial` | First two tracks only; last hunk is half full | 0, 20 | 36 |
| `cd-edge-subcode` | 8 MODE1/2352 and 6 AUDIO frames, each with 96 RW_RAW bytes | 0, 8 | 16 |

Track 2 has two stored pregap frames; track 3 declares a two-frame unstored
pregap. Four-frame alignment is zero padding outside track-local reads. The
subcode case deliberately retains `PGSUB:NONE` with `SUBTYPE:RW_RAW`: stored
pregap subcode follows the track layout. V2 uses the existing arithmetic data
and audio patterns extended with a track discriminator; its new source hashes
are pinned in `V2SourceIdentitiesAreStable`.

CUE extraction omits subcode, so the subcode case uses CDRDAO TOC extraction.
Its raw `.extracted.toc.bin` is retained and hashed in `rawExtracted`. TOC audio
is big-endian; `recoveryTransform: swap-audio-16-after-track1` explicitly swaps
only the 2,352 audio bytes in each track-2 frame, retaining every subcode byte,
to write `.extracted.bin` and require exact recovery of the CUE source bytes.
Transfer verification checks the raw extraction, named transform, normalized
bytes, fixed track metadata, source projection and zero alignment independently
of self-consistent manifest hashes. All generated outputs are flat files.

`ChdCdFixtureV2Tests` checks every stored frame and sector against source files,
audio swapping, subcode including stored pregaps, zero padding, track bounds,
reads crossing hunk boundaries and stream EOF. Separate integrity cases require
both hashes for every new compressed fixture. The source/geometry cases also
apply to the preserved historical reviewer originals recorded in
[reviewed-cd-identities.json](reviewed-cd-identities.json). Those references pin
measured CHD, CUE and source bytes only. V2 uses separate arithmetic recipes
and does not claim to reproduce those historical bytes. In particular, the
historical `sub.chd` is uncompressed with no stored hashes;
its source/geometry evidence is distinct from the v2 compressed-integrity gate.

Generator `libchdr-synthetic/v3` keeps every v2 source, recipe and chdman argument
list unchanged and adds `dvd-zstd-hs4096`: the same DVD source encoded with `-c zstd -hs 4096`, a
candidate ROMD recipe geometry, with the same verification and exact recovery
as the 16,384-byte-hunk DVD cases. It is a libchdr fuzz seed for that geometry;
`GuardParity.RequiredDvdSeeds` lists the DVD seeds the fuzz gate requires.
