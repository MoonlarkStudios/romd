# Explainable library Games workspace

Libraries identify audiences. Library policy selects eligible titles; shared
collection attachments control presentation. Reusable release-selection profiles
will choose representatives independently of those decisions. Device compatibility
remains downstream. Full DAT imports remain intact.

## This increment

- Evaluate unsaved Games rules beside searchable results, with a Rules / Results
  switch on smaller screens.
- Compare saved and draft policies against one repeatable-read catalog snapshot.
  Display exact additions/removals and one primary exclusion reason per removal.
- Inspect matching, added, removed, excluded, or all catalog titles; show the
  selected original rating board/category and attached collection count.
- Use the materialization projection builder for both policy results. Extend its
  existing exclusion checks for diagnostics rather than implement UI rule logic.
- Never apply the draft, enqueue work, or alter saved membership during evaluation.
- Cancel obsolete requests and hide results that belong to an older draft.
- Explain that eligibility and owned/completed payloads are distinct; no claim of
  destination compatibility is made.

## Boundaries and risks

api clients: admin - new read-only POST evaluation endpoint accepts a draft policy.
No consumer contract or schema migration. Existing compare-token activation and
fail-closed consumer reads during rebuilding remain in place. Evaluations describe
saved rules against current catalog data, not the previously activated projection.

The evaluator reads all catalog candidates to give exhaustive counts, then returns
48 rows using an ID cursor. Each page is a fresh snapshot; concurrent catalog edits
can change counts between pages. This first version does not cache evaluations.
Catalog rebuilds block evaluation so partially rebuilt data cannot masquerade as
an exact comparison. Production-scale latency and query cancellation need review
before enabling rapid evaluations over very large catalogs.

## Verification

Backend: `mise run test`, `mise run test:integration`; PostgreSQL evaluation tests
cover policy deltas, exclusions, collection context, no writes, invalid configuration,
and catalog rebuild gating. Domain checks cover scope and reason consistency.
Web: `pnpm api:update`, `pnpm lint`, `pnpm test`, `pnpm build`; check draft replacement,
errors, query identity, and responsive Rules / Results controls. Browser review
requires an available browser surface. Parallel agents: none.

## Follow-ups

Reusable release-selection profiles, deliberate release pins, explicit grouping
policy, and “why this release?” belong in a separate curator capability. Destination
compatibility needs its own facts and evaluator. Library creation starting points
and category-native policy choices can follow without conflating those capabilities.

## Verification record

- Synced `main` with `origin/main` at `e8ad9c3f`; restored local work without
  conflicts and regenerated clients from the combined backend contracts. The
  `romd-admin-work-before-main-sync-2026-09-06` stash remains as a backup.
- `mise run test` passed: domain 435, DAT 83, application 426, storage 67,
  infrastructure 1,322. After adding invalid-draft coverage, the focused
  PostgreSQL library suite passed all nine tests.
- `mise run test:integration`: 453 passed, three path-import tests failed with
  HTTP 400 instead of 202. The library endpoint tests passed. The host had
  approximately 921 MiB free, below the import preflight's 1 GiB headroom; this
  is a likely environmental explanation, not confirmed from response bodies.
  Further database-heavy runs were stopped. The full suite is not green.
- Web lint, 492 tests, and all web builds passed. After the error-type guard,
  all 13 library tests passed again; the admin build passed after sticky-summary
  styling. Existing React act warnings and Vite bundle-size warnings remain.
- Demo Admin API restarted with the new endpoint. Readiness passed and an
  unauthenticated evaluation returned 401. Authenticated browser walkthrough and
  1920×1080 / mobile visual review remain unverified (no connected browser).
- Initial verification caught and corrected new EF usage outside Persistence,
  an admin registration of the worker-only candidate port, and generated error
  union handling in TypeScript. The shared read-only candidate port is now
  separate from the worker materialization port; consumer hosts exclude both
  admin evaluation handlers and worker execution services.

- Merge validation after disk cleanup: `mise run test:integration` passed all
  456 tests, including the three previously failing path-import cases.
