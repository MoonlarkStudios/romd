# Orchestration Handoff: Profile Home, Local Library, And Store

Status: Gate 0 accepted on 2026-07-17 after independent contract re-review
confirmed the prior four Medium findings were resolved. Gate 1a implementation
and security, generated-contract, and build-fix reviews passed. Gate 1a was
accepted on 2026-07-17 after its assembled review's one Medium production
identity runbook finding was repaired and fresh independent documentation
re-review passed with no findings. Gate 1b and Gates 2-6 remain pending.

Date: 2026-07-17

Audience: the primary orchestration agent taking this feature from accepted
design through the final quality gate before human testing.

## Mission

Deliver a profile-first ROMD Console where Home and Local Library are scoped to
one selected ROMD server namespace; acquired games
remain generously playable offline; online launch learns explicit revocation;
Store remains the live current-account Library; and optional PIN protects local
profile entry/settings without becoming content policy.

This is an end-to-end implementation handoff, not permission to stop at
scaffolding. The result spans server identity, Consumer API, generated clients,
Flutter discovery/persistence/acquisition/launch/runtime paths, polished server
switching, migration, and durable documentation.

The product owner's exit criterion remains exact:

> You are the last quality gate before a human tests the result. Nothing you
> hand over may be half-finished, untested, visually rough, or "works but ugly."
> The bar is world-class: UX a designer would sign off on, code an architect
> would sign off on. Deficient work returns for another cycle.

## Start Here

1. Read all sources of truth and applicable skills.
2. Preserve the accepted Gate 1a contracts and exact evidence.
3. Scope Gate 1b before assigning its Flutter discovery/access adapter work.
4. Do not begin Gate 2 while Gate 1b remains open.
5. Preserve the dirty worktree; do not commit/push unless separately requested.

Source precedence:

1. explicit human product direction and quality bar;
2. repository instructions/safety;
3. `docs/decisions/console-profile-access-and-offline-grants.md`;
4. `docs/console-profile-home-library-plan.md`;
5. this orchestration procedure;
6. likely file lists.

Primary and workers read the closest `AGENTS.md`, the decision/plan, runtime and
parental-control decisions, save-ownership roadmap, and `docs/known-issues.md`
before retrying unexpected validation failures. Endpoint/OpenAPI work uses
`romd-web-api-client`; runtime path work uses `romd-console-runtime`.

## Non-Negotiable Contract

### Stable server instance identity

- Canonical lowercase UUID-D is atomically stored at
  `<Romd__DataDirectory>/identity/server-instance-id`.
- Split hosts sharing the data directory share one id. Concurrent first startup
  cannot create divergent ids.
- Restart, origin move, and full logical restore preserve id. Fresh data gets a
  new id. Malformed/unreadable identity fails startup. Independent clones rotate
  before exposure.
- Anonymous exact `GET /api/server/identity` returns `{instanceId}`.
- Console discovery disables redirects and occurs before reading/sending any
  legacy/current refresh credential for an origin.
- Origin is a mutable locator, never the durable local key.
- Instance UUID is public/non-authenticating and prevents accidental namespace
  collision only. Configured HTTPS/TLS + ordinary OAuth authenticate. Copied-id
  active impersonation requires future key/cert pinning; clone rotation is an
  operator correctness requirement.

### Console selection and storage

- `ServerConnections` maps instance id to last known origin and seen times.
- Each profile selects zero or one instance and has a monotonic
  `serverSelectionGeneration` ABA guard.
- No selection means no ROMD Home/Local Library.
- Switching hides without deleting; switching back restores remembered state.
- Sign-out preserves selection and cached local games.
- Same instance/new origin preserves state but may require auth. Same
  origin/replacement instance receives no former token and mounts no former rows.
- Secure tokens key by profile + instance. Do not invent simultaneous active
  credentials for inactive instances.
- Origin-keyed legacy tokens are delete-only and never read/sent/rekeyed. Delete
  them before network auth; failure leaves reauth-required/network-disabled with
  retry across restart. Fresh auth stores a new profile+instance token.
- `PendingServerLocators` holds unverified locators only and mounts nothing.
  Discovery success atomically upserts connection, selects/increments generation,
  and deletes pending; failure retains pending only.

### Access and offline behavior

- `ProfileLocalGame` key is profile + instance + release; fields are title,
  authorization boolean, acquired time, last checked time.
- Row creation occurs only after verified Store download/attach. Shared files,
  manifest visibility, or a first-time revoke never create a row.
