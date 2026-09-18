# Consumer Runtime Boundary

Status: runtime-manifest boundary accepted; server identity, live-Library
single-release access, and manifest instance binding are implemented on the
backend. Gate 1a was accepted on 2026-07-17 after generated-contract and
build-fix review passed, its assembled review's one Medium production identity
runbook finding was repaired, and fresh independent documentation re-review
passed with no Critical/High/Medium/Low findings. Flutter acquisition,
persistence, and cleanup were accepted in Gate 2 on 2026-07-18. Central launch
enforcement was accepted in Gate 3 the same day. Profile history and launcher/
PIN UX remain pending.

## Context

ROMD can decide whether the current authenticated user's live materialized
Library owns/exposes a release and can describe delivery. Only the console can
decide whether exact local files and runtime dependencies are ready. For
multi-server local state, release/title ids are meaningful only with the ROMD
server instance that issued them.

## Decision

ROMD owns metadata, live Library policy, release selection, access responses,
content grants, hashes, BIOS catalog data, and runtime intent. Console owns
instance-scoped local game rows, installs/cache, verification, emulator/core
installation, generated configuration, save/state paths, launch, and process
supervision.

The Release manifest is delivery/runtime intent, not authorization. Acquisition
requires a separate strict allow and then validates every manifest
`serverInstanceId`, release, title, and platform field against the discovered
selected instance, chosen title/release, and access response. The backend,
OpenAPI, and generated consumer client now require exact `serverInstanceId` on
`ConsumerReleaseManifestDto`. The strict nonredirecting Flutter parser and Gate
2 acquisition are implemented. Verification yields an install keyed by that
same instance/release before a profile local game row can be created.

## Stable Server Identity Boundary

The server instance id is a public canonical UUID stored atomically in the ROMD
data directory and exposed anonymously by exact
`GET /api/server/identity`. Origin is a mutable locator. Console discovers the
instance with redirects disabled before reading/sending credentials.

The UUID is a namespace/accidental-collision boundary, not authentication or
cryptographic continuity. Configured HTTPS/TLS and ordinary OAuth authenticate
transport/user. Copied-id active impersonation is out of scope without key/cert
pinning; clone rotation is operator correctness.

All local runtime identities include `serverInstanceId`:

- install key and content namespace;
- profile local game key;
- planned profile play-history key;
- save/state namespace; and
- generated per-title config namespace.

This prevents two ROMD installations that reuse encoded ids from sharing files,
progress, or configuration. Same instance at a new origin preserves state;
different instance at the same origin receives no former token or state.

## Backend Single-Release Access Boundary

Authenticated `POST /api/releases/{releaseId}/access` returns one exact boolean
decision including `serverInstanceId`. The server uses ordinary OAuth subject
and a coherent read of current `Users.LibraryId` plus the current valid
materialized projection. It has no security/access revision or token-
invalidation freshness scheme; the same issued token observes live Library
reassignment.

This backend boundary is implemented through `LiveConsumerLibraryQuery`, which
performs live assignment/configuration/materialization and projection reads in
one serializable SQLite transaction. The closed application result hierarchy
distinguishes `Found`, `ItemNotFound`, `LibraryUnavailable`, and
`ProjectionInconsistent`. Invalid or incoherent Library state is therefore an
unavailable response, never an implicit revoke. Current Library, Browse,
Collections, BIOS, manifest, and access share this live-assignment boundary.

On `401`, the caller performs one normal refresh/retry. Unresolved `401` and all
other non-authoritative failures preserve cached state. Launch can update only an
existing matching profile+instance+release row and never creates/adopts one.

## Central Launch Enforcement

Presentation supplies only an unresolved `PlayRequest`; the root-composed play
graph owns profile/server authority. `RomdPlayCoordinator` invokes its authorizer
before the coordinator-only installed resolver. The authorizer's coherent local
snapshot binds selected profile/server/origin/generation, the exact cached grant,
and strict installed identity/fingerprint.

Launch performs one exact release-access request and at most one ordinary `401`
credential restoration/retry. A strict allow must win a compare-and-set against
the snapshot. A strict revoke blocks immediately and is persisted when its
compare-and-set wins. Non-authoritative failure may use cached authorization only
after re-reading and proving the complete authority/grant/install snapshot is
unchanged. A missing row, cached revoke, changed identity, or inconsistent state
can never become an allow.

Only an exact permit reaches installed resolution; the resolved profile/server/
release/title is checked again before runtime work. Denial has zero installed-
probe, dependency, provisioning, config, process, runtime-preference, or play-
marking side effects. An app-scoped `ActiveLaunchSession` continues supervising
a running emulator across profile/server graph replacement and prevents a second
process. Learned revocation does not terminate active gameplay.

```text
artifact resolver: no - authorization/acquisition reorders existing content resolution; runtime dependency resolution did not change
config writer: no - existing roots/config writers unchanged
```

## Accepted Manifest Scope

The accepted strict manifest vocabulary supports:

- content type `single_rom|unknown` with closed cardinality;
- item role `rom|disk`;
- launch target `file`;
- packaging `direct_files`; and
- local footprint `minimumInstallBytes`.

Unsupported semantics must not receive an invented launch target.

## Consequences

- Server-side `IsPlayable` remains delivery-oriented, not console Ready state.
- Ready requires an authorized profile local game plus exact instance-scoped,
  verified install and compatible runtime.
- Shared or version-10 legacy files grant nothing; legacy adoption requires
  discovery, fresh auth, allow, manifest validation, nondestructive staging,
  full verification, transactional new install/local game commit, then
  idempotent legacy cleanup.
- Version-10 origin-keyed credentials are delete-only and never read, sent, or
  rekeyed. Cleanup failure disables network auth until retry.
- Temporary cache, permanent install, pinning, eviction, and zero-reference
  cleanup are local runtime decisions.
- Emulator-specific settings remain outside ROMD, but their persisted/generated
  paths must include server instance identity.
- The backend identity/access/manifest slice has passed focused/full tests and
  independent security/correctness, generated-contract, and build-fix review.
  Its assembled review's one Medium production identity backup/restore/clone
  runbook finding was repaired, fresh independent documentation re-review passed
  with no Critical/High/Medium/Low findings, and Gate 1a was accepted on
  2026-07-17. The earlier revision-bearing partial implementation remains
  superseded history.
- Gate 3 passed implementer focused 59, full Console 926, clean analysis/diff;
  independent reviewer focused 39, clean focused analysis/diff, and PASS; and
  primary clean analysis plus full Console 926. No release build, GUI, Gate 4-6,
  or assembled-feature completion is claimed.
