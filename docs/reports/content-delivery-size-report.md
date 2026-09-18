# Content Size and Platform Distribution Report

Related issue: #12

## Scope

This report estimates which ROMD content is likely small enough for full-buffer
browser loading and which content should use launch-cache or range-based
delivery. It is grounded in the current domain and persistence model, with
local measurements limited to checked-in test fixtures.

## Where Size and Platform Data Live

- Expected catalog content size is stored on `DatRoms.Size`. These records
  belong to `DatGames`, which belong to `DatFiles`.
- Platform assignment lives on `DatFiles.PlatformId`, joined to `Platforms.Id`.
- Ingested physical file size is stored on `Files.Size` and `Files.SizeOnDisk`,
  joined through `RomFiles.FileId`.
- Matched catalog entries point from `DatRoms.RomFileId` to `RomFiles.Id`.
- Disk/CHD-style entries are stored in `DatDisks`, but `DatDisks` does not
  currently store a byte size.

For delivery planning, the most useful unit is:

```text
DatGame payload bytes = SUM(DatRoms.Size) for that DatGame
```

Per-ROM-entry buckets are still useful for storage and hashing, but they
understate delivery cost for multi-file disc formats such as `.cue` plus `.bin`.

## Local Measurements

No populated ROMD SQLite database exists locally. The only local content-like
data is:

- `tests/Romd.Infrastructure.Tests/Resources/nointro_nintendo_n64.dat`
- `tests/Romd.Infrastructure.Tests/Resources/redump_sony_ps1.dat`
- two tiny storage test `.rom` blobs: 32 KiB and 512 KiB

The DAT files are test fixtures, not a representative ROMD library.

### Fixture Results by Game Payload

| Fixture | Games with ROMs | p50 | p90 | p95 | p99 | Max |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| No-Intro Nintendo 64 | 1,147 | 16 MiB | 32 MiB | 32 MiB | 64 MiB | 128 MiB |
| Redump PlayStation | 563 | 577 MiB | 700 MiB | 709 MiB | 716 MiB | 716 MiB |

Bucket distribution by game payload:

| Fixture | <=16 MiB | >16-64 MiB | >64-256 MiB | >256 MiB-1 GiB | >1 GiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| No-Intro Nintendo 64 | 556 (48.5%) | 566 (49.3%) | 25 (2.2%) | 0 (0.0%) | 0 (0.0%) |
| Redump PlayStation | 0 (0.0%) | 2 (0.4%) | 40 (7.1%) | 521 (92.5%) | 0 (0.0%) |

Threshold coverage:

| Fixture | <=16 MiB | <=64 MiB | <=128 MiB | <=256 MiB | <=512 MiB | <=1 GiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| No-Intro Nintendo 64 | 76.5% | 99.9% | 100.0% | 100.0% | 100.0% | 100.0% |
| Redump PlayStation | 0.0% | 0.4% | 1.4% | 7.5% | 38.5% | 100.0% |

## Recommended Buckets and Thresholds

| Bucket | Delivery interpretation |
| --- | --- |
| `<=16 MiB` | Safe full-buffer default. |
| `>16-64 MiB` | Full-buffer acceptable default for cartridge-style content. |
| `>64-128 MiB` | Conditional full-buffer. |
| `>128-256 MiB` | Launch-cache preferred. |
| `>256 MiB-1 GiB` | Range or launch-cache. |
| `>1 GiB` | Range/streaming required. |
| `disk/unknown size` | Range/seek-heavy path until `DatDisks` stores byte size. |

Policy thresholds:

- `FullBufferMaxBytes = 64 MiB`
- `ConditionalFullBufferMaxBytes = 128 MiB`
- `LaunchCacheMinBytes = 64 MiB`
- `RangeRequiredMinBytes = 256 MiB` or any disk/unknown-size payload

## Platform and Media Categories

Likely small/full-buffer-friendly:

- Cartridge and early handheld/home-console platforms such as NES, SNES,
  Genesis, GB/GBC/GBA, N64, Atari-era systems, and similar ROM-first systems.

Likely seek-heavy or large:

- Optical disc and newer platforms such as PlayStation, Sega CD, Saturn,
  Dreamcast, GameCube, Wii, Xbox, PSP, and similar image-based systems.
- Any `.cue`, `.bin` pair, `.iso`, `.chd`, `.gdi`, `.cdi`, `.rvz`, `.wbfs`,
  `.wud`, or `.xiso` should be classified as optical/large-media evidence.
- Any `DatDisks` row forces the unknown-size seek-heavy category until byte size
  is added.

## SQL Report

Run `docs/reports/report-content-delivery.sql` against a populated ROMD SQLite
database:

```sh
sqlite3 /path/to/romd.db < docs/reports/report-content-delivery.sql
```

The SQL emits:

- expected DAT game-payload bucket distribution by platform/media category;
- threshold coverage by platform/media category;
- ingested physical ROM file distribution joined to matched platforms;
- `DatDisks` counts by platform so unknown-size disk content is visible.

## Schema Gap

`DatDisks` cannot currently participate in byte-size bucket reports because it
has no size column. If disc planning needs real thresholds, add a nullable
`Size` or `UncompressedSize` to `DatDisk`/`DatDisks` when source DATs provide it.
