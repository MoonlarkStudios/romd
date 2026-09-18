# ROMD Console Profile Home And Library Plan

Status: Gate 0 accepted on 2026-07-17 after independent contract re-review
confirmed the prior four Medium findings were resolved. The Gate 1a backend
implementation, security/correctness, generated-contract, and build-fix reviews
passed. Gate 1a was accepted on 2026-07-17 after its assembled review's one
Medium production identity runbook finding was repaired and fresh independent
documentation re-review passed with no findings. Gate 1b-A adapter foundations
and Gate 1b-B strict manifest foundations were accepted on 2026-07-17. Gate 1b-C
credential/session composition was accepted the same day, closing Gate 1b as the
strict Console contract/auth foundation only. Gate 2 persistence, acquisition,
and cleanup were accepted on 2026-07-18 after all five slices and the assembled
diff passed independent review. Gate 3 central launch authorization was accepted
on 2026-07-18 after independent bypass/side-effect review. Gate 4 profile-owned
play history was accepted the same day after three material review findings were
repaired and fresh re-review passed. Gates 5-6 remain pending.

Date: 2026-07-18

## Outcome

- Home is local-first history for the profile's selected ROMD instance,
  intersected with exact instance-scoped installs and authorized local games.
- Local Library shows all installed `ProfileLocalGames` for that selected
  instance, retaining explicit revokes as `Access required`.
- No selected instance means no ROMD Home or Local Library content.
- Store is the selected instance's live Library for the current account and owns
  Featured, All Games, Systems, Search, download, and attach.
- A polished server switcher discovers stable instance identity before touching
  credentials and swaps remembered per-instance local projections safely.

The normative contract is
`docs/decisions/console-profile-access-and-offline-grants.md`.

## Frozen Contract Summary

### Stable instance identity

- A canonical lowercase UUID-D is atomically stored at
  `<Romd__DataDirectory>/identity/server-instance-id` and shared by split hosts.
- It survives restart, origin moves, and full logical restore; fresh data gets a
  new id; corrupt identity fails startup; independent clones rotate before
  exposure.
- Anonymous `GET /api/server/identity` returns exactly `{instanceId}`.
  Discovery disables redirects and runs before reading/sending refresh tokens.
- Origin is a mutable locator only. Same instance at another origin preserves
  state; another instance at the same origin receives no former token and mounts
  no former rows.
- Instance UUID is a public namespace/collision marker, not cryptographic proof.
  Configured HTTPS/TLS and ordinary OAuth provide transport/user authentication;
  copied-id impersonation needs future key/certificate pinning. Clone rotation
  is operator correctness.
- Legacy origin-keyed secrets are delete-only and never read/sent/rekeyed.
  Cleanup failure disables network credentials until retry; fresh auth always
  creates a profile+instance token after discovery.

### Local identity

- `ServerConnections(instanceId, lastKnownOrigin, firstSeenAt, lastSeenAt)` owns
  discovered locators.
- `PendingServerLocators(profileId, normalizedOrigin, createdAt, lastAttemptAt?)`
  holds an unverified locator only and mounts nothing.
- Each profile has nullable `selectedServerInstanceId` and monotonic
  `serverSelectionGeneration` for switch-away/back ABA protection.
- `ProfileLocalGames` key is exactly profile + instance + release, with title,
  strict `authorizationState`, `acquiredAt`, and `lastCheckedAt`.
- Secure refresh-token keys are profile + instance.
- Installs/content/history/saves/states/config are instance-scoped because
  release/title ids are server-local.
- `LegacyLocalInstalls` exactly preserves the latest pre-cutover release-only
  install schema—unchanged from v10 through v11—including columns, paths,
  manifest, and timestamps, and is never projected or played.
- Switching instances hides without deleting; switching back restores rows.
  Sign-out preserves selected instance and cached play.

### Access and acquisition

- `POST /api/releases/{releaseId}/access` has no body.
- Allowed is exact `{serverInstanceId,releaseId,allowed:true,titleId}`; revoked is
  exact `{serverInstanceId,releaseId,allowed:false}` with no title.
- Server uses ordinary OAuth `sub` plus coherent live current Library/materialized
  projection. There is no security claim, token invalidation, access revision,
  authority object, decision list, or background refresh.
- A `401` gets one normal refresh/retry; unresolved/non-authoritative failure
  preserves the row exactly.
- Launch updates existing rows only. Row creation occurs only after Store access,
  selected title, manifest, server instance, install instance, and release
  identities all match and download/attach verification completes.
- `ConsumerReleaseManifestDto` carries required exact `serverInstanceId` plus
  release/title/platform identity.
- Shared or legacy files never grant. Version-10 installs are hidden legacy
  orphans requiring online verified adoption and rekey/relocation.

### Projections

```text
Home = selected-instance history
       intersect selected-instance installs
       intersect selected-instance ProfileLocalGames where isAuthorized

Local Library = selected-instance installs
                intersect selected-instance ProfileLocalGames

Store = selected instance/current account live Library
```

Server Library materialization is the only content-policy evaluator. The console
has no local policy mode or content blocks. Optional PIN protects local profile
entry/settings only; recovery requires HTTPS/TLS, fresh OAuth/Admin proof, and a
matching selected/enrolled instance response. UUID alone proves nothing.

## Implementation Gates

