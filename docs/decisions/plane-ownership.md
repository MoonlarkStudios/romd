# Host Plane Ownership

Related decisions: [API contract and compatibility policy](api-contract-policy.md),
[admin use-case, transaction and domain-event boundaries](admin-use-case-transaction-event-boundaries.md),
[PostgreSQL single provider](postgresql-single-provider.md)

## Context

ROMD serves two audiences with different trust. Administrators curate
catalogs, configure libraries and manage users. Consumers browse and play a
library that someone else curated. A consumer surface that can reach curation
state, configuration internals or broad write paths widens what a compromised
or buggy consumer client can damage, and couples consumer clients to admin
internals.

Background work such as ingestion, enrichment, materialization and exports
runs long, needs a single owner for recovery, and must outlive any request.
Schema migration needs exactly one owner. Every executable that composes a
surface must prove each startup, security, contract and migration change
again, so executables that duplicate a role cost more than they protect.

The [split-host topology](../architecture/split-host-topology.md) describes
the current process map and its operation.

## Decisions

### Three Hosts, One Role Each

- **Exactly three ROMD executables.** `Romd.Admin.Host`, `Romd.Consumer.Host`
  and `Romd.Worker.Host`, plus the static `romd-player` origin. A change of
  roles changes these three; ROMD never adds a compatibility host, wrapper
  executable or redirect for a retired one.
- **Shared composition is not a deployable.** `Romd.Hosting` is the shared
  composition library. Namespace names inside it, such as `Romd.Host.*`, do
  not define a host.
- **The admin host requests work.** It serves the admin API and realtime hubs
  and enqueues background work. It never executes jobs, registers recurring
  jobs, migrates or provisions schemas, runs seeders, inspects media or
  ingests ROMs.
- **The consumer host serves.** It serves the consumer API, consumer media
  and signed content delivery. It never maps admin endpoints and never
  executes background work.
- **The worker executes.** It runs ingestion and media inspection,
  enrichment, materialization, export execution and cleanup. It alone
  migrates and provisions the application and Hangfire schemas, runs seeders
  and registers recurring jobs. It has no HTTP surface, and exactly one runs.
  Why: recovery and migration each need one owner, and API hosts stay
  restartable because they hold no job state.

### Consumer Writes Only The Caller's Own State

- The consumer host writes only state that belongs to the authenticated
  caller: their password, sign-in metadata and settings, their account
  sessions and play sessions, their VFS connections, their OpenIddict tokens
  and authorizations, and their own export requests.
- It never writes catalog, curation or library configuration, never creates
  or deletes users or changes roles, never writes another user's state, and
  never writes OpenIddict applications or scopes.
- Why: a consumer client can then affect only its own account.

### Consumer Library Scope Is Implicit

- The caller's library comes from their account, read live at request time,
  never from a token claim, the route or the request body.
- Consumer routes carry no library segment. Browse resources sit at the API
  root, and library context sits under `/api/me/library`.
- `/api/me/library` returns the consumer library-context projection: display
  name, facets, counts and browse context. It never returns the admin library
  representation, its configuration, materialization state or curation state.
- **Re-decision trigger:** letting a user switch between or browse several
  libraries reopens this rule, most likely with nested library routes.

### Collections Are Admin-Curated

- A collection is a named set of titles that administrators and contributors
  curate, from explicit titles, saved filters or both.
- Consumers read collections and their titles. They never create, change,
  reorder or delete collections.
- Consumer collection projections carry only navigation and display fields,
  never curation configuration.
- User-owned lists, such as favorites or play-later, are a separate resource
  and never reuse `Collection`.

### Delivery Markers Do Not Expose Administrative Provenance

- Release-manifest items and disc-set files may carry the closed `Original` /
  `Derived` delivery marker. It classifies the exact delivered file bytes;
  it exposes no source imports, catalog authority, curation history or internal
  storage identity.
- Delivery completeness remains separate from original inventory completeness.
  A derived descriptor never establishes preservation of the required original.
- Every other consumer projection continues to exclude administrative provenance
  and curation configuration. Do not broaden the exception beyond those two
  file properties or the closed marker vocabulary.

### Exports Execute On The Worker

- Only the worker executes exports.
- Admin exports are operational and may target any library the caller is
  authorized to administer.
- Consumer exports are self-service: a consumer may request exports only of
  their own library or of an accessible collection in it.

## Trade-offs

- **Background work waits for the worker.** A stopped worker leaves accepted
  work queued, and API hosts stay unready until the worker publishes the
  expected schema versions.
- **One worker bounds background throughput.** Scaling background work means
  scaling within that one process until a distributed claim protocol exists.
- **New consumer writes need a deliberate widening.** A consumer feature that
  writes new state changes this rule and its write guard first.
- **No library switching.** A user who needs several libraries needs a new
  decision.

## Rejected Alternatives

- **A compatibility admin executable alongside `Romd.Admin.Host`.**
  Rejected: it duplicated the admin composition with drifting bootstrap
  behavior, so every startup, security, OpenAPI and migration change had to
  be proven twice, and no client required it.
- **A wrapper executable or redirect for a retired host.** Rejected: every
  user is first-party and moves with the repository, and a shim would need
  its own retirement.
- **Executing jobs on the API hosts.** Rejected: request lifetimes cannot own
  long-running recovery, and several executors per job family would need
  cross-host claims.
- **A library segment in consumer routes.** Rejected while a user has one
  library: the account already determines it, and a route segment would let
  a client assert library authority it does not hold.
- **User-owned lists as collections.** Rejected: collections are curated
  catalog constructs, and personal lists have different ownership and
  privacy.
- **Renaming the shared `Romd.Host.*` namespaces.** Rejected as churn:
  projects, registrations and mapped routes define hosts, not namespace
  text.

## Guardrails

- **`ConsumerBoundaryTests`:**
  - exactly three deployable hosts and no legacy contract-generation path
  - consumer code references no management or worker namespaces and injects
    no broad persistence, identity or storage primitives
  - consumer application code takes no library authority from the token or
    the request
- **`ConsumerWriteGuardInterceptor` and `ConsumerPersistenceWriteGuardTests`:**
  consumer persistence rejects every write outside an allow-list of
  caller-owned entities and user fields, including catalog writes, user
  creation and deletion, role, claim and link writes, and OpenIddict
  application and scope writes.
- **`HostDeployableSmokeTests`:**
  - the admin host is enqueue-only and composes no media inspection or ROM
    ingestion
  - the worker starts without an HTTP surface
  - the consumer host maps catalog routes read-only and account routes as
    limited writes
- **`JobLifecyclePolicyArchitectureTests`:** API hosts compose no job
  lifecycle policy.
- **Review:** consumer collection and library-context projections are
  checked against these rules when their contracts change; the consumer
  contract snapshot makes every change visible.
