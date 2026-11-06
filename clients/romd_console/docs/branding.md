# Ottercade branding

`brand/ottercade/` contains a focused snapshot of canonical variation directories.
Its two-line `SOURCE.md` records the version and exact source commit. Canonical
paths match the source kit across applications; edit approved source artwork
before copying another snapshot.

```text
assets/
  logos/
    mascot-soft/logo.png
    wordmark-primary/logo.svg
    wordmark-primary/logo.png
    wordmark-reversed/logo.svg
    wordmark-reversed/logo.png
  icons/
    standard/icon.png
    square/icon.png
```

`primary` is the wordmark for light backgrounds; `reversed` is for dark ones.
Soft Romp is raster-only. Its flat vector trace, precomposed lockups and optional
illustrations are separate source variations, omitted from this snapshot.
The kit also retains `tokens.json`, font families and original notices.

`tool/export_ottercade_brand.sh` is the application-owned placement adapter.
Run it explicitly for updates. Normal builds use checked-in files and need no
branding checkout, credentials or branding package manager.

## Placement and ownership

- The three Flutter hero PNGs are exact copies from the mascot and wordmark
  directories. Preserve framing/aspect ratios and use the supplied primary or
  reversed artwork. The stacked lockup follows the active app brightness.
- Native sizes and Windows' seven-frame ICO use Lanczos resizing from the canonical
  1024 px icon. These resolutions share the same artwork. True optical drawings,
  when supplied by a product, remain separate variations rather than resizes.
- macOS receives the opaque `square` source and owns its corner mask. Android's
  adaptive foreground retains the centered 66 dp area on its 108 dp canvas.
  Its fixed background comes from `palette.tile`; the splash and semantic themes
  stay application-owned.
- Archivo and IBM Plex Mono come from the separately pinned `brand/romd/fonts/`
  with their notices. Existing Flutter registration stays unchanged. The kit's
  DM Sans/Fraunces families and notices remain together; they are not registered
  as runtime replacements by this update.

Framework-facing filenames retain existing compatibility names. The canonical
snapshot uses the variation convention above. The package stays `romd_console`;
identifiers, stored preferences, focus, themes and launch motion stay app-owned.
Root `brand/ottercade-*.svg` files and legacy metadata are historical compatibility
material and are not placement inputs.

## Update the snapshot

Use clean, committed source and application worktrees. Approve canonical changes
first; the application PR can preview a committed source draft, with that source
review completed before adoption. From the ROMD root:

```sh
set -eu
BRANDING_CHECKOUT=/absolute/path/to/branding-checkout
test -z "$(git -C "$BRANDING_CHECKOUT" status --porcelain)"
(cd "$BRANDING_CHECKOUT" && npm run check && npm run build && npm run verify:imports)
SOURCE_COMMIT="$(git -C "$BRANDING_CHECKOUT" rev-parse HEAD)"
SOURCE_VERSION="$(cd "$BRANDING_CHECKOUT" && node -p 'require("./registry.json").version')"
KIT_DIR="$BRANDING_CHECKOUT/dist/kits/ottercade"

# Replace the snapshot, preserving complete approved variation directories.
rm -rf brand/ottercade
mkdir -p brand/ottercade/assets/logos brand/ottercade/assets/icons
cp "$KIT_DIR/NOTICES.md" "$KIT_DIR/tokens.json" brand/ottercade/
cp "$KIT_DIR/assets/LICENSE" brand/ottercade/assets/
cp -R "$KIT_DIR/fonts" brand/ottercade/
for variation in mascot-soft wordmark-primary wordmark-reversed; do
  cp -R "$KIT_DIR/assets/logos/$variation" brand/ottercade/assets/logos/
done
for variation in standard square; do
  cp -R "$KIT_DIR/assets/icons/$variation" brand/ottercade/assets/icons/
done
printf 'Version: %s\nCommit: %s\n' "$SOURCE_VERSION" "$SOURCE_COMMIT" > brand/ottercade/SOURCE.md

# Requires Bash, Python 3 and ImageMagick 7 only for this explicit update step.
bash clients/romd_console/tool/export_ottercade_brand.sh
```

Update `brand/romd/` separately using [web branding](../../../web/docs/branding.md)
when adopting its layout or body-font inputs. Each kit keeps its own source
record. Retain original font/asset notices and `.gitattributes` byte-preservation
rules. For 0.5.0, replace the old flat filenames and copied resolution ladders;
the adapter now generates sizes from canonical PNGs.

## Validate and review

From `clients/romd_console/`:

```sh
mise run analyze
mise run test
```

The suite checks exact hero/font copies, raster dimensions, the ICO directory,
adaptive transparency/safe area and the separate icon background. It needs no
ImageMagick or source checkout. Run placement twice and compare outputs; review
`git diff --stat`, the kit/placement diff and both `SOURCE.md` records.
Run `mise run agents:check` from the root when documentation routing changes.

Inspect entry/profile screens, primary/reversed wordmarks, controller transitions,
16/24/32 px icons, the macOS square and Android circle/rounded masks. Build/run
each native platform on its supported SDK host before release qualification;
see [development](development.md). Source checks do not establish packaged-app
qualification. Commit snapshots, source records, adapter changes and placed
outputs together. Each application chooses when to adopt a later source revision.