Each slice follows scout → implementer → focused/full validation → independent
review → repair/re-review → primary inspection → durable status update.

### Gate 0 — Contract freeze

Status: accepted on 2026-07-17 after independent contract re-review confirmed
the prior four Medium findings were resolved.

Gate closes only after independent review of instance identity lifecycle,
discovery-before-credentials, exact API, migration, server switching/ABA,
origin-collision storage, acquisition ordering, cleanup, launch bypasses, PIN
recovery identity, UX states, and test ownership.

### Gate 1a — Server identity and single-release backend

Status: accepted on 2026-07-17. Implementation, focused/full-surface security,
generated-contract, and build-fix reviews passed with no backend code/security
findings. The assembled review's one Medium production identity runbook finding
was repaired; fresh independent documentation re-review passed with no
Critical/High/Medium/Low findings.

Implemented backend state:

- `ServerInstanceIdentity` persists one canonical lowercase UUID-D at
  `<Romd__DataDirectory>/identity/server-instance-id`, publishes first creation
  atomically across processes, fails on malformed existing identity, and is
  initialized before hosts serve requests. The UUID remains a public namespace,
  not authentication.
- Anonymous `GET /api/server/identity` and authenticated
  `POST /api/releases/{releaseId}/access` are mapped through Consumer Host with
  exact OpenAPI contracts. Access returns one allowed/revoked decision; revoked
  responses omit title identity.
- `LiveConsumerLibraryQuery` resolves `Users.LibraryId`, validates live Library
  configuration/materialization state, and reads its projection in one
  serializable SQLite transaction. Current Library, Browse, Collections, BIOS,
  manifest, and access use this shared boundary rather than a Library token
  claim.
- The closed result hierarchy distinguishes `Found`, `ItemNotFound`,
  `LibraryUnavailable`, and `ProjectionInconsistent`; unavailable or
  inconsistent state never becomes a policy revoke.
- `ConsumerReleaseManifestDto` now includes required canonical
  `serverInstanceId`, binding delivery identity to the server namespace.
- Superseded batch/revision/security-claim/token-invalidation artifacts were
  removed. The consumer generated contract changed for identity, access, and
  manifest; the admin generated contract did not drift.

Implement server identity as a prerequisite to the access endpoint.

Required server-identity coverage:

- atomic first creation under concurrent split-host startup;
- identical id across all hosts/data-directory consumers;
- canonical persistence across restart and origin change;
- full logical restore preserves id;
- fresh directory gets another id;
- corrupt/noncanonical/unreadable file fails startup;
- independent clone rotation workflow/test; and
- anonymous exact Consumer Host identity endpoint, mapped-file allowlist,
  registrations, OpenAPI, and host-boundary tests.

The UUID is tested/documented as a public namespace boundary only. Configured
HTTPS/TLS and ordinary OAuth remain authentication; copied-id impersonation is
out of scope without pinning.

Implement `POST /api/releases/{releaseId}/access` with exact boolean DTOs and
`serverInstanceId`. It uses ordinary OAuth subject, live `Users.LibraryId`, and a
coherent valid materialized projection read. Retain atomic materialized boolean/
projection replacement.

Removing the token Library/security revision applies to every consumer
Library-scoped operation, not only access. Gate 1a moves current-Library, Browse,
Collections, BIOS delivery, and manifest issuance to a shared live-assignment
application boundary and coherent repository reads. No consumer policy/read
handler may authorize or select a Library from a token claim. An already-issued
ordinary token must observe reassignment consistently across Store, delivery,
and access.

Remove superseded partial artifacts: multi-release endpoint/query/repository/
DTOs/tests; authority/revision response types; Library `AccessRevision` domain,
entity, migration/designer/snapshot/materialization/repository/DI/OpenAPI/tests;
security-revision claims/current-user paths; access-token invalidator; assignment
token-invalidation transaction/tests. Preserve ordinary authentication and live
assignment. Regenerate EF snapshot, Consumer OpenAPI, and consumer generated
client; explain any admin-client drift.

Gate 1a also adds required exact `serverInstanceId` to
`ConsumerReleaseManifestDto`, backend mapping, OpenAPI, generated consumer
client, and Consumer Host tests.

Tests include allowed/revoked exact shapes, invalid id, no/invalid/unmaterialized
Library, two Libraries, denied metadata omission, anonymous identity, same-token
Library reassignment without invalidation, absence of a security claim, coherent
live read during materialization, same-token Store/current-Library/collection/
BIOS/manifest reassignment behavior, and Consumer Host integration.

### Gate 1b — Flutter discovery and access adapters

Status: Gates 1b-A, 1b-B, and 1b-C accepted on 2026-07-17. Gate 1b is closed as
the strict Console contract/auth foundation only. At that checkpoint Gate 2 was
not shipped; its later accepted state is recorded below.

Accepted Gate 1b-A state:

- `RomdPublicId` validates the backend's canonical Sqids alphabet, minimum
  length, signed 32-bit range, single decoded value, and exact re-encoding.
  `RomdServerInstanceId` accepts canonical lowercase UUID-D only.
- `ServerDiscoveryApiClient` is a separate anonymous exact-contract adapter;
  `ReleaseAccessApiClient` is a separate strict single-release authenticated
  adapter with typed allowed, revoked, HTTP, transport, malformed, and identity-
  mismatch results.
