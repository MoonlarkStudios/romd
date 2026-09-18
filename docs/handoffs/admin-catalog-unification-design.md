# Admin Catalog Unification — Consolidated Design

Response to `admin-catalog-unification-brief.md`. Grounded against the codebase
as of 2026-06-11 (branch `refactor/admin-nav-consolidate-systems`).

---

## 1. Verdict on "System as the primitive"

**Right diagnosis, slightly wrong slogan.** The operator's pain is not "there is
no System container" — it's that the relationships are real in the database and
invisible in the UI. But making System the *only* door breaks on the brief's own
constraints: ROMs route by hash, unidentified files have no system, Collections
and Libraries deliberately span systems, and enrichment triage is a cross-system
worklist.

The design rule:

> **System is the spine, not the cage.** Every noun gets exactly one global
> lens; the System hub shows *the same lenses pre-scoped*. Scope is a filter,
> not a fork.

Concretely: there is **one Titles surface**. `/titles` renders it globally; the
System hub's Titles tab renders the *identical component* with a locked system
chip in the filter bar — visually the same as a user-applied filter, just
non-removable in context. Same for Sources. This is what prevents the
"two apps" feeling: there literally is one surface, parameterized by scope. It
also makes query-scoped bulk fall out for free — the locked chip *is* the scope
of the filter → count → confirm → job pipeline.

---

## 2. Information architecture

```
ROMD admin
├── Dashboard        what needs me, then what's mine, then everything
├── Inbox            unrouted DATs + unidentified ROMs (badge = team-wide count)
├── Systems          the spine — index → System hub (/systems/:id)
├── Titles           global catalog lens (same surface as the hub's Titles tab)
├── Curation
│   ├── Collections  cross-system by design — stays global
│   └── Libraries
└── Admin
    ├── Users
    ├── Regions & Languages
    ├── Storage      disk size, compression state, CAS management (grows later)
    └── Settings     global provider priority lives here; cascade links into it
```

### What dissolves

| Today | Becomes |
|---|---|
| `/dats` (flat global table) | Routed DATs → System hub **Sources** tab. Unrouted DATs → **Inbox**. The page retires as a destination. |
| `/roms` (Library page) | `Unidentified` queue → **Inbox**. `Unrouted` queue → dissolved (see §4). `All`/`Cataloged` + size stats → **Storage**. |
| `/registry/explorer` + `/systems` (same component, context-selected) | Routed **System hub** at `/systems/:id`. Tree panel retires. |
| Enrichment page | Health overview on **Dashboard**; row-level actions deep-link into **Titles** with saved filters (`needs-attention`, `low-confidence`, `never-enriched`). Triage is a worklist over titles, not a separate noun. |

### Naming

One user-facing word: **System**. "Registry" dies (UI copy first, component
renames opportunistically). `Platform` survives as the domain/API term — that
boundary is fine because it's invisible to operators.

### Decisions settled with the product owner

- **Storage stays** as a real destination (not just an audit view): on-disk
  size, compressed vs. uncompressed state, and future CAS management
  (dedupe, orphan cleanup, repack).
- **Inbox is team-wide**, not per-operator. It is also the multi-operator
  handoff surface: a Contributor drops files at night, a Manager triages in
  the morning.

---

## 3. Ingest: one pipeline, many doors, fallout lands in one place

The "two upload doors" fracture is not fixed by closing a door — it's fixed by
making every door feed the **same pipeline** and making its fallout *visible in
one place* (the Inbox) instead of silently stranded in a flat table.

- **Global door** (Dashboard + Cmd+U anywhere): accepts DATs and ROMs mixed.
  `UploadCenterModal`'s "Smart Ingestion" tab (`useUploadGeneric`) already
  auto-detects DAT vs. ROM — what's missing is only routing of detected DATs.
  - **DATs** → parse header name → match against System **aliases** →
    auto-route. No match → Inbox "Unrouted DATs."
  - **ROMs** → hash-route themselves, as they must. No match → Inbox
    "Unidentified files."
