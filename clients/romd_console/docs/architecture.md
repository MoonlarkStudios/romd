# Ottercade Console Architecture

Ottercade is a Flutter Consumer Host client for couch-distance browsing,
local content, and emulator launching. This page describes the current
baseline; source and tests are authoritative where they differ. The launcher
and parental PIN experiences described in the
[profile access decision](decisions/profile-access-and-offline-grants.md)
are not shipped yet.

## Boundaries

ROMD server Library materialization owns content policy. Console owns 10-foot
navigation, selected server connection, local game/install state, verification,
offline launch, instance-scoped history/saves/config, and process supervision.
Admin curation and storage authorization stay outside the app.

## Profiles And Servers At A Glance

- Profiles are local. A profile can enter the launcher without any server
  connection; Home and the Local Library need a selected server instance and
  that profile's local acquisitions. Store uses the selected instance's live
  account Library.
- Local game ownership is keyed by **profile + stable server instance +
  release**. A verified Store download/attach creates the local acquisition.
  Installed content, saves, states, generated config and play history are all
  instance-scoped.
- Signing out keeps the selected instance and cached games. Switching instances
  changes the visible local library without deleting the previous instance's
  data. A server that moves origin keeps its instance namespace; a different
  server at the same origin receives no former token or local state.
- Discovery disables redirects and happens before any credential access. The
  public instance UUID is a namespace marker; HTTPS/TLS and OAuth provide
  authentication. Version-10 origin-keyed secrets are deleted without being
  read, sent or rekeyed, and a cleanup failure blocks network credentials until
  cleanup succeeds.
- SQLite uses the owner-approved fresh version-17 baseline. Unsupported existing
  databases are refused; release-only legacy rows are not adopted or migrated.

## Profiles, Servers, And Authentication

Two canonical value boundaries:

- `RomdPublicId` accepts one backend Sqids value only when it uses the exact
  alphabet/minimum length, decodes inside the signed 32-bit id range, and
  re-encodes byte-for-byte; and
- `RomdServerInstanceId` accepts canonical lowercase UUID-D only.

`ServerDiscoveryApiClient` anonymously reads the exact
`GET /api/server/identity` contract. `ReleaseAccessApiClient` issues the exact
bodyless `GET /api/releases/{releaseId}/access` request and strictly validates
allowed/revoked shape plus expected server, release, and allowed-title identity.
Both adapters disable redirects and return typed failure results rather than a
usable identity/decision after HTTP, transport, malformed, or mismatched input.

`ReleaseManifestApiClient` issues the bodyless authenticated manifest
request and returns one immutable `ServerBoundReleaseManifest` only after atomic
validation of the exact root/nested shapes and expected server, release, title,
platform, and platform-short-name identities. Supported semantics are closed to
`single_rom|unknown`, `direct_files`, `file`, and `rom|disk`, with explicit
item/launch cardinality, availability/hash/grant, int64 byte-total, timestamp,
content-grant URL, and launch-item coherence.

The backend producer sanitizes source names into a portable relative-path
vocabulary. The adapter rejects unsafe relative paths, exact duplicates,
portable case-insensitive collisions, and file/directory collisions. This is not
Unicode target-filesystem equivalence: NFC/NFD-distinct paths can both pass manifest
validation and must be rejected later when the actual destination filesystem considers
them equivalent.

All three adapters use `OwnedHttpOperation`: each operation creates a fresh
credential-empty `HttpClient`. Discovery removes authorization/cookie headers and
does not retry an authentication challenge; access and manifest add only the
caller-supplied bearer token. Timeout and adapter `close()` force-abort the exact
active request, including a request still opening, without cancelling an
unrelated overlapping operation.

Startup wires discovery into one fail-closed credential bootstrap sequence:
delete the exact version-10 origin-keyed secret without reading it, discover the
public stable server instance anonymously, then read only the v2 profile+
canonical-instance token partition. Cleanup failure stops discovery; discovery
failure stops token lookup.

`SecureRefreshTokenStore` uses the exact shipped origin key only for legacy
deletion. Current keys encode the profile component with unpadded base64url and
include the canonical instance UUID. `SerializedRefreshTokenStore` linearizes
all cleanup/read/write/replace/delete operations in invocation order; a failed
operation does not poison later work or another authority partition. A
rotated refresh token is stored through compare-and-set `replace` against the
token that was presented, even when its refresh outlived the authority (for
example a profile switch mid-refresh), because the server has already redeemed
the presented token. The compare-and-set never resurrects a deleted credential
or overwrites a newer sign-in. A stale authority or rejected replacement never
publishes a session; a rejected replacement also clears stale access state.
Sign-out revokes the stored token, which is never older than the published
session. Access tokens remain in memory.

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

Acquisition and profile/install persistence use the strict access/manifest
adapters, and launch applies the single-release access boundary before
installed resolution.

The UUID is a public non-authenticating namespace/collision boundary. Configured
HTTPS/TLS and ordinary OAuth authenticate transport/user; copied-id malicious
impersonation is outside scope without key/certificate pinning.

