# Console Profile Access And Offline Grants

Status: Gate 0 accepted on 2026-07-17 after independent contract re-review
confirmed the prior four Medium findings were resolved. The revised server
identity, live-Library single-release access endpoint, and manifest identity
binding are implemented and passed independent security/correctness review.
Gate 1a was accepted on 2026-07-17. Generated-contract and build-fix reviews
passed; the assembled review's one Medium production identity runbook finding
was repaired, and fresh independent documentation re-review passed with no
Critical/High/Medium/Low findings. Gate 1b-A canonical identifier and strict
discovery/access adapters and Gate 1b-B strict server-bound manifest adapter were
accepted on 2026-07-17. Gate 1b-C credential/session composition was accepted the
same day, closing Gate 1b as the strict Console contract/auth foundation only.
Gate 2 selected-instance/profile-game persistence, acquisition, adoption,
reference removal, and cleanup were accepted on 2026-07-18. Gate 3 central
launch enforcement and Gate 4 profile-owned play history were accepted the same
day. Launcher UX and PIN work remain pending in Gates 5-6.

Date: 2026-07-18

## Context

ROMD Console installs content locally while the selected server's materialized
Library remains the sole evaluator of ownership, exposure, platform, title,
genre, and content-rating policy. A profile begins with no ROMD games and gains
a durable local row only after Store completes an authoritative allowed and
fully verified download or attach.

Normalized URL alone is not a safe identity: a different ROMD installation can
later occupy the same origin, while the same ROMD installation can move to a
different origin. Durable local access and data therefore bind to a stable ROMD
server instance id. Origin is only a mutable network locator.

## Server Instance Identity

Each ROMD data directory owns one canonical lowercase UUID-D `instanceId`.

- It is stored atomically at
  `<Romd__DataDirectory>/identity/server-instance-id`.
- It is public, non-secret, and shared by admin, consumer, worker, and
  compatibility hosts using that data directory.
- It survives process restart, origin/port/host moves, and a full logical
  restore of that ROMD data directory.
- A fresh data directory creates a new id exactly once using atomic
  create/publish semantics safe under split-host concurrency.
- A missing identity directory is created safely. An existing malformed,
  noncanonical, empty, or unreadable identity file fails host startup; it is
  never silently replaced.
- Independently exposed clones must rotate the copied id before exposure. The
  restore/clone runbook and tests distinguish an intentional logical restore
  from an independent clone.

The UUID is a public, non-authenticating namespace and accidental-collision
boundary. It is not cryptographic continuity proof. Console relies on its
configured HTTPS/TLS origin plus ordinary OAuth for transport and authentication,
then requires response ids to match the selected namespace. A copied UUID or
malicious active-server impersonation is outside this boundary and would require
key/certificate pinning. Clone rotation is an operator correctness requirement,
not an authentication mechanism.

Consumer Host exposes the anonymous exact endpoint:

```text
GET /api/server/identity
200 { "instanceId": "<lowercase-uuid-D>" }
```

No extra/missing field or noncanonical id is accepted. Console discovery uses
the configured HTTPS/TLS origin and disables redirects. It discovers and
validates identity before reading, refreshing, or sending a current
profile+instance credential. Version-10 origin-keyed credentials are never read,
sent, or rekeyed, even after discovery.

## Terms

- **Server connection**: a discovered instance id and its most recently verified
  origin locator.
- **Selected server**: the one `ServerConnections.instanceId` selected by a
  local profile.
- **Profile local game**: durable evidence that the profile completed an online
  allowed and verified Store download/attach from that instance.
- **Authorized**: `isAuthorized = true`; the exact instance-scoped install may
  launch offline.
- **Unauthorized**: an existing row explicitly updated by a successful revoke;
  it remains in Local Library as `Access required` and blocks offline.
- **Legacy orphan**: a version-10 install without trustworthy instance identity;
  hidden and unplayable until verified online adoption and relocation.

A profile local game is not a content grant. Content grants remain short-lived
delivery URLs.

## Decision

### Selection and connection behavior

