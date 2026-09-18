# Console Living Catalog Design Contract

Status: **approved and locked for implementation planning**

Decision date: 2026-07-22

User-facing destination name: **Discover**

Internal domain/API term: **Catalog**

This contract turns the approved Living Catalog mockups into observable behavior
for the ROMD Console client. The mockups define hierarchy and tone, not exact
artwork, platform facts, or pixel measurements. Production UI must use real
catalog data, the shipped ROMD type and theme system, and deterministic
controller behavior.

## Product Intent

Discover should feel like a living, cover-forward game collection rather than a
database browser. ROMD's archival identity remains visible through typography,
metadata, platform identity, and ambient motion, while artwork and the focused
game become the visual center.

The redesign preserves these product boundaries:

- Library remains local-first and practical.
- Discover remains online Catalog browsing and acquisition entry.
- Game Detail remains authoritative for release choice, installation, removal,
  runtime choice, and play.
- Catalog authority invalidation, profile switching, and server switching remain
  fail-closed with no stale metadata flash.
- Controller, keyboard, pointer, text scaling, and reduced motion remain
  first-class.

## Observable Acceptance Criteria

The first implementation wave is accepted only when all of the following are
true:

1. The launcher opens a full-screen destination labeled `Discover`; internal
   Catalog operation and API names do not need to be renamed.
2. Discover has three sections: Featured, All Games, and Systems. The persistent
   left sidebar is removed.
3. The header is a normal focusable navigation zone. Up from the first content
   navigation row enters the active section tab, except where an explicit
   in-surface control row such as All Games Sort & Filter sits between them;
   that control remains arrow-reachable and the next Up enters the tab.
   Left/Right changes the focused tab without activating it; Right from Systems
   enters Search; `A`/Enter activates the focused tab or Search; Down returns
   to that section's previously focused content item. Tab/Shift+Tab may enter
   and leave the zone naturally.
   Focused-tab and active-section states are visually distinct. LB/RB and Page
   Up/Page Down remain section-switch accelerators that do not move focus. The
   existing two-bumper controller-join chord still works and never also changes
   section.
4. `X` opens Search from every Discover section. `Y` opens filters only where
   filters exist. `A` opens or selects. `B` returns or dismisses.
5. Each section remembers its focused item and scroll position for the current
   Catalog operation. Returning from Game Detail restores the exact prior item.
6. Switching profile, server authority, or Catalog operation clears cached
   remote data before another authority can render.
7. Featured renders a compact focused-title spotlight plus at least one complete
   shelf and a visible indication of the next shelf at the primary 1280×720
   geometry.
8. The Featured spotlight reacts to focus without delaying D-pad navigation.
   Missing detail media, slow detail requests, and failed detail requests use a
   deterministic cover/monogram fallback.
9. All Games renders a four-column grid plus a contextual inspector at the
   primary geometry. The inspector can show basic card metadata immediately and
   enrich after focus settles.
10. All Games no longer exposes the partial-data A–Z rail. Pagination prefetches
    as focus approaches the loaded boundary; a focusable retry/load action
    remains when automatic paging fails.
11. Systems uses one-dimensional left/right platform traversal, one emphasized
    platform identity plate, passive catalog previews, and `A` to enter the
    selected system.
12. Search shows controller keyboard and results simultaneously at the primary
    geometry. Right-edge keyboard movement and `RB` both enter results; left
    from the first result returns to the nearest keyboard key.
13. Hardware typing continues to update Search without moving D-pad focus into a
    text field. Backspace, clear, and query debounce remain deterministic.
14. Installed/readiness language is explicit. `READY TO PLAY` means the Consumer
    default release is installed locally. Catalog completeness is labeled
    separately and never presented as local installation state.
15. Loading, empty, offline, request failure, paging failure, and stale-authority
    states are product copy, not exception copy, and always provide a reachable
    next action where one exists.
16. At 2× text scale, no navigation label, filter, focused title, result count,
    or primary action overflows. Secondary descriptions may clamp or leave the
    layout before primary information does.
17. Reduced motion removes scale/parallax and uses opacity or immediate state
    changes without weakening focus visibility.
18. Dark and light skins use their own theme extensions and retain contrast; no
    new presentation component imports legacy/global design tokens.
19. Current full-screen Discover states have deterministic goldens. The obsolete
    component board is updated or replaced so its information architecture no
    longer contradicts the shipped screen.
