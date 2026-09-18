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

See [the architecture and HTTP contract](../docs/shared-reference-data.md) for
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