- Every operation owns a fresh credential-empty HTTP transport. Discovery sends
  no authorization or cookies and does not retry an authentication challenge;
  access adds only its explicit bearer token. Redirects are disabled.
- Timeout and `close()` abort the exact active request, including a request that
  is still opening. A timed-out access operation does not cancel a concurrent
  operation.
- These types and adapters are not yet wired to credential discovery/refresh,
  persistence, acquisition, launch authorization, composition roots, or UI.
  Gate 1b-A itself writes no local game/install state; Gate 1b-B/C below record
  the subsequently accepted manifest and credential/session foundations.

Gate 1b-A evidence:

| Evidence | Status |
|---|---|
| Focused identifier/discovery/access tests | PASS: 31 |
| `mise run analyze` | PASS |
| `mise run test` | PASS: 755 |
| Initial independent review | FAILED: two Medium findings—authentication-challenge credential leakage and requests not aborted on timeout/close |
| Repair | Fresh per-operation transports plus explicit active-operation abort ownership |
| Fresh independent re-review | PASS: no findings |
| Gate 1b-A owner decision | Accepted 2026-07-17 |

Accepted Gate 1b-B state:

- `ReleaseManifestApiClient` is a separate strict, server-bound manifest
  adapter. It requires the expected server, release, title, platform, and
  platform-short-name identities before returning one immutable
  `ServerBoundReleaseManifest`.
- Parsing is atomic across the exact root, runtime, launch, item, content-grant,
  identifier, numeric, hash, timestamp, and cross-field contract. One malformed
  nested value rejects the whole manifest; no partial item list or usable
  manifest escapes.
- Semantic vocabulary and cardinality are closed: content type is `single_rom`
  or `unknown`, packaging is `direct_files`, launch type is `file`, and item role
  is `rom` or `disk`. `single_rom` requires exactly one item and a launch target;
  `unknown` rejects exactly one item. Launch must name an item, availability must
  agree with hash/grant presence, and minimum install bytes must equal the
  overflow-safe item total.
- Relative paths use the backend's portable sanitization boundary. The Console
  rejects absolute/backslash/traversal/control/empty-dot paths, portable unsafe
  characters and reserved device segments, trailing dot/space, exact duplicate,
  portable case-insensitive, and file/directory collisions. The backend producer
  sanitizes unsafe source names into that portable vocabulary.
- Discovery, access, and manifest adapters share `OwnedHttpOperation`: a fresh
  credential-empty `HttpClient` per request with operation-local abort/dispose
  behavior. Manifest redirects are disabled; timeout/`close()` abort only the
  affected operation, including while opening.
- Gate 1b-B does not solve target-filesystem Unicode equivalence. It deliberately
  does not collapse NFC/NFD paths. Gate 1b-B itself is not wired to persistence,
  install/acquisition, launch authorization, composition roots, or UI and writes
  no local state. Gate 1b-C below adds credential/session composition without
  turning the manifest into an install or launch grant.

Gate 1b-B evidence:

| Evidence | Status |
|---|---|
| Initial `mise exec -- flutter test test/romd_public_id_test.dart test/romd_server_instance_id_test.dart test/server_discovery_api_client_test.dart test/release_access_api_client_test.dart test/release_manifest_api_client_test.dart` | PASS: 44/44 |
| Initial `mise run analyze` | PASS: clean |
| Initial `mise run test` | PASS: 768/768 |
| Initial independent review | FAILED: 2 Medium—open semantic vocabulary/cardinality and missing portable case-insensitive path-collision rejection; 2 Low—backend unsafe-path mismatch and three duplicated HTTP-operation helpers |
| Repair format | PASS: `mise exec -- dart format` on 7 owned Dart files; 7 formatted, 0 changed |
| Fixture correction | Initial contradiction was test-only and corrected before final validation; no production contract bypass remained |
| Final `mise exec -- flutter test test/romd_public_id_test.dart test/romd_server_instance_id_test.dart test/server_discovery_api_client_test.dart test/release_access_api_client_test.dart test/release_manifest_api_client_test.dart` | PASS: 45/45 |
| Final `mise run analyze` | PASS: clean |
| Final `mise run test` | PASS: 769/769 |
| `dotnet test tests/Romd.Infrastructure.Tests/Romd.Infrastructure.Tests.csproj --filter FullyQualifiedName~ConsumerReleaseManifestRepositoryTests` | PASS: 18/18; 0 skipped |
| Root `mise run test` | PASS: 1,175; Domain 398, Application 225, Storage 67, Infrastructure 485; 0 skipped |
| `git diff --check` | PASS |
| Fresh independent re-review | PASS after rerunning focused Console 45/45, backend 18/18 with 0 skipped, analyze, and diff check; no unresolved findings |
| Warnings/skips/generated artifacts | None |
| Gate 1b-B owner decision | Accepted 2026-07-17 |

Accepted Gate 1b-C state:

- `SecureRefreshTokenStore` preserves the exact version-10 origin-key algorithm
  for deletion only. It never reads, sends, or rekeys that secret. Current
  refresh secrets use the versioned v2 secure-storage key composed from a
  base64url profile component and canonical server instance UUID.
