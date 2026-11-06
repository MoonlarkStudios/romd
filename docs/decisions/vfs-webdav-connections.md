# VFS Delivery Through WebDAV And Registered Connections

Related decisions: [media delivery](media-delivery.md) (per-connection
formats and naming), [API contract and compatibility policy](api-contract-policy.md)
(protocol surfaces), [host plane ownership](plane-ownership.md). Current design,
limits and qualification evidence are in
[the client qualification matrix](../architecture/vfs.md).

## Context

Players want their authorized library to appear as a local folder for
emulator frontends on devices such as a Steam Deck, without copying every game
first. Mounting, caching and filesystem integration are hard, platform-specific
problems that mature clients such as rclone and FUSE already solve. A ROMD
mount daemon or filesystem engine would have to be built, distributed and
qualified on every platform.

Device configurations get copied, shared and lost, so they must never carry
the account password. Catalogs change as DATs update, so paths derived from
them change too. Many devices on one account, and many accounts on one server,
share bounded read capacity and must all make progress.

## Decisions

### Standard Read-Only WebDAV

- **Standard protocol.** ROMD exposes the user's authorized library through
  standards-correct, read-only WebDAV over HTTPS. Users register a named
  connection, then use a compatible client.
- **Clients own the mount.** ROMD manages and delivers the collection. Clients
  own mounting, fetching, caching and local filesystem integration.
- **No client-specific server behavior.** The server never requires rclone's,
  or any client's, behavior for ordinary access. ROMD provides ready-made
  configurations and tested setup guides for selected clients; a downloaded
  rclone configuration is an export of standard connection details plus
  tested recommendations.
- **Stock configuration stays first-class.** A thin setup or authentication
  utility is added only where demonstrated friction justifies it, and never
  replaces the stock-client path.
- **Correct ranges and EOF.** ROMD never adds a client-specific workaround that
  breaks correct HTTP range or EOF behavior.
- **Placement.** WebDAV is a consumer-host surface, and storage stays
  protocol-neutral.

### Product Promise

> Your library appears as a read-only local folder. Games are fetched as needed
> and cached locally. First launch of a large disc depends on connection speed;
> playability also depends on the game's read pattern and network latency.

This is not a guarantee of instant launch, uninterrupted gameplay during an
outage, or offline availability. Client-cached bytes cannot be recalled after
server access is revoked. A partial mount cache is not an offline game install.

### DAT-Derived Default Namespace

- **A projection, not a store.** The VFS is a data-driven projection of the
  current DAT-derived catalog within the user's currently authorized,
  published library. Layouts compute names and hierarchy deterministically from
  that data. ROMD persists no assigned directory names, placements,
  retired-path reservations or namespace snapshot.
- **The default layout** groups releases under per-platform folders:
  `/vfs/library/<platform-id> <platform-name>/<release-id> <release-name>/<delivered-path>`,
  with public Sqids for the id components. The ids refer to the current
  catalog, so identical DAT imports into independent databases need not
  receive identical ids.
- **DAT changes propagate.** Changes that become visible through catalog
  processing and library publication appear in the VFS: renames, moves,
  additions and removals. Identical source data and layout rules produce
  identical paths. ROMD never freezes old paths or creates compatibility
  aliases, and external enrichment never substitutes for DAT-derived naming
  inputs.
