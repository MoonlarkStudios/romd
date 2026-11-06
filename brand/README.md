# Application brand snapshots

`romd/` contains the canonical ROMD tokens, artwork, fonts, and notices used by
web applications. Its [SOURCE.md](romd/SOURCE.md) records the kit version and
exact source commit. [Web branding](../web/docs/branding.md) owns integration
and update instructions. `ottercade/` contains the console's focused subset of
canonical artwork, tokens, fonts and notices, pinned by its own
[SOURCE.md](ottercade/SOURCE.md).
[Console branding](../clients/romd_console/docs/branding.md) owns its placement,
font registrations, native icon adaptations and update commands.

Both kits use canonical variation directories with `logo.svg`/`logo.png` or
`icon.svg`/`icon.png`; primary/reversed names describe the intended background.
Applications keep these paths in the snapshot and generate extra sizes through
their own placement adapters. Fonts, tokens and notices retain their kit paths.

[Snapshot metadata](brand-source.json) records the central source revision,
application baseline, and local source hashes for the root compatibility files.
Those files remain inputs for hosted authentication; the earlier vector
Ottercade artwork is historical and is no longer used by the console exporter.
Application components, theme contrast, launch motion, identifiers, and stored
preferences remain owned by ROMD.

## Reproducible exports

The repository social preview composes canonical ROMD artwork and bundled OFL
fonts on a 1280 × 640 canvas. With ImageMagick 7 installed, run from the root:

```sh
bash brand/tools/export_social_preview.sh
```

Review [the exported social preview](../docs/assets/social-preview.png) before
uploading it under GitHub repository settings. This local export does not upload
anything or establish publication approval for the source artwork.

The web command copies approved files from `brand/romd/` into the existing web
foundation and browser favicon paths. It does not generate geometry or fetch
branding. Follow [web branding](../web/docs/branding.md) to copy a new kit, update
`SOURCE.md`, place files, and review the application diff. Run from the root:

```sh
bash brand/tools/export_web_brand.sh
```

For native console placement, install Python 3 and ImageMagick 7 (`magick`), then:

```sh
cd clients/romd_console
./tool/export_ottercade_brand.sh
```

Preserve exact platform sizes, aspect ratios, and notices. Verify icons at
16/32 px and compare consecutive native-export hashes. Check both wordmark
colors in the attract composition at their actual aspect ratios.

When a running Vite session retains an older inlined SVG, follow the
[confirmed cache caveat](../docs/development/known-issues.md#vite-can-retain-inlined-brand-svgs-after-an-export).

## Updating this snapshot

The web and console kits follow the manual copy workflow and record their
sources in `romd/SOURCE.md` and `ottercade/SOURCE.md`. The console also places
body fonts from the ROMD kit. Root compatibility metadata covers only the
remaining legacy files, not these kit snapshots. Adopt a reviewed scope, retain
notices and run the affected application's checks and visual review. Contributor
builds retain the approved checked-in files and require no private branding access.
Keep artwork changes in the canonical source repository; editable masters and
approval guides stay there. Application placement scripts belong here.
