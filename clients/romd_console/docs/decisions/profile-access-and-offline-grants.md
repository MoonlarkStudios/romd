# Console Profile Access And Offline Grants

Related decisions: [consumer runtime boundary](../../../../docs/decisions/consumer-runtime-boundary.md)
(server identity, the access endpoint and the manifest contract),
[original disc sets](../../../../docs/decisions/original-disc-sets.md),
[Flutter client](flutter-client.md). Current design is described in the
[console architecture](../architecture.md).

## Context

The console installs content locally, while the selected server's
materialized Library remains the only evaluator of ownership, exposure,
platform, title, genre and content-rating policy. Several local profiles share
one device. A profile begins with no ROMD games and plays offline from what it
acquired online.

A normalized URL is not a safe identity: a different ROMD installation can
later occupy the same origin, and the same installation can move to a new one.
Durable local access and data therefore bind to the stable server instance id,
and the origin is only a mutable locator. Couch devices lose their network
often, and losing it must neither revoke a legitimate grant nor turn into
unauthorized play.

## Terms

- **Server connection:** a discovered instance id and its most recently
  verified origin locator.
- **Selected server:** the one `ServerConnections.instanceId` a local profile
  has selected.
- **Profile local game:** durable evidence that the profile completed an online,
  allowed and verified Store download or attach from that instance. It is not a
  content grant; content grants remain short-lived delivery URLs.
- **Authorized:** the exact instance-scoped install may launch offline.
- **Unauthorized:** an existing row explicitly updated by a successful revoke.
  It stays in the Local Library as `Access required` and blocks offline play.

## Decisions

### Server Selection

- **One selected instance per profile.** With no selected instance, a profile
  has no ROMD Home games and no Local Library, and Store shows the
  server-selection action.
- **Switching swaps, never deletes.** Selecting an instance increments the
  profile's selection generation and immediately swaps Home and the Local
  Library to that instance's rows, without deleting other instances' state.
  Switching back restores them.
- **A pending locator is not identity.** An unverified origin is held as a
  pending locator. It mounts no Library and permits no credential lookup or
  transmission. Only the selected instance mounts. Successful discovery
  atomically records the connection, selects it, increments the generation and
  deletes the pending locator. Failure records the attempt and changes no
  selection.
- **Async work captures its authority.** Operations capture the profile,
  selected instance and generation, so stale work cannot win after a switch
  away and back.
- **Discovery decides continuity:**
  - same instance at a new origin: update the locator, keep all
    instance-scoped state, and authenticate again if required
  - a different instance at the same origin: a replacement. No former refresh
    token is read or sent, no former rows mount, and the user selects and
    authenticates explicitly.
  - same instance and origin: an ordinary reconnect

### Credentials

- **Keyed by profile and instance.** Refresh tokens are keyed by local profile
  and server instance id. The origin is never a credential or cached-access
  partition key.
- **One active session.** Only the selected connection holds active session
  state. Inactive instances keep their local rows and files, not simultaneous
  active credentials.
- **Sign-out keeps local play.** Sign-out clears the active session and
  credentials, but keeps the selection and cached local games usable.
- **Discovery sends nothing.** Discovery uses the configured HTTPS origin with
  redirects disabled. It sends no authorization or cookies, answers no
  authentication challenge, and validates identity before any credential is
  read, refreshed or sent. Access requests add only the bearer token. Response
  shapes and identities are exact.
- **Ordered credential work.** One serialized credential queue orders legacy
  cleanup and token reads, writes and deletes. Bootstrap runs cleanup, then
  anonymous discovery, then the matching instance-token lookup.
- **A rotation outlives its refresh.** A rotated refresh token replaces the
  presented one by compare-and-set in that queue, even after its authority went
  stale, because the server has already redeemed the presented token. The
  compare-and-set keeps a late rotation from resurrecting a deleted credential
  or overwriting a newer sign-in, and a stale refresh never publishes a session.
- **Legacy secrets are delete-only.** On first credential initialization after
  migration, every origin-keyed secret for the profile is deleted before
  network authentication is enabled, and fresh OAuth follows discovery. A
  deletion failure leaves the profile reauthentication-required, with network
  credentials disabled and a retry action. It never falls back to reading the
  legacy secret, and restart repeats the fail-closed state.
- **Profile deletion clears credentials first.** Deleting a profile first
  deletes every profile and instance refresh token and every known legacy key.
  A credential deletion failure keeps the profile and its references.

### No Local Content Policy

The console stores no content-policy mode or local content blocks. The server's
Library materialization is the only evaluator of content policy.

### Acquisition Is The Only Row-Creation Path

- **Five steps, in order.** A download or attach captures the profile, selected
  instance, verified origin, selection generation, title and release, then:
  1. receives a strict allowed response matching the instance, release and
     title
  2. requests the release manifest from the same verified instance connection.
     Its `serverInstanceId`, release, title and platform must match the
     captured selection and the access result.
  3. writes or reuses files only in that instance's content namespace
  4. fully verifies an install keyed by the same instance and release
  5. rechecks selection and generation, then commits the profile local game