20. Existing Library, controller joining, Game Detail install/play, profile
    switching, and Catalog privacy tests remain green.

## Information Architecture

### Header

The header contains:

- ROMD archive mark and `Discover`
- Featured, All Games, and Systems section labels
- Search affordance
- Existing profile/time utilities when present in the active shell

The section labels and Search form a focusable, one-dimensional navigation zone
for controller, keyboard, pointer, and accessibility use. Back is a global
command exposed through `B`/Escape and the footer hint rather than a header
destination:

- Up traverses any explicit in-surface control row before the active tab; where
  no such row exists, the first content-row Up focuses the tab directly.
- Left/Right moves the header focus through Featured, All Games, Systems, and
  Search. Search is the rightmost destination.
- `A`/Enter activates the focused tab or opens Search. Focus alone never changes
  section, opens Search, starts a request, or reloads data.
- Down leaves the header and restores the active section's last stable content
  focus target.
- Tab and Shift+Tab may enter and leave the zone using normal Flutter traversal.
- The focused tab uses the focus ring/fill contract; the active but unfocused
  tab uses the quieter selected-state contract. These states remain visually
  distinguishable in dark, light, reduced-motion, and 2× text modes.
- LB/RB and Page Up/Page Down activate the previous/next section without moving
  focus into the header.

### Global input contract

| Input | Discover behavior |
|---|---|
| D-pad / left stick | Move within content; Up traverses explicit surface controls then enters the active header tab; Left/Right moves between focused tabs; Down restores active-section content |
| A / Enter | Open or select the focused item |
| B / Escape | Dismiss the current layer or return |
| X | Open Search |
| Y | Open filters when supported; otherwise no hidden action |
| LB / RB | Previous / next Discover section |
| Page Up / Page Down | Optional keyboard accelerators for previous / next section |
| LT / RT | Page/accelerate in All Games; contextual no-op elsewhere |
| Pointer | Activate visible controls without changing controller rules |

Keyboard equivalents should be discoverable in keyboard-mode footer hints:

- Arrow keys and Enter: normal header/content navigation and activation
- Tab / Shift+Tab: enter or leave the header naturally
- Page Up / Page Down: optional previous / next section accelerators
- `/` or platform-standard Find shortcut: Search
- `F`: filters where supported
- Enter: open/select
- Escape: back

`X` and `Y` keep their existing global intent mapping. Discover binds those
intents to Search and filters in its nearest active `Actions` scope; routes such
as Game Detail retain their current contextual behavior. Bumper section intents
are likewise handled only by Discover or its Search layer, so other launcher
destinations do not acquire hidden shoulder actions or misleading footer hints.

### Bumper and join-chord requirement

`GamepadNavigator` currently leaves bumpers unmapped while controller joining
recognizes a two-bumper chord. Implementation must add chord-aware shoulder
dispatch:

- A single bumper tap dispatches section navigation on release, after the input
  can no longer become an overlapping two-bumper chord.
- Pressing the opposite bumper while the first remains held suppresses both
  section actions and preserves the existing join behavior. There is no new
  finite chord window; an arbitrarily slow overlapping hold remains a valid join
  chord.
- Holding both bumpers never changes section before, during, or after joining.
- Duplicate down events and the releases that end a recognized chord never
  leak a delayed section action.
- Search may contextually bind the right-section action to `Results`, but the
  two-bumper chord rule still wins.
- Ownership/navigation and capture-suppression gates are re-evaluated when a
  single-bumper action is dispatched, not only when its press begins.

This input work is part of the first wave, not an optional polish item.

## Screen Contracts

### Featured

Reference: [featured mockup](design/console-living-catalog/featured.png)

Featured contains:

1. Compact focused-title spotlight, no taller than approximately 42% of usable
   content height at the primary geometry
2. Complete first shelf
3. Visible start of the following shelf

Spotlight behavior:

- Focused card data updates title, platform, year, rating/genre when available,
  and local readiness immediately.
- A focus-settle debounce may request existing title details for description and
  media. Navigation never awaits that request.
- Detail responses are cached by Catalog operation plus title id.
- Stale or out-of-order responses cannot replace the current focus.
- Preferred media order is primary background/banner/screenshot, then cover,
  then the existing platform-tone monogram treatment.
- Reduced motion uses a cross-fade or immediate swap. Normal motion may use a
  restrained cross-fade; no continuous parallax is required.
- The focused rail card remains the only controller focus ring. The spotlight is
  contextual, not a duplicate focus target.