- Authorized row permits offline launch indefinitely by default. Unauthorized
  row blocks offline and remains in Local Library as `Access required`.
- `POST /api/releases/{releaseId}/access` has no body. Exact allowed response is
  `{serverInstanceId,releaseId,allowed:true,titleId}`; revoked is
  `{serverInstanceId,releaseId,allowed:false}` with no title.
- Server uses ordinary OAuth subject and coherent live current Library/current
  valid materialized projection. No security/access revision or token
  invalidation participates. Same issued token observes Library reassignment.
- A `401` gets one normal refresh/retry. Unresolved `401`, redirect, timeout,
  transport, malformed response, other 4xx, or 5xx preserves cached state.
- Every successful response's instance id must match before write.
- Launch can update an existing row only and never creates/adopts one.
- No batch, periodic, resume, connectivity-return, or background access refresh.
  Delayed offline revocation is accepted.

### Origin-safe runtime identity

Profile local games, installs, content, history, saves, states, and generated
title config all include server instance identity. Version-10 installs are
hidden legacy orphans. Adoption requires discovery, allow, matching manifest,
full verification, and nondestructive copy/staging before DB commit.
`LegacyLocalInstalls` exactly preserves v10 rows and is never projected/played.

Physical cleanup counts all authorized/unauthorized rows for exact instance +
release and serializes with row creation, attach/adoption, removal, and cleanup.

### Policy, projections, and UX

- Server Library materialization is the sole content-policy evaluator.
- Console has no local policy mode or local content blocks.
- Home = selected-instance history ∩ installs ∩ authorized local games.
- Local Library = selected-instance installs ∩ all local-game rows.
- Store = selected instance/current-account live Library.
- Store owns Featured, All Games, Systems, Search, acquisition.
- Server switching is polished, controller-complete, honest about sign-in/offline
  state, and cannot flash metadata from another instance.
- Optional PIN protects profile entry/settings only. Recovery requires configured
  HTTPS/TLS, fresh discovery and OAuth, response-id match, and live Admin proof.
  UUID alone is not authentication.

## Verified Starting Traps To Reconfirm

- Console schema was version 10; installs were keyed only by release and device
  `lastPlayedAt` was not profile/server scoped.
- Stored origins and account links do not prove server instance identity.
- Existing secure refresh tokens may be origin-keyed; never read/send them
  before discovery proves same instance.
- Existing install/content/save/config keys can collide across servers.
- `LocalProfileEntryMode.pin` is not a security boundary.
- Presentation resolves installed content before current play coordination;
  authorization must move before file probing/install mutation.
- Current Home mixes device history and online discovery and has high-value
  focus/text-scale/reduced-motion/controller test patterns to preserve.
- Prior partial Gate 1 added multi-release/revision/security-token artifacts.
  They must be removed, not adapted into the revised contract.

## Operating Model

Use at most three workers alongside the primary. Named roles:

- `romd-scout`: read-only topology/risk/test mapping;
- `romd-implementer`: one bounded owned slice plus validation;
- `romd-reviewer`: independent read-only correctness/UX gate;
- `romd-librarian`: durable facts after acceptance.

Mandatory loop:

```text
scout -> scoped implementer -> validation -> independent reviewer
  -> findings: implementer repair -> fresh review
  -> pass: primary diff/raw-output verification -> librarian -> next gate
```

After two failed repair cycles on the same defect, re-scout/reduce/redesign.
Never waive it.

Every task packet includes outcome, exact scope/ownership, instructions/skills,
invariants, neighboring dirty edits, tests/commands, forbidden shortcuts, and
return shape. Every handback lists files, symbols/behavior, tests, exact raw
command status, warnings/skips/known-issue mapping, artifacts, visuals, findings,
residual risk, docs need, and review readiness. Track slices as
`not started|scoped|implementing|review failed|rework|accepted|integrated`.

## Phase Gates

### Gate 0 — Revised contract freeze

Status: accepted on 2026-07-17 after independent contract re-review confirmed
the prior four Medium findings were resolved.

Review server identity creation/restore/clone semantics, discovery-before-token,
exact API, live Library read, removal of partial revision artifacts, console
schema/migration, same-origin replacement, origin move, ABA, acquisition/adoption,
launch bypasses, cleanup, instance-collision runtime paths, server-switcher UX,
and PIN recovery identity. Attach every test to a gate.

### Gate 1a — Server identity and backend access