```text
ServerConnections
  instanceId       PRIMARY KEY, canonical lowercase UUID-D
  lastKnownOrigin  normalized mutable locator
  firstSeenAt
  lastSeenAt

PendingServerLocators
  localProfileId    PRIMARY KEY, FOREIGN KEY -> LocalProfiles ON DELETE CASCADE
  normalizedOrigin
  createdAt
  lastAttemptAt?

LocalProfiles
  selectedServerInstanceId?  FK -> ServerConnections
  serverSelectionGeneration  NOT NULL DEFAULT 0
```

No selected instance means no ROMD Home games and no Local Library. Store shows
the server-selection/connect action. Selecting a known or newly discovered
instance increments `serverSelectionGeneration` and immediately swaps Home and
Local Library to that instance's rows without deleting other instances' state.
Switching back restores remembered rows.

`PendingServerLocators` holds only an unverified v10/new locator. It is not
identity, mounts no Library, and permits no credential lookup/transmission.
Selected instance and pending locator may coexist as stored transition facts but
only the selected instance can mount; pending never projects content. Successful
discovery atomically upserts `ServerConnections`, selects that instance,
increments generation, and deletes the pending row. Failure updates
`lastAttemptAt` and retains pending only without changing selection.

Sign-out clears the active session/credentials but keeps the selected instance
and cached local games usable. Only the selected connection has active session
state; inactive instances retain local rows/files, not simultaneous active
credentials unless a later product decision requires it.

Async operations capture profile, selected instance, and generation. Switching
away and back still changes generation, so stale work cannot win an ABA race.

Discovery determines continuity:

- same instance at a new origin: update `lastKnownOrigin`, preserve all
  instance-scoped state, and authenticate again if required;
- same normalized origin returning a different instance: treat as a replacement,
  send/read no former instance refresh token, select/mount no former rows, and
  require an explicit selection/authentication flow; and
- same instance and origin: ordinary reconnect.

Secure refresh-token keys are local profile + server instance id. Origin is
never the credential or cached-access partition key.

On first post-migration credential initialization, the console deletes every
legacy origin-keyed refresh secret for that profile before enabling network
authentication. Deletion success still requires fresh OAuth after discovery and
stores a new profile+instance token. Deletion failure leaves the profile
reauth-required with network credentials disabled, exposes a retry action, and
never falls back to reading the legacy secret. Restart repeats the fail-closed
cleanup state.

### Library is the only content-policy evaluator

The console stores no local content-policy mode or local content blocks. PINs
protect local profile entry/settings only. Server Library materialization is
the only evaluator of content policy.

The access endpoint uses ordinary OAuth `sub`, then reads the user's current
live `Users.LibraryId` and current valid materialized Library in one coherent
operation. It does not use a security-revision claim, token invalidation, or an
access revision. Atomic materialized-state/projection replacement remains.
Reassigning a user does not invalidate an issued token; the same token observes
the new live Library on its next request.

### Exact access endpoint

```text
POST /api/releases/{releaseId}/access
```

There is no request body. Successful responses are exactly:

```json
{
  "serverInstanceId": "<lowercase-uuid-D>",
  "releaseId": "<encoded-release-id>",
  "allowed": true,
  "titleId": "<encoded-title-id>"
}
```

```json
{
  "serverInstanceId": "<lowercase-uuid-D>",
  "releaseId": "<encoded-release-id>",
  "allowed": false
}
```

Fields are exact. `titleId` is required only when allowed and must be absent,
including not `null`, when revoked. A `200` is authoritative only after strict
shape, canonical instance/release/title, requested-release, selected-instance,
and selected-title validation. A server-instance mismatch changes nothing.

- `400`: malformed/noncanonical release id;
- `401`: missing, invalid, or expired ordinary OAuth authentication;
- `409`: no assigned Library, invalid configuration, or no current valid
  materialized Library;
- `200 allowed:false`: the current Library does not own and expose the release;
- redirect, timeout, transport, malformed response, other 4xx, and 5xx:
  non-authoritative failure.

