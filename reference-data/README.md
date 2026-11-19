# ROMD reference catalog

ROMD owns canonical system, company, region, language, and rating identities.
Edit `catalog/*.json`. Stable keys and explicit code symbols are independent of
user-facing labels. Rating board numbers are explicit persisted values.

The Roslyn incremental generator reads these files through `AdditionalFiles` and
emits C# into compilation. `RatingBoardCatalog` retains handwritten normalization
and policy; the generated partial owns definitions. No runtime network access is
needed. The CLI uses the same parser and validation implementation.

From the repository root:

```sh
dotnet run --project tools/Romd.ReferenceData.Tool -- .
dotnet run --project tools/Romd.ReferenceData.Tool -- . --check
node scripts/presentation/validate.mjs
```

Generated TypeScript and Dart definitions and `dist/` exports are checked in.
The database embeds `dist/reference-data.json`. The worker installs definitions
and content-addressed assets offline. Stable canonical keys are separate from
database IDs and editable display names. Explicit presentation overrides are
stored separately from built-in definitions. Clients consume the server's
versioned effective snapshot, not generated system lists.

`catalog/system-hardware.json` supplies built-in category, generation, and first
public release year for admin system setup. It is keyed by canonical system key
and embedded separately from the schema-1 client snapshot. Optional facts can
be omitted.

System descriptions summarize platform facts in original wording. The
hardware index was the starting point for console research. [Video Game Console Library](https://www.videogameconsolelibrary.com/console/).

Manufacturer histories and hardware records help distinguish successive models
and regional names. [Nintendo](https://press.nintendo.com/CompanyHistory),
[SEGA](https://www.sega.jp/history/hard/index.html),
[PlayStation](https://www.playstation.com/en-us/playstation-history/1994-ps-one/),
[Xbox](https://news.xbox.com/en-us/2021/05/13/celebrating-20-years-of-xbox/),
[SNK](https://neogeomuseum.snk-corp.co.jp/english/whats/index.php), and
[Bandai](https://www.bandai.co.jp/releases/images/3/204.pdf).

The home computer descriptions also draw on museum collection records. [Computer History Museum](https://www.computerhistory.org/revolution/story/298) and
[The Centre for Computing History](https://www.computinghistory.org.uk/det/509/Sinclair-ZX-Spectrum-3/).

See [the architecture and HTTP contract](../docs/architecture/shared-reference-data.md) for
the agreed resource-oriented API (`systems`, companies, regions, languages,
rating boards, and ratings), direct resource edits, the common `overrides` reset
pattern, snapshot caching, Flutter offline behavior, and DAT boundaries. That
document distinguishes the target API from the currently implemented routes.

`dist/system-keys.json` is the publisher-facing export. The DAT tooling repository
vendors this exact file under `definitions/`, validates its catalog system IDs
against it, and owns its own `catalogs.json`. It does not own application labels,
provider enrichment mappings, companies, regions, languages, or rating data.
Update that export explicitly when adding canonical systems. Neither build nor
startup downloads an unpinned catalog from the network.

`reference-data/assets/presentation/` owns reviewed server-default artwork.
Canonical definitions reference these files; the server embeds and publishes them
at immutable hash URLs. Web clients render server descriptors and Flutter caches
them for offline use. There are no generated client artwork bundles or local
system-logo lookup tables. Clients retain generic text/icon fallbacks.