Status: accepted on 2026-07-17. Implementation and independent
hierarchy/security, generated-contract, and build-fix reviews passed with no
backend code/security findings. The assembled review's one Medium production
identity runbook finding was repaired; fresh independent documentation
re-review passed with no Critical/High/Medium/Low findings.

Implemented state:

- one atomic, canonical data-directory server identity plus anonymous Consumer
  identity endpoint;
- exact authenticated one-release allow/revoke endpoint with metadata-free
  denial;
- shared serializable `LiveConsumerLibraryQuery` for current Library, Browse,
  Collections, BIOS, manifest, and access;
- closed `Found`/`ItemNotFound`/`LibraryUnavailable`/
  `ProjectionInconsistent` result hierarchy so failure is not revocation;
- required `serverInstanceId` on manifests; and
- generated consumer identity/access/manifest contract with no admin-client
  drift from this slice.

Implement identity storage/service and anonymous Consumer endpoint. Test:

- split-host concurrent creation and shared id;
- restart/origin-move/full-restore stability;
- fresh-directory difference;
- corrupt/noncanonical/unreadable startup failure;
- independent-clone rotation workflow;
- exact anonymous response/OpenAPI/host boundary; and
- production data-directory ownership/permissions behavior.

Review/tests state explicitly that UUID is a public namespace boundary, not
cryptographic continuity; HTTPS/TLS and OAuth authenticate, and malicious copied-
id impersonation is outside scope without pinning.

Implement exact boolean single-release access including `serverInstanceId`.
Use ordinary OAuth subject, live `Users.LibraryId`, and coherent materialized
read. Test same-token reassignment without invalidation, no security claim,
allowed/revoked/no metadata, invalid id, invalid/unmaterialized Library, two
Libraries, concurrent materialization, unauthenticated, and Consumer Host flow.

Move every consumer Library-scoped operation—current Library, Browse,
Collections, BIOS delivery, manifest, and access—off token Library claims and
onto one shared live-assignment application boundary with coherent repository
reads. Test that one already-issued ordinary token observes reassignment across
Store, delivery, and access without token rotation.

Remove old multi-release endpoint/query/repository/DTO/tests and all partial
`AccessRevision`, `SecurityRevision`, token invalidator, assignment-invalidation,
migration/designer/snapshot/materialization/repository/DI/OpenAPI/client/test
artifacts. Retain atomic materialized boolean/projection replacement and live
assignment. Regenerate EF snapshot, Consumer OpenAPI, and consumer client.

Add required exact `serverInstanceId` to `ConsumerReleaseManifestDto`, backend
mapping, OpenAPI, generated consumer client, and Consumer Host tests.

Gate closes after root tests/integration/build as applicable, generation/web
checks, independent auth/identity review, fixes, and re-review.

### Gate 1b — Flutter discovery/access adapters

Implement anonymous discovery before credentials and strict boolean access.
Tests cover redirects, exact fields, canonical ids, typed failures, instance/
release/title mismatch, revoked title rejection, no-credential-before-discovery,
same-origin replacement, different-origin move, and exact `401` propagation for
one caller-owned refresh/retry. Remove old authority/list/revision types/tests.

Legacy origin-keyed secrets are never read/sent/rekeyed. Test delete-before-auth,
cleanup failure disabling credentials, restart/retry, and mandatory fresh auth.
The strict manifest adapter requires exact instance/release/title/platform,
disables redirects, and returns no usable manifest on mismatch.

### Gate 2a — Persistence and migration

Add `ServerConnections`, `PendingServerLocators`, selected instance/generation,
`ProfileLocalGames`, instance-keyed installs/history, and exact-copy
`LegacyLocalInstalls`. Enforce FKs/checks/strict parsing; regenerate Drift.

Realistic v10 fixture: origin creates pending row, no instance/game/history
fabrication, installs copy exactly to never-projected legacy rows, credentials
are delete-only, unrelated profile/link/controller/runtime/save data survives.
Discovery success atomically upserts/selects/increments/deletes pending; failure
retains pending only and mounts nothing. Cover every
supported migration path, restart, idempotence, corrupt rows, indexes/FKs, and
origin/release collision fixtures.

### Gate 2b — Discovery, selection, and sessions

Implement known/new discovery and selected connection ownership. Test no
selection, sign-out, switch/hide/restore, switch-away/back ABA, stale async work,
profile switch, same instance/new origin, same origin/replacement instance, token
profile+instance keys, no replacement credential access, same-instance account
change, and only selected active session.