On `401`, the caller performs one normal refresh-token restoration for the
selected profile+instance and retries once. Unresolved `401` and every other
non-authoritative result preserve the entire cached row, including
`isAuthorized` and `lastCheckedAt`. No failure becomes revocation.

The backend portion of this boundary is implemented. `LiveConsumerLibraryQuery`
resolves the authenticated user's current `Users.LibraryId`, validates the live
Library configuration/materialization state, and reads the materialized
projection in one serializable SQLite transaction. Its closed result hierarchy
keeps `Found`, `ItemNotFound`, `LibraryUnavailable`, and
`ProjectionInconsistent` distinct. A missing projected release becomes an
explicit metadata-free revoke only inside a valid live Library snapshot;
unavailable, invalid, or inconsistent Library/projection state returns `409` and
does not masquerade as a revoke.

Current Library, Browse, Collections, BIOS delivery, manifest issuance, and
release access use this same live-assignment boundary. The server identity and
access endpoints are present in Consumer Host/OpenAPI, and the manifest contract
requires `serverInstanceId`.

Gate 1b-A adds canonical Console value types for the backend Sqids public ids and
lowercase UUID-D server instance ids, plus separate handwritten anonymous server-
discovery and strict single-release access adapters. Each request owns a fresh
credential-empty transport; discovery sends no authorization/cookies and does
not answer an authentication challenge, while access adds only the supplied
bearer token. Redirects are disabled, response shapes and expected identities
are exact, and timeout/close abort the affected operation.

Gate 1b-B adds a strict `ReleaseManifestApiClient` that returns an immutable
server-bound manifest only after exact server/release/title/platform/short-name
identity and whole-response validation. It closes the supported semantic
vocabulary and cardinality, validates availability/hash/grant and total-byte
coherence, requires launch to name an item, and rejects the entire response on
any malformed nested value.

Manifest paths are checked against the backend's portable path vocabulary. The
backend producer sanitizes unsafe source names; the Console rejects unsafe
relative paths, exact duplicates, portable case-insensitive collisions, and
file/directory collisions. Discovery, access, and manifest adapters share a
fresh per-operation credential-empty HTTP lifecycle with operation-local abort.

Gate 1b-C connects anonymous discovery to credential bootstrap without treating
origin as authority. The exact version-10 origin key is delete-only; current
secure-storage keys are versioned v2 profile+canonical-instance keys. One
serialized, non-poisoning credential queue orders legacy cleanup and current
token reads/writes/deletes. Bootstrap order is exactly cleanup → anonymous
discovery → matching instance-token lookup.

Root composition creates a fresh origin-bound `ConsumerApiClient` and aligned
`PlayServices` graph for each active origin. In-memory early operation epochs,
profile-origin generations, Connect single-flight, and current-operation checks
suppress stale async results. Cancel, unchanged-origin, prompt/update/remove/
attach failure, and pending-restore recovery return to honest retryable state;
local-only selection performs no credential work.

At Gate 1b closure, access and manifest adapters were not connected to profile
access persistence, acquisition, or launch authorization. Gate 2 has since
connected them to acquisition and profile/install persistence as described
below, and Gate 3 has since connected the central launch path to the existing
cached rows. Gate 4 has since connected exact completed launches to profile-owned
history. Gates 5-6 launcher UX/PIN remain pending. None of these accepted gates
is a launcher UI redesign, and their acceptance does not claim GUI validation.

### Acquisition is the only row-creation path

The accepted Gate 2 download/attach path captures profile, selected
`instanceId`, verified origin, `serverSelectionGeneration`, title, and release,
then:

1. receives a strict allowed response matching instance/release/title;
2. requests `ConsumerReleaseManifestDto` from the same verified instance
   connection; its exact required `serverInstanceId`, release, title, and
   platform identity fields must match the captured selection and access result;
3. writes/reuses files only in that instance's content namespace;
4. fully verifies an install keyed by the same instance and release; and
5. after rechecking selection/generation, commits the `ProfileLocalGame` row.

