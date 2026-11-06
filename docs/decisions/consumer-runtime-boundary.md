# Consumer Runtime Boundary

Related decisions: [API contract and compatibility policy](api-contract-policy.md),
[console profile access and offline grants](../../clients/romd_console/docs/decisions/profile-access-and-offline-grants.md)
(how the console enforces these answers),
[media delivery](media-delivery.md), [original disc sets](original-disc-sets.md).
Current delivery behavior is in
[consumer delivery](../architecture/consumer-delivery.md).

## Context

ROMD can decide whether the current user's live materialized Library owns and
exposes a release, and can describe how to deliver it. Only the console can
decide whether exact local files and runtime dependencies are ready to play.

A console can use several ROMD servers. Release and title ids mean something
only together with the server that issued them, an origin URL is a mutable
locator, and a different installation can later occupy the same origin.
Network failures are routine on couch devices, and they must neither revoke
legitimate play nor turn into unauthorized play.

## Decisions

### Ownership Split

- **ROMD owns** metadata, live Library policy, release selection, access
  responses, content grants, hashes, BIOS catalog data and runtime intent.
- **The console owns** instance-scoped local game rows, installs and cache,
  verification, emulator and core installation, generated configuration, save
  and state paths, launch and process supervision.
- **The Library is the only policy evaluator.** The server's Library
  materialization alone evaluates ownership, exposure, platform, title, genre
  and content-rating policy. The console stores no content-policy mode or
  local content blocks.
- **Ready is a console state.** Server-side `IsPlayable` stays
  delivery-oriented. Ready requires an authorized profile local game, plus an
  exact, instance-scoped, verified install and a compatible runtime.
- **Local runtime decisions.** Temporary cache, permanent install, pinning,
  eviction and zero-reference cleanup are decided on the console. Emulator
  settings stay outside ROMD, but their persisted and generated paths include
  the server instance identity.

### The Manifest Is Not Authorization

- The release manifest is delivery and runtime intent, not authorization.
- Acquisition requires a separate strict allow. It then validates every
  manifest `serverInstanceId`, release, title and platform field against the
  discovered selected instance, the chosen title and release, and the access
  response. `ConsumerReleaseManifestDto` carries an exact `serverInstanceId`.
- Verification yields an install keyed by that instance and release before a
  profile local game row can be created.

### Stable Server Identity Boundary

- **A stored public UUID.** Each ROMD data directory owns one canonical
  lowercase UUID-D instance id, stored atomically at
  `<Romd__DataDirectory>/identity/server-instance-id`. It is public, not secret,
  and shared by every host that uses that data directory. The origin is a
  mutable locator.
- **Stable and never replaced.** The id survives process restart, origin, port
  and host moves, and a full logical restore of the data directory. A fresh
  data directory creates it exactly once, safely under split-host concurrency.
  A missing identity directory is created safely. A malformed, noncanonical,
  empty or unreadable identity file fails host startup and is never silently
  replaced.
- **Clones rotate.** An independently exposed clone must rotate the copied id
  before exposure. The restore and clone runbook distinguishes an intentional
  logical restore from an independent clone.
- **Exact discovery response.** `GET /api/server/identity` is anonymous and
  returns exactly `200 { "instanceId": "<lowercase-uuid-D>" }`. A response with
  extra or missing fields, or a noncanonical id, is rejected.
- **Discovery before credentials.** The console discovers the instance with
  redirects disabled before it reads or sends credentials.
- **A namespace, not authentication.** The UUID is a namespace and
  accidental-collision boundary, not authentication or cryptographic
  continuity. Configured HTTPS/TLS and ordinary OAuth authenticate the transport
  and the user. Active impersonation with a copied id is out of scope without
  key or certificate pinning, and rotating a clone's identity is an operator
  responsibility.
- **Every local identity carries it.** These all include `serverInstanceId`:
  - the install key and content namespace
  - the profile local game key
  - the profile play-history key
  - the save and state namespace
  - the generated per-title configuration namespace
- **No sharing across installations.** Two installations that reuse encoded ids
  never share files, progress or configuration. The same instance at a new
  origin keeps its state, and a different instance at the same origin receives
  no former token or state.