- **System hub door**: the same drop zone with `platformId` pre-bound for DATs
  (born routed — this already works in `SystemDetailView`). ROMs dropped here
  still hash-route globally, and the zone says so: *"ROM files match by hash —
  files belonging to other systems will still route correctly."* The System is
  a real funnel for DATs and an honest non-funnel for ROMs.

### Aliases (backend slice 1)

A System gains user-managed aliases of two badge-typed kinds:

1. **Name aliases** ("SNES", "Super Famicom") — drive DAT header auto-routing
   and provider name-search.
2. **Provider ID mappings** (e.g., `IGDB: 19`) — replace the hardcoded map in
   `IgdbMetadataProvider.cs`, which is the *only* behavioral branch on canonical
   platform short names. Replacing it makes user-created Systems fully real.

---

## 4. The Inbox

### Grounding: it's half-built

- `pages/Library/index.tsx` already has queue filters
  (`all / Cataloged / Unrouted / Unidentified`, hotkeys 1–4, URL-synced),
  team-wide counts from `useLibraryStats`, and an explicit `isInboxMode()` that
  retitles the page "Inbox" with "You're caught up!" empty states.
- **A ROM can only be `Unrouted` via an unrouted DAT** (matched a DAT entry,
  DAT has no platform). So unrouted ROMs are not a queue of their own — they
  are a *property of the unrouted DAT*. This collapses the design from three
  zones to two.
- Today's triage loop is a cross-page dance: orange Alert on the ROMs page
  ("go assign platforms") → `/dats` inline `Assign platform…` Select
  (`useAssignDatPlatform`) → hop back. Both halves of the fix already exist;
  the Inbox fuses them.

### Layout

```
INBOX                                      team-wide · badge = 12 + 847
┌─────────────────────────────────────────────────────────────────┐
│ ▾ Unrouted DATs (12)               blocking 3,412 matched ROMs  │
│ ┌─────────────────────────────────────────────────────────────┐ │
│ │ Nintendo - Super Famicom (20260301)                         │ │
│ │ 1,842 games · 312 of your ROMs matched and waiting          │ │
│ │ [Assign to System ▾]  [+ New System]                        │ │
│ │ ☑ Remember "Super Famicom" as an alias of the chosen system │ │
│ └─────────────────────────────────────────────────────────────┘ │
│                                                                 │
│ ▾ Unidentified files (847)          matched no DAT entry       │
│ │ virtual list — reuses RomRow / RomDetailPane / BulkActionBar │ │
└─────────────────────────────────────────────────────────────────┘
```

### Behavior

- **Unrouted DATs zone is the primary work item.** Each card reuses
  `useAssignDatPlatform`, reframed with leverage: "312 of your ROMs matched and
  waiting" — assigning one DAT clears hundreds of unrouted ROMs in a stroke.
  The blocked-ROM count is the motivation. On assign: success toast with
  "View {System} →" (needs the routed hub URL).
- **The alias checkbox is the self-healing loop**: every manual triage
  permanently shrinks future triage. Lands with backend slice 1; the card works
  without it day one. The Inbox remains valuable even after auto-routing works —
  it's where routing *learns*, not a shame pile.
- **`+ New System`** creates a System inline (name pre-filled from the DAT
  header) and assigns in one step. Requires the user-create path for Systems
  (backend slice 1 scope).
- **Unidentified files zone** lifts the existing `Unidentified` queue
  wholesale — `RomRow`, `RomDetailPane`, `BulkActionBar`, keyboard nav all
  transfer. Unidentified is a *permanent* state; the zone's empty state
  celebrates ("Everything routed itself") rather than implying failure.
- **`Unrouted` disappears as a user-facing ROM status.** It survives only as
  the "blocking N ROMs" annotation on DAT cards. One less concept to teach.
- **Counts are already team-wide** (`useLibraryStats` is global). Nav badge =
  unrouted DAT count (client-derivable from `useDats`) + `unidentifiedCount`.

---

## 5. The System hub

### Grounding

- `RegistryExplorer` selection is **context state, not URL** —
  `select('dat', datId)`. There is no `/systems/:id`. Every flow the redesign
  depends on (Dashboard drill-ins, Inbox "View System →", title → its system)
  is blocked on addressability. **Routing is task zero.**