Any identity mismatch, failure, cancellation, or stale generation creates no
row and changes no cached authorization. A crash after files arrive leaves a
hidden orphan. Shared files never grant another profile a row; attach repeats
the online allow, manifest, and full verification.

Before any staging write, acquisition normalizes candidate destination paths
using the actual target filesystem and rejects NFC/NFD, case, exact, and
file/directory collisions. Gate 1b-B deliberately does not equate Unicode
normalization forms; its portable parser is not evidence that target-filesystem
path equivalence is safe. Acquisition tests own this preflight invariant.

Version-10 installs have no trustworthy instance identity. Schema v11 leaves
the release-only install table untouched while adding the connection/grant
foundation. Schema v12 renames that exact latest pre-cutover table—unchanged
from v10 through v11—to `LegacyLocalInstalls`, preserving all columns, values,
paths, manifest snapshots, and timestamps. It then creates an empty active
instance+release `LocalInstalls`; no legacy row becomes active or grants access.

Adoption requires discovery, fresh OAuth, explicit allow, and exact selected/
access/manifest instance-release-title-platform identity. Files are copied or
materialized into instance-namespaced staging and fully verified. One transaction
persists the new `LocalInstall` and initiating `ProfileLocalGame` last. Only
after commit may old files and the legacy row be deleted. Never destructively
move old files before database commit.

Adoption never follows the stored legacy content path. It reads only the
independently derived canonical v10-v11 content root, and only matching installed/
permanent legacy evidence may enter staging. Retry cleanup removes a legacy
duplicate only after the active install is canonical, snapshots/fingerprints
match, and every legacy file passes size/hash proof.

A crash before the transaction leaves hidden legacy/staging cleanup. A crash
after commit leaves a playable new install plus a hidden legacy duplicate;
restart idempotently cleans it. Cleanup failure retains the hidden legacy row for
retry. Legacy credentials are never read, sent, or rekeyed; replacement and
honest same-origin servers both require fresh authentication.

### Accepted Gate 3 launch updates existing rows only

Presentation passes only an unresolved `PlayRequest` with release/title/display
identity and an optional runtime selection. The root-composed play graph owns
profile, selected instance/origin/generation, and credentials. The coordinator,
not presentation, invokes the authorizer and then the coordinator-only installed-
target resolver.

Every launch reaches that authorizer before file probing, install mutation,
runtime/config/process/history side effects. One coherent local snapshot binds
the exact selected profile/server/origin/generation, `ProfileLocalGame`, and
strict installed-row identity/fingerprint. Missing grants do not trigger an
online request; absent/corrupt/mismatched installs or authority are unavailable
or not granted without probing files.

An exact instance-scoped install and existing matching `ProfileLocalGame` are
required. Authorized and unauthorized rows may reach the online check. Launch
snapshots pre-check `authorizationState`:

- strict matching allow compare-and-sets `authorized`, updates `lastCheckedAt`,
  preserves `acquiredAt`, title, history, saves, and states, and permits
  launch only when the exact snapshot write wins;
- strict matching revoke blocks the current attempt and attempts to compare-and-
  set `revoked`/`lastCheckedAt`; a successful write blocks later offline play;
- `401` receives one ordinary exact-profile/instance refresh/retry;
- unresolved/non-authoritative failure re-reads the local snapshot and falls
  back only if the complete grant/install snapshot is unchanged and authorized;
  unchanged cached revoke blocks, while changed/inconsistent state is
  unavailable; and
- no row or profile/instance/generation/release/title mismatch blocks without
  any write.

Launch never creates/adopts a row. An allow from another or replacement instance
cannot mount former rows. Offline unauthorized rows remain blocked. Active
gameplay is not terminated. There is no batch/background access refresh; delayed
offline revocation is accepted.

Only a matching authorization permit may reach the installed resolver, and its
resolved server/profile/release/title must match again before runtime preference,
dependency, config, process, or play-marking work. Denial therefore has zero
installed-resolution/file-probe, runtime/provisioning/config/process, preference-
write, or history/play-marking side effects.

