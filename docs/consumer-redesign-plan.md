# Consumer App Redesign — Implementation Plan

> **Update (2026-06-06) — IA pivot landed.** A later UX review steered the product
> away from the original left-rail/Netflix-Home mockups toward a **game-first,
> lens-based, "Shelves + All Games"** information architecture. That direction is
> now **implemented in the frontend** (no sidebar; top-nav Shelves / All Games;
> game-record detail page with Versions + side rail). Per user direction the rule
> was **honest scaffold**: build the full layout, wire data that the backend
> already exposes, and render everything play-state/user-shelf-dependent as
> clearly-labelled "Soon"/empty states — **no fabricated data**.
>
> Backend work still required to "bring it to life" (unchanged from the phases
> below): **play tracking** (Continue Playing, Your Activity, unplayed counts,
> resume), **per-user & smart shelves** (Save/Add to Shelf, pinned, Unplayed,
> One-Hour), **Recently Added** (materialized-at timestamp), **CRC32** on
> `ConsumerReleaseDto`, and player-count/playtime filters. The phasing below still
> describes the backend sequencing; only the frontend IA/shell changed.

Status: **Draft for review.** This plan moves the reference
consumer app (`web/packages/romd-consumer-app`) toward the console-launcher
mockups (rich left rail, library hero, grid/list views, detailed title page with
region-grouped releases, screenshots, and play tracking).

In-scope engagement features for this effort (confirmed): **play/launch tracking**
and **ratings + screenshots**. Favorites and Playlists are **deferred** — they
appear in the nav as disabled/stub slots only.

---

## Guiding principle: split *tracking* from *launching*

The single insight that de-risks this work:

- **Play tracking** (last-played, play-count, recently-played list,
  "Resume Last Game") is a coherent backend feature regardless of how a game is
  actually launched. It's one per-user entity plus a "record play event"
  endpoint, with aggregates derived from it. Fully designable now, zero ambiguity.
- **Play launching** (what the green *Play* button physically does) is **already
  decided at the architecture level** — see "Recorded decisions" below. The
  target is an **in-browser WASM emulator** loading the Release's content as a
  full buffer. The remaining open items are narrow (adopt the emulator dependency;
  cartridge-now vs. disc-later), not the whole question.

The user picked play *tracking*, so tracking is the deliverable; the launch
trigger plugs into the already-chosen in-browser model.

## Recorded decisions (from the Phase 0A spike — do not relitigate)

`docs/decisions/api-surface-separation-phase-0a.md` ("Browser Launch Model") and
`docs/decisions/cas-delivery-policy.md`, plus issues #7 and #58, already settled
the launch model:

- **Web app + in-browser WASM emulator** (EmulatorJS / Emularity / JSMESS) is the
  initial Play target. **Not** a native/desktop launch. The window min/max/close
  chrome in the mockups is therefore **cosmetic** — it is not a Tauri/Electron
  signal.
- **Full-buffer loading.** Cores fetch ROM URLs as files (no `Authorization`
  header), buffer them, and mount into the Emscripten FS. The existing
  **anonymous signed content-grant URL** model was purpose-built for exactly this
  — Play reuses the current manifest + content-grant delivery, not a new path.
- **Size thresholds** (configurable): `≤64 MiB` full-buffer; `64–128 MiB`
  conditional full-buffer for cartridge content; `>128 MiB` launch-cache;
  `>256 MiB`/disc → range-capable artifact (future). Practically: **cartridge
  platforms in the mockup (SNES/N64/GB/GBA/Genesis) play in-browser today;
  disc/large content (PSX) is deferred** to the future launch-cache/range surface.