- **Release structure is preserved.** Release-relative directory relationships
  survive projection. Formats, collisions and companion files follow
  [VFS Formats](media-delivery.md#vfs-formats).
- **A stable mount point.** The local mount location stays the same across
  reboot and reconnect. That does not freeze the paths inside it.
- **Change and removal:**
  - DAT updates appear in listings and path resolution. Clients may need to
    refresh directory caches or rescan their libraries.
  - A format change or convergence can rename a disc between `.iso` and
    `.rvz`. Old paths return 404.
  - Removing access or withdrawing content makes entries unavailable to new
    authorized requests. Client directory caches may delay their
    disappearance.
  - Restored content is projected from its current data, never from a saved
    historical path. A path may identify different content after a DAT update,
    and changed bytes always change the content validator.

### Code-Defined Layouts

- **Keys route to code.** A unique, stable layout key routes to a layout
  implementation. `library` selects `DefaultLibraryLayout` at `/vfs/library/`,
  and there is no unkeyed or legacy route. "View" names the resulting
  namespace, not a persisted view-definition entity. Additional layouts provide
  alternative projections of the same authorized content.
- **No layout versions.** A layout key selects an implementation, not a frozen
  namespace. There is no layout version field or versioned URL, connections own
  no copy of the namespace, and DAT-driven path changes need no migration.
- **Scoped queries.** Each layout composes the queries, naming rules, platform
  mappings and structure it needs, using current, scoped library queries
  rather than loading the whole library. A registry dispatches to layouts and
  rejects duplicate keys at startup.
- **Layouts confer no access.** A layout maps a path to a release and file
  locator. The application service performs one scoped lookup per request,
  verifies the canonical layout path, and uses that result for both metadata
  and content. Only service-resolved files are opened, layout-supplied hashes
  confer no access, and a resolved entry is never a durable authorization
  grant.
- **Adding a layout** means registering its key and testing its structure
  alongside shared authorization, deterministic output, DAT update
  propagation, collision handling, companion paths, cancellation and
  enumeration bounds.
- **Existing projects.** VFS lives in the existing Clean Architecture projects:
  - Domain owns path safety and deterministic naming invariants.
  - The consumer application owns layout contracts and implementations,
    listing and resolution use cases, and repository ports.
  - Persistence owns bounded, indexed queries over catalog and library data.
  - Hosting owns WebDAV translation, connection authentication and admission.
  - Storage owns protocol-neutral positional content reads.

### Registered Connections And Credentials

- **Named, not device-bound.** A user registers a named connection, such as
  Steam Deck, Laptop or Living room. It is associated with that user's device
  for management, but it is not cryptographically device-bound: a copied
  credential works elsewhere until it is rotated, invalidated or revoked. ROMD
  never presents a connection as hardware attestation.
- **Its own identity.** Connection identities, verifiers and revocation state
  have their own schema. They never silently reuse console authentication or
  device identity.
- **A dedicated app password.** Each connection has its own read-only
  username and app-password credential for standard WebDAV authentication over
  HTTPS. The account password is never the WebDAV password. The secret is long
  and cryptographically random. ROMD retains only a cryptographic verifier,
  compared securely and cheaply per request, as suits a high-entropy machine
  secret. Secrets, `Authorization` headers and credential-bearing
  configurations are never logged.
- **One-time issuance.** At creation or rotation, the user downloads an rclone
  configuration or copies the standard endpoint, username and app password in a
  one-time flow. ROMD keeps no recoverable secret for later export. Downloading
  a replacement configuration rotates the credential and invalidates previous
  copies, and the user is told so before acting. Rclone's password obscuring is
  reversible, not encryption: holding the configuration grants access.
- **Lifecycle.** Users list, name, rotate and revoke their own connections.
  Password changes and account recovery revoke every connection credential,
  and account disablement denies access. App passwords have no scheduled
  expiry; they stay valid until revoked, rotated or invalidated by an account
  security change, and the setup screen says so.
- **Live authorization.** Every metadata request and every new content request
  is authenticated and authorized against current account and library access.
  A credential identifies the user and connection; it does not freeze the
  library permissions from registration time. No hash route exposes CAS
  objects around authorization.
- **Revocation affects new requests.** An already-authorized response may
  finish, and bytes a client cached cannot be recalled. ROMD promises neither
  recall nor instant cancellation of open reads.

### Admission And Resource Bounds

- **Wait, don't reject.** Each connection has a concurrency allowance, and a
  request beyond it waits, bounded and cancellation-aware, rather than being
  rejected at once. Global limits and account-level fairness keep additional
  connections from monopolizing the server. Queue length, wait duration, active
  readers and enumeration are all bounded.
- **Every device progresses.** Devices on one account, such as a Steam Deck and
  a laptop, both make progress under load.
- **Authorization is re-checked on admission.** Queued work is authorized
  against current access when it is admitted.
- **Listings keep moving.** PROPFIND, HEAD and OPTIONS use a separate bounded
  metadata lane, so listings progress while transfers occupy every content slot.
- **Measured defaults.** Numerical defaults come from measured client behavior.
  Overload responses are standards-correct and carry retry guidance.

### Client Support Boundary

- **Qualified combinations only.** Any compatible WebDAV client may connect, but
  only qualified combinations receive reliability claims. Protocol
  compatibility alone never qualifies a mount engine, operating system or
  emulator.
- **What qualification means.** A combination is qualified only by a recorded
  end-to-end run on the named target:
  - fresh-user registration through mounted gameplay
  - reboot
  - real remote interruption and recovery
  - cache pressure
  - simultaneous connections
  - credential rotation and revocation
  - account changes
  - graceful write failures at the same mount path

  Implementation completion never substitutes for that run.
- **Native OS mounts are unsupported.** Native Finder and Explorer WebDAV
  mounting are unsupported until qualified. Finder and Explorer may still
  browse a folder that a supported client mounted.
- **Writable locations.** Setup instructions redirect saves, save states, game
  lists, artwork, playlists and frontend databases to writable local locations
  wherever the chosen applications would otherwise write next to ROMs. An
  attempted write to the mount fails gracefully and understandably.
- **Offline needs explicit preparation.** Offline play, when ROMD supports it,
  requires verified complete local preparation with defined retention and
  access semantics. Offline readiness is never inferred from a partial cache.

## Trade-offs

- **Rescans after changes.** Clients rescan after DAT updates and format
  changes, and anything they key by path does not follow a rename.
- **Copyable credentials.** A connection credential works on any device until
  it is rotated or revoked.
- **No recall.** Revocation cannot remove bytes a client already cached, or
  stop a response that is already being sent.
- **Third-party clients.** Users install and configure a mount client, and
  ROMD can stand behind qualified combinations only.
- **No offline play from a mount.**

## Rejected Alternatives

- **A mandatory ROMD mount daemon or custom filesystem engine.** Rejected:
  mature clients already own mounting, caching and filesystem integration, and
  a ROMD engine would need its own distribution and qualification on every
  platform. This supersedes the earlier layered direction with a mandatory
  daemon.
- **An SMB gateway.** Rejected: native SMB testing showed no path to ROMD
  authorization integration and no transparent recovery after a server
  restart.
- **Client-specific workarounds such as Finder's EOF 503.** Rejected: they
  break correct range and EOF behavior for every other client.
- **Persisted namespace assignments or path aliases.** Rejected: paths are
  projections of current data, and stored names would drift from the catalog.
- **The account password as the WebDAV password.** Rejected: device
  configurations are copied, so each connection needs its own revocable secret.
- **Recoverable secrets for later export.** Rejected: one-time issuance means
  nothing recoverable stays on the server.
- **A separate `Romd.Vfs` project.** Rejected: the existing layers already own
  every concern the VFS needs.

## Guardrails

- **`ConsumerVfsEndpointTests`:**
  - metadata requests use reserved capacity when content slots are full
  - a layout that supplies unauthorized content never opens CAS
  - a file resolves catalog access once
  - disabled VFS constructs no database-backed layout
- **`ConsumerVfsAdmissionTests` and `VfsAdmissionCleanupTests`:**
  - competing connections and accounts take turns without head-of-line
    blocking
  - cancelled waiters and faulted requests free their capacity
  - full queues and timeouts recover
  - invalid queue settings fail startup
- **`VfsConnectionStoreTests`:**
  - only the verifier is stored
  - rotation and revocation invalidate previous copies without resurrection
  - account invalidation denies existing credentials
  - the active-connection limit holds
- **`ConsumerVfsRepositoryTests`:** listings are scoped, bounded and batched,
  and decoder admission is reassessed on every request.
- **`VfsProbe*` tests** cover the real-mount probe harness itself. Real-mount
  and real-network probes are opt-in, and ordinary integration success does
  not imply that they ran.
- **Review:** reliability claims are checked against the recorded qualification
  run for the exact combination in
  [the client qualification matrix](../architecture/media-status.md#network-client-scope).
