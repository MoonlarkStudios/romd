# DAT disc naming preview

Read-only inventory and disc-name grouping candidates for explicit raw DAT files.
The tool uses ROMD's Logiqx XML and ClrMamePro readers and the same parser tested
in `Romd.Dat.Parsing.Tests`. It opens no database, host configuration or CAS and
does not change input DATs or catalog relationships. CHDs and ZIPs are not inputs.

Run from the repository root with the configured .NET 10 SDK:

```sh
mise exec -- dotnet build tools/Romd.Dat.Preview/Romd.Dat.Preview.csproj -warnaserror
mise exec -- dotnet tools/Romd.Dat.Preview/bin/Debug/net10.0/Romd.Dat.Preview.dll \
  tests/Romd.Dat.Parsing.Tests/Resources/redump_sony_ps1.dat \
  tests/Romd.Dat.Parsing.Tests/Resources/nointro_nintendo_n64.dat \
  > /private/tmp/dat-disc-preview.json
```

Build separately so build output cannot contaminate the JSON. Each input file
gets a separate source scope consisting of its basename and SHA-256. Sources
are never merged. The output records input digest/length, actual parsed entry
count, header identity, parser rule version, unsupported names and ordered
candidate groups. A DAT header's advertised disc count is not an inventory count.
No full source paths, timestamps or game bytes are embedded.

`dat-disc-name/v1` accepts a single parenthesized ASCII `Disc N` or `Disc N of M`,
case insensitive. It normalizes whitespace/case for grouping while retaining
every other qualifier, punctuation and edition tag. Unknown labels are never
silently stripped. Parenthesized `Disk`, `CD`, textual/Roman ordinals,
unparenthesized numeric markers, multiple markers and malformed disc qualifiers
need review. Bounds are 500
characters per name, ordinals/totals 1–99, 100,000 entries per source and 256 MiB
per input file. Unsupported names remain explicit observations, not failed DAT
imports. A malformed/unreadable DAT, empty source or exceeded file/entry bound
or reader warning about skipped source data fails the command with exit 1 and
no partial JSON. No arguments exits 2;
`--help` exits 0 and prints usage on stderr.

`IsNameCandidate` means at least two unique, contiguous ordinals within one exact
retained edition key, with consistent explicit counts. It does not establish
catalog title assignment, media/file equivalence, ownership, payload availability
or a complete game. Singleton groups, duplicate ordinals, conflicting totals and
gaps have concrete `ReviewReasons`. All names remain available for inspection;
the preview never chooses a winner for duplicate ordinals.

`DeclaredDiscCount` stays null without an explicit total; the highest ordinal is
only a lower bound. `HasAllDeclaredDiscNames` describes the source names present
under a consistent explicit total, never available media. Labels such as
`(Allies)` and `(Soviet)` conservatively stay in separate review groups until
catalog-level curation can establish their relationship. Content-based duplicate
claims and cross-source editions also belong to later catalog work.

Run the focused checks after building:

```sh
mise exec -- dotnet test tests/Romd.Dat.Parsing.Tests/Romd.Dat.Parsing.Tests.csproj -warnaserror
mise exec -- python3 tools/Romd.Dat.Preview/test_preview.py
```

Both are included in the default backend check, `mise run test`. The CLI checks
cover usage, file-size limits, unchanged input digests, isolated sources,
skipped-row warnings, malformed/unknown/empty/missing inputs, absence of partial
reports, ClrMamePro support and serialized review
reasons.

## Corpus verification

On 2026-10-15 the preview was run against seven standard Redump exports
(PlayStation, Saturn, Mega CD/Sega CD, Dreamcast, PC Engine CD, PlayStation 2
and PlayStation Portable; 31,453 entries) downloaded directly from Redump.
Every entry was accounted for exactly once, and preview counts and digests
matched independent XML counts and the downloaded DATs. The run produced 2,328
supported numbered names, 750 name candidates, 548 review groups and 207
individual review entries. No name supplied an explicit disc total, so expected
counts remain unknown. Upstream DATs are not bundled into this repository.

That run found and fixed one regression: `CD 32X` inside `(Sega CD 32X)` and
`(Mega-CD 32X)` edition qualifiers was read as a disc ordinal. The numeric
marker detector now requires a word boundary, and regression tests cover it.

Cases that still require catalog review rather than parsing:

- lettered discs such as `(Disc A)` on Saturn and PC Engine CD;
- `(Disc 0)` opening discs on Saturn (the supported range is 1–99);
- numeric markers combined with significant labels such as `(Disc-B)`;
- unparenthesized demo, magazine, update and kiosk disc numbers;
- region sets, revisions and per-disc labels that partition related names.

This establishes name parsing and conservative review behavior, not complete
multi-disc catalog semantics or emulator support.