### Backend Single-Release Access Boundary

- **One exact decision.** Authenticated `GET /api/releases/{releaseId}/access`
  takes no request body and returns one exact boolean decision:

  ```json
  { "serverInstanceId": "<lowercase-uuid-D>", "releaseId": "<encoded-release-id>", "allowed": true, "titleId": "<encoded-title-id>" }
  { "serverInstanceId": "<lowercase-uuid-D>", "releaseId": "<encoded-release-id>", "allowed": false }
  ```

  Fields are exact. `titleId` is present only when allowed, and absent, not
  `null`, when revoked. A `200` is authoritative only after strict shape,
  canonical identity, requested-release, selected-instance and selected-title
  validation, and a server-instance mismatch changes nothing.
- **Status meanings:**
  - `400`: a malformed or noncanonical release id
  - `401`: missing, invalid or expired OAuth authentication
  - `409`: no assigned Library, an invalid configuration, or no current valid
    materialized Library
  - `200` with `allowed: false`: the current Library does not own and expose
    the release
  - a redirect, timeout, transport failure, malformed response, other 4xx or
    5xx: a non-authoritative failure
- **Live reads, no freshness scheme.** The server uses the ordinary OAuth
  subject and one coherent read of the user's current Library assignment and the
  current valid materialized projection. There is no access revision or
  token-invalidation freshness scheme, so the same issued token observes a live
  Library reassignment.
- **One consistent snapshot.** `LiveConsumerLibraryQuery` performs that read in
  one transaction over a consistent snapshot. Its result distinguishes `Found`,
  `ItemNotFound`, `LibraryUnavailable` and `ProjectionInconsistent`. Current
  Library, Browse, Collections, BIOS, manifest and access reads share this
  live-assignment boundary.
- **Unavailable is not revoked.** Invalid or incoherent Library state is an
  unavailable response, never an implicit revoke.
- **Failures preserve cache.** On `401` the caller performs one normal refresh
  and retry. An unresolved `401`, and every other non-authoritative failure,
  preserve cached state. Launch updates only an existing matching profile,
  instance and release row, and never creates or adopts one.

### Console Enforcement

- The console enforces these answers centrally, before any launch side effect,
  and only a strict server answer changes authorization. Its rules, including
  cached offline authorization and legacy adoption, are in
  [console profile access and offline grants](../../clients/romd_console/docs/decisions/profile-access-and-offline-grants.md).

### Manifest Scope

The strict manifest vocabulary supports:

- content type `single_rom|unknown`, with closed cardinality
- item role `rom|disk`
- launch target `file`
- packaging `direct_files`
- local footprint `minimumInstallBytes`

Unsupported semantics never receive an invented launch target.

## Trade-offs

- **Identity is not authentication.** Protection against impersonation rests
  on TLS and OAuth, and a copied instance id goes undetected without pinning.
- **Revocation lands at the next check.** A Library reassignment takes effect
  at the next access request rather than by invalidating issued tokens.

## Rejected Alternatives

- **The origin URL as server identity.** Rejected: a different installation
  can occupy the same origin, and one installation can move to a new one.
- **The manifest as authorization.** Rejected: delivery intent and access are
  separate decisions, and a manifest can be stale.
- **An access revision or token-invalidation freshness scheme.** Rejected: a
  live read of the current assignment answers access directly. An earlier
  revision-bearing implementation was superseded by this boundary.
- **Treating non-authoritative failures as revocations.** Rejected: an outage
  would revoke legitimate play, so only a strict server answer revokes.

## Guardrails

- **`ServerInstanceIdentityTests` and `ServerInstanceIdentityProcessTests`:**
  - concurrent creators converge on one published identity
  - restart preserves it
  - separate data directories get different identities
  - crash orphans are reaped safely
- **`LiveConsumerLibraryQueryTests`, `ConsumerReleaseAccessRepositoryTests`,
  `GetConsumerReleaseAccessQueryHandlerTests` and `ConsumerAccessEndpointsTests`:**
  access reads live assignment in one snapshot, distinguishes unavailable from
  revoked, and returns the instance id.
- **`ConsumerBoundaryTests`:** consumer code takes no library authority from
  the token or the request.
