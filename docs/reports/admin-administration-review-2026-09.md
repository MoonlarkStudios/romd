# Administration assessment — 2026-09-10

Status: implemented for Docker review. Assessment baseline: commit 958c0c40.
The findings below describe the pre-change application; implementation notes and
verification are recorded in [Administration implementation](admin-administration-implementation-2026-09.md).

## Recommendation

Make Administration a set of task-oriented workspaces: Users, Integrations,
Reference data, and a smaller installation Settings area. Put My account in the
avatar menu. Put per-system metadata policy inside its System workspace. Add an
Audit log only alongside durable audit evidence. Keep Jobs/Diagnostics in Operations
and library composition in Curation.

Users already exists separately at `/users`. The missing distinction is between
managing other people's accounts, managing one's own account, connecting external
services, and configuring how the installation processes content. Moving cards
alone will not fix several concrete correctness gaps.

Observable success: an admin can onboard someone, explain their effective access,
change that access safely, recover or revoke it, inspect what happened, configure
a provider, and see exactly which content a policy change affects. A returning
admin sees saved values rather than a blank editor or stale success indication.

## What works and should be retained

- User administration is Admin-only at both the route and endpoint group. Role and
  library assignment are explicit. The backend already has application command
  handlers and a user-administration port, rather than putting Identity directly
  in React or every endpoint.
- Backend user mutations use transactions. Existing infrastructure tests cover
  rollback after Identity flushes, role removal, password updates and commit
  failures. The database is not the cause of the composite-profile UX problem.
- Self-deletion and system-user deletion have backend protection. The system actor
  is also seeded with indefinite lockout; it is not an ordinary human account.
- User search and role/library filters are useful for small installations.
- Provider setup distinguishes configured/enabled state and deployment ownership,
  preserves saved secrets on blank input, requires intentional credential removal,
  and offers dated connection-test evidence. IGDB deliberately avoids putting its
  plaintext secret in React Query's mutation cache. Preserve those safeguards.
- SteamGridDB and automatic artwork settings carry revisions; their conflict
  handling is a useful pattern for other editable configuration.
- Taxonomy supports canonical values, aliases, and an explicit directional merge
  warning. Shared reference-data updates have preview tokens, before/after changes,
  conflict choices and local-value preservation. This is a particularly good
  model for consequential admin changes.

## Confirmed problems

### 1. Per-system metadata defaults are not a trustworthy editor — high priority

`pages/Settings.tsx`, `PlatformDefaults`, initializes an empty map and resets it to
empty on every system selection. It never reads the persisted defaults. Every field
therefore appears Auto even if a preference is saved.

Selecting Auto deletes the field from the outgoing map. The backend PATCH only
changes keys present in that map; it clears a preference only when that key is
sent with an empty/null value. Therefore the UI cannot clear a saved preference
back to Auto. When the map becomes empty, Save disappears. This is an observed
source/contract mismatch, not a prediction that the API replaces the whole map.

The editor also lacks a visible mutation-error state and accessible labels on its
individual field selectors. Its success message promises rematerialization without
linking to evidence of that work. It is shown to all authenticated portal users,
although the write endpoint requires Manager; provider configuration is correctly
Admin-gated separately.

Required fix: a read model for saved and effective defaults, explicit clear
semantics, dirty tracking against the loaded baseline, field-level validation,
revision/conflict handling, role-appropriate controls, durable outcome evidence,
and a preview/description of affected scope. Place this at System → Settings →
Metadata policy. Installation automation remains separate.

Evidence: `web/packages/romd-admin-app/src/pages/Settings.tsx`;
`src/Romd.Hosting/Endpoints/EnrichmentEndpoints.cs`, `SetPlatformFieldDefaults`;
`src/Romd.Persistence/Repositories/PlatformFieldDefaultRepository.cs`.

### 2. Users mixes identity, access and recovery into one mutable detail pane

The roughly 860-line page owns the list, selection, create flow, filters, password
reset, profile editing, role ranking, metrics and bulk default assignment. It has
no `/users/:id` UI route despite an existing get-by-ID backend endpoint. Filters and
selected account do not survive a link/reload. At the reviewed 1280px viewport the
detail pane falls below the list. With more users the editor gets farther away.

Save Changes can update email, then role, then library via three separate requests.
If the third fails, the first two remain committed but the UI shows Update failed.
Each success also invalidates the list; the detail effect resets drafts whenever
its user object changes. Switching accounts or receiving refreshed user data can
therefore discard unsaved work without an explicit decision.

Required fix: a full-width directory and durable account workspace with Overview,
Access, and Security sections. Give identity and access clearly scoped saves, or
introduce one deliberately atomic profile command if the UI promises one save.
Protect dirty drafts from selection/navigation/refetch. Use account links rather
than only click handlers on rows; retain keyboard-accessible actions.

