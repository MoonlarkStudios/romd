# Audience library workspace

Libraries represent audiences regardless of destination. Collections are reusable,
ordered title lists that can be attached to multiple libraries. An attachment owns
its library-specific order and featured placement; it never grants title access.

## Acceptance criteria

- Libraries have a scannable list and a consistently sized, addressable workspace
  with Overview, Games, Collections, People, and Preview tabs. The existing
  `tab=access` URL opens People for link compatibility.
- Rating restrictions appear during creation and first in Games, followed by game
  selection. People manages membership only. Handpicked games retain the
  audience's rating policy. Unsaved changes require a decision
  before leaving the workspace.
- Collections can be attached, detached, reordered, and featured per audience.
  Shared collection editing displays attached audiences. Empty filtered
  collections remain manageable but are absent from consumer collection reads.
- Preview reads saved, owned title projections for the selected audience and
  preserves collection ordering. It is not an emulator compatibility simulation.
- Consumer collection reads enforce attachments and live audience assignment.
  Ottercade uses attachment order and featured flags for discovery shelves.

## Persistence and migration

`LibraryCollections` has a composite library/collection key, cascading foreign
keys, an ordering field, and a featured flag. The application owns the attachment
replacement transaction; the repository locks the library row to serialize full
replacements. Selection and attachment changes are independent operations.

The migration attaches existing collections to existing libraries and retains the
old collection ordering, preserving previous presentation. New libraries and new
collections require explicit attachment. PostgreSQL expected schema version is
10. Only the worker applies migrations; restart it before the API hosts.

The materialized title and release access rules remain authoritative. Collection
attachment does not enqueue materialization because it changes presentation,
not title eligibility. Consumer reads continue to fail closed while a library
needs materialization. The admin preview waits for that update to complete.

## Scope and verification

Touched surfaces: backend application/persistence, admin endpoints and web app,
consumer contracts and queries, generated clients, Flutter discovery, and tests.

api clients: both - admin attachment/preview endpoints and consumer featured
placement metadata.

Verification routes:

| Command | Evidence sought |
| --- | --- |
| `mise run test` | Domain/application invariants, PostgreSQL attachment filtering, transactional replacement, migration preservation, ordered preview |
| `mise run test:integration` | Endpoint metadata, ID/cursor mapping, privileges, live consumer reassignment |
| `pnpm api:update` in `web` | Clients regenerated from backend contracts |
| `pnpm lint`, `pnpm test`, `pnpm build` in `web` | Typed UI, library authoring flows, shared placement, preview pagination |
| `mise run analyze`, `mise run test` in console | Consumer flag parsing and featured discovery behavior |
| Browser walkthrough | Gallery, workspace, responsive layout, keyboard interaction, audience preview |

Known validation caveats: sandbox MSBuild named-pipe restrictions, existing web
React act warnings, and the documented parallel Flutter synthetic-cover golden
collision. Browser review requires a connected browser surface.

Parallel agent work: none.

Follow-ups: shared release-selection profiles and 1G1R, wider admin navigation
reorganization, and richer device-specific preview. They are separate from this
library/collection slice.

## Verification record

- Backend safety suite passed: domain 433, DAT parsing 83, application 424,
  storage 67, infrastructure 1,311. After the preview fail-closed guard was added,
  the application suite passed again with 426 tests.
- Integration suite passed: 454 tests.
- Web suite passed: 488 tests across 78 files. Web lint and builds passed.
- Production `mise run build` passed. Vite still reports large bundles; those
  warnings were not suppressed.
- Flutter analysis passed. The full suite failed only the documented dark
  catalog synthetic-cover golden collision (33,995 pixels, the third cover).
  The golden suite passed all 27 tests, and the affected baseline passed all
  nine tests in isolation. Baselines were not regenerated.
- Live browser review was not available: no browser surfaces were connected,
  and the in-app browser was unavailable. Visual acceptance remains a follow-up.
- No new known-issues entry: the environment and golden behaviors are already
  documented; implementation regressions were fixed in this change.
- Final shared-collection cache propagation check passed with the library and
  collection suites: 15 tests. The admin build passed again after that change.
- Focused Flutter API parsing and discovery tests passed: 26 tests.

- Library UI refinement: web lint, all 489 tests across 78 files, and all web
  builds passed. Coverage checks the creation rating ceiling and preservation of
  handpicked games when editing ratings. Existing React act and Vite bundle-size
  warnings remain. No browser was connected for a 1920×1080 visual check.