Shelf behavior:

- Top Rated uses the existing rating-sorted search.
- Top Rated omits unrated titles. If no rated title is available, the shelf is
  omitted in favor of the next honest Featured collection; an unrated title is
  never presented as top rated.
- Curated collections retain their archival eyebrow, title, and count.
- Collection data is cached for the active Catalog operation.
- Empty Featured provides a focusable route to All Games.
- A collection `View all` endcap or heading action may open its full title list,
  but it must not be required to enter the first implementation slice.

### All Games

Reference: [All Games mockup](design/console-living-catalog/all-games.png)

Primary layout:

- Four-column portrait grid on the left
- Contextual inspector on the right
- One compact `Sort & Filter` trigger and applied-filter summary above the grid
- A modal mini-menu over the grid for infrequent sort/filter changes
- No repeated `All Games` page heading; the active header tab supplies location
- No permanent alphabet rail

Inspector behavior:

- Immediately available: title, platform, year, genre, rating, and local
  readiness.
- Detail enrichment after focus settles: description, player count, and preferred
  background/media.
- Inspector requests use the same operation-generation and stale-response rules
  as Featured.
- Inspector failure preserves basic card data and does not show an error panel.
- `A` opens existing Game Detail; the inspector never installs or launches.

All Games mini-menu:

- Sort: Title / Rating
- System: All Systems / selected platform
- Reset filters

`Y`/`F` or the trigger opens the mini-menu. Its root page contains `Sort by`,
`System`, and `Reset filters`; Sort and System open checked option lists so
large platform libraries never require repeated value cycling. Arrow focus is
trapped inside the active menu page. `A`/Enter selects, Left or `B`/Escape
returns from an option page, and `B`/Escape on the root closes the menu and
restores the prior grid focus.

All Games intentionally has no release-completeness filter. Every visible
Catalog title belongs in this surface regardless of complete/partial release
metadata. Release completeness remains a separate Catalog/API concept where
another task surface explicitly needs it; it is never installation status.

Local installation remains an inspector status in the first wave. An
`On Device` catalog-wide filter is deferred because filtering a paged remote
catalog against local installs would otherwise produce incomplete or misleading
pages.

Pagination behavior:

- Initial and next-page requests remain keyset/cursor based.
- Reaching the final loaded row starts the next request before focus hits a dead
  end.
- Existing cards and focus remain usable while paging.
- Duplicate ids are ignored deterministically.
- Paging failure shows a focusable retry after the loaded cards.
- LT/RT may accelerate by a viewport/page but cannot skip into unloaded content
  without first completing the required request.

Responsive behavior:

- At the primary 1280×720 geometry, render four columns plus inspector.
- At narrower/large-text geometries, descriptions and decorative media leave the
  inspector before title, platform, year, rating, and readiness.
- If four columns plus a legible inspector cannot coexist, use a full-width grid
  with a compact top inspector; do not shrink covers below the existing
  couch-legibility floor.

### Systems

Reference: [Systems mockup](design/console-living-catalog/systems.png)

Systems is a horizontal, centered platform carousel:

- One emphasized selected system
- Adjacent system identities visible as navigation context
- D-pad left/right changes platform
- `A` enters the existing system title route
- Platform change updates ambient tone and passive preview covers

First-wave facts are limited to current contract data:

- Platform name and short name
- Manufacturer
- Title count
- Platform cover/identity presentation

The mockup's lifespan and editorial description are illustrative and are not
first-wave requirements. Preview covers come from the existing platform-filtered
Catalog search and remain passive in this view. Their purpose is to communicate
the selected system's catalog character without creating another focus axis.

At 1280×720, at least the selected system and meaningful portions of both
neighbors are visible. Larger viewports may show five plates.

### Search

Reference: [Search mockup](design/console-living-catalog/search.png)

Search is a full-screen task layer over a dimmed Discover context:

- Query and an honest loaded-result count at top
- Controller keyboard on the left
- Results and filters on the right
- Footer hints reflect the active input mode and zone

Behavior:

- Controller keyboard defaults to the current shipped ordering unless a focused
  usability test approves QWERTY. The mockup's QWERTY layout is not a locked
  implementation requirement.
- Hardware printable characters update the query from either zone.
- Query debounce remains 300 ms unless measurement supports a change.
- Existing results stay visible, dimmed if necessary, while the next query is in
  flight. Search should not collapse to a blank `Searching…` screen.
