# Admin Operations review — 2026-09-10

Status: Operations implementation complete for review on 2026-09-10.
The confirmed gaps below describe the original baseline, not the delivered UI.

## Delivered review set

Jobs, Storage, and Diagnostics share the admin workspace layout, responsive panels,
and action conventions. Loading, empty, failed, and stale evidence are distinct.

- Jobs has server-filtered history with stable keyset pagination, search, outcome,
  type, archive and date filters persisted in the URL. History has an independent
  cache and bounded polling; the dashboard and activity center retain their recent
  feeds. Non-managers only receive their own history.
- Durable job details expose outcomes, timing, errors, correlation IDs, source
  identifiers, related resources, and shared provenance. Returning to history
  preserves filters. Archived jobs explain the existing 30-day purge policy.
- Shared outcome semantics distinguish queued, running, completed, partial, failed,
  cancelled, deferred, and unknown states in history, recent activity and notifications.
- Jobs uses a Catalog-style table with page-scoped selection, shared table styling
  and a fixed-height Actions menu beside the page count. Selected archive/cancel/retry actions confirm eligible
  counts, use existing authorized per-job endpoints sequentially, report succeeded,
  skipped and failed jobs, and keep failures selected. Filters and pagination clear
  selection. Result feedback survives an emptied page. Unsupported Delete and
  misleading global Clear Completed remain removed; retention behavior is unchanged.
- Storage reports registered CAS object counts and attribution, with administrator-only
  volume capacity and root availability. Registry totals are not presented as a fresh
  filesystem integrity scan.
- Diagnostics starts with bounded actionable findings, links affected jobs/platforms,
  and preserves evidence through partial failures and failed refreshes. Clean catalog
  projections are collapsed so recovery and worker evidence remain easy to reach.
- Import provenance now belongs to a shared feature component. Backend history lives
  in an application query and repository read model, using existing indexes; no
  schema migration or host ownership changes are required.

api clients: admin history contract added; OpenAPI and TypeScript clients regenerated
with `pnpm api:update`. Generated files were not edited manually.

## Review environment

The existing `romd-artwork-testing` Docker stack serves the bundled admin application
at <http://localhost:21337>. Admin and worker images were rebuilt from this checkout;
existing databases and volumes were retained. Consumer/player services were unchanged.
Compose configuration: `/private/tmp/romd-admin-walkthrough/compose.json`.

The optional integrity, backup-verification, and administrative-audit capabilities
listed below remain future product work. They require durable evidence contracts and
are not represented by placeholder controls in this implementation.

## Recommendation

Bring Jobs, Storage, and Diagnostics into the shared admin workspace conventions,
starting with correctness and investigation workflows. Keep their responsibilities
distinct: Jobs explains work and outcomes, Storage explains capacity and stored
content, and Diagnostics explains operational problems and recovery evidence.
Add an attention summary to Diagnostics before considering another top-level page.

The observable goal is that an admin can identify a problem, inspect its evidence,
reach the affected resource, take an authorized action, and verify the outcome.
Loading, missing evidence, stale evidence, and healthy state must be distinguishable.

The baseline assessment traced source, contracts, persistence, and existing tests.
Implementation verification and populated-container browser checks are recorded below.

## Confirmed gaps, ordered by priority

### 1. Job actions do not match their contracts

- **Delete cannot succeed.** The UI offers it on every row. The endpoint rejects
  unarchived jobs with 400 and returns 501 for archived jobs. Remove the action
  until permanent deletion is a supported product capability.
- **Clear Completed archives more than the UI implies.** The button is shown
  from the visible terminal/error-free count. Persistence archives all unarchived
  terminal phases, including Failed, CompletedWithErrors, Cancelled, and Deferred,
  across the repository. It is not limited to the visible jobs. Define an explicit
  success-only bulk operation, with scope and affected count; provide separate
  intentional archival of other outcomes if needed.
- **Manager-only actions are shown to lower roles.** Delete, bulk archive, and
  retry require Manager at the API; Jobs does not consult permissions. The server
  boundary remains enforced, but the UI offers actions that will be rejected.