The active launch-session handle is app-scoped across profile/server play-graph
replacement. A running emulator remains supervised and prevents a second
process; later revocation does not terminate it.

### Accepted Gate 4 profile-owned history

Schema v13 adds `ProfilePlayHistories`, keyed by local profile + server instance
+ title, with the exact last release, last completed timestamp, and play count.
Every supported v8-v12 upgrade creates it empty. Migration never attributes the
device-wide `LocalInstalls.lastPlayedAt` value to any profile or invents history.

The coordinator records history only when a session returned `LaunchStarted`
and that same started session later completes as `LaunchExited`. Exit code zero
and nonzero both count as completed launches. Denied, cancelled, failed, or
`LaunchNotStarted` outcomes do not count, including a fabricated
`LaunchNotStarted(LaunchExited)` result. The record uses the exact resolved
profile, server, title, and release captured by the launch, even if the active
profile/server graph changes before the process exits.

Recent history is read through the exact selected profile + server partition and
intersected with matching authorized profile games and installed releases.
Revocation or install removal hides a row without deleting it; restored access
restores its ordering. When the last-played release is ineligible but another
authorized installed release for that title exists, selection is deterministic:
newest profile-game acquisition, then newest install time, then release id
ascending. A valid authorized candidate with a missing/non-installed install is
omitted without invalidating otherwise valid recent history. Malformed or
cross-authority joined data fails closed.

### Accepted history projection and planned Gate 5 destinations

```text
Home = selected-instance profile history
       intersect selected-instance installs
       intersect selected-instance ProfileLocalGames where isAuthorized

Local Library = selected-instance installs
                intersect selected-instance ProfileLocalGames

Store = selected instance/current account live Library
```

No selected instance means empty/unavailable Home and Local Library with a clear
server-selection action. Unauthorized rows are hidden from Home and retained in
Local Library as `Access required`. First-time revoke creates no row or denied
metadata. Restore can occur through strict online launch or verified Store
attach without deleting history/saves.

### Instance-safe local identity and cleanup

Server-local ids must never collide across instances:

- `ProfileLocalGames`: profile + instance + release;
- `LocalInstalls`: instance + release;
- play history: profile + instance + title;
- content: `content/servers/{instanceId}/{platform}/{title}/{release}`;
- saves/states:
  `profiles/{profileId}/servers/{instanceId}/games/{platform}/{title}/...`; and
- generated per-title config:
  `config/servers/{instanceId}/{platform}/{title}`.

Gate 2 removal deletes only that profile's exact instance/release row. Saves,
states, and history remain. Physical cleanup counts every authorized or
unauthorized `ProfileLocalGame` for the exact instance/release across profiles;
any row prevents deletion. Cleanup serializes on instance+release against
creation, attach/adoption, removal, and cleanup, then rechecks zero rows before
deletion.

The serializer is one app-scoped FIFO keyed by exact instance+release and also
covers installed-target resolution/corrupt marking and profile deletion. Startup
cleanup scans active rows and canonical instance content roots, removes only
unreferenced proven artifacts, and leaves ambiguous/corrupt evidence intact.
Profile deletion first deletes every enumerable profile+instance refresh token
and exact known legacy-origin key; credential deletion failure preserves the
profile and its references.

## Accepted Persistence