- The label is `N RESULTS` only when the loaded page is terminal and `N+ RESULTS`
  while `HasNextPage` is true. The first wave does not claim an exact server
  total that the Consumer API does not provide.
- `RB` enters results. Right from the keyboard's final column does the same.
- Left from the first result returns to the nearest key row.
- Returning to the keyboard restores its prior key.
- Result focus and scroll survive a Game Detail round trip.
- Approaching the loaded result boundary prefetches the next cursor page.
  Existing results remain usable while paging, duplicate ids are ignored, and a
  focusable retry remains after a paging failure.
- Empty query, no results, request failure, and authority invalidation retain
  their current privacy and route-closing guarantees.

First-wave filters mirror All Games:

- System
- Release completeness

Search does not add local installed-only filtering in the first wave.

## State and Visual Rules

### Focus

- Exactly one primary focus ring is visible.
- Contextual inspector/spotlight changes do not create a second selection ring.
- Focused cards may scale only within the theme's focus-motion contract.
- Row dimming must not reduce unfocused titles below readable contrast.
- Focus restoration is keyed by stable title/platform id, not list index alone.

### Readiness language

| State | User-facing language |
|---|---|
| Consumer default release installed locally | `Ready to Play` |
| Catalog release available but not installed | No badge; Game Detail offers `Install` |
| No playable/default release | `Unavailable` only where an action would otherwise be implied |
| Catalog complete filter | `Complete Release` / `Partial Release` |
| Server unreachable with installed content | Local Library remains available; Discover is explicitly offline |

Discover does not claim to preserve Game Detail's route-local release choice.
The absence of `Ready to Play` is not a negative readiness assertion: Game
Detail remains authoritative and may select an installed non-default release.

### Loading and failure

- First load may use a centered product-status panel or deterministic skeletons.
- Section revisits render cached data without an opening flash.
- Detail-enrichment loading is silent and never blanks the screen.
- Catalog failure preserves the route's back action and states that local games
  are unaffected.
- Authority invalidation clears remote artwork and text before route closure or
  privacy-shield rendering.

### Motion

- Spotlight and inspector media: restrained cross-fade
- Card focus: existing scale/ring/glow contract
- Section switch: short spatial or opacity transition, never a long carousel
  animation
- Systems platform change: centered scroll plus ambient-tone cross-fade
- Reduced motion: no scale, parallax, or animated scroll; focus and content
  changes remain immediate and clear

### Primary geometry and theme ownership

The reference canvas is 1280×720. Discover uses the existing 48px surface frame
margin rather than the outgoing Catalog's 96px screen gutter, reserves roughly
92px for the header and 64–68px for the footer, and treats the remaining band as
the primary content region.

Discover-specific composition values live in one nested
`ConsoleDiscoverMetrics` value on `ConsoleLayoutTheme`: header/content bands,
Featured spotlight and compact-card geometry, All Games grid/inspector geometry,
Systems carousel geometry, Search zone geometry, and the compact/large-text
breakpoint. The redesign reuses existing color, typography, focus, motion,
platform-identity, and cover-fallback roles. It does not add flat global spacing
tokens or screen-owned colors.

At large text or a constrained width, All Games moves the inspector above the
grid before shrinking cover cards below their readable target. New Discover
cards resolve to scale 1.0 under reduced motion; a zero-duration animation with
a non-unit resting scale does not satisfy the reduced-motion contract.

## Data and API Boundary

The first implementation wave is console-led with one targeted backend
correctness repair.

Existing Consumer contracts already support:

- Title cards with platform, cover, genre, release date, rating, release count,
  and default release
- Title details with description, player count, media, and releases
- Title query, platform filter, genre filter, release completeness filter,
  Title/Rating sort, and cursor pagination
- Platform summaries and platform-filtered title search
- Collections and collection titles

The existing Consumer Complete predicate is changed to require at least one
exposed release in addition to every exposed release being complete. This does
not change an endpoint shape, OpenAPI schema, generated TypeScript client,
database schema, or web call site. Generated clients must not be edited as part
of this wave.

Every operation-scoped request uses a composite generation key containing the
authority operation, surface/section, query, sort, filters, platform, and cursor
where applicable. A response may update state only if its key still matches.
Systems preview requests additionally compare platform id, debounce/deduplicate
per operation, cache per platform, and use deterministic failure fallbacks.

## Deferred: Tonight

Reference: [Tonight concept](design/console-living-catalog/tonight-concept.png)