### Gate 2c — Acquisition, adoption, removal, cleanup

Capture profile/instance/origin/generation/title/release. Strictly match access,
manifest, and verified install identity before row commit. Test all transfer/
cancel/retry/crash/mismatch paths, shared attach no grant, server switch in
flight, and legacy adoption: instance staging/full verification, transaction
persists new install and initiating row last, no destructive precommit move,
every crash point, idempotent postcommit duplicate cleanup, retained hidden
legacy row on cleanup failure, instance collision, and origin move.

Removal deletes one profile row. Cleanup counts all rows for exact
instance/release regardless authorization and serializes/rechecks under races.
Test unauthorized protection, another-instance non-counting, profile deletion,
and cleanup failure.

### Gate 3 — Central launch authorization

One authorizer below presentation receives exact profile/selected instance/
generation/title/release before file probe or runtime side effects.

Test existing authorized/unauthorized online checks; allow/revoke row updates;
one refresh/retry; exact failure preservation; instance mismatch no write;
offline authorized/unauthorized; no row; cross-profile/instance/title/release;
launch never creates/adopts; every mapped route; and zero denial calls to install
mutation, resolver, provisioner, config, process, history, or feedback.

### Gate 4 — Instance-scoped history and runtime paths

Implement profile+instance+title history with completed-launch semantics. Add
instance to content/save/state/generated-config namespaces and resolution
contracts. Preserve ambiguous legacy data without automatic attribution.

Test same encoded ids across instances, two profiles, multi-release selection,
hidden/restored history, ties/counts/restart, rejected launch, guest ownership,
and every emulator/config path. Runtime workers read `romd-console-runtime`.

### Gate 5 — Launcher, Store, and server switcher

Slices:

1. 5a shell/destination extraction with focus parity.
2. 5b selected-instance Home/Local Library including no selection,
   `Access required`, ordering/detail/removal states.
3. 5c live Store plus discover/select/switch UX: one selected instance,
   remembered libraries, locator/status, sign-in prompt, moved-origin continuity,
   replacement warning, no implied inactive authentication.
4. 5d integrated focus/back/accessibility/motion/type/copy/screenshots.

Required UI tests: keyboard/gamepad directional traversal, focus restoration,
switching while content focused, no cross-instance metadata flash, Back,
sign-in/offline/error/loading/download/adoption states, long copy, text 1.0/1.5/
2.0, reduced motion, 1280x720/1920x1080/ultrawide, controller reconnect.

### Gate 6 — Optional PIN/recovery

Add/Change/Remove PIN and profile entry/settings verification. Secure storage,
throttling, controller flows, route/service bypass prevention remain mandatory.
Enrollment/reset uses configured HTTPS/TLS plus fresh discovery/OAuth, matching
selected/enrolled response id, then live ROMD Admin authorization. UUID alone
proves nothing. Test moved-origin recovery, same-origin replacement denial/no
former token, wrong instance, cancellation, restart, throttling, and guest
isolation.

## Cross-Cutting Matrix

| Scenario | Home | Local Library | Store | Launch |
|---|---|---|---|---|
| No selected instance | selection action | unavailable | select server | blocked |
| Authorized offline | visible by history | visible | offline | allowed |
| Unauthorized offline | hidden | `Access required` | offline | blocked |
| Authorized + failure | unchanged | unchanged | honest error | cached fallback |
| Explicit revoke | hidden | `Access required` | live | blocked |
| Switch instance | swapped | swapped | selected live/sign-in | selected only |
| Switch back | restored | restored | sign-in as needed | exact cached row |
| Same instance/new origin | preserved | preserved | may reauth | preserved |
| Same origin/new instance | no former rows | no former rows | new setup | no former state/token |
| Legacy orphan | hidden | hidden | adopt action | blocked |
| Same ids/two instances | isolated | isolated | isolated | isolated paths |

Also test restart during discovery/acquisition, profile switch in-flight,
malformed identity/access, same-token Library reassignment, disk/runtime errors,
active-game behavior, title multiple releases, and uninstall/removal copy.

## Validation And Evidence

Gate 1a evidence as of 2026-07-17:

- focused application 54 pass; infrastructure 66 pass; Consumer Host 104 pass;
  closed-hierarchy boundary 1 pass;
- `mise run test`: 1,171 pass, 0 fail, 0 skip;
- `pnpm api:update`: pass; both hosts generated with 0 warnings/errors, admin
  generated/schema diff empty, consumer changed for identity/access/manifest
  plus a legitimate pre-existing BIOS catch-up;