```text
ServerConnections
  instanceId             PRIMARY KEY
  lastKnownOrigin
  firstSeenAt
  lastSeenAt

PendingServerLocators
  localProfileId         PRIMARY KEY, FOREIGN KEY -> LocalProfiles ON DELETE CASCADE
  normalizedOrigin
  createdAt
  lastAttemptAt?

LocalProfiles
  selectedServerInstanceId?  FOREIGN KEY -> ServerConnections
  serverSelectionGeneration  NOT NULL DEFAULT 0

ProfileLocalGames
  localProfileId         FOREIGN KEY -> LocalProfiles ON DELETE CASCADE
  serverInstanceId       FOREIGN KEY -> ServerConnections
  releaseId
  titleId
  authorizationState    exact `authorized` or `revoked`
  acquiredAt
  lastCheckedAt
  PRIMARY KEY (localProfileId, serverInstanceId, releaseId)
  INDEX (serverInstanceId, releaseId)

LocalInstalls
  serverInstanceId
  releaseId
  ...
  PRIMARY KEY (serverInstanceId, releaseId)

LegacyLocalInstalls
  exact latest pre-cutover release-only LocalInstalls table (v10-v11)
  releaseId              PRIMARY KEY
  original contentRoot/path/manifest snapshot/timestamps retained
  never projected or played

RomdAccountLinks
  serverInstanceId?      nullable selected-instance binding

ProfilePlayHistories
  localProfileId         FOREIGN KEY -> LocalProfiles ON DELETE CASCADE
  serverInstanceId       FOREIGN KEY -> ServerConnections
  titleId
  lastReleaseId
  lastCompletedAt
  playCount              CHECK >= 1
  PRIMARY KEY (localProfileId, serverInstanceId, titleId)
```

Schema v13 adds `ProfilePlayHistories`. Every supported v8-v12 upgrade starts
that table empty and does not copy device-wide install timestamps into profile
history.

Strict database/repository parsing rejects noncanonical instance/public ids,
invalid booleans/timestamps, and cross-instance identity mismatches.
`ProfileLocalGames` intentionally has no install foreign key.

Migration uses schema v11 then v12. Each v10 profile origin becomes a
`PendingServerLocators` row; no `ServerConnections` identity is fabricated. It
creates no profile local games/history. Every old install is renamed exactly to
hidden `LegacyLocalInstalls`; the new active `LocalInstalls` requires nonnull
instance+release. Profiles, origins, account links, controller/runtime rows,
saves, and unrelated state survive.

Secure-storage initialization separately performs the mandatory delete-only
legacy credential cleanup before network auth. It never migrates/rekeys an
origin-keyed secret.

## Planned Optional PIN

PINs protect local profile entry/settings only, with Add/Change/Remove flows.
Secrets/verifier/throttling/recovery binding remain in secure storage. Enrollment
and reset require successful fresh ordinary OAuth authentication over the
configured HTTPS origin, a discovered instance matching the selected/enrolled
instance id, and live ROMD Admin authorization. UUID match alone proves nothing;
an honest same-origin replacement presenting a different id receives no former
credentials and cannot reset the PIN. Copied-id active impersonation remains
outside this namespace-only boundary.

## Acceptance

- Identity creation is split-host safe, stable across restart/move/restore,
  fails on corrupt files, and is rotated for independent clones.
- Discovery is anonymous, exact, nonredirecting, and precedes credential access.
- Legacy credentials are deleted before auth enablement and are never read,
  transmitted, or rekeyed; cleanup failure remains fail-closed across restart.
- No selected instance means no ROMD Home/Local Library.
- Sign-out preserves selection and cached local play.
- Instance switching hides/restores rows without deletion; generation blocks
  switch-away/back ABA.
- Same-instance origin move preserves state; same-origin replacement receives no
  token and mounts no former state.
- Same-origin account/Library changes on one instance update the same cached row;
  the same token observes live reassignment without invalidation/security claim.
- Every allowed/revoked response instance id is validated before write.
- Failures preserve cached authorization exactly; Gate 3 launch never creates a
  row.
- Instance collisions cannot cross installs/content/saves/states/config/history.
- Completed history belongs to the exact launch profile/server/title/release;
  revocation hides it and restored access reveals it without data loss.
- Legacy orphans require verified adoption/rekey.
- Both authorized and unauthorized rows prevent exact-instance install cleanup.
- Pending locators mount nothing; discovery success/failure follows the exact
  atomic lifecycle above.
- Legacy adoption is nondestructive before commit and crash-idempotent afterward.
- PIN recovery requires HTTPS/TLS, fresh OAuth/Admin proof, and matching response
  instance; UUID alone is not authentication.

## Deferred

- maximum offline age;
- proactive/realtime revocation;
- remote gameplay termination;
- simultaneous active credentials for multiple instances;
- personalized recommendations; and
- server-synced console play history.