- **Any failure creates nothing.** An identity mismatch, failure, cancellation
  or stale generation creates no row and changes no cached authorization. A
  crash after files arrive leaves a hidden orphan.
- **Shared files grant nothing.** Files shared with another profile never give
  it a row. Attaching repeats the online allow, the manifest and full
  verification.
- **The manifest is validated whole.** The manifest client returns an
  immutable, server-bound manifest only after exact identity and whole-response
  validation:
  - closed vocabulary and cardinality
  - coherent availability, hashes, grants and total bytes
  - a launch target that names an item

  Any malformed nested value rejects the entire response.
- **Paths are checked before staging.**
  - Manifest paths must fit the portable path vocabulary. Unsafe relative
    paths, exact duplicates, case-insensitive collisions and file/directory
    collisions are rejected.
  - Destination paths are normalized against the actual target filesystem
    before any staging write. NFC/NFD, case, exact and file/directory
    collisions are rejected there too.

### Launch Updates Existing Rows Only

- **The play graph owns authority.** Presentation supplies only an unresolved
  `PlayRequest`; the root-composed play graph owns the profile, the selected
  instance, origin and generation, and the credentials. `RomdPlayCoordinator`
  invokes its authorizer before the coordinator-only installed resolver.
- **Authorize before anything else.** Every launch reaches the authorizer before
  any file probe, install mutation, or runtime, configuration, process or
  history side effect. One coherent local snapshot binds the exact selected
  profile, server, origin and generation, the profile local game, and the
  strict installed-row identity and fingerprint.
- **Missing state stays offline.** A missing grant triggers no online request.
  An absent, corrupt or mismatched install or authority is unavailable or not
  granted, without probing files. An exact instance-scoped install and an
  existing matching profile local game are required, and both authorized and
  unauthorized rows reach the online check.
- **The online check:**
  - A strict matching allow compare-and-sets the row to authorized and updates
    its last check. It keeps the acquisition time, title, history, saves and
    states, and permits launch only when that exact snapshot write wins.
  - A strict matching revoke blocks the current attempt and compare-and-sets
    the row to revoked. A successful write blocks later offline play.
  - A `401` gets one ordinary refresh and retry for the exact profile and
    instance.
  - A non-authoritative failure re-reads the snapshot. It falls back to cached
    authorization only if the complete grant and install snapshot is unchanged
    and authorized. An unchanged cached revoke blocks, and changed or
    inconsistent state is unavailable.
  - No row, or any profile, instance, generation, release or title mismatch,
    blocks without a write.
- **Launch never creates.** Launch never creates or adopts a row. An allow from
  another or a replacement instance cannot mount former rows, and offline
  unauthorized rows stay blocked.
- **Denial has no side effects.** Only a matching permit reaches the installed
  resolver, and its resolved server, profile, release and title must match
  again before runtime preference, dependency, configuration, process or
  play-marking work.
- **One running session.** The app-scoped `ActiveLaunchSession` keeps
  supervising a running emulator across profile and server graph replacement,
  and prevents a second process. A later revocation does not terminate it.
- **No background refresh.** There is no batch or background access refresh,
  and delayed offline revocation is accepted.

### Profile-Owned History

- History is keyed by local profile, server instance and title, with the exact
  last release, last completed time and play count.
- A completed launch is a session that returned `LaunchStarted` and later
  completed as `LaunchExited`, with any exit code. Denied, cancelled, failed and
  `LaunchNotStarted` outcomes never count, including a fabricated
  `LaunchNotStarted(LaunchExited)`. The record uses the exact profile, server,
  title and release the launch captured, even if the active graph changed
  before the process exited.
- Recent history reads the exact selected profile and server partition,
  intersected with matching authorized profile games and installed releases.
  Revocation or install removal hides a row without deleting it, and restored
  access restores its place.
- When the last-played release is ineligible but another authorized installed
  release of the title exists, selection is deterministic: newest profile-game
  acquisition, then newest install time, then release id ascending. An
  authorized candidate whose install is missing is omitted without invalidating
  valid history, and malformed or cross-authority joined data fails closed.

### Projections

```text
Home          = selected-instance profile history
                ∩ selected-instance installs
                ∩ selected-instance profile local games where authorized
Local Library = selected-instance installs ∩ selected-instance profile local games
Store         = the selected instance's live Library for the current account
```

- Without a selected instance, Home and the Local Library are empty or
  unavailable, with a clear server-selection action.
- Unauthorized rows are hidden from Home and kept in the Local Library as
  `Access required`. A first-time revoke creates no row and no denied metadata.
- Access is restored through a strict online launch or a verified Store
  attach, without deleting history or saves.

### Instance-Safe Local Identity And Cleanup

- **Server-local ids never collide across instances:**
  - profile local games: profile, instance and release
  - local installs: instance and release
  - play history: profile, instance and title
  - content: `content/servers/{instanceId}/{platform}/{title}/{release}`
  - saves and states:
    `profiles/{profileId}/servers/{instanceId}/games/{platform}/{title}/...`
  - generated per-title configuration:
    `config/servers/{instanceId}/{platform}/{title}`