- `ProfileCredentialBootstrap` is fail-closed and ordered exactly as legacy
  cleanup → anonymous identity discovery → profile+instance token lookup.
  Cleanup failure prevents discovery; discovery failure prevents token lookup.
- `SerializedRefreshTokenStore` provides one global invocation-order credential
  queue for legacy cleanup and current reads/writes/deletes. A failed operation
  reports its error without poisoning later queued work or another authority
  partition.
- Every activated origin receives a fresh root-owned `ConsumerApiClient` and an
  aligned `PlayServices` graph using that same origin/client and a live in-memory
  access-token provider. Switching origin closes the old owned client graph.
- In-memory operation epochs and profile-origin generations are advanced before
  async work; Connect is single-flight; stale profile/prompt/bootstrap/refresh/
  repository results are suppressed. These guards are not the persisted
  monotonic selected-instance generation required by Gate 2.
- Cancel, unchanged-origin, prompt failure, origin update failure, remove
  failure, and local-only attach failure recover to an honest retryable state.
  A pending silent restore is restarted where appropriate so the launcher does
  not remain stuck on Connecting. A local-only profile performs zero credential
  cleanup, discovery, refresh, or device-authorization work until Connect.
- Gate 1b-C does not wire the access/manifest adapters into profile access
  persistence, acquisition, launch authorization, or a launcher UI redesign. It
  does not prove real OS secure-storage, live OAuth/server, controller, or GUI
  behavior; accepted evidence uses automated seams and fixtures.

Gate 1b-C evidence:

| Evidence | Status |
|---|---|
| Initial implementation focused suite | PASS: 30 |
| Initial `mise run test` | PASS: 787 |
| Initial independent review | FAILED: 2 High and 1 Medium |
| Repair cycle 1 focused suite | PASS: 45 |
| Repair cycle 1 `mise run test` | PASS: 802 |
| Re-review 1 | Prior findings closed; 1 Medium stuck-Connecting recovery finding remained |
| Repair cycle 2 focused suite | PASS: 52 |
| Repair cycle 2 `mise run analyze` | PASS: clean |
| Repair cycle 2 `mise run test` | PASS: 809 |
| Repair cycle 2 `git diff --check` | PASS: clean |
| Fresh independent re-review | PASS: zero findings; primary final evidence |
| Final warnings/skips/generated artifacts | None |
| Optional compact-reporter run | Interrupted; non-authoritative and not a durable known issue |
| Gate 1b-C owner decision | Accepted 2026-07-17 |

Gate 1b closure itself did not implement Gate 2. The accepted Gate 2 work below
now owns selected instance, pending locator, monotonic selection generation,
migration, `ProfileLocalGames`, instance-scoped installs, and acquisition's
target-filesystem collision preflight. Launch still requires the Gate 3 central
authorizer before side effects.

Add separate handwritten boundaries for anonymous server discovery and the
single-release access request.

Required behavior/tests:

- discovery exact URL/shape/canonical UUID, redirects disabled, typed failures;
- no current profile+instance token lookup/transmission before discovery;
- legacy origin-keyed secrets are never read/sent/rekeyed; initialization
  deletes them before network auth, deletion failure stays reauth-required and
  network-disabled across restart, and success still requires fresh auth;
- access exact boolean DTO, canonical ids, selected expected instance/release/
  title validation, revoked title absence, redirects disabled;
- typed `401` so callers perform exactly one ordinary refresh/retry;
- instance mismatch and every malformed/non-200 result are non-authoritative;
- no old authority/list/revision response types remain; and
- same-origin replacement and different-origin same-instance fixtures.
- strict manifest parsing requires exact instance/release/title/platform fields,
  disables redirects, and returns nothing usable on mismatch.

This gate writes no local game or install state.

### Gate 2 — Instance persistence, acquisition, and cleanup

Status: accepted 2026-07-18. All five slices passed focused/full validation,
independent review, repair where required, re-review, and primary inspection.
This closes persistence, acquisition/attach/adoption, reference removal, and
orphan cleanup only. At that checkpoint it did not ship Gate 3 launch
authorization; Gate 3 is now accepted below. Gate 4 profile play history, Gate 5
launcher projections, and Gate 6 PIN UX remain pending.

Gate 2 is intentionally split across two schema versions. Version 11 is an
additive compatibility foundation that leaves the current release-only
`LocalInstalls` table and its repositories untouched. Version 12 performs the
instance-scoped install cutover only when acquisition and adoption can own the
new table end to end. This avoids redirecting current repositories into a
half-wired table. Gate 2 closed only after the version-12 cutover, cleanup, and
assembled review were accepted.

A fresh pre-implementation scout confirmed that the current database is schema
version 10, migration fixtures are shallow, and the install seam remains
permissive. That scout did not return a final handback after interruptions, so
it is not acceptance evidence. The primary agent independently verified those
facts and narrowed Gate 2a accordingly. A separate independent map-review
attempt also failed to return and is not claimed as passed. Neither interrupted
attempt is part of the acceptance evidence below.

### Gate 2a — Schema v11 additive compatibility foundation

Status: accepted. Schema version 11 added/regenerated only the compatibility
foundation:

