# Ottercade Console Experience System

Status: approved direction ("One Archive, Two Moods") as of 2026-08-13.
Slice 1 (shared tokens and primitives) is implemented; later slices adopt the
system per surface. This document records the rules; the theme seam and
[`theme-token-governance.md`](theme-token-governance.md) record how values are
owned.

## Page anatomy

Every surface except Home and the entry ceremony composes vertically as
**Stage → Band → Content → Footer**:

- **Stage**: the single persistent `ConsoleAmbientBackground`. Quiet surfaces
  never fully occlude it. Intensity ladder: attract 0.25 → profile selection
  1.0 → in-app dimmed/frozen.
- **Band**: `ConsolePageBand`, the compact "Waterline" header. Transparent at
  rest; anchors (chrome fill + hairline) only while content is scrolled
  beneath it. Carries eyebrow trail + title, an optional
  `ConsoleSegmentedSwitcher`, and a right-aligned `ConsoleBandStatusCluster`
  (connection · clock — the only home either has). The band never carries a
  back button: Back belongs to the footer hint vocabulary.
- **Content**: left-anchored at the stage gutter. Utility columns cap near the
  settings content width and may pair with one right-hand contextual panel
  (identity, totals, guidance). No panels inside panels.
- **Footer**: `ConsoleFooterBar` on chrome surfaces; floating muted
  `ConsoleHintBar` only on Home and threshold ceremonies. Hints may take
  `onPressed` as a pointer affordance; they never join focus traversal.

Immersive surfaces (Featured, cover-forward detail) share the anatomy with the
band starting un-anchored over media and the status cluster hidden until it
anchors.

Home remains the chromeless stage: profile corner, recent rail, dock,
floating hints. Its empty rail speaks through still vessels — no copy; empty
rail slots are not focusable and first-run initial focus lands on the dock's
Catalog action.

## Anchors and rhythm

- `layout.screenGutter` (64) is the one horizontal anchor: band title, content
  left edge, and footer caption align to it. It is the ~5% action-safe margin
  at the 1280×720 design canvas.
- Spacing stays on the governed token scale; band→content and section gaps use
  `lg`, row gaps `sm`, card gaps `lg`.
- Game-grid covers hold their designed presence at every viewport width:
  `GameTile.gridColumnCount` keeps tiles between `gameRail.strip.tileWidth`
  and `gameRail.gridTileMaxWidth`, so wide windows gain columns instead of
  scaling art up, and narrow windows drop below the design column count
  rather than crushing covers.

## Typography

- **Prose is Archivo.** IBM Plex Mono is metadata only: chips, counts,
  timestamps, hostnames, codes, keycaps, eyebrows, section labels.
- Band titles use `sectionHeading`. Large display roles belong to content
  (game titles, ceremonies), not chrome.

## Color roles

- **Teal ring** = focus, only focus.
- **Selection fill/border** = persistent "where I am / what's chosen".
- **Green (`connected`)** = positive state and the single primary action per
  view.
- **Orange (`warning`)** = failures and destructive actions — never resting
  content.
- **Gold (`catalogAccent`)** = eyebrows and invitation (first-run emptiness
  worth attention). Retired from tabs and navigation.

## Actions

`ConsoleActionButton` is the button vocabulary: `primary` (filled, at most one
per view), `secondary` (outlined, neutral foreground), `quiet` (text). The
`destructive` flag borrows the warning role; a destructive primary belongs
only on an explicit confirmation surface.

## Status states

`ConsoleStatusState` is the one empty/loading/offline/error presentation.
The gold `invitation` variant is reserved for first-run empties on surfaces
without another affordance in view — never errors, never Home. Loading holds
a static arc under reduced motion. Skeletons remain only for media rails and
grids: a still vessel means "empty", a breathing skeleton means "fetching".

## Keycaps and controller glyphs

Keycap strings come from `ConsoleHintGlyphs`; every character is verified
against the shipped IBM Plex Mono face (`↵`, `⏎`, `◂`, `▸` do not exist in it
and must not be used). Hint bars render one input mode at a time; contextual
verbs use the reserve-label crossfade.

## Motion

All durations and curves come from `ConsoleMotionTheme` and pass through
`motion.resolve` so reduced motion collapses to zero-duration. Band anchoring
uses `chromeTransition`; selection fills use `selection`. No raw `Duration`
literals in presentation code.

## Skins

Both production skins share the anatomy unchanged; the band anchors with each
skin's chrome fill. Key art and media stay dark-backed in every skin.

## Adoption state

Implemented: tokens (`pageBand`, hint stacking breakpoints, 64 px gutter,
settings context panel), primitives (`ConsolePageBand`,
`ConsoleSegmentedSwitcher`, `ConsoleBandStatusCluster`,
`ConsoleActionButton`, `ConsoleStatusState`), font-verified keycap
vocabulary, clickable hints, and the system-components golden boards for both
skins.

The Settings family (root, Profile & Account, Storage, Emulation,
Controllers) has adopted the band, the left-anchored utility column with
optional contextual panel, `ConsoleFooterBar`, unified status states, and
Ottercade product copy. The settings shell parks focus in its own scope when
a state has no focusable child, so Esc/B dismissal always works while child
autofocus claims still win.

Catalog and Library share the band chrome. The Catalog header is the
immersive band variant: same anatomy and gutter, but anchoring is
progress-driven over the Featured hero, foregrounds lerp from the on-media
roles, and the section switcher carries the on-media chip backing until the
band anchors. The Library scaffold mounts the standard band with no back
affordance; the footer's Back hint is tappable, and content handed the
header focus node simply stays put when moving up from its top row. Discover
metrics share the 64 px stage anchor and the 72 px band height.

Game Detail's action zone speaks the button vocabulary: one primary
(Play / Install / Check access, with the shared busy treatment while
preparing), secondaries for Details / Shortcuts / Run with, and a
destructive-tinted Remove that always confirms through an explicit dialog —
the same contract Storage follows. Detail state chips hold the colour
contract (green marks READY only; awaiting-action states stay neutral), and
the synopsis reads in the prose face, never metadata mono. The
system-components boards pin the full action row (including busy) and the
state-chip family.

Home's empty shelf is the silent vessel register: five still 3:4
vessels, no copy, never focusable. Before the first library result the same
vessels breathe on the shared pulse token (respecting reduced motion); a
known-empty shelf holds still. First-run focus lands on the dock's Catalog
action — the invitation. The no-server and unavailable rail states keep
their one-line explanations.

The ceremony flows (server origin, device-flow login, profile creation)
present the button vocabulary: one primary per view (Save / Try again /
Create) with Cancel demoted to quiet. Profile selection says "Add player" in
both the tile and the footer verb.

The copy pass is complete: product behavior speaks as Ottercade
(controller identity, Players safety notes, library read-failure states,
emulator data-safety messages); "ROMD server / account / library / catalog"
remain the names for the backend service and its data. The legacy
pre-redesign surfaces (`category_browse.dart`, `search_screen.dart`,
`discover_legacy_navigation.dart`) are removed along with their orphaned
theme tokens; the pre-refactor golden images remain as immutable historical
evidence (see `test/goldens/README.md`). Both skins render the full system;
the light skin was reviewed against the same contracts.

Known gaps: no Home or Library surface golden (requires lifting the
PlayServices fakes out of `console_home_test.dart` into shared fixtures);
settings row descriptions still set in the metadata mono — candidate for a
future typography refinement.
