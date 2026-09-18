# Ottercade Console Architecture

Status: active baseline; Gate 1a backend plus Gate 1b-A identifier/discovery/
access, Gate 1b-B strict manifest, and Gate 1b-C credential/session composition
foundations are accepted as of 2026-07-17. Gate 1b is closed as this strict
contract/auth foundation only. Gate 2 persistence, instance-scoped acquisition,
adoption, reference removal, and cleanup are accepted as of 2026-07-18. Gate 3
central launch authorization is accepted as of 2026-07-18. Gate 4 profile
history is accepted as of 2026-07-18. The Gate 5/6 launcher/PIN experiences are
not shipped.

Ottercade is a Flutter Consumer Host client for couch-distance browsing,
local content, and emulator launching.

## Boundaries

ROMD server Library materialization owns content policy. Console owns 10-foot
navigation, selected server connection, local game/install state, verification,
offline launch, instance-scoped history/saves/config, and process supervision.
Admin curation and storage authorization stay outside the app.

## Profiles, Servers, And Authentication

Gate 1b-A provides two canonical value boundaries:

- `RomdPublicId` accepts one backend Sqids value only when it uses the exact
  alphabet/minimum length, decodes inside the signed 32-bit id range, and
  re-encodes byte-for-byte; and
- `RomdServerInstanceId` accepts canonical lowercase UUID-D only.

`ServerDiscoveryApiClient` anonymously reads the exact
`GET /api/server/identity` contract. `ReleaseAccessApiClient` issues the exact
bodyless `POST /api/releases/{releaseId}/access` request and strictly validates
allowed/revoked shape plus expected server, release, and allowed-title identity.
Both adapters disable redirects and return typed failure results rather than a
usable identity/decision after HTTP, transport, malformed, or mismatched input.

Gate 1b-B's `ReleaseManifestApiClient` issues the bodyless authenticated manifest
request and returns one immutable `ServerBoundReleaseManifest` only after atomic
validation of the exact root/nested shapes and expected server, release, title,
platform, and platform-short-name identities. Supported semantics are closed to
`single_rom|unknown`, `direct_files`, `file`, and `rom|disk`, with explicit
item/launch cardinality, availability/hash/grant, int64 byte-total, timestamp,
content-grant URL, and launch-item coherence.

The backend producer sanitizes source names into a portable relative-path
vocabulary. The adapter rejects unsafe relative paths, exact duplicates,
portable case-insensitive collisions, and file/directory collisions. This is not
Unicode target-filesystem equivalence: NFC/NFD-distinct paths can both pass Gate
1b-B and must be rejected later when the actual destination filesystem considers
them equivalent.

All three adapters use `OwnedHttpOperation`: each operation creates a fresh
credential-empty `HttpClient`. Discovery removes authorization/cookie headers and
does not retry an authentication challenge; access and manifest add only the
caller-supplied bearer token. Timeout and adapter `close()` force-abort the exact
active request, including a request still opening, without cancelling an
unrelated overlapping operation.

Gate 1b-C wires discovery into one fail-closed credential bootstrap sequence:
delete the exact version-10 origin-keyed secret without reading it, discover the
public stable server instance anonymously, then read only the v2 profile+
canonical-instance token partition. Cleanup failure stops discovery; discovery
failure stops token lookup.

`SecureRefreshTokenStore` uses the exact shipped origin key only for legacy
deletion. Current keys encode the profile component with unpadded base64url and
include the canonical instance UUID. `SerializedRefreshTokenStore` linearizes
all cleanup/read/write/delete operations in invocation order; a failed operation
does not poison later work or another authority partition. Access tokens remain
in memory.

Origin is still only a mutable locator; instance id is durable identity. Each
active origin gets a fresh root-owned `ConsumerApiClient` and `PlayServices`
graph sharing that exact origin/client and a live in-memory access-token provider.
Changing origin closes the former owned client graph.

The root uses in-memory early operation epochs, profile-origin generations,
Connect single-flight, and current-operation checks to suppress stale profile,
prompt, bootstrap, refresh, and repository results. Cancel, same-origin,
prompt/update/remove/local-attach failure, and pending silent-restore recovery
return to an honest retryable state instead of leaving Connecting stuck. A
local-only profile performs no credential cleanup, discovery, refresh, or device
authorization until Connect.

These were the Gate 1b credential/session composition foundations. Gate 2 now
wires the strict access/manifest adapters into acquisition and profile/install
persistence, and Gate 3 now applies the single-release access boundary before
installed resolution. None of these gates is a launcher UI redesign. Acceptance
does not claim GUI validation.

The UUID is a public non-authenticating namespace/collision boundary. Configured
HTTPS/TLS and ordinary OAuth authenticate transport/user; copied-id malicious
impersonation is outside scope without key/certificate pinning.

`ServerConnections` now remembers instance + last origin. Each profile selects
zero or one instance and owns a monotonic selection generation. No selection
means no ROMD Home or Local Library. Switching instances swaps remembered local
projections without deletion. Sign-out keeps selection and cached games.