```text
ServerConnections(instanceId PK, lastKnownOrigin, firstSeenAt, lastSeenAt)
PendingServerLocators(profileId PK/FK cascade, normalizedOrigin, createdAt, lastAttemptAt?)
LocalProfiles.selectedServerInstanceId? + serverSelectionGeneration
RomdAccountLinks.serverInstanceId? (nullable compatibility binding)
ProfileLocalGames PK(profileId, instanceId, releaseId)
  titleId, authorizationState (`authorized` or `revoked`), acquiredAt, lastCheckedAt
```

The release-only `LocalInstalls` schema and active install repositories remained
unchanged through version 11. The slice did not add the final instance-scoped
install table, copy legacy installs, or redirect install call sites.
`ProfilePlayHistory` was not added and remains owned by Gate 4.

Version-10 origins migrate as pending locators, not fabricated identities.
Existing account links receive no invented instance binding. No games, access,
history, selection, or grants are invented. Preserve exact existing profiles,
origins, links, installs, controller/runtime rows, saves, and unrelated state.
Accepted tests cover every supported harness path, a realistic version-10 fixture,
restart, foreign-key/uniqueness behavior, corrupt stored values, one-time
migration, and exact survival of the untouched release-only installs.

### Gate 2b — Strict repositories

Status: accepted. Strict repositories and transactional operations now own
server connections, pending locators, selected instance/generation, nullable
account-link instance binding, and `ProfileLocalGames`. Repository boundaries
validate canonical instance/public ids, normalized origins, authorization state,
timestamps, and instance/release/title coherence. Unknown or corrupt persisted
values fail closed.

Pending state never mounts content. Repository operations must support atomic
connection upsert/selection/generation/pending deletion without consulting or
rewriting the version-11 install repositories. Tests cover rollback, restart,
profile and authority isolation, pending-with-selection, stale generations,
strict parsing, and failure preserving prior rows.

### Gate 2c — Persisted selection and root composition

Status: accepted. Discovery-first connection ownership and selected-instance
generation are wired into the root composition. Discovery success atomically
upserts the connection, selects/increments generation, binds the matching
account link when applicable, and deletes pending; failure retains pending only.
The in-memory Gate 1b-C epoch guards remain cancellation aids and do not replace
the persisted monotonic generation.

Tests cover first discovery, known reconnect, sign-out preserving selection,
switching/hiding/restoring, no selected server, switch-away/back ABA, profile
switch during discovery, stale responses, same instance at new origin, different
instance at the same origin, no former credential access for replacement, secure
token profile+instance isolation, account changes on one instance, and only the
selected connection holding active session state. Also test pending success/
failure atomicity, pending-with-selection never mounting, legacy-secret deletion
success/failure/restart, and mandatory fresh authentication.

### Gate 2d — Schema v12 install cutover and acquisition

Status: accepted after 2a-2c. Schema version 12 renamed the exact latest pre-
cutover release-only table—unchanged from v10 through v11—to hidden
`LegacyLocalInstalls`, preserving every column and value without projecting or
playing it. The final `LocalInstalls` is keyed by instance+release, and all
active install repositories and call sites use the instance-scoped contract.
New installs require a nonnull canonical instance and release.

Acquisition captures profile/instance/origin/generation/title/release, requires
exact access and manifest identities, copies or materializes into instance
staging, and fully verifies before transactionally persisting the new
`LocalInstall` and initiating `ProfileLocalGame` after a final generation check.
One ordinary `401` refresh/retry is permitted; other failures create no grant.
Shared attach repeats authoritative access, exact manifest/platform validation,
and full verification. Adoption is nondestructive before commit and never treats
a legacy row or shared files as a grant.

Legacy adoption reads only the independently derived canonical v10-v11 content
root; a stored legacy path is evidence, never a path to follow. The selected,
access, and manifest instance must match, while legacy/current release, title,
platform, state, and mode must cohere. Adoption copies only current-manifest
items and verifies their sizes/hashes before staging can be promoted.

Before any staging write, acquisition normalizes every candidate destination
path using the actual target filesystem's equivalence rules and rejects NFC/NFD,
case, exact, and file/directory collisions. Tests must exercise those collisions
on the target-filesystem abstraction. Gate 1b-B's portable parser checks do not
solve Unicode filesystem equivalence and must not be treated as this preflight.

Content, profile save/state roots, and generated per-title config roots now
include the selected canonical server instance. This path isolation is shipped;
Gate 3 authorization and Gate 4 play-history ownership are not.

Tests cover version-11-to-12 migration and direct supported upgrade paths,
download success/failure/cancel/retry, shared attach, manifest or access
instance/release/title mismatch, verified-install mismatch, profile/server
switch mid-flight, no row on failure, shared install no grant, every adoption
crash point before/after the transaction, hidden staging cleanup, playable new
install plus hidden duplicate after post-commit crash, retained exact legacy row
on cleanup failure, origin collision isolation, and same-instance origin move.

### Gate 2e — Reference removal, orphan cleanup, and assembled review

Status: accepted. Reference removal is profile-only. Physical cleanup counts
every authorized and unauthorized `ProfileLocalGame` for the exact instance+
release, serializes with creation/attach/adoption/removal, and rechecks zero
references before deleting shared content. Profile deletion and interrupted work
feed an idempotent orphan cleanup path; cleanup failure retains the hidden
legacy/staging fact for retry and never broadens access or deletes a referenced
install.