- `pnpm lint`: 305 files pass;
- `pnpm test`: 46 files/250 tests pass, with 50 documented pre-existing React
  `act` warnings across 11 files;
- `pnpm build`: pass with existing SignalR PURE-annotation and chunk-size
  warnings;
- `mise run test:integration`: 234 pass, 2 documented unrelated failures, 0
  skip; blame confirmed all tests completed;
- `git diff --check`: pass; and
- `mise run build`: pass twice after serializing the root build with `-m:1`; 0
  MSBuild warnings/errors, stable target order, and byte-matching package indexes
  across all three outputs.

The independent hierarchy/security, generated-contract, and build-fix reviewers
passed with no backend code/security findings. The assembled review failed on a
single Medium production identity backup/restore/clone runbook gap and Low stale
Library identity wording/grammar only. The documentation repair was applied,
fresh independent re-review passed with no Critical/High/Medium/Low findings,
and the Gate 1a owner accepted the gate on 2026-07-17.

Required commands:

- root backend: `mise run test`, `mise run test:integration`, assembled
  `mise run build`;
- `web/`: `pnpm api:update`, `pnpm lint`, `pnpm test`, `pnpm build`;
- console: format changed Dart, `mise run analyze`, `mise run test`;
- hygiene: `git status --short`, `git diff --check`, `git diff --stat`;
- final macOS Release build and strict codesign verification when supported.

Inspect raw warnings/skips/hangs. Consult known issues before retrying. Build does
not replace tests; focused does not replace full. Maintain per-gate ledger:
owner/files/behavior/tests/focused/full/visuals/findings/fix-review/warnings/
residual risk/decision.

## Mandatory Visual Gate

Primary runs assembled app and captures representative screenshots for profile
entry/PIN, no server, discovery, first selection, moved origin, replacement
instance, switching/back, two instances with colliding fixture ids, populated/
empty/offline/error Home and Library, Store lenses/acquisition/adoption,
revocation while focused, Controllers/Settings return, all input/text/motion/
geometry cases.

Reject rough routes, focus loss/off-screen focus, mouse-only action, metadata
flash, unreadable rest state, generic errors, layout jumps, clipping, ignored
reduced motion, inconsistent destinations, or missing screenshots.

## Architecture And Security Reviews

Obtain independent assembled passes for:

1. identity/security: file creation/clone/corruption, discovery-before-token,
   replacement isolation, live Library read, failure preservation, migration,
   instance collisions, launch bypass;
2. UX/controller: live traversal/switching/focus/back/states/copy/accessibility;
3. integration: complete architecture, generated artifacts, warnings, full
   commands, release signature, evidence/docs truth.

Every relevant Critical/High/Medium returns to implementer and fresh review.

## Automatic Send-Back Conditions

Send back if:

- hosts can diverge identity, corrupt identity self-heals, or clone guidance is
  unsafe;
- credentials are read/sent before identity discovery or to replacement server;
- origin is used as durable state/token key;
- response instance mismatch writes anything;
- auth/network/parser/server failure changes authorization;
- same-token reassignment depends on token invalidation/security claim;
- migration fabricates instance/game/history or exposes legacy orphan;
- server-local ids cross install/history/save/state/config namespaces;
- ABA/stale response writes after selection changes;
- acquisition/manifest/verification mismatch creates row;
- launch creates row/bypasses authorizer/reaches side effects on denial;
- cleanup deletes while any exact-instance row exists;
- Home/Library show without selection or flash another instance;
- navigation/focus/visual criteria fail;
- generated artifacts are hand-edited/drifted; or
- PIN recovery trusts locator instead of enrolled instance.

## Definition Of Done

All Gates 0-6 accepted/integrated; identity and migration deterministic; no
credential/replacement leak; no launch bypass; offline allow/revoke/failure
verified; two profiles and two colliding server instances isolated; selected-
server UX polished; keyboard/gamepad/focus/accessibility live-inspected; full
validation/build/signature truthful; generated/docs current; no relevant TODO,
placeholder, skip, suppressed warning, or unresolved review finding.

Final human handoff leads with behavior, exact migrations/files, evidence ledger,
screenshots, review/fix cycles, limitations, and a short controller/two-server/
offline/revoke/acquisition/PIN test script. Do not ask the human to discover
obvious defects or prove the security boundary.
