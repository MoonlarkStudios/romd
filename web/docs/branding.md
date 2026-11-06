# ROMD web branding

Canonical [tokens](../../brand/romd/tokens.json), artwork, fonts, and original
[notices](../../brand/romd/NOTICES.md) are checked in under `brand/romd/`.
[SOURCE.md](../../brand/romd/SOURCE.md) records the kit version and source commit.

`@romd/foundation` owns web integration: semantic themes, component styles,
layout, motion, and typography roles. The interface uses Archivo and metadata
uses IBM Plex Mono. The kit also supplies DM Sans; adopting it is an application
change. Use the supplied vector wordmark and symbol. Canonical variations live under
`assets/logos/wordmark-{primary,reversed}/`,
`assets/logos/symbol-{primary,reversed}/` and `assets/logos/horizontal-{primary,reversed}/`,
with `logo.svg` and one reference `logo.png` in each directory. The icon uses
`assets/icons/standard/icon.svg` and `icon.png`. Primary artwork is for light
surfaces; reversed artwork is for dark surfaces.

## Update and place files

Replace `brand/romd/` with the complete reviewed ROMD kit, preserving fonts and
original notices. Record its version and exact source commit in `SOURCE.md`.
Version 0.5.0 replaces the earlier flat artwork paths with variation directories;
update the placement adapter with the snapshot. Framework-facing asset names
remain compatibility outputs. Then run from the repository root:

```sh
bash brand/tools/export_web_brand.sh
```

The command places the required artwork and token CSS in the foundation's
`src/brand/`, Archivo/IBM Plex Mono files and notices in `src/fonts/`, and the
favicon in each web application's public directory. Font inputs live under
`brand/romd/fonts/<family>/`. Review these derived placements with the kit diff.

`brand.css` maps fixed ink/cream artwork values to light/dark variants. Headers
use the wordmark at full width and the symbol at the compact breakpoint; keep
one signature visible per placement. Portal colors, theme behavior, and font
roles remain application-owned. Normal builds use checked-in local files.

Run `pnpm lint`, `pnpm test`, and `pnpm build` from `web/`. The placement regression
runs with only the kit and placement script. Review light/dark headers, sign-in,
and icons at 16/32 px. For stale development artwork, follow the
[Vite cache procedure](../../docs/development/known-issues.md#vite-can-retain-inlined-brand-svgs-after-an-export).

## Compatibility assets

Root `brand/romd-*.svg` files and [legacy snapshot metadata](../../brand/brand-source.json)
remain inputs for hosted authentication. Their drawing
geometry matches the kit; accessibility labels can differ. The web command reads
only `brand/romd/`. Hosted-resource migration needs separate host validation.
Ottercade console assets, native icons, and launch behavior remain separate.