- **Removal is exact.** Removing a game deletes only that profile's exact
  instance and release row. Saves, states and history remain.
- **Cleanup counts every profile.** Physical cleanup counts every authorized or
  unauthorized profile local game for the exact instance and release across
  profiles; any row prevents deletion.
- **One serializer.** One app-scoped FIFO, keyed by exact instance and release,
  serializes creation, attach, adoption, removal, cleanup, installed-target
  resolution, corrupt marking and profile deletion. Cleanup rechecks zero rows
  before deleting.
- **Startup cleanup is conservative.** It scans active rows and canonical
  instance content roots, removes only unreferenced, proven artifacts, and
  leaves ambiguous or corrupt evidence intact.

### Persisted Keys

- `ServerConnections` is keyed by instance id. Each profile has at most one
  `PendingServerLocators` row, deleted with the profile. `LocalProfiles`
  carries the optional `selectedServerInstanceId` and the
  `serverSelectionGeneration`.
- `ProfileLocalGames` are keyed by profile, instance and release. Their
  `authorizationState` is exactly `authorized` or `revoked`, and they
  intentionally have no install foreign key.
- `LocalInstalls` are keyed by instance and release. `ProfilePlayHistories` are
  keyed by profile, instance and title, with a play count of at least one.
  `RomdAccountLinks` carry an optional selected-instance binding.
- Strict database and repository parsing rejects noncanonical instance and
  public ids, invalid booleans and timestamps, and cross-instance identity
  mismatches.

### Fresh Baseline And Derived Files

- Open only the supported fresh database baseline. Refuse unsupported versions
  and nonempty unversioned files without modifying them; provide no upgrade,
  quarantine or legacy adoption path.
- Persist every file's actual path, size, digest and provenance, without grants.
  `Derived` bytes never become evidence of a preserved catalog original.
- Require delivery completeness independently of original inventory completeness.
  A generated CUE and original BINs may form a complete delivered file set.
- Bind generated descriptors to their dedicated signed route. On renewal, keep
  the captured file identities and provenance or refuse the changed plan.

### Optional PIN

- A PIN protects local profile entry and settings only, with Add, Change and
  Remove flows. Its secret, verifier, throttling and recovery binding live in
  secure storage.
- Enrollment and reset require a successful fresh ordinary OAuth
  authentication over the configured HTTPS origin, a discovered instance that
  matches the selected and enrolled instance, and live ROMD Admin
  authorization.
- A matching UUID alone proves nothing. An honest replacement at the same origin
  that presents a different id receives no former credentials and cannot reset
  the PIN. Active impersonation with a copied id stays outside this
  namespace-only boundary.

## Trade-offs

- **Launch checks online when it can.** Each launch makes one access request,
  and offline launch relies on cached authorization proven against an unchanged
  snapshot.
- **Revocation can lag offline.** Offline authorization has no maximum age, so a
  revocation takes effect at the next successful online check.
- **One active account.** Only the selected server holds an active session, so
  switching servers can require signing in again.
- **Serialized storage work.** Acquisition, removal and cleanup for one install
  wait on one another.

## Rejected Alternatives

- **The origin as the credential or access key.** Rejected: a different
  installation can occupy the same origin, and one installation can move.
- **Local content-policy evaluation.** Rejected: a second evaluator could
  disagree with the server's Library, so the Library stays the only one.
- **Background access refresh.** Rejected: the next online launch check
  enforces revocation, and delayed offline revocation is an accepted cost.
- **Terminating active gameplay on a learned revocation.** Rejected: ending a
  session over a network race punishes the player, and the next launch
  enforces the revocation.
- **Migrating or rekeying origin-keyed legacy credentials.** Rejected: the
  origin may now belong to a different installation, so legacy secrets are
  only ever deleted.
- **Moving legacy files before the database commit.** Rejected: a crash would
  lose the only copy.

## Guardrails

- **`profile_server_connection_repository_test.dart`,
  `profile_session_coordinator_test.dart` and
  `server_discovery_api_client_test.dart`:** selection, generation,
  continuity and non-redirecting, credential-free discovery.
- **`profile_credential_bootstrap_test.dart` and
  `refresh_token_store_test.dart`:** ordered bootstrap, profile and instance
  token keys, and delete-only legacy cleanup.
- **`release_access_api_client_test.dart` and
  `release_manifest_api_client_test.dart`:** exact access and manifest shapes,
  identities and paths.
- **`play/install_service_test.dart`, `play/install_launch_flow_test.dart` and
  `profile_local_game_repository_test.dart`:** acquisition is the only
  row-creation path, and strict persistence.
- **`play/launch_authorizer_test.dart` and
  `play/romd_play_coordinator_test.dart`:** launch authorization semantics and a
  denial with no side effects.
- **`play/profile_play_history_repository_test.dart`:** profile-owned history
  and its projection.
- **`play/profile_game_cleanup_service_test.dart`:** cross-profile reference
  counting and serialized cleanup.
- **`app_database_baseline_test.dart`:** fresh ownership constraints and
  unsupported-file refusal without modifying existing data.