- `useCatalogFilters` already supports `platformId` and `enrichmentStatus` as
  URL params, and `useCatalogSearch` accepts them — the Titles tab is an
  extraction job, not new architecture.
- `PlatformFieldDefaults` table + repository already exist with **no UI
  anywhere** — the Defaults tab is new UI over an existing backend.
- `TreePanel` is a flat alphabetical system list (DAT children were already
  removed from the tree) — a Systems index page + hub tabs + Cmd+K covers
  everything it did, with URLs. The tree retires.

### Layout

```
/systems/:id
┌──────────────────────────────────────────────────────────────────┐
│ [art]  Super Nintendo Entertainment System          [Add DAT ▾]  │
│        SNES · Super Famicom · +2 aliases            [Enrich ▾]   │
│        Nintendo · 1990                              [⋯ merge /   │
│                                                        delete]   │
│  412 titles · 3 DATs · 94% ROM coverage · ⚠ 31 need attention    │
├──────────────────────────────────────────────────────────────────┤
│  Titles*  │  Sources  │  Identity  │  Defaults                   │
└──────────────────────────────────────────────────────────────────┘
```

- **Titles** (default tab) — the global catalog lens with a locked
  `System: SNES` chip. The header's "⚠ 31 need attention" clicks through to
  this tab with the health filter applied.
- **Sources** — master-detail: DAT list (left) → per-DAT entries/coverage
  (right). Absorbs `SystemDetailView`'s DAT cards and `DatDetailView`. The
  pre-bound `DatUploadZone` + `UploadProgressModal` move here; "Enrich All
  Titles" popover moves to the hub header.
- **Identity** — metadata *about the console*: name, short name, art,
  description, manufacturer, and **Aliases** (both kinds, badge-typed).
  Blocked on backend slice 1.
- **Defaults** — metadata rules *for its titles*: field-source defaults,
  region priority. Manager+. Backend already exists.

Identity vs. Defaults as separate tabs is deliberate: "metadata about the
console" and "metadata defaults for its titles" are different objects with
different verbs — never on one form.

### State & deep links

Tab + filter state in the URL (`/systems/:id?tab=titles&enrichmentStatus=low_confidence`),
same pattern `useCatalogFilters` already uses. Saved health filters on Titles
are the landing targets for Dashboard and Enrichment drill-ins.

### Cosmetic flag

`SystemDetailView`'s teal/cyan gradient hero predates the "restrained identity,
not consumer teal" decision. It gets replaced by the Phase 2 identity layer —
don't polish it during the port.

---

## 6. Surfacing the metadata cascade

The principle: **render the cascade where it resolves, edit it at the layer you
own.** On Title detail, every provenance badge opens a popover showing that
field's resolution chain — override → user → platform default → provider
priority — with the winning layer highlighted. A Manager sets an override *in
the popover*; the other layers are links: "resolved by platform default → edit
SNES defaults" (hub Defaults tab), "resolved by provider priority → Settings."
The four-page tour (Title detail / Enrichment / Settings / Taxonomy) collapses
into one popover with three exits.

---

## 7. Guardrails (System becomes user-owned)

- **Delete a System** = impact manifest first, then typed confirm, then a job.
  The manifest is *fetched*, not generic: "Deletes 412 titles, 3 DATs, all
  enrichment and media. 9,801 stored files keep their bytes in CAS but lose
  their match and return to Unidentified. No undo." Honest about what cascades
  (`Title.PlatformId` is `OnDelete: Cascade`) and what survives (CAS bytes).
- **Merge Systems** = wizard: pick survivor → preview title conflicts
  (normalized-name collisions: keep survivor's / keep other's / keep both) →
  runs as a job in the Activity center. **The loser's name automatically
  becomes an alias of the survivor** — merging "Super Famicom" into "SNES"
  makes the duplicate structurally unable to recur. Merge isn't just cleanup;
  it's training data.

---

## 8. Dashboard (first screen)

Ordered by *what needs me*:

1. **Inbox strip** — unrouted DATs n, unidentified files n; zero-state
   celebrates.
2. **Needs attention** — failed jobs + low-confidence enrichment (feeds from
   Activity center + health stats).