One app-scoped FIFO mutation serializer owns each exact instance+release key
across acquisition, attach/adoption, resolution/corrupt marking, reference
removal, profile deletion, and cleanup. Startup scans canonical instance roots
and active install rows for orphan work. Legacy deletion is retryable and
proof-heavy: an active canonical install, matching legacy snapshot/fingerprint,
and verified legacy files are required before the legacy root/row is removed.
Profile deletion is credential-complete and fail-closed: it deletes every
enumerable profile+instance refresh-token partition and exact legacy-origin key
before deleting the profile and its local references.

Accepted tests cover unauthorized-reference protection, cross-instance non-
counting, profile deletion, concurrent removal/acquisition/attach/adoption,
crash/restart, cleanup retry, and exact file/row preservation on failure. The assembled review
exercised version 10 → 11 → 12 and supported direct migration paths with a
realistic fixture, verified no invented access/history/selection, inspected the
raw schema/generated Drift diff, and reran full Console validation. Gate 2 closed
only after all five slices passed independent review and the assembled
persistence, authority-isolation, acquisition, and cleanup review had no
unresolved finding.

Gate 2 acceptance evidence:

| Slice/evidence | Exact result |
|---|---|
| Gate 2a focused / full `mise run test` / `mise run analyze` | PASS: 25/25; 811/811; clean |
| Gate 2b focused / full `mise run test` / hygiene | PASS after one material repair: 45/45; 830/830; analyze and diff check clean |
| Gate 2c focused / full `mise run test` / hygiene | PASS after one High-finding repair: 70/70; 833/833; analyze and diff check clean |
| Gate 2d final focused acquisition/composition suite before final narrow parser/migration fixes | PASS: 107/107 |
| Gate 2d final full `mise run test` / hygiene | PASS: 880/880; analyze and diff check clean |
| Gate 2d fresh independent re-review | PASS after exact-authority, shared-attach, legacy-adoption/crash-ordering, one-`401` retry, and strict platform/lifecycle fixes |
| Gate 2e initial then repaired full `mise run test` | PASS: 895/895; final 897/897 |
| Gate 2e final reviewer focused / `mise run analyze` / `git diff --check` | PASS: 56/56; clean; clean |
| Gate 2e targeted independent re-review | PASS after consolidated legacy deletion, crash retry, serialized corrupt marking, and credential-complete profile deletion |
| Primary current `mise run test` | PASS: 897/897 |
| Warnings, skips, known issues | None; no new known issue |
| Release build | Not run or claimed for this gate |
| Gate owner decision | Gate 2 accepted; Gates 3-6 remain open |

### Gate 3 — Central launch authorization

Status: accepted 2026-07-18. Presentation submits an immutable unresolved
`PlayRequest` containing release/title/display identity and an optional runtime
choice. Profile, selected server/origin/generation, and credentials cannot be
supplied by a route; they come from the root-composed play graph.

The enforced boundary is:

```text
unresolved PlayRequest
  -> coordinator-owned authorizer under exact composed authority
  -> coordinator-only installed-target resolver
  -> runtime selection/dependency preparation/process launch
```

The coordinator parses exact release/title ids and acquires the app-scoped
instance+release mutation lease before authorization. The authorizer reads one
coherent SQLite snapshot containing selected profile/server/origin/generation,
the exact `ProfileLocalGame`, and the strict installed-row identity/fingerprint.
No row, wrong authority/title/release, corrupt projection, or absent exact install
can reach an online policy request or file probing.

With a current token, launch performs one strict release-access request. The
first `401` may restore the exact profile+instance credential and retry once.
Authentication, transport, server, parser, identity, or restoration failure is
not a revoke. After an awaited failure, fallback re-reads the snapshot and
requires the cached grant and install identity to be unchanged: cached
`authorized` permits, cached `revoked` blocks, no row is not granted, and stale
or inconsistent state is unavailable.

A strict allow compare-and-sets `authorized`/`lastCheckedAt` against the exact
snapshot and launches only if that write wins. A strict revoke blocks the
current attempt even if its compare-and-set cannot safely persist; when the
write wins, later offline attempts observe cached `revoked`. Launch never
creates or adopts a profile game.

Only after authorization may the coordinator call the installed-target resolver.
The resolved server/profile/release/title must match the permit and unresolved
request before runtime preference writes, runtime resolution, provisioning,
config/process work, or completed-play bookkeeping. Denial has zero calls into
installed resolution/file probing, runtime/config/provisioning/process, runtime-
preference persistence, or history/play marking.

`ActiveLaunchSession` is app-scoped rather than play-graph scoped. Profile/server
graph replacement therefore cannot lose supervision of a running emulator or
start a second process; ROMD still does not terminate active gameplay on a later
revoke.

Runtime boundary report:

```text
artifact resolver: no - authorization/acquisition reorders existing content resolution; runtime dependency resolution did not change
config writer: no - existing roots/config writers unchanged
```

Gate 3 acceptance evidence:

| Evidence | Exact result |
|---|---|
| Implementer focused launch suite | PASS: 59 |
| Implementer full `mise run test` | PASS: 926 |
| Implementer `mise run analyze` / `git diff --check` | PASS: clean / clean |
| Independent reviewer focused launch suite | PASS: 39 |
| Independent reviewer focused analyze / `git diff --check` | PASS: clean / clean |
| Independent reviewer decision | PASS |
| Primary `mise run analyze` / full `mise run test` | PASS: clean / 926 |
| Release build, GUI, assembled feature | Not run or claimed for this gate |
| Gate owner decision | Gate 3 accepted 2026-07-18 |