- Archiving removes rows from the default list with no archive view. This also
  matters for retention: the worker schedules daily purging of jobs archived
  for more than 30 days. Explain that retention boundary in the archive experience.

Evidence: `web/packages/romd-admin-app/src/pages/Jobs/index.tsx`,
`src/Romd.Hosting/Endpoints/JobEndpoints.cs`,
`src/Romd.Persistence/Repositories/JobRepository.cs`,
`src/Romd.Infrastructure/Jobs/RecurringJobRegistrar.cs`.

### 2. Jobs is a recent activity feed, not an operational history

The hook requests the default 50 most recent unarchived jobs. All filters and
counts are computed locally; there is no pagination, search, date/type/resource
filter, or archive control. A still-active job older than that window can be
absent on initial load. Realtime events prepend additional jobs without enforcing
the same window, so the scope can differ before and after reload.

Add a server-filtered, bounded, keyset-paginated history query. Keep dashboard
recent activity separate from history query keys and realtime reconciliation.
Provide URL-backed filters and a durable `/jobs/:jobId` route, including jobs
outside the recent window. Clearly distinguish page counts from total counts.
The existing API already supports fetching a job by ID and including archived
jobs in recent results; full history needs a new or extended query contract.

Evidence: `hooks/api/useJobs.ts`, `hooks/realtime/useJobSocket.ts`, `routes.tsx`
under the admin app, and `JobEndpoints.GetRecent` / `JobRepository.GetRecentAsync`.

### 3. Job outcome semantics lose important distinctions

The shared outcome helper calls every non-terminal job Running, including Pending.
An error-free Cancelled job becomes Completed. Deferred has a distinct badge but
is included in the page's Completed filter/count. CompletedWithErrors is presented
as Failed, hiding the distinction between partial success and failure.

Define one presentation model for queued, running, completed, completed with
errors, failed, cancelled, and deferred outcomes. Use it consistently in rows,
filters, grouping, dashboard activity, and notifications. Preserve backend phase
and terminal semantics; do not change stored phase names to achieve UI labels.
Represent unknown future states explicitly. Add the existing `artwork-import`
type to the registry; it currently falls through the generic presentation.

Evidence: `components/Jobs/jobTypeRegistry.ts`, `pages/Jobs/index.tsx`,
`src/Romd.Domain/Jobs/Job.cs`, `src/Romd.Contracts.Management/Models/Job.cs`.

### 4. Unavailable data can look like an empty or healthy installation

Jobs reads loading/data but not the query error. An initial request failure
eventually displays “No jobs have been run yet.” Storage ignores loading and
errors for both queries and substitutes zero sizes and counts. These are
misleading operational signals.

Adopt explicit loading, initial error/retry, successful empty, and stale-data
states. Preserve last successful evidence during refresh failures with a visible
warning and timestamp. Diagnostics already handles initial errors and per-probe
unavailability well; its failed-refresh path could retain the previous snapshot
as explicitly stale evidence instead of removing all sections.

Evidence: `pages/Jobs/index.tsx`, `pages/Storage/index.tsx`,
`pages/Diagnostics/index.tsx` under the admin app.

### 5. Storage mixes different populations and lacks capacity context

The physical size describes all registered CAS files, while “files on disk” uses
the ROM library count. DAT and media objects, and deduplicated references, make
these different populations. Use the CAS compressed + uncompressed counts for
the registered object count; label ROM file counts separately if useful.
The size metrics come from database records, not a fresh filesystem audit.

Retain the useful attribution breakdown and its overlap explanation. Do not sum
overlapping categories as a unique total or equate unattributed content with
content safe to delete. Storage free bytes already exist in admin-only Diagnostics;
surface capacity through an appropriately authorized storage read model. Do not
expand lower-role access to the entire diagnostics payload to reuse one metric.

Evidence: `pages/Storage/index.tsx`, `hooks/api/useStorageStats.ts`,
`src/Romd.Persistence/Repositories/FileRepository.cs`,
`src/Romd.Admin.Application/Dashboard/Queries/GetStorageStats/GetStorageStats.cs`.