`ServerConnections` now remembers instance + last origin. Each profile selects
zero or one instance and owns a monotonic selection generation. No selection
means no ROMD Home or Local Library. Switching instances swaps remembered local
projections without deletion. Sign-out keeps selection and cached games.

Those selected-instance, pending-locator, and monotonic-generation records are
persisted in schema v11. The in-memory operation generations remain
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
| Server locator | instance id → last known normalized origin |
| Profile local game | profile + instance + release |
| Install/content | instance + release |
| Play history | profile + instance + title |
| Saves/states | profile + instance + title paths |
| Generated title config | instance + title path |

`ProfileLocalGame` stores title, strict `authorized|revoked` state, acquired
time, and last-checked time. Acquisition creates it only after an exact allowed
response, exact instance/title/release/platform manifest and platform identity,
target-filesystem collision preflight, and full install verification. Shared
attach repeats those proofs; shared/legacy files never grant by themselves.

The current Continue Playing data source is selected-profile/instance history ∩
installs ∩ authorized local games. Local Library is selected-instance installs ∩
all local-game rows, including `Access required`. Store is selected instance/
current account live Library. The complete destination restructuring is not
shipped yet.

## Access And Launch

The server's `GET /api/releases/{releaseId}/access` returns an exact boolean
response including server instance id. The handwritten adapter validates
that response and preserves `401` as a typed result. Acquisition owns one
ordinary profile+instance refresh/retry on `401`; other non-authoritative results
create no local grant. The server uses ordinary OAuth subject and the current
live user Library, without a security/access revision or token-invalidation
scheme. Launch uses a separate online-first authorizer over the same exact
endpoint and one ordinary `401` restoration.

The console validates the server-bound delivery/runtime manifest as an atomic
contract. Acquisition binds it to access, selected generation,
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

Authorization and play history wrap the existing runtime pipeline: they do not
change runtime dependency resolution or config writers. History consumes the
exact resolved launch identity after process exit.

## Persistence, Acquisition, And Cleanup

The owner approved one fresh SQLite baseline (version 17) on 2026-10-31,
with no version-16 upgrade path. This is a pre-1.0 exception to the normal
migration-aware persistence rule. Do not restore a migration chain for this
cutover. A different version,
or a nonempty unversioned database, is refused without modifying its data.
There is no legacy install quarantine, upgrade chain or adoption path.
`LocalInstalls` is keyed by instance + release, profile grants by profile +
instance + release, and play history by profile + instance + title. Device-wide
install timestamps never establish a person's play history.

Exact local play sessions use a profile/server-scoped sync preference and
durable outbox. Sync defaults off. Enabling it chooses future sessions only or
inclusion of stored sessions; disabling it cancels queued uploads. Deleting
local sessions hard-deletes their cascading outbox rows.

Every installed file persists its actual path, length, SHA-256 and `Original`
or `Derived` provenance in a grant-free snapshot. Provenance participates in
fingerprints and refresh comparisons. Delivery format is explicit; a delivered
PS2 CHD stores its physical file identity separately from the logical ISO
identity. Required `isDeliveryComplete` permits an original BIN plus generated
CUE to install while `isComplete` continues to describe original inventory.
A generated CUE uses its own signed descriptor grant and actual identity.
Grant renewal refuses changed generated-file bytes or provenance, including an
arriving original; a new install attempt selects the new manifest.

Release acquisition keeps planning, authority checks and verification in
`RomdInstallService`. `ReleaseAcquisitionCommitter` owns verified filesystem
publication, the transactional install/profile grant and restoration of the
previous files/projection when commit fails. Artwork retention runs only after
that transaction succeeds and cannot roll back verified game content.

Install resolves the runtime once with the launch path's resolver and rules,
then chooses the CHD only when the manifest's CHD entry is available and
coherent and the preferred runtime opens delivered CHD (the PCSX2 adapter);
otherwise, or when the resolver fails, it installs the Original. Reuse wins
over a swap: before anything is downloaded, a verified Original install for the release is kept even when PCSX2 is
preferred, while an existing verified CHD install is reused as before. An
unusable CHD entry never rejects the manifest. A failed artifact verification
fails the install without substituting another format, and a grant refresh
re-plans the same format.
Launch refuses a runtime that cannot open the installed format; removing the
game and installing it again selects the format for the runtime preferred
then.

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
- `data/local_profiles/app_database.dart`: the fresh baseline, unsupported-file refusal, and
  instance-scoped persistence.
- `play/content/data/romd_install_service.dart`: exact access/manifest/platform
  acquisition, shared attach, exact derived delivery, and final authority
  recheck.
- `play/content/data/install_mutation_serializer.dart` and
  `profile_game_cleanup_service.dart`: exact-key mutation ownership, reference
  removal, profile deletion, and startup orphan cleanup.
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

The normative contract is the
[profile access and offline grants decision](decisions/profile-access-and-offline-grants.md),
with the server side in the
[consumer runtime boundary](../../../docs/decisions/consumer-runtime-boundary.md).