- The old `ConsumerLaunchOption` / `LaunchOptionDto` abstraction was **deliberately
  removed** (#58) in favor of Release-addressed manifests. **Do not reintroduce
  it.** Play = select Release → manifest → fetch signed content URL(s) → mount in
  the emulator.

---

## What already exists vs. what is net-new

A large fraction of the mockup is **frontend-only** — the backend already exposes
the data.

### Already available (no backend work)
- **Release fields**: `regions`, `languages`, `revision`, `sizeBytes`,
  `isComplete`, `isRecommended` on `ConsumerReleaseDto`.
- **Title detail**: `description`, `publisher`, `developer`, `genre`,
  `releaseDate`, `players`, `rating`, `contentRating`, plus a full `media` list.
- **Screenshots**: already in the `media` collection (`MediaType.Screenshot`,
  alongside Cover/Banner/Logo/Background/Video/Box3d/TitleScreen). The mockup's
  screenshots gallery + "View All (N)" + lightbox is **pure frontend**.
- **Provider rating**: `Title.Rating` (`double?`) backs the "★ 4.9" display.
- **Browse**: search by name, filter by platform/genre/completeness, sort by
  name or rating, cursor pagination.
- **Delivery**: signed-grant manifest + per-file SHA-256 verified download
  (`ReleaseManifestPanel`, `useReleaseDownloads`, `downloadVerification`) — the
  one genuinely load-bearing piece, kept as-is and reskinned.

### Net-new backend (small)
- **Per-release detail for the Information panel**: CRC32 exists on `DatRom` but
  is **not** surfaced on `ConsumerReleaseDto`. Region is present;
  CRC32/per-release file detail is not. Battery/Rumble (in the mockup) are **not
  in the data model** — omit or mark future.
- **Sort options**: only Name/Rating today. Mockup wants Title A–Z (have),
  "Recently Added", and (implicitly) "Recently Played".
- **"Recently Added"**: `MaterializedLibraryItemEntity` is int-keyed and has **no
  timestamp** — only GUID entities carry `CreatedAt`. So this needs a schema
  change (add a materialized-at column) before it can be a real sort, OR we drop
  it from v1. Not a free win.
- **Rating count**: the "(120)" next to the stars has **no backing field**
  (`Rating` is a scalar, no count). Display the scalar; omit the count unless we
  add one.

### Net-new backend (the engagement layer)
- **Play tracking**: per-user play events → last-played, play-count, recently-
  played list, "Resume Last Game" target. New entity + endpoints (see Phase 3).
- **User ratings**: only if "Ratings" was meant as *personal* ratings rather than
  *displaying provider* ratings — see Open Decision 3.

---

## Phasing

Ordered to front-load visible progress and quarantine the risky launch question.

### Phase 1 — Shell + library + detail redesign (frontend-only)

No backend changes. Reskin and restructure against existing data + mocked
engagement state where needed.

1. **App shell / nav rail** (`AppLayout.tsx`)
   - Left rail with sections: Library, Collections, Systems, Genres, Recently
     Added, Playlists (stub), Downloads.
   - "SYSTEMS" sub-list with per-platform title counts (data exists via
     `usePlatforms`/`ListConsumerPlatforms` — counts already returned).
   - User account footer + Settings entry.
   - Centered global search affordance (⌘K). Window min/max/close chrome is
     **cosmetic** in a web build (see Open Decision 1).
2. **Library page** (`Library.tsx`)
   - Stats strip (games / systems / collections) — counts already in
     `LibraryContextDto`.
   - Hero/"Welcome back · Resume Last Game" band — wired to mocked/empty state in
     this phase; real data arrives in Phase 3.
   - Grid **and** list view toggle; sort dropdown (Title A–Z works now; other
     options land with Phase 2/3).
   - Reskinned `TitleCard` (cover-forward, platform chip, heart slot rendered
     **disabled** — favorites deferred).
3. **Title detail page** (`TitleDetail.tsx`)
   - Cover + metadata header, big primary CTA (Play/Download — see Decision 1/2),
     provider rating display, genre/players chips.
   - **Screenshots gallery** + "View All (N)" + lightbox — frontend over the
     existing `media` list.
   - **Releases rail** grouped by region with flags, "Default" badge on
     recommended, per-release download/⋯ actions. Region data exists; flags are a
     frontend mapping from region string.
   - **Information panel**: Region, Size (have). CRC32 shows as "—"/placeholder
     until Phase 2. Battery/Rumble omitted. Last Played / Play Count show empty
     until Phase 3.
4. **New browse pages**: Systems (platform grid), Genres (facet list →
   filtered library). Both over existing data.

Deliverable: the app *looks* like the mockups and is fully navigable; engagement
fields render as empty/disabled where their backend doesn't exist yet.

### Phase 2 — Contract extensions (small backend)

1. Extend `ConsumerReleaseDto` (or a new detail projection) with per-release
   **CRC32** and any file-level detail the Information panel needs. Touch
   `ConsumerReleaseProjection` / `ConsumerBrowseRepository`.
2. Add **"Recently Added"** support: add a materialized-at timestamp to the
   library-item entity (migration) + a sort field, *or* formally drop it from v1.
   → **Open Decision 4.**
3. Extend the browse sort enum with the new options; regenerate the consumer
   TS client.
4. Wire the now-real fields into the Phase 1 UI (Information panel, sort
   dropdown).

### Phase 3 — Play tracking (isolated backend + UI)

Independent of Phases 1–2; nothing else depends on resolving the launch question.

1. **Domain/entity**: `ConsumerPlayEvent` (or rolled-up `ConsumerTitlePlayStats`)
   per `(UserId, TitleId[, ReleaseId])` — follows the established EF entity
   pattern (`ConsumerUserSettingsEntity` is the template: entity +
   `Configure` + `ToDomain`/`FromDomain`, DbSet on `RomdDbContext`, migration).
2. **Endpoints** (consumer): `POST .../play` to record an event; queries for
   last-played, play-count, and a recently-played list; a "resume target" for the
   hero.
3. **Play action (in-browser emulator)**: the Play button selects a Release,
   issues its manifest, fetches the signed content-grant URL(s), mounts the
   verified buffer into the WASM emulator, and records a play event. Gated on the
   size thresholds — cartridge platforms play now; disc/large content shows
   "Download" only until the launch-cache/range surface exists. "Resume Last Game"
   re-launches the most recent event's Release.
4. **Emulator integration**: adopt the chosen browser core (EmulatorJS is the
   reference target) — a **new frontend dependency**, so it needs sign-off
   (Decision 1). Map platform → core, and feed the content URL as the ROM source.
5. **UI wiring**: hero "Resume Last Game", play count + last-played in the
   Information panel, a "Recently Played" nav view, and sort-by-recently-played.

---

## Open decisions (resolve at plan review — not blockers to this doc)

1. **Emulator dependency.** The launch *model* is decided (in-browser WASM core,
   see Recorded decisions). The open item is adopting the concrete library —
   **EmulatorJS** is the reference target and would be a **new frontend
   dependency** (per repo policy, needs explicit sign-off). Confirm EmulatorJS vs.
   an alternative core before Phase 3.
2. **Play scope for v1.** Confirm: Play is enabled for **cartridge platforms**
   (full-buffer, ≤128 MiB) and **disc/large content shows Download-only** until
   the launch-cache/range surface is built. (This follows the recorded size
   thresholds — flagging it because it means PSX in the mockup won't "Play" yet.)
3. **Ratings = read-only?** "★ 4.9 (120)" maps to the existing provider
   `Rating` scalar; there is **no** rating-count field. Default: display provider
   rating, omit the count, build **no** user-rating backend. Confirm if personal
   user ratings were actually intended.
4. **Recently Added.** Worth a schema migration (materialized-at timestamp) for
   v1, or drop it from the nav until later?
5. **Favorites / Playlists.** Confirm they render as visible-but-disabled stub
   nav entries in v1 (current assumption), vs. hidden entirely.

---

## Risk / cost summary

| Mockup element | Backing | Cost |
|---|---|---|
| Nav rail, systems list w/ counts | Exists | FE only |
| Library stats strip | `LibraryContextDto` | FE only |
| Grid/list toggle, Title A–Z sort | Exists | FE only |
| Screenshots gallery + lightbox | `media` list | FE only |
| Provider rating "★ 4.9" | `Title.Rating` | FE only |
| Rating count "(120)" | None | Omit / new field |
| Region-grouped releases + flags | `regions` | FE only |
| Info panel: Region, Size | Exists | FE only |
| Info panel: CRC32 | `DatRom` (not on DTO) | Small BE (Phase 2) |
| Info panel: Battery, Rumble | Not modeled | Omit / future |
| Recently Added sort | No timestamp | Schema migration |
| Resume / Last Played / Play Count | Net-new | Phase 3 |
| Play button (launch) | Net-new | Phase 3 + Decision 1/2 |
| Favorites, Playlists | Net-new | Deferred (stub) |

---

## Next-work backlog (post-IA-pivot, 2026-06-07)

The Shelves + All Games + game-record-detail + immersive-player redesign is **built
and honest-scaffolded** (backed data live; everything play-state / user-shelf shows
`—` or a `Soon` badge — no fabricated data). The work below "brings it to life."
Ordered by leverage. **BE** = needs backend, **FE** = frontend-only.

### Epic 1 — Play Tracking (BE + FE) — unlocks the most stubs
- [ ] Domain entity + endpoints: record play event; derive last-played, play-count,
      time-played, completion %, recently-played list, resume target.
- [ ] Record a play event when the browser emulator starts (`BrowserPlayer`).
- [ ] Wire Shelves **Continue Playing** card + quick action to the resume target.
- [ ] Wire detail **Your Activity** panel (Last played / Play count / Time played /
      completion).
- [ ] **Unplayed** smart shelf + "Unplayed Gems" quick action.
- [ ] Browse sort: **Recently Played**.

### Epic 2 — Personal & Smart Shelves (BE + FE) — the product identity
- [ ] Per-user shelf CRUD + pinning (collections today are admin-managed/global only).
- [ ] Smart-shelf rule storage + evaluation engine.
- [ ] **Save as Shelf** (All Games filtered view) — currently a disabled stub.
- [ ] **Add to Shelf** — detail hero + On Shelves panel + player Session drawer (stubs).
- [ ] **Edit / Manage Shelves** affordances (Shelves header, Versions "Manage").
- [ ] Smart shelves: **Unplayed**, **One-Hour Games**, **2-Player** (need play-state +
      playtime + a players filter).

### Epic 3 — Release detail + sorts (small BE + FE)
- [ ] Surface **CRC32** on `ConsumerReleaseDto` → fills the `—` in Technical Details.
- [ ] **Recently Added**: add a materialized-at timestamp (migration) + sort, OR drop.
- [ ] Extend browse sort enum (Recently Added / Recently Played); regenerate TS client.

### Epic 4 — Emulator integration depth (FE, some BE)
- [ ] **Exit save-race fix**: post the iframe `exit` event only after EmulatorJS's
      `saveSaveFiles()` promise resolves, so a fast exit can't drop an SRAM write.
- [ ] **Manual & Map** viewer (player Session drawer stub) — depends on manual/map media.
- [ ] **Notes** (player drawer + Your Activity) — ties to per-user data.
- [ ] **2-Player / playtime** filters to back those smart shelves.

### Epic 5 — Settings (BE exists, FE missing)
- [ ] Settings panel against existing `ConsumerUserSettingsDto` (theme); wire the
      header gear + the disabled "Settings (soon)" menu item.

### Epic 6 — Personal ratings / favorites (BE + FE)
- [ ] User rating + favorite backend → Your Activity "Personal rating" / "Favorite";
      working favorite on cards + detail (currently deferred entirely).

### Epic 7 — Search → command palette (FE)
- [ ] **⌘K is cosmetic today** — search only navigates to `/library?q=`. Wire a real
      palette with grouped results (games / shelves / systems / actions). May want a
      dependency (e.g. `@mantine/spotlight`) — needs sign-off per repo policy.

### Cross-cutting
- [ ] **Visual verification** against a running backend (none of the four redesigned
      screens has been confirmed by the agent; local runs are the only validation).
- [ ] **Related games** on the detail page (mock had it) — needs a similar/same-series
      query.

**Closest to free wins:** Epic 5 (Settings — backend already exists), Epic 3 CRC32
(small BE), Epic 4 exit save-race (pure FE correctness fix).