### 6. Diagnostics exposes evidence without completing investigation

The page reports catalog projection failures, stalled DAT jobs, stranded
enrichment, outbox backlog, worker heartbeats, recurring jobs, and storage
availability. The API isolates probes, bounds work, limits lists, and reports
truncation; preserve those strengths and operator-driven refresh.

Job IDs and platform IDs are text rather than investigation links. Available
means the probe succeeded, not that the underlying subsystem is healthy. Place
an attention summary first, with specific findings, timestamps, affected-resource
links, and an explanation of automatic recovery or an available admin action.
Keep detailed evidence underneath. Do not infer overall health from probe
availability or prescribe arbitrary queue/heartbeat thresholds.

Some links can use existing IDs and routes; exact source links and affected-library
lists may require enriched, bounded read models. Retain visibility diagnosis inside
Libraries and source lifecycle diagnosis inside Sources & Files, linking to those
workspaces instead of duplicating their rules.

Evidence: `pages/Diagnostics/index.tsx`, `pages/Libraries/LibraryDiagnostics.tsx`,
`src/Romd.Admin.Application/Diagnostics/Queries/GetOperationalDiagnostics/GetOperationalDiagnostics.cs`.

### 7. Presentation and component boundaries lag the redesigned workspaces

All three Operations pages use local Stack/Paper/Title layouts instead of the
shared workspace width, heading scale, panels, and action props. Jobs imports
`ProvenanceLedger` from another page and keeps rows, grouping UI, filters, formatting,
and mutation notifications inside one large module. Group expansion is a clickable
Group without button semantics; the job menu icon has no accessible name.

Use `Workspace.module.css` and `workspaceActionProps`, compact semantic page
headers, responsive rows/tables, keyboard-operable disclosure, and named actions.
Extract shared job presentation and import provenance into feature-owned modules
consumed by pages. Reuse existing provenance search, paging, exports, and resource
links. Share primitives and feature behavior where there are actual consumers;
avoid a generic Operations framework or copied page-level orchestration.

## Delivery plan and acceptance criteria

| Slice | Deliverable | Acceptance |
| --- | --- | --- |
| 1. Trustworthy workspaces | Shared styles, truthful loading/error states, correct outcomes and CAS counts, role-aware supported actions, explicit archival semantics, accessibility fixes | No failed request appears as empty/zero success; cancelled and deferred jobs are never labelled completed; every offered action has a working authorized contract; bulk archival matches its stated scope |
| 2. Job investigation | Server-filtered history, archives, URL filters, job detail, related resources, detailed errors and correlation ID | An older active job and archived job are reachable directly; filters search the server dataset; navigation/back restores context; reconnect and mutations reconcile the correct lists |
| 3. Operational diagnosis | Attention summary, contextual links, recovery explanation, storage capacity | Every finding identifies evidence and a next step or explicit automatic-recovery expectation; partial probe failure preserves other evidence; restricted diagnostics remain restricted |
| 4. Additional admin capabilities | Integrity reports, backup verification visibility, administrative audit | Each feature has durable evidence, clear scope and authorization, and a verified outcome rather than a fire-and-forget button |

Slices 1–3 are delivered, with archival scoped to explicitly selected records.
Slice 4 remains future product work. Treat success-only archival as a backend use-case change,
not just a label change. Keep the existing recent feed available while introducing
history; do not replace its contract without reviewing dashboard and realtime consumers.

## Architecture and risk map

- **Backend ownership:** job orchestration belongs in application command handlers.
  Current archive/cancel endpoints contain persistence and Hangfire orchestration.
  Refactor touched use cases toward the accepted transaction/event-boundary ADR,
  preserving durable cancellation before transport signalling and artwork transactions.
- **Domain ownership:** source activation remains a source use case; materialization
  remains a library use case. Operations provides observation and entry points to
  those commands. Runtime scheduling and storage probes remain infrastructure adapters.