### Gate 4 — Instance-scoped history and runtime ownership

Status: accepted 2026-07-18. Schema version 13 adds
`ProfilePlayHistories(localProfileId, serverInstanceId, titleId)` with
`lastReleaseId`, `lastCompletedAt`, and `playCount >= 1`. Profile deletion
cascades history; server/profile/public-id parsing and counters fail closed.

Every v8-v12 upgrade creates the table empty. Device-wide
`LocalInstalls.lastPlayedAt` is preserved only as install/legacy evidence and is
never attributed to a local profile, copied into history, or used by active Home
or detail ordering.

History uses exact completed-process ownership. The coordinator records once
only when `LaunchStarted` supplied the supervised session and that same session
completed with `LaunchExited`; exit code zero and nonzero both count. Denied,
cancelled, failed, `LaunchNotStarted` (even if it wraps `LaunchExited`), or merely
selected games do not record. The exact resolved target retains the initiating
local profile, server instance, title, and release even if the play graph closes
or the visible profile changes while gameplay is active.

The reactive recent projection is scoped to the composed profile+server and
intersects history with authorized `ProfileLocalGames` and installed releases.
Revocation/removal hides without deleting history; restored authorization makes
the same history visible again. If `lastReleaseId` is no longer eligible, the
alternate release is deterministic: newest acquisition, then newest install,
then ascending release id. Invalid joined identity/root data fails closed, while
a valid authorized row with an absent or non-installed release omits only that
candidate and preserves other eligible recents.

Gate 2 already shipped instance-scoped content, save, state, and generated per-
title config roots. Gate 4 consumes the exact resolved launch identity after
process completion; it does not alter runtime dependency resolution or config
writers.

Runtime boundary report:

```text
artifact resolver: no - history consumes the exact resolved launch identity after process completion; runtime dependency resolution did not change
config writer: no - existing roots/config writers unchanged
```

Gate 4 review disposition:

- M1 reviewer Medium: `LaunchNotStarted(LaunchExited)` could record/reclaim
  because started provenance was collapsed. Repaired with explicit
  `startedSessionCompleted` and `LaunchExited(0)`/`LaunchExited(7)` regressions.
- M2 reviewer Medium: the reactive projection lacked direct identical-id
  profile/server metadata-isolation and malformed joined-row proof. Repaired
  with distinct metadata for two authorities, wrong-authority-empty, and foreign-
  root fail-closed coverage.
- M3 primary material finding: valid authorized rows whose install was absent or
  non-installed invalidated the whole projection. Repaired to omit only that
  ineligible candidate while retaining history and other eligible recents.
- Fresh re-review closed M1/M2/M3 with no findings.

Gate 4 acceptance evidence:

| Evidence | Exact result |
|---|---|
| Implementer initial repository / migration suites | PASS: 9 / 9 |
| Implementer initial focused / full `mise run test` / analyze | PASS: 96 / 939 / clean |
| Initial independent review | REJECTED: 2 Medium (M1, M2) |
| Repair `mise run generate:database` | PASS with scoped SDK-cache escalation |
| Repair focused / full `mise run test` / analyze | PASS: 69 / 942 / clean |
| Re-review focused suite / decision | PASS: 33 / no findings after M1/M2/M3 repair |
| Primary `mise run generate:database` | PASS: 0 outputs after implementation generation |
| Primary `mise run analyze` / full `mise run test` / `git diff --check` | PASS: clean / 942 / clean |
| SDK external-cache escalation | Existing documented constraint; no new known issue |
| Release build, GUI, assembled feature | Not run or claimed for this gate |
| Gate owner decision | Gate 4 accepted; Gates 5-6 remain open |

### Gate 5 — Launcher, server switcher, Local Library, and Store

- **5a decomposition:** extract launcher shell and destination state without
  focus/behavior regression.
- **5b local projections:** Home/Local Library for selected instance, authorized
  Home, authorized/unauthorized Library, no-selected-server state, local ordering,
  multi-release detail, and profile-only removal copy.
- **5c Store and server switching:** live Store plus a polished controller-first
  discover/select/switch flow. Show one selected instance, recognizable locator,
  signed-in/signed-out/offline status, sign-in prompt, same-instance moved-origin
  continuity, replacement-instance warning, and remembered local libraries.
  Do not imply simultaneous inactive-instance authentication.
- **5d polish:** deterministic D-pad/keyboard focus/back restoration, no
  cross-instance metadata flash, focused-item changes, accessible announcements,
  motion/type/spacing/copy, screenshots, text scaling, reduced motion, and all
  target geometries.

Home never waits for network or includes discovery rails. Store owns Featured,
All Games, Systems, Search, and acquisition. Preserve ROMD portrait/ambient/
Archivo/IBM Plex/teal identity and 10-foot legibility.

### Gate 6 — Optional profile PIN

Implement controller-complete Add/Change/Remove PIN and protected profile entry/
settings. PIN is unrelated to content access. Secrets/throttle/recovery binding
stay in secure storage. Enrollment/reset require configured HTTPS/TLS, fresh
discovery, fresh OAuth, matching selected/enrolled response instance id, and live
ROMD Admin authorization. UUID alone proves nothing; same-origin replacement
cannot recover or receive former credentials.