Those selected-instance, pending-locator, and monotonic-generation records are
persisted in schema v11. Gate 1b-C's in-memory generations remain operation-
cancellation aids and do not replace the persisted ABA boundary.

Same instance at a new origin preserves local state but may require sign-in.
Different instance at the same origin is a replacement: former refresh tokens
are not read/sent and former rows are not mounted. Secure refresh tokens are
keyed by profile + instance; access tokens remain memory-only. Inactive
connections retain local rows, not simultaneous active sessions.

`PendingServerLocators` stores an unverified profile locator only and mounts no
Library. Discovery success atomically upserts/selects the instance, increments
generation, binds the account link to that instance when authentication
completes, and deletes pending; failure retains pending only. Version-10 origin-
keyed secrets are never read/sent/rekeyed. Initialization deletes them before
network auth; failure remains reauth-required/network-disabled across restart,
and success still requires fresh auth.

## Local Ownership

| State | Owner/key/status |
|---|---|
| Server locator | instance id → last known normalized origin; shipped Gate 2 |
| Profile local game | profile + instance + release; shipped Gate 2 |
| Install/content | instance + release; shipped Gate 2 |
| Play history | profile + instance + title; shipped Gate 4 |
| Saves/states | profile + instance + title paths; shipped Gate 2 |
| Generated title config | instance + title path; shipped Gate 2 |

`ProfileLocalGame` stores title, strict `authorized|revoked` state, acquired
time, and last-checked time. Acquisition creates it only after an exact allowed
response, exact instance/title/release/platform manifest and platform identity,
target-filesystem collision preflight, and full install verification. Shared
attach repeats those proofs; shared/legacy files never grant by themselves.

The current Continue Playing data source is selected-profile/instance history ∩
installs ∩ authorized local games. Local Library is selected-instance installs ∩
all local-game rows, including `Access required`. Store is selected instance/
current account live Library. The complete destination restructuring remains
Gate 5 work.

## Access And Launch

The server's `POST /api/releases/{releaseId}/access` returns an exact boolean
response including server instance id. Gate 1b-A's handwritten adapter validates
that response and preserves `401` as a typed result. Gate 2 acquisition owns one
ordinary profile+instance refresh/retry on `401`; other non-authoritative results
create no local grant. The server uses ordinary OAuth subject and the current
live user Library, without a security/access revision or token-invalidation
scheme. Gate 3 launch uses a separate online-first authorizer over the same exact
endpoint and one ordinary `401` restoration.

Gate 1b-B validates the server-bound delivery/runtime manifest as an atomic
contract. Gate 2 acquisition now binds it to access, selected generation,
canonical platform identity, target-filesystem preflight, and verified content;
the manifest still does not authorize play by itself.

Presentation creates an unresolved `PlayRequest` with release/title/display
identity and an optional runtime choice. Authority is captured by the composed
play graph, not supplied by a route. The implemented launch flow is:

```text
unresolved PlayRequest
 -> coordinator-owned authorizer reads exact authority/grant/install snapshot
 -> one-release online access may compare-and-set that grant
 -> failure may use only the unchanged exact cached snapshot
 -> coordinator-only installed resolver probes/verifies after authorization
 -> runtime/config is resolved in instance namespace
 -> process launches with profile+instance save/state roots
 -> a completed started session records its exact profile+instance+title+release
```

No row is not granted. Cached or newly learned revoke blocks. A strict allow
must win a compare-and-set against the original snapshot; a strict revoke blocks
the current attempt even when its persistence cannot safely win. After an
awaited non-authoritative failure, fallback re-reads and requires authority,
grant timestamps/state, and strict installed identity/fingerprint to be
unchanged. Launch never creates/adopts a local-game row.

The coordinator holds the app-scoped instance+release mutation lease across
authorization and installed resolution, and rechecks the resolved server/profile/
release/title against the permit and request. Denial precedes installed file
probing, runtime selection/preference writes, dependency provisioning, config/
process work, and play marking. `ActiveLaunchSession` is app-scoped across play-
graph replacement so a running emulator stays supervised and excludes another
process. Learned revocation does not terminate active gameplay.

Runtime boundary report:

```text
artifact resolver: no - authorization/acquisition reorders existing content resolution; runtime dependency resolution did not change
config writer: no - existing roots/config writers unchanged
```

Gate 4 runtime boundary report:

```text
artifact resolver: no - history consumes the exact resolved launch identity after process exit; runtime dependency resolution did not change
config writer: no - existing roots/config writers unchanged
```

## Persistence, Acquisition, And Cleanup

Schema v11 additively introduced `ServerConnections`,
`PendingServerLocators`, selected instance/generation, nullable account-link
instance binding, and `ProfileLocalGames` while leaving the release-only install
table untouched. Schema v12 renamed that exact v10-v11 table to never-projected
`LegacyLocalInstalls`, then created the active `LocalInstalls` keyed by
instance+release. Neither migration invents selection, grants, active installs,
or history. Schema v13 adds `ProfilePlayHistories`, keyed by profile + instance +
title, with `lastReleaseId`, `lastPlayedAt`, and `playCount`. Every supported
v8-v12 upgrade creates it empty; device-wide install `lastPlayedAt` is never
attributed to a profile.