`What should we play tonight?` is approved as a north-star follow-up, not part of
the first implementation wave.

Current data can honestly support:

- Genre/mood approximation
- Rating
- Release period
- Local installed status
- Player count after title-detail loading
- Random/surprise selection

Current data cannot honestly support:

- Session-duration estimates
- Co-op versus competitive multiplayer
- Recommendation percentages or `Perfect Match` scoring

IGDB enrichment currently reduces multiplayer/co-operative modes to a player
count. A future Tonight contract must preserve explicit game modes and add a
defined duration source before using the mockup's `CO-OP`, time, or match-score
language.

An API-backed Tonight follow-up must use the ROMD web/API-client workflow:
backend contract first, Consumer OpenAPI regeneration second, generated clients
third, call sites last.

## Touched Surfaces

### First wave

- Console presentation and theme metrics
- Console input intents and gamepad navigation
- Console handwritten Consumer API usage only
- Console widget/focus tests
- Console full-screen goldens
- Consumer browse Complete-predicate repository fix and focused backend tests
- This design/runbook documentation

### Not touched in the first wave

- Backend endpoint shapes, contracts, or handlers
- Host topology
- Web applications or generated TypeScript clients
- Console SQLite schema or migrations
- Emulator/runtime resolution, config writers, or runtime profile ids
- Auth/token storage

## Risk Map

| Risk | Required control |
|---|---|
| Bumper navigation breaks join chord | Chord-aware dispatch and focused controller regressions |
| X/Y contextual shortcuts break Detail controls | Bind existing intents at the nearest active route; retain Detail tests |
| Stale focus-detail response flashes another title | Operation generation plus title-id compare before render |
| Server/profile switch leaks remote artwork | Clear caches synchronously on authority invalidation |
| Inspector increases request volume | Focus debounce, operation-scoped cache, request deduplication |
| Filter/query races render stale pages | Composite request-generation key checked before every state update |
| Four-column layout overflows at 2× text | Responsive inspector degradation and explicit overflow tests |
| Automatic paging steals focus | Append-only node reconciliation by stable id |
| Completeness is confused with installation | Omit release completeness from All Games; keep local readiness as inspector status |
| Complete includes a title with no releases | Backend predicate requires `Any()` plus `All(IsComplete)` and repository coverage |
| Search count implies an unavailable total | `N+ RESULTS` while another cursor page exists |
| Top Rated includes unrated titles | Omit null-rated cards and omit/fallback the shelf if necessary |
| Systems preview adds a second focus axis | Preview remains passive in first wave |
| Mockup art becomes an accidental asset requirement | Use real Catalog media and deterministic fallbacks only |
| Header focus activates or reloads a section accidentally | Separate focused-tab and active-section state; request only on activation |
| Header traps or loses content focus | Stable per-section content focus target plus Up/Down and Tab traversal tests |
| Shared launcher file causes parallel merge conflicts | Freeze shared foundation, then keep surface ownership disjoint |

No persisted schema keys, runtime profile ids, or generated artifacts change in
the first wave.

## Implementation Gates and Ownership

The primary agent remains the orchestrator and gate owner. ROMD custom agents
may be used because the user explicitly approved a coordinated specialist team.

### Gate 0 — Contract and baseline

- Preserve current focus/privacy behavior in tests.
- Add deterministic dark/light full-screen current Catalog and Search goldens
  before structural replacement; do not substitute the component board.
- Repair and test the Consumer Complete predicate before exposing the filter.
- Establish exact first-wave file ownership.

### Gate 1 — Shared shell and input

One implementer owns all shared boundaries:

- `presentation/catalog/discover_models.dart`: public section/filter/sort
  values, immutable snapshots, and stable focus keys
- `presentation/catalog/discover_catalog_session.dart`: operation-scoped remote
  state, generation/privacy invalidation, caches, installed-release state,
  paging, filters, sort, and stable-id focus/scroll snapshots
- `presentation/catalog/discover_shell.dart`: header, section state, route-local
  actions/layers, adaptive footer, and fixed feature slots; it does not issue
  Consumer requests
- `presentation/catalog/{featured,all_games,systems,search}/`: the four disjoint
  feature boundaries and their immutable inputs/callbacks, initially preserving
  behavior until Gate 2 replaces their internals
- The compatibility wrapper in
  `presentation/launcher/catalog_launcher_destination.dart` and route callbacks
  in `presentation/launcher/launcher_surface_screen.dart`