- **Contracts:** history, action capabilities if introduced, capacity, and recovery
  evidence flow from backend DTOs through OpenAPI generation. Never hand-edit clients.
- **Persistence:** new indexes/read models may require migrations and the schema-version
  bump. Preserve job IDs, phase discriminators, ownership, archival timestamps,
  cancellation fences, dispatch durability, and existing retention semantics unless
  explicitly changing the product policy.
- **Authorization:** preserve user-owned job visibility, Manager-wide job operations,
  and Admin diagnostics. Add dedicated permission names where useful rather than
  making operational permissions depend on `canManageUsers`.
- **Realtime:** paginated/history cache updates must respect filters, archives, order,
  limits, and ownership. Detail updates and list invalidation have different needs.
- **Performance:** keep diagnostics bounded and separate from readiness. Avoid fetching
  an unbounded job list or performing full storage scans in a page request.

## Worth building after the foundation

1. **Storage integrity and maintenance reports:** last scan time, scope, missing or
   corrupt objects, affected ROMs/titles, and cleanup outcomes. Start with inspection;
   recovery commands must honor shared references and existing retention rules.
   Existing scheduled cleanup is a foundation, not proof of a full integrity audit.
2. **Backup and restore verification visibility:** show last completed backup set,
   manifest/schema validation, and last recorded restore drill. ROMD already has
   quiesced backup/restore scripts; expose their evidence before adding orchestration.
3. **Admin activity audit:** actor, action, resource, time, result, and correlation
   for source activation, permissions, library changes, retries, and archival.
   Job execution history and administrative audit answer different questions.

Defer a new overview page, generalized alerting, arbitrary scheduler controls,
one-click restore, and broad destructive maintenance until these workflows and
their evidence contracts are defined. Provider configuration stays in Settings;
provider-related failures should link there with useful context.

## Verification

- Backend build: passed with zero warnings.
- `mise run test`: **2,680 passed**, including application validation/ownership/cursor
  tests and PostgreSQL history ordering/filtering coverage.
- `mise run test:integration`: **513 passed**, covering existing split-host behavior.
- `pnpm api:update`: passed, regenerated from backend contracts.
- Final Docker admin and worker builds: passed, including production web builds.
  Rebuilt services started successfully against the retained review database.
- Web lint: passed. Full web suite: **629 passed across 97 files**.
  Regression coverage includes URL filters, rapid filter changes,
  paging, job details, stale failures, outcomes, role gates, capacity and disclosure.
  Table regressions cover partial batch failures, ineligible skips, selection reset,
  duplicate submission, retry permissions and feedback after archiving the last row.
- Populated Docker browser review covers history/archive/search/detail navigation,
  storage capacity, diagnostic links, and desktop/mobile light/dark layouts. Table
  review additionally covered select-page, eligible-count confirmation, mobile
  selection and the shared Catalog toolbar. Existing
  jobs were not cancelled, retried, or archived during review; those actions have
  automated coverage, not a new live destructive-action exercise.

Frontend commands used
`--config.manage-package-manager-versions=false --config.verify-deps-before-run=false`
to use installed workspace tooling; test preparation and package builds ran.
Known existing AuthProvider `act(...)`, bundle-size and SignalR annotation warnings
remain. An earlier consumer BrowserPlayer timing failure passed unchanged on later
full runs. The new repository test initially lacked required user fixture rows;
its setup was corrected and both focused and full backend suites passed.

No agents were delegated. Contract generation and frontend adoption were sequential.

### Selection UX and rendering follow-up

Jobs keeps its count, Actions menu and clear-selection control in a fixed-height
strip above the table. Selection does not insert another toolbar. Browser geometry
checks measured the previous desktop shift at 64px and the new shift at 0px;
mobile document coordinates also remain stable through selection changes.

Rows are memoized with a stable selection callback. A 50-row regression test proves
that toggling one checkbox renders only that row, and a refreshed changed job still
renders while selection is preserved. Eligibility counts are computed once per
selection rather than repeatedly per action. This verifies reduced render work;
no browser FPS trace was captured. Docker production build and web lint passed.