Evidence: `pages/Users/index.tsx`, `UserDetailPane` and `handleSaveProfile`;
`hooks/api/useUsers.ts`; `src/Romd.Hosting/Endpoints/UserEndpoints.cs`.

### 3. “Invite” and “temporary password” promise behavior that is not implemented

The Invite User modal calls CreateUser with email/password/role/library. The
backend immediately creates an email-confirmed account. No invitation acceptance,
expiration, delivery status or mandatory password change is represented in the
reviewed flow. Admin password reset directly sets a replacement password.

Short term: call this Create account and label its password accurately. Product
completion: add single-use, expiring activation/recovery links with accepted,
pending, expired and revoked states. For a self-hosted installation without mail,
allow a deliberately generated link to be copied once for out-of-band delivery;
do not make SMTP a prerequisite or claim a message was sent. Sensitive links and
passwords must stay out of logs and long-lived caches.

GitHub treats invitations, cancellation and membership as distinct lifecycle
operations. OWASP recommends single-use expiring recovery credentials and an
explicit session-invalidation decision. These support the lifecycle model, not a
requirement to copy GitHub's account architecture.

Sources: [GitHub membership](https://docs.github.com/en/organizations/managing-membership-in-your-organization),
[OWASP recovery guidance](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html).

Evidence: `CreateUserModal`; `src/Romd.Infrastructure/Identity/UserAdministration.cs`,
`CreateAsync` and `UpdateAsync`; `src/Romd.Contracts.Management/Users/UserContracts.cs`.

### 4. Access changes need stronger safeguards and clearer scope — high priority

- The role-assignment path has no last-administrator or self-demotion guard. It
  accepts any valid role after checking that the target exists. A lone admin can
  remove the installation's administrative access. Protect this invariant in a
  transaction with concurrency-safe enforcement, not just a disabled dropdown.
- System-user deletion is protected, but the ordinary detail form still exposes
  email, role, library and password editing for it. Those mutation paths do not
  have corresponding protected-actor checks. Exclude it from human management and
  reject unsupported identity/access edits at the application boundary. Its seeded
  lockout was verified in source; this review does not claim a sign-in exploit.
- Assign Default Library has no selection or scope confirmation. Persistence updates
  every user whose library is null, including the system actor. The live container's
  sole unassigned account was the system actor, and the button was enabled. Replace
  this with explicitly selected eligible human accounts and show the chosen library
  and affected count before granting access.
- The page describes “admin portal accounts,” but these are shared ROMD identities.
  A User role is not eligible for the admin client; consumer library scope is a
  separate dimension from Contributor/Manager/Admin capabilities. Show both.

User details should answer: Can this account sign in? Which ROMD surfaces can it
use? What can it administer? Which consumer library is assigned? Is that library
configured and usable? A missing library should be explained, with a link to the
library workspace, rather than treated as a generic account failure.

OWASP's deny-by-default and per-request authorization guidance reinforces preserving
server enforcement while making the UI's capabilities explicit.
[OWASP authorization](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html).

Evidence: `AssignUserRoleCommandHandler`, `UpdateUserCommandHandler`,
`DeleteUserCommandHandler`, `UserAdministration.AssignDefaultLibraryAsync`,
`AdminSeeder`, `RomdOpenIddictAccountService.IsEligibleForClient` (client eligibility
logic), and `pages/Users/index.tsx`.

### 5. Session and account lifecycle evidence is missing

The public user DTO has identity, roles, library and created/updated timestamps.
It has no administrative status, last sign-in, invitation state or active-session
summary. There is no reviewed UI/API workflow for suspend/reactivate, list/revoke
sessions, or inspect account activity.

OpenIddict already supports reference refresh tokens and token-entry validation.
Configured lifetimes are 15 minutes for access tokens and 30 days for refresh
tokens. Refresh reconstructs a principal from the current user and roles. The
reviewed user update/role commands do not explicitly revoke those tokens, and
refresh does not compare a password security-stamp claim. A changed Identity
security stamp alone is not proof that all existing API sessions are revoked.

Implement and integration-test the chosen policy for password recovery, suspension,
role reduction and device/session revocation across admin, consumer and console
clients. Do not invent a “signed out everywhere” success message before this is
verified. No active-token experiment was performed in this review.

The first useful additions are suspension/reactivation, session revocation and
last sign-in evidence. MFA for administrators is worth planning next, including
recovery, rather than exposing a decorative toggle. Custom roles, SSO and SCIM
should follow actual deployment needs.

Evidence: `RomdHostRegistrationExtensions`, `OpenIddictEndpoints.ExchangeToken`,
`RomdOpenIddictAccountService`, `UserAdministration`, `UserContracts.cs`.

### 6. Error and loading states can mislead

Users reports zero-valued metrics while data is loading. A user-list error can
also show No users found. Library-load failures are not surfaced; assignments
may appear Unknown library while the form still exists. Deletion discards the
backend's specific error message in its handwritten hook.

Taxonomy replaces evidence with an error alert on failed refresh, with no inline
retry control. Some alias-removal buttons have no accessible name in the live
accessibility tree. Repeated Alias and Merge buttons need the target entity in
their accessible names. Keep the existing merge warning, but add impact counts
and links before irreversible consolidation.

Required pattern: initial loading, successful empty, initial failure, stale data,
and action failure are different states. Keep selected account and saved evidence
when refreshing fails. Do not replace drafts with error fallbacks.

### 7. Settings has inconsistent ownership and configuration semantics

Provider cards belong together as Integrations, with a compact overview and
individual configuration details. IGDB and SteamGridDB currently duplicate similar
query, draft, secret, test and save orchestration, and use local styling. Share the
safe form shell and query conventions, while keeping provider-specific credential
rules typed and local.

SteamGridDB has a revision; the IGDB contract does not. Reconcile the lost-update
policy. Show enabled/configured/deployment-managed state separately from historical
connection-test results. Editing a credential should require a new test, never
make an old test look current. Existing source already handles several of these
states well and should be retained rather than replaced wholesale.

Artwork auto-fill policy is installation behavior, not authentication to a provider.
It belongs in Settings → Automation, linking to provider availability. Per-system
metadata source preferences belong with the system. My password belongs in My
account, reachable directly from the avatar menu.

### 8. Reference data is broader than “Regions & Languages”

Taxonomy's Shared data updates tab also manages system/company names and catalog
definitions. The current heading understates that scope. Rename the workspace
Reference data, with Regions, Languages and Updates as URL-backed tabs. Keep
installed definitions separate from DAT source enrollment and imported content.
Link from a system or curation workflow into a specific reference-data review.
Do not add another top-level Updates page just to move one tab.

The review-before-apply flow is good. Future merge work should show affected
associations and record the action. Preserve preview tokens, local overrides,
signed update verification and tombstones.

### 9. Audit infrastructure is not yet an administrative audit log

`AuditInterceptor` stamps creation/update actor and time on compatible entities.
It does not provide a queryable event history of account changes, credential
configuration, access grants or failed sensitive actions. Logging a technical
exception also does not establish a complete audit trail.

Add explicit administrative events with actor, target, action, time, result,
correlation and safe before/after values. Never record passwords, provider secrets,
activation URLs or raw tokens. Successful mutation and audit evidence should be
committed reliably together; failed attempts need a deliberately separate path.
Job history remains execution evidence and should be linked, not renamed Audit.
[OWASP logging guidance](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html).

## Proposed navigation and ownership

| Destination | What it owns | Access / boundary |
| --- | --- | --- |
| Administration → Users | Directory; account details; roles; consumer library assignment; activation/recovery; later sessions/activity | Admin; identity/access use cases |
| Administration → Integrations | IGDB and SteamGridDB configuration, secrets, deployment ownership, tests | Admin; provider configuration |
| Administration → Reference data | Regions, languages, aliases, merges, reviewed shared updates | Manager read where already supported; Admin mutation |
| Administration → Settings | Installation automation and genuine installation-wide preferences; version/configuration provenance when available | Capability-specific; no empty speculative tabs |
| Avatar → My account | Own identity, password, preferences; later own sessions/MFA | Authenticated self-service, separate from managing others |
| Systems → system → Settings → Metadata policy | Saved/effective field preferences and affected-system outcome | Manager; metadata resolution |
| Curation → Libraries | Library composition, publishing and diagnostics | Existing library ownership; Users links here |
| Administration → Audit log (later) | Durable administrative change history | Admin initially; add only with evidence contract |
| Operations | Jobs, capacity and runtime diagnostics | Preserve current responsibilities |

Keep `/users` as the directory and add `/users/:userId`. Use `/integrations` and
provider detail URLs, `/reference-data?tab=...`, `/account`, and the existing System
workspace route for policy. Redirect old Settings/Taxonomy links without guessing
which unsaved draft they contained.

A local media platform is a more relevant comparison than a billing-heavy SaaS
console. Jellyfin similarly treats user access and device permissions as explicit
management concerns; ROMD should adapt that idea to its own library and client
contracts rather than import every setting.
[Jellyfin user management](https://jellyfin.org/docs/general/server/users/adding-managing-users/).

## Architecture

- Feature modules: `users`, `account`, `integrations`, `reference-data`, and
  system metadata policy. Pages compose feature components; do not import another
  page's private editor or put all administration into a generic Settings engine.
- Share workspace styles, table/selection behavior, draft/save/error primitives,
  confirmation patterns and permission summaries. Retain stable action placement
  and isolated row rendering from the Jobs work.
- Consolidate duplicated library-list hooks/key ownership. `useUsers.ts` and
  `useLibraryManagement.ts` currently expose separate implementations using the
  same `['libraries', 'list']` key. This is duplication, not evidence that their
  caches are currently divergent. User management consumes a library read model;
  it should not own library infrastructure or composition rules.
- Use named capabilities such as manageUsers, configureIntegrations,
  editReferenceData and editMetadataPolicy. Currently taxonomy uses
  `canManageUsers` as a proxy for Admin. Keep the existing four-role hierarchy
  initially; avoid building a custom RBAC editor merely to rename permissions.
- User mutations remain application commands with infrastructure Identity adapters.
  Move the touched metadata-default mutation out of endpoint orchestration into
  an application use case. Generate API clients after backend contracts change.
- Use keyset pagination and server filtering for a growing user directory. Current
  listing makes one flat query, which is a useful baseline, but materializes all
  users and filters in the browser. Do not add client-only paging and call it scale.

## Delivery sequence and acceptance

1. **Correctness first:** fix defaults read/clear/errors; protect the last admin and
   system actor; expose correct role capabilities; make default-library assignment
   explicitly scoped; correct Invite/temporary-password language.
2. **Cohesive workspaces:** durable user details, dirty-draft handling, independent
   or atomic saves, honest loading/errors, My account, Integrations, Reference data,
   and system-local metadata policy. Preserve URLs and use shared workspace styles.
3. **Complete account lifecycle:** real activation/recovery, suspension/reactivation,
   effective access explanations, session revocation and sign-in evidence.
4. **Administrative accountability:** durable audit events and browsing; merge and
   configuration impact previews. Add MFA with tested recovery as security work.

Acceptance examples:

- Selecting a configured system loads its saved preferences. Changing one to Auto
  persists a clear; reload confirms the effective policy.
- A failed access save does not misleadingly imply a successful identity change
  was rolled back, nor erase the user's remaining draft.
- No concurrent sequence of normal admin actions can remove the final viable admin.
- A system actor cannot receive human onboarding, password reset or bulk access grants.
- An invitation can be accepted once and expires; no mail configuration still has
  an honest self-hosted onboarding path.
- A suspended account cannot obtain fresh tokens; existing-token behavior matches
  the stated policy across all three clients.
- Settings changes expose their scope, actor and durable result; secrets never
  appear in the audit payload.

## Risks and validation plan

Persisted identity IDs, role names, library foreign keys, password/session state,
OpenIddict grants, provider secret formats, metadata defaults and reference-data
preview/tombstone contracts are migration-sensitive. New invitations, lifecycle
status and audit events likely need migrations and a schema-version bump. Never
apply schema migrations from the APIs; the worker retains provisioning ownership.

| Surface | Validation before implementation is considered complete |
| --- | --- |
| Web | `pnpm lint`, `pnpm test`, `pnpm build`; new regressions for dirty drafts, partial writes, failed loads, clear-to-Auto, route/role gates and selection scope |
| Application/persistence | `mise run build`, `mise run test`; transactional last-admin and protected-actor checks, default assignment scope, concurrency and audit persistence |
| Auth/hosts | `mise run test:integration`; real admin/consumer/client access-token and refresh-token behavior, session revocation and cross-surface eligibility |
| Contracts | `pnpm api:update`, then package/app build; no hand-edited generated contracts |
| Browser | Populated Docker review at desktop/mobile widths, light/dark, keyboard navigation, deep links, dirty navigation, stale data and scope confirmations |

Parallelization: none for this assessment. Contract changes precede generated
client adoption. Future independent provider and reference-data UI work can be
separated only after the shared navigation and permission contracts are agreed.

## Assessment evidence and limits

- Read the three Administration surfaces, shared navigation/permissions, user
  hooks/contracts/commands/Identity adapter, metadata-default write path, provider
  and reference-data configuration, audit stamping and token configuration.
- Inspected Settings, Users, Taxonomy and the Shared data updates tab in the
  populated Docker review app as Admin at desktop width. No account, password,
  provider, access, reference-data or taxonomy mutations were submitted.
- Focused frontend tests: **26 passed across 6 files** (Users, provider settings,
  artwork policy, taxonomy and reference-data review). Existing React auth
  `act(...)` warnings remain. Used the installed runner; no new install or client
  regeneration was needed. Current coverage does not prove the missing workflows.
- Backend transaction and endpoint tests were read, not rerun. No lower-role live
  session, last-admin mutation, token-revocation experiment, mobile audit or new
  performance profile was executed. Findings explicitly distinguish code-confirmed
  gaps from proposed capabilities and runtime questions.
- No application source changes were made. This report is the only new artifact.