Schema v14 renames the legacy aggregate column `last_completed_at` to
`last_played_at` and adds exact local play sessions plus a profile/server-scoped
sync preference and durable outbox. Sync defaults off. Enabling it explicitly
chooses future sessions only or inclusion of stored sessions; disabling it
cancels queued uploads. Deleting local sessions hard-deletes their cascading
outbox rows.

History is written only after a `LaunchStarted` session reports
`LaunchExited`; zero and nonzero exit codes both count. Denied, cancelled,
failed, and `LaunchNotStarted` outcomes do not. The exact resolved launch
profile/server/title/release owns the row even if the active composition changes
while the emulator runs. Recent projection is strictly profile/server scoped;
revocation hides without deleting, restore reveals again, and an unavailable
last release falls back deterministically by newest acquisition, newest install,
then release id ascending. A missing/non-installed candidate is omitted without
discarding other valid recents. Home/detail no longer orders from or writes
device-wide `LocalInstalls.lastPlayedAt`.

Acquisition requires fresh exact access/manifest/platform proof, exercises every
candidate path on the target filesystem to reject case/Unicode/file-directory
collisions, stages in the selected instance namespace, verifies all files, then
atomically commits the active install and initiating profile grant after a final
selection-generation check. Attach repeats the same proof. Legacy adoption reads
only the derived canonical v10-v11 root, copies nondestructively into staging,
and verifies against the current manifest.

One app-scoped FIFO mutation serializer is keyed by exact instance+release and
coordinates acquisition, attach/adoption, resolution/corrupt marking, profile-
reference removal, profile deletion, and cleanup. Both authorized and revoked
profile rows count as references. Startup scans active rows and canonical
instance content for orphans; legacy deletion additionally requires matching
active/legacy snapshots and fingerprints plus verified legacy files. Ambiguous
or failed cleanup remains retryable.

Profile deletion removes every enumerable v2 profile+instance credential and
known exact legacy-origin secret before deleting profile-owned references. A
credential deletion failure leaves the profile intact. Content, saves, states,
and generated config include instance UUID in their paths to prevent cross-
server encoded-id collisions.

## Source Layout

- `app/`: shell/theme/composition.
- `config/`: runtime configuration.
- `data/server_discovery_api_client.dart`: anonymous exact server identity
  adapter.
- `data/release_access_api_client.dart`: strict single-release access adapter.
- `data/release_manifest_api_client.dart`: strict server-bound manifest adapter.
- `data/owned_http_operation.dart`: shared per-request HTTP ownership and abort
  lifecycle.
- `data/profile_credential_bootstrap.dart`: ordered delete-only legacy cleanup,
  anonymous discovery, and instance-token selection.
- `data/refresh_token_store.dart`: v2 profile+instance secure keys and serialized
  credential operation ownership.
- `data/local_profiles/profile_server_connection_repository.dart`: strict
  pending/selected connection and persisted generation transactions.
- `data/local_profiles/profile_local_game_repository.dart`: strict profile+
  instance+release grant reads, updates, and reference counts.
- `data/local_profiles/drift_profile_play_history_repository.dart`: exact
  profile+instance history writes and eligible recent-game projection.
- `data/local_profiles/app_database.dart`: schema v11-v13 migrations and current
  instance-scoped persistence.
- `play/content/data/romd_install_service.dart`: exact access/manifest/platform
  acquisition, shared attach, canonical legacy adoption, and final authority
  recheck.
- `play/content/data/install_mutation_serializer.dart` and
  `profile_game_cleanup_service.dart`: exact-key mutation ownership, reference
  removal, profile deletion, startup orphan cleanup, and proof-heavy legacy
  cleanup.
- `play/session/data/launch_authorization_store.dart`: coherent exact authority,
  grant, and installed-identity snapshot plus compare-and-set writes.
- `play/session/data/online_first_launch_authorizer.dart`: one-release online
  check, one `401` restore, explicit revoke, and unchanged-snapshot fallback.
- `play/session/data/romd_play_coordinator.dart`: authorization-before-resolution
  enforcement, resolved-target identity recheck, app-scoped launch-session
  reservation, and exact completed-session history ownership.
- `play/content/domain/installed_play_target_resolver.dart`: coordinator-only
  installed-content resolution seam.
- `domain/romd_public_id.dart` and `domain/romd_server_instance_id.dart`:
  canonical backend identifier value types.
- `domain/release_access.dart`: typed access decisions and failures.
- `domain/server_bound_release_manifest.dart`: immutable validated manifest,
  runtime, launch, item, grant, result, and failure types.
- `domain/`: remaining launch, cache, Store, and server-selection models.
- `app/romd_console_app.dart` and `presentation/console_root_screen.dart`:
  origin-bound client/service composition and in-memory stale-operation guards.
- `presentation/`: controller-first screens/widgets.

The accepted plan is in `docs/console-profile-home-library-plan.md`; the
normative decision is
`docs/decisions/console-profile-access-and-offline-grants.md`. Replace planned
language only as reviewed implementation gates land.
