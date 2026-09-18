# Ottercade brand system

This directory contains the canonical Ottercade identity approved for
[issue #72](https://github.com/JackSkylark/romd/issues/72). Ottercade is the
user-facing console product name. Migration-sensitive technical identities
remain `romd_console`, `com.romd...`, `ROMD_*`, existing storage roots and keys,
and backend contracts.

## Brand position

Ottercade is a local-first home for a personal game library. The identity is
warm, characterful, and recognizable at couch distance without relying on neon
gamer styling, arcade clichés, pixel nostalgia, or a persistent product
masthead.

Romp, the otter holding a controller, is the primary character mark. The
approved art preserves the tilted head, friendly expression, paws, controller,
and original illustration palette from the selected concept.

## Canonical sources

- `ottercade-romp.svg`: full freestanding Romp mark.
- `ottercade-wordmark.svg`: outlined OTTERCADE wordmark.
- `ottercade-lockup-horizontal.svg`: default balanced signature.
- `ottercade-lockup-stacked.svg`: hero and launch-attract signature.
- `ottercade-icon-square.svg`: unmasked square platform source.
- `ottercade-icon-plated.svg`: contained desktop icon with transparent corners.
- `ottercade-icon-adaptive-foreground.svg`: Android mask-safe foreground.
- `concepts/romp-upright.svg`: preserved earlier upright exploration; not a
  production source.

Generated PNG and ICO assets are derived from these files. Do not hand-edit
exports.

Regenerate every checked-in client asset from the repository sources:

```sh
cd clients/romd_console
./tool/export_ottercade_brand.sh
```

The exporter requires Inkscape and ImageMagick, emits exact platform sizes,
and strips variable PNG metadata. Two consecutive runs must produce identical
file hashes.

## Wordmark

The wordmark uses Funnel Display at the approved heavy Poster treatment,
converted to paths so it is deterministic and does not require the font at
runtime. Its custom spacing is part of the artwork:

- Keep a narrow optical notch between the two `T` caps.
- Give the distinctive `R` leg enough room before `C` to remain visible.
- Tighten the `CA` diagonal pair.
- Treat OTTERCADE as one word. Never split `OTTER / CADE`.
- Do not add an underline, divider, or muzzle inside the `O`.
- Do not retype or automatically re-kern the canonical wordmark.

Funnel is copyright 2025 The Funnel Project Authors and licensed under the SIL
Open Font License 1.1. See `Funnel-OFL.txt` and the
[upstream project](https://github.com/Dicotype/Funnel).

Funnel is identity artwork only. Archivo continues to own console interface
hierarchy and readable copy; IBM Plex Mono remains restricted utility metadata.

## Lockups and minimum sizes

- Horizontal lockup: default signature; 320 px provisional digital minimum.
- Stacked lockup: hero and attract contexts; 240 px provisional digital
  minimum.
- Wordmark alone: small branded placements; 120 px provisional digital
  minimum.
- Full Romp alone: 96 px minimum height, with clear space equal to 10% of its
  width.
- Lockup and wordmark clear space: at least half the wordmark cap height.

Final minimums must be verified against exact outlined and raster exports. Peek
uses platform safe areas rather than the general lockup clear-space rule.

## Compact icon

The approved compact composition is the close `Peek` crop of the original Romp
art. Peek is always contained by a Midnight plate or adaptive-icon background;
it is never used as a transparent truncated mark.

- macOS uses the square, unmasked source and lets the system apply its mask.
- Android uses the same art at a smaller scale inside the adaptive foreground
  safe zone.
- Windows and Linux use the intentionally plated source.
- The transparent freestanding symbol remains full Romp.

Do not create a reduced-detail mascot until exact small raster exports expose a
real failure. At strict one-color, prefer the standalone wordmark; the Romp
silhouette is a fallback only.

## Color roles

### Foundations

- Midnight `#07101A`: app-icon plate, native first paint, and attract state.
- Ink `#183B3F`: illustration outlines and dark type on light fields.
- Soft White `#FFF9EE`: wordmarks on dark fields and warm light surfaces.

### Character-owned colors

- River Rust `#B96849`: fur.
- Warm Peach `#F2C9A5`: face and light fur.
- Controller Teal `#397A82`: controller body.
- Current `#5F9DA1`: controller highlight.
- Coin Gold `#F3B944`: restrained spark and invitation cue.
- Button Light `#C6C5E3` and Button Dark `#7769B4`: illustration only.

The identity palette does not mechanically replace semantic console theme
roles. Focus, warning, disabled, text, and surface colors remain independently
contrast-checked theme decisions. Do not recolor Romp arbitrarily or place the
full-color mark directly on busy imagery; use a Midnight plate.

## Product visibility and launch motion

The brand earns one clear moment, then yields to the player’s task:

1. Native first paint uses Midnight and static Romp where the platform exposes
   a pre-Flutter frame. Desktop runners use Midnight as the flash safeguard.
2. The attract state uses the stacked lockup and `Press L + R to Start`.
   Romp and the wordmark remain still; only the prompt breathes subtly.
3. On start, the prompt leaves immediately. The identity lifts 16 px and is
   fully clear by about 180 ms.
4. Only after the brand clears do `Who’s playing?` and the profile carousel
   enter, settling around a 480 ms baseline.
5. Profile selection and the launcher contain no persistent Ottercade masthead.

The transition remains reversible on B/Escape. Reduced motion uses a
zero-duration state change and no pulse. Start input is live on the first Flutter
frame; there is no artificial minimum splash duration.

## Prohibited treatments

Do not stretch or rotate lockups, separate the name, add gradients or neon
glows, add a muzzle to the `O`, add an underline, substitute mascot colors for
semantic UI roles, persist the logo as an in-product masthead, or place Peek
without its container.