- Shared input intent/navigation files and their focused tests, including
  chord-dominant bumper handling
- `ConsoleDiscoverMetrics`, both production skins, theme registry coverage, and
  shared Discover test/golden harness seams

The dependency direction is:

`launcher route → DiscoverShell → feature widget`, with
`DiscoverShell → DiscoverCatalogSession → ConsumerApiClient/domain`. Feature
widgets may depend on shared Discover models and reusable presentation widgets;
they do not import another feature, launcher route code, or the input
implementation. Game Detail and System drill-in remain callbacks upward.

Once the current shared validation is clean, these files are frozen and surface
implementation begins immediately. No additional planning gate is required
unless a concrete shared-contract blocker appears.

### Gate 2 — Parallel surfaces

Parallel work is allowed only after Gate 1, with disjoint ownership:

- Featured owner: spotlight, shelves, focused-detail enrichment
- All Games owner: grid, inspector, toolbar, pagination
- Systems owner: platform carousel and passive previews
- Search owner: two-zone layout, query/results state, zone traversal

Each owner adds focused widget tests for its files and must not edit shared input,
theme, session/models, launcher, shared golden harness, or shell files without
orchestration. The gate owner retains cross-surface regression tests.

Each owner also produces a deterministic dark/light visual checkpoint for its
surface as soon as its first complete state lands, so visual progress can be
reviewed against the approved mockup before final integration.

### Gate 3 — Assembled review

- Integrate all surfaces
- Review visual consistency and request behavior
- Run full console validation and goldens
- Perform live keyboard and physical-gamepad inspection
- Repair findings, then conduct a fresh independent review

### Gate 4 — Optional API follow-up

Tonight and any new Consumer metadata contract require a separate approved plan,
the ROMD web/API-client skill, backend ownership, generated-client regeneration,
and backend/web/console validation. They are not absorbed into Gate 3.

## Verification Matrix

Run from `clients/romd_console/` unless noted.

| Evidence | Command or method | Expected result |
|---|---|---|
| Static/theme boundaries | `mise run analyze` | Clean; no token-governance or analyzer findings |
| Focus, privacy, paging, search, input | `mise run test` | All tests pass; no skipped regressions |
| Visual baselines | `mise run goldens:test` | Featured, All Games, Systems, Search, offline/error, dark/light baselines match |
| Formatting/hygiene | `mise exec -- dart format <changed files>` and root `git diff --check` | Clean |
| 2× text | Focused widget tests | No overflow; required labels/actions present |
| Reduced motion | Focused widget/golden tests | No scale/parallax; focus remains obvious |
| Authority change | Focused widget tests | Remote data clears before another operation renders |
| Join chord | Focused navigator tests plus physical controller | Join works; zero accidental section changes |
| Controller traversal | Physical gamepad at 1280×720 and target display | Every zone reachable; back/focus restoration correct |
| Keyboard traversal | Live keyboard | Page section keys, Search, filters, Enter, Escape work |
| Visual review | Captured dark/light screenshots | Mockup hierarchy retained with real theme/data |
| Complete semantics | Focused infrastructure repository test from repo root | Zero exposed releases is not Complete; one-or-more all-complete releases is Complete |

The targeted repository predicate change requires its focused infrastructure
test plus root `mise run test`. Integration, web, OpenAPI, and generated-client
validation are not required because no endpoint shape or host composition
changes.

## Known-Issue Interpretation

- Flutter/Dart commands may first fail while writing the mise-managed SDK cache
  outside the agent sandbox. Rerun the same command with scoped escalation; do
  not change project code based on that sandbox-only failure.
- If `mise run analyze` reports the documented macOS Flutter ephemeral Swift
  package path, regenerate the documented ephemeral state before treating it as
  a project failure.
- Final packaging confidence uses a release macOS build; the documented debug
  bundle-seal issue is not a source regression by itself.

## Mockup Notes

The approved mockups contain AI-generated illustrative art and occasionally
approximate platform/game branding. They are not production assets and do not
authorize copying generated covers into the application. Their locked decisions
are:

- Hierarchy
- Relative density
- Navigation placement
- Focus language
- Inspector/spotlight behavior
- Systems carousel interaction
- Search zone layout
- ROMD visual tone

Exact text may be corrected by this contract when the mockup would otherwise
misrepresent current data, as with `ANY STATUS` versus `ANY RELEASE`.
