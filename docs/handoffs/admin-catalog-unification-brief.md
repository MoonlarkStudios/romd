# Design brief: unifying the Romd admin experience around Systems

**Audience:** the designer picking up the Romd admin-portal IA.
**Status:** problem framing + intent. The solution is yours to shape.
**Author's note:** this is written by an engineer who just mapped the current
code. Everything under "Current state" is verified against the running app;
everything under "Direction" is the product owner's intent, not a spec.

---

## 1. What Romd is (one paragraph)

Romd is a self-hosted ROM manager. Operators ingest **DAT files** (XML catalogs
that describe a console's games and the hashes of each ROM), upload **ROM files**
(verified against those hashes), and the system builds a **catalog of Titles**
enriched with metadata and media (from IGDB today, more providers later).
The admin app is used by a **multi-operator team** with three role tiers
(Contributor → Manager → Admin). The design ethos is **restrained tool-identity**:
optimize for velocity, legibility, and confidence — not chrome. (This is the
*admin* app; a separate consumer app has its own cinematic treatment — do not
port that here.)

The data has a real hierarchy:

```
System (console, e.g. "Super Nintendo")
  └─ DAT files (one or more catalogs for that system)
       └─ Games (DAT entries) ──matched by name──> Titles (the catalog)
            └─ ROMs (uploaded files) ──matched by hash──> Games
                                                            └─ Title carries
                                                               metadata + media
```

A **Title belongs to exactly one System**. That relationship is already
required in the data model — it just isn't surfaced as the organizing idea.

---

## 2. The problem: the experience is fractured

The same underlying data is split across many sibling pages, each showing one
slice, with no place that brings a System and everything in it together. The
operator has to reassemble the pipeline in their head by hopping between pages.

**Walk it through.** An operator gets a new Super Nintendo DAT and 200 ROMs.
They open the Library upload modal and drop the DAT — it **vanishes**, landing
unrouted with no system. They go hunting, find it on the *DATs* page, and assign
a platform. They switch to the *ROMs* page to upload the 200 files, which route
themselves by hash. One game matched the wrong release, so they open *Title
detail* to fix it; to change which source wins that title's genre they leave for
*Settings*; to check whether enrichment finished they go to the *Enrichment*
page. Five destinations, one shipment of files — and at no point did they see
"my Super Nintendo" as a single place.

**Current IA (15 pages, verified):**

| Section | Pages |
|---|---|
| *(top)* | Home · **Catalog** |
| Sources | **Systems** · **DATs** · **ROMs** |
| Metadata | Enrichment · Regions & Languages |
| Curation | Collections · Libraries |
| Admin | Jobs · Users · Settings |

The fractures, concretely:

1. **The catalog is scattered across 5+ surfaces.** "Catalog" (Titles),
   "Systems" (a systems→DATs tree explorer), "DATs" (a flat global DAT table),
   "ROMs" (a flat ROM list), and "Title detail" each show a slice of the *same*
   System's data. There is no single place where "this System and all of it"
   lives.

2. **The nav flattens a hierarchy into peers.** Systems / DATs / ROMs sit as
   three coequal items, but a ROM belongs to a DAT entry belongs to a System; a
   Title belongs to a System. And "Catalog" floats in a separate top section,
   divorced from the Sources that produce it — even though a Title *is* the
   curated face of a System's DAT+ROM data.

3. **Two upload doors, divergent outcomes.** Upload a DAT from the global Library
   modal → it lands **unrouted** (no system) and you must go to the DATs page to
   assign one. Upload the same DAT from *inside* a System → it's born routed. The
   same action in two places manufactures orphaned data.

4. **Duplicated routes and three names for one thing.** `/systems` and
   `/registry/explorer` are the *same* component mounted twice. The single
   concept is called "System" (nav), "Registry" (page/component), and "Platform"
   (domain) depending on which layer you're looking at.

5. **Fixing one Title means touring four pages.** Its metadata/media live on
   Title detail; its enrichment status lives on the Enrichment page; the rules
   for which source wins each field live in Settings; the regions/languages that
   classify it live in Taxonomy.

6. **Inconsistent UX language.** Some pages have the mature pattern (master-detail,
   keyboard nav, async/empty states — Catalog, Collections, Libraries); others
   are barebones tables (DATs, Settings). It doesn't read as one product.

---

## 3. The direction the product owner wants to explore

> "I want to bring the catalog together with everything else. One big happy
> family. I'm thinking **Systems might be our top-level primitive.**"

The hypothesis: a **System is the home that gathers everything about a console** —
its catalog of titles, its source DATs and ROMs, its own metadata and media
(box art, description, manufacturer), and its enrichment health. You *open a
System* and ingest, browse, curate, and fix in one coherent place, instead of
hopping between five pages and rebuilding the relationships mentally.

A System would also gain **user-managed aliases** — alternate names ("SNES",
"Super Famicom", "Super Nintendo"). Aliases do double duty: they match the
system to third-party metadata providers, *and* they let a dropped DAT
auto-route to the right System by reading its header name. (Today the
provider mapping is hardcoded in source and unknown systems are a dead end.)

**This direction is a starting hypothesis, not a mandate. You are explicitly
invited to challenge it** — including whether "System as primitive" is the right
spine at all, or whether the catalog deserves to remain a cross-system lens.

---

## 4. Hard constraints (please design within these — they're load-bearing)

These come from how the system actually works; a design that violates them
isn't buildable without large backend changes.

- **ROM routing is hash-driven, not system-driven.** A loose ROM finds its place
  by hash-matching a DAT entry — you cannot (and users should not) "pick a
  System" to upload a ROM into. So a System can be the home for *DATs and
  catalog*, but it is **not** a mandatory funnel for ROM uploads. Design for
  "drop ROMs, they route themselves," separate from "open a System, add its DAT."

- **Unidentified ROMs are a real, permanent state.** A ROM that matches no DAT
  entry has no system and no title. System-first does **not** solve this — these
  files need a home of their own (a tray/inbox) regardless of the IA you choose.

- **Bulk actions must be query-scoped.** The catalog uses keyset (cursor)
  pagination — there is no stable "select all 4,000 across pages." Bulk
  operations have to follow **filter → count → confirm → run**, not
  checkbox-the-whole-list. Design bulk affordances accordingly.

- **Three roles, gating is decluttering not security.** Contributor (ingest +
  author collections), Manager (manage titles/metadata, destructive ops), Admin
  (users, taxonomy). Hiding nav by role is for focus; it is not access control.

- **If a System becomes user-owned, destructive ops need guardrails.** Deleting a
  System cascades away its entire catalog, titles, enrichment, and media; merging
  two Systems (the inevitable duplicate "SNES") has to reconcile their titles.
  Delete/merge flows are part of the design, not an afterthought.

---

## 5. What already exists (build on it, don't start from zero)

The redesign should **absorb and elevate** these, not discard them:

- **Registry Explorer / System detail** — already a two-pane systems→DATs
  browser, and uploading a DAT *from inside a System already routes it correctly*.
  The system-first ingest flow is half-built here; it's the seed of the vision.
- **Title detail** — metadata / media / enrichment / curation tabs, with
  provenance badges showing which source provided each field.
- **Activity center** — a Cmd+J slide-over fed by realtime job updates (running /
  needs-attention / earlier), with retry + cancel + dismiss. Job surfacing is
  largely solved; reuse it.
- **Collections & Libraries** — curation surfaces that intentionally **span
  systems** (a collection can mix consoles). Note this: not everything is
  system-scoped, so the IA needs a home for cross-system views.
- **The metadata cascade** — per-field source resolution (user override →
  platform default → provider priority). Powerful but currently buried; surfacing
  it legibly is part of the prize.
- **Master-detail + keyboard nav + async/empty-state** patterns already proven on
  the strong pages — make them the universal language.

---

## 6. Questions worth wrestling with

- If a System is the primitive, **where do cross-system views live?** "All
  titles," "all unidentified ROMs," "enrichment health across everything,"
  Collections, Libraries — these span systems. Does the catalog stay a global
  lens *and* appear system-scoped? How do the two relate without feeling like two
  apps?
- **What's the first thing an operator sees** — a grid of Systems? A dashboard of
  pipeline health? Their most recent work?
- **What happens when someone "just has a pile of files"** and doesn't know the
  system? (Auto-route by alias + an "unmatched, pick a system" tray is the likely
  answer — but the entry point's shape is yours.)
- **How does a System earn its own identity** (art, description) without
  confusing "metadata *about the console*" with "metadata defaults *for its
  titles*"?
- **Does "DATs" survive as a destination at all,** or dissolve into System detail
  + an "unrouted" tray?

---

## 7. What success looks like

- An operator goes from *"I have files for a system"* to *"a clean, enriched,
  browsable catalog"* without leaving a coherent space or rebuilding
  relationships in their head.
- There is **one obvious place** for any given system and everything in it.
- The duplicated/competing entry points (two upload doors, two routes to one
  explorer, three names for one concept) are gone.
- The whole app speaks **one UX language**.
- Cross-system needs (search, collections, fleet-wide enrichment health) still
  have a clear home — unification doesn't trap everything inside a single system.

---

*Engineering will turn the chosen direction into reviewable, incremental slices —
you don't need to scope for buildability beyond respecting §4. Push on the
hypothesis; the goal is the right model, not the easy one.*
