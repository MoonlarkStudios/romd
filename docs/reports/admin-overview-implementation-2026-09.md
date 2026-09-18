# Admin overview

The home page now leads with actionable file and tracked-metadata issues and recent failed jobs, followed by running and queued work, collection progress, metadata success, and administrative changes. It uses the shared workspace heading, panel, and action styles. Missing collection goals are informational, not incidents.

## Data and domain boundaries

`useOverview` composes existing domain read models instead of adding an HTTP aggregation layer. Collection state is computed once rather than separately for stats plus three complete title lists. Job queries use the existing authorization-aware history endpoint with explicit outcome, active archive scope, and a five-row server limit for each section. More-results labels link to the full filtered workspace. Audit retrieval is enabled only for admins and uses the existing bounded history page; the overview displays its first five entries.

Every section has loading, error/retry, empty, and last-updated states. An unavailable summary cannot produce a reassuring empty state. Refresh updates each enabled query independently; existing job-history refresh behavior is reused. Enrichment success includes only completed titles. Failure, no-match, and low-confidence counts have separate exact-filter destinations, and Catalog now offers the low-confidence filter explicitly.

The page does not run Diagnostics probes or infer provider availability from configuration. Global health remains in the application shell; detailed live dependency checks belong to Diagnostics. No schema, authentication, worker, or backend API behavior changed.

api clients: none - the overview consumes existing generated admin contracts; generated client output is unchanged.

## Verification

- Web lint and production build passed.
- Full web suite: 631 tests passed. The removed legacy dashboard tests were replaced with coverage for exact links, correct enrichment semantics, bounded queries, non-admin audit exclusion, partial failure/retry, empty state, and shared job outcome presentation.
- Existing React test `act` and bundle-size/SignalR annotation warnings remain.
- No backend or console tests run: those surfaces were not changed.
- Docker admin image rebuilt and deployed at port 21337. Signed-in browser review verified the live overview in dark and light themes, the missing-release Catalog drill-down, and mobile stacking at 390px (document width matched viewport width; no page overflow). Restored the user's original dark theme and default viewport.