3. **Systems overview** — compact grid/table with per-system coverage bars.
4. **Recent team activity.**

Per-role landings (Contributor = inbox-forward, Manager = health-forward)
remain Phase 4 polish per the existing roadmap.

---

## 9. Component reuse map

| Existing asset | Destination |
|---|---|
| `Library` queue filters + `isInboxMode` + stats (`useLibraryStats`) | Inbox (Unidentified zone) + Storage (All/Cataloged + size) |
| `RomRow` / `RomDetailPane` / `BulkActionBar` / `VirtualList` keyboard nav | Inbox Unidentified zone; Storage |
| `Dats.tsx` inline assign (`useAssignDatPlatform`) | Inbox Unrouted-DAT cards |
| `UploadCenterModal` "Smart Ingestion" (`useUploadGeneric`) | The global door (unchanged shell; gains auto-route on backend slice 1) |
| `SystemDetailView` (DAT cards, pre-bound `DatUploadZone`, Enrich All popover) | Hub Sources tab + hub header |
| `DatDetailView` | Hub Sources tab (detail pane) |
| `TreePanel` / `useTreeData` / `RegistryExplorerContext` | Retired — replaced by Systems index + routed hub |
| `CatalogFilters` / `CatalogGrid` / `useCatalogFilters` / `useCatalogSearch` | Extracted into a Titles lens accepting `lockedFilters`; serves `/titles` and the hub Titles tab |
| `PlatformFieldDefaults` (backend, no UI) | Hub Defaults tab |
| Activity center (Cmd+J slide-over) | Unchanged; delete/merge jobs surface here |

---

## 10. Backend needs

| Need | For | Notes |
|---|---|---|
| System aliases (name + provider-ID kinds) + user-create path | Inbox auto-route, alias checkbox, Identity tab, IGDB map replacement | Slice 1. The IGDB hardcoded map (`IgdbMetadataProvider.cs`) is the only short-name branch — replacing it is the proof-of-value. |
| DAT header → alias auto-route on upload | Global door | Part of slice 1. |
| System metadata/media (art, description) | Identity tab | Platform today is bare `{id, name, shortName, manufacturer}`, seeded-only. |
| Per-system stats rollup (title count, ROM coverage %, needs-attention count) | Hub header strip | Check whether `GET /enrichment/stats` can grow a per-platform breakdown vs. a new `/systems/{id}/stats`. Current `SystemDetailView` stats are client-side DAT sums — not coverage. |
| Delete-impact manifest endpoint | Delete guardrail | Counts of titles/DATs/files affected, before confirm. |
| Merge job (+ conflict preview) | Merge wizard | Runs via Hangfire; surfaces in Activity. |
| Compression state on ROM/storage stats | Storage page | Per product owner: disk size + compressed-or-not is the page's reason to exist. |

---

## 11. Build sequence

Backend-first order holds (the rejected "classify-before-upload" slice taught
us: do the seam, then the surface). Inbox stage A is the one frontend-only
exception — it consolidates *existing* affordances, which is real, not cosmetic.

1. **Slice 1 — aliases + provider mapping (backend).** System gains
   aliases/provider-IDs + user-create; IGDB map reads from the table; DAT
   upload auto-routes by header → alias.
2. **Inbox stage A** (frontend-only, can run parallel to slice 1): fuse the
   Dats-assign affordance + Unidentified queue into `/inbox`; retire the
   cross-page Alert dance; nav badge.
3. **Inbox stage B**: alias checkbox + "+ New System" + auto-route fallout
   handling (post slice 1).
4. **Hub routing** (`/systems/:id` + tabs shell; task zero for the hub) →
   **Sources tab port** → **Titles lens extraction + locked facet** →
   **Defaults tab** → **Identity tab** (post slice 1).
5. **Nav re-IA**: Systems · Inbox · Titles · Curation · Admin; retire `/dats`,
   `/registry/explorer`; carve Storage out of `/roms`.
6. **Cascade popovers** on Title detail.
7. **Guardrails**: delete manifest + merge wizard.

Each slice ships standalone value; nothing depends on a big-bang cutover.
