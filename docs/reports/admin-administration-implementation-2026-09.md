# Administration implementation — 2026-09-10

This change implements the [Administration assessment](admin-administration-review-2026-09.md).
The assessment remains a record of the original problems, not a description of the new UI.

## Workspaces

- **Users**: server-filtered directory with cursor pagination, scoped selection actions,
  and durable `/users/:id` pages. Identity, access, security, and activity have independent
  tabs. Email, role, and library changes save separately; dirty forms survive background
  refresh and block accidental navigation.
- **Integrations**: provider overview with saved status and last connection test, plus
  separate IGDB and SteamGridDB configuration routes. Settings changes use the revision
  captured with the draft; credentials remain outside mutation caches.
- **Reference data**: regions, languages, aliases, and reviewed definition updates share
  URL-backed tabs. Merge confirmation loads source-game, alias, and canonical-release
  impact. Merges preserve canonical release associations and deduplicate existing targets.
- **Settings**: installation artwork automation. **My account** lives in the avatar menu
  and owns password changes and the current user's sessions.
- **System settings**: metadata policy reads persisted defaults, supports explicit
  Automatic clearing, detects stale revisions, and queues durable recalculation.
- **Audit log**: cursor-paginated successful administrative changes with actor, target,
  time, and safe before/after evidence. A user's Activity tab scopes the same component.

## Account behavior and boundaries

Activation links are single-use, expire after 48 hours, and are displayed once for secure
manual sharing. Recovery links expire after one hour. A newly issued link revokes previous
pending links. Only a SHA-256 digest of the random token is stored; raw links and passwords
are not audit payloads. No email delivery is implied or implemented.

Suspension blocks sign-in and invalidates sessions and pending links. Role, password, and
library changes invalidate existing tokens. Session revocation covers both access and
refresh tokens, including an individual authorization grant. Identity security stamps are
validated across the admin and consumer hosts; console device-flow tokens use the consumer
host. Already accepted server work can finish after revocation.

The system actor is not listed as a human account and cannot be modified through account
administration. Destructive access changes preserve another viable administrator. Default
library assignment requires an explicit selection and skips already assigned or suspended
accounts. The backend repeats these checks independently of UI affordances.

Application handlers own transactions. Identity lifecycle persistence is in Persistence;
HTTP endpoints delegate to handlers. The admin client is regenerated from OpenAPI. The
consumer API contract is unchanged, while its host participates in shared authentication
and receives only the additional LastSignedInAt column write privilege. Account links and
administrative audit tables are denied to the consumer database role.

Audit evidence starts with this migration. It is a history of saved administrative changes,
not a failed-login/security-event stream or a complete historical backfill. Evidence uses
an explicit safe-field allowlist. OIDC `sub` and legacy identity claims both resolve the
HTTP actor; anonymous link acceptance records the account identified by the link explicitly.
MFA, SSO, custom roles, email delivery, and device fingerprints remain separate future work.

## Deployment and review

Schema version 21 is worker-owned. Upgrade worker, admin, and consumer together, starting
the worker first. Existing pre-change access tokens lack the account stamp and require a
fresh sign-in. Database and content volumes are preserved.

Review stack: `http://localhost:21337` (project `romd-artwork-testing`). A database backup was
taken before migration. The dedicated `administration-review@example.invalid` account is
suspended and exists only to make lifecycle and audit states reviewable.

## Verification

- `mise run test`: 2,696 passed across Domain, DAT parsing, Application, Storage,
  and Infrastructure, including real PostgreSQL lifecycle, concurrency, and merge tests.
- Complete Hosting integration suite with developer connection-string exports removed:
  522 passed. Covers real admin authorization-code and consumer/console token flows,
  session listing and individual revocation, password/access changes, audit attribution,
  protected-account behavior, host authorization, contract policy, and migration boundaries.
- `pnpm test`: 633 passed in 99 files. The shared Mantine test wrapper uses test mode to
  remove animation-dependent menu timing; no production animation behavior was changed.
- `pnpm lint`: clean. `mise run build`: passed with zero MSBuild warnings/errors.
  Existing Vite chunk-size and third-party PURE annotation notices remain; these are not
  compiler failures. The local EF CLI/runtime version mismatch is documented in known issues.
- Generated admin OpenAPI/client update succeeded. Consumer generation produced no contract diff.
- Final Docker admin, consumer, and worker images built and started on preserved volumes.
  Admin and consumer readiness endpoints report ready.
- Docker lifecycle smoke: pending account creation, activation, rejection of reused links,
  recovery-link revocation, suspension, real session listing, and exact audit actor checks passed.
  Earlier disposable smoke records were replaced after fixing OIDC actor attribution.
- Browser review: desktop and 390px mobile layouts, light/dark themes, selection menus,
  dirty-navigation protection, separate account/security routes, session visibility, audit
  history, Reference data deep links, provider configuration, and system metadata choices.
  Repeated provider entries in deployment configuration are deduplicated by the selector.

API clients: **admin** — user lifecycle/directory/audit, provider revision, taxonomy impact,
and metadata-policy contracts. Consumer wire contracts are unchanged. No console UI files
were changed; console device-flow authentication was exercised through integration tests.