## Cross-Cutting Matrix

| Scenario | Home | Local Library | Store | Launch |
|---|---|---|---|---|
| No selected instance | server-select action | unavailable/empty | select server | blocked |
| Authorized row offline | history item | visible | offline | allowed |
| Unauthorized row offline | hidden | `Access required` | offline | blocked |
| Access failure after authorized | unchanged | unchanged | honest error | cached authorized fallback |
| Explicit revoke | hidden | `Access required` | live Library | blocked |
| Switch instance | new instance projection | new instance projection | new live Library/sign-in | exact instance only |
| Switch back | remembered history | remembered rows | sign-in as needed | cached exact instance |
| Same instance, new origin | preserved | preserved | reauth if needed | preserved exact instance |
| Same origin, replacement instance | no former rows | no former rows | explicit new setup | no former token/state |
| Legacy orphan | hidden | hidden | verified adoption action | blocked |
| Two profiles, one install | independent | independent | current account | independent rows |

## Risk And Validation

Key risks are identity-file races/corruption/clones, credentials sent before
discovery, same-origin replacement leakage, server-local id collision, migration
fabrication, ABA work, acquisition crash ordering, launch bypass, cleanup races,
and focus/metadata leakage during switching. Each owning gate has explicit tests
above.

Required phase-gate commands:

- root: `mise run test`, `mise run test:integration`, and assembled
  `mise run build`;
- `web/`: `pnpm api:update`, `pnpm lint`, `pnpm test`, `pnpm build`;
- `clients/romd_console/`: format changed Dart, `mise run analyze`,
  `mise run test`;
- hygiene: `git status --short`, `git diff --check`, `git diff --stat`;
- final supported macOS Release build and strict codesign verification.

Maintain one evidence ledger per gate with exact commands/results, warnings,
skips, visual artifacts, reviewer findings, fix/re-review, residual risk, and
owner decision. Consult `docs/known-issues.md` before interpreting unexpected
failures. Automated tests never replace live keyboard/gamepad/visual inspection.

## Historical Superseded Evidence

The former multi-release/revision implementation reported backend 1099/1099,
integration 222/224 with two documented unrelated failures, clean Release build,
web generation/lint/build, Flutter focused 27/27 then repaired 30/30, clean
analysis, and console 743/743. Those results validate only the superseded code.
They do not accept server identity, boolean access, removal of security/token
revision artifacts, instance-scoped migration, discovery, acquisition, launch,
runtime namespaces, or switching UX.

## Gate Ledger And Sequencing

| Evidence | Status |
|---|---|
| Human approval of stable instance identity | Accepted product direction |
| Scout identity/topology/schema audit | Complete; read-only |
| Durable contract rewrite | Complete |
| Independent Gate 0 review | Passed after the prior four Medium findings were resolved |
| Gate 0 owner decision | Accepted 2026-07-17 |

Gate 1a evidence as of 2026-07-17:

| Evidence | Status |
|---|---|
| Focused application handlers | PASS: 54 |
| Focused infrastructure | PASS: 66 |
| Focused Consumer Host | PASS: 104 |
| Closed-hierarchy boundary | PASS: 1 |
| Independent hierarchy/security review | PASS: no Critical/High/Medium findings |
| `mise run test` | PASS: 1,171; 0 failed; 0 skipped |
| `pnpm api:update` | PASS; Admin Host 0 warnings/errors, Consumer Host 0 warnings/errors; admin generated/schema diff empty; consumer identity/access/manifest changed plus pre-existing BIOS catch-up |
| `pnpm lint` | PASS: 305 files |
| `pnpm test` | PASS: 46 files, 250 tests; 50 documented pre-existing React `act` warnings across 11 files |
| `pnpm build` | PASS; existing SignalR PURE-annotation and chunk-size warnings |
| `mise run test:integration` | PARTIAL: 234 passed, 2 documented unrelated failures, 0 skipped; blame confirmed all tests finished |
| `mise run build` | PASS twice after serializing the root build with `-m:1`; 0 MSBuild warnings/errors, stable target order, and byte-matching package indexes across all three outputs |
| Generated-contract review | PASS |
| Build-fix review | PASS |
| Assembled Gate 1a review | FAILED on documentation only: one Medium production identity backup/restore/clone runbook finding plus Low stale Library identity wording/grammar; repair applied |
| Fresh independent documentation re-review | PASS: no Critical/High/Medium/Low findings |
| `git diff --check` | PASS |
| Gate 1a owner decision | Accepted 2026-07-17 |

Accepted sequence through Gate 4: Gate 0 → 1a identity/backend → 1b Flutter
adapters → 2a schema v11 → 2b strict repositories → 2c persisted selection/
composition → 2d schema v12/acquisition → 2e removal/cleanup → 3 launch
authorization → 4 schema v13/profile history. Remaining sequence: 5 UX → 6 PIN.
Generated artifacts, migrations, composition roots, and shared launcher files
remain single-owner. Gate 5b/5c may parallelize only after 5a with disjoint
files.

## Deferred

- maximum offline age;
- push/proactive revocation;
- remote gameplay termination;
- simultaneous active credentials for multiple instances;
- recommendations; and
- server-synced console history.
