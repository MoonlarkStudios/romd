# API Contract And Compatibility Policy

Related decisions: [admin use-case, transaction and event boundaries](admin-use-case-transaction-event-boundaries.md),
[host plane ownership](plane-ownership.md),
[consumer runtime boundary](consumer-runtime-boundary.md)

## Context

ROMD exposes two resource-oriented HTTP APIs from separate hosts:

- the **admin API**, consumed by the admin SPA
- the **consumer API**, consumed by the consumer web app and the native
  console

Every client is first-party and lives in this repository. The SPAs use
clients generated from committed OpenAPI documents. The console uses a
hand-written Dart client that the generated-client drift gate cannot check.

ROMD follows useful REST and HTTP conventions and deliberately omits full
hypermedia for these first-party clients.
[REST's uniform-interface constraints](https://ics.uci.edu/~fielding/pubs/dissertation/rest_arch_style.htm#sec_5_1_5)
include hypermedia. The nesting limits and restrictions on PUT/PATCH returning
202 below are ROMD conventions, not universal HTTP or REST requirements.

APIs that grow one endpoint at a time drift apart unless a policy holds them
together. The drift shows up as several error shapes, leaked database
identity, lenient parsing that hides client bugs, and inconsistent paging
and job contracts. It also shows up as competing canonical paths for one
concept, verbs used against their HTTP meaning, concurrency tokens in
some request bodies but headers elsewhere, and two hosts with different
guarantees.

This policy fixes those rules once for both surfaces, so every client gets
the same guarantees and tests can check them mechanically.

## Decisions

### Scope And Surfaces

- The policy governs the JSON `/api` routes of `Romd.Admin.Host` and
  `Romd.Consumer.Host`.
- `/api` routes that return bytes follow the policy for authorization and
  errors; only their success body is a byte stream.
- These protocol surfaces keep their own semantics, because their external
  contracts are not ROMD's to redesign:
  - byte delivery: `/media/{mediaId}`, `/artwork/{assetId}/…` and
    `/delivery/*/{token}`
  - WebDAV: `/vfs/{**path}`
  - OAuth and OIDC: `/connect/*`
  - health probes
- Protocol surfaces still appear in the authorization inventories.
- Admin realtime payloads are part of the admin contract (see
  [Event Payload Compatibility](#event-payload-compatibility)).

### First-Party Audience And Repo-Atomic Compatibility

Both APIs are first-party contracts. Source changes are repo-atomic: one
accepted change updates everything below together.

- **Admin API:**
  - the backend contract
  - its committed OpenAPI snapshot
  - the regenerated admin client
  - every admin SPA call site
- **Consumer API:**
  - the backend contract
  - its committed OpenAPI snapshot
  - the regenerated consumer client
  - every consumer app call site
  - every call site in the console's hand-written client

The console's checks are part of every consumer contract change. Updating
source call sites does not update installed console builds.

- **Supported pairing.** Before 1.0, the supported pairing is a console and
  web apps built from the same commit or release as the server. A console
  built from an older commit may stop working against a newer server.
- **Persisted console identity.** These keep their migration-aware rules
  however routes move:
  - Sqid encodings
  - `serverInstanceId`
  - runtime profile ids
  - payload protocol versions, such as the disc-set `contractVersion`

  A payload protocol version is not a precedent for URL versioning.
- **No external promise.** No stability promise is made to any client
  outside this repository. An external consumer is a new decision, not a
  broadening of this one.
- **Re-decision trigger.** Before the first console build distributed
  independently of a server release, or 1.0, whichever comes first,
  maintainers record a new compatibility decision defining supported
  server/console version skew. The
  [release-readiness checkpoint](../maintainers/release-readiness.md#candidate-and-publication-gates)
  enforces this boundary.

### No URL Versioning

- **No versions anywhere.** Routes carry no version segment, version header
  or version media type.
- **The document is the compatibility mechanism.** Each surface's OpenAPI
  document has a deterministic committed snapshot, and a drift gate fails
  when the backend and the snapshot disagree.
- **Breaking changes are repo-atomic.** Deprecation means migrating every
  call site and deleting the old path in the same program of work.
- **No dual paths.** A compatibility shim exists only with explicit
  retirement conditions and a tracking issue.

### Canonical Hosts And Contract Documents

- **One host per surface.**
  - `Romd.Admin.Host` is the sole admin executable and the source of
    `admin-v1`.
  - `Romd.Consumer.Host` is the sole consumer executable and the source of
    `consumer-v1`.
  - Snapshots live in `web/schemas/`.
- **Generated clients are derived artifacts.** They are regenerated, never
  hand-edited.
- **One shared HTTP contract configuration.** Both hosts register it:
  - strict number handling
  - named-string enums
  - enveloped binding failures
  - the ProblemDetails customization
  - the sanitized exception handler
  - the `/api` routing-problems handling, which answers an unmatched route,
    a wrong method, an unsupported media type and an oversized body with the
    envelope
  - the `/api` query-name check, which refuses an undeclared query name or repeated
    scalar before any parameter binds or the body is read
  - the `/api` parameter-validation filter, which runs the declared-parameter
    stage: parameter constraints, exact member names for enum-typed route and
    query parameters, and system-key resolution
  - the system-key schema rule, which publishes every bound `systemKey` as a
    string

  Surface-specific behavior layers on top of it.

### Protected By Default

- **Every route declares its authorization.** Each route on either host
  carries an explicit decision: a named policy, or `AllowAnonymous`. A
  group-level fallback policy on `/api` denies any route that fails to
  declare one.
- **Inventory tests list every anonymous route.** Each host has a test that
  enumerates its anonymous routes, and an anonymous route missing from the
  inventory fails that test. Adding one requires review.
- **Allowed anonymous categories:**
  - liveness and readiness probes
  - OAuth and OIDC protocol endpoints
  - account-link redemption, which carries its own link credential
  - server identity discovery (`/api/server/identity`) and public playback
    configuration
  - immutable public display assets and media: reference assets
    (`/api/assets/{hash}`), artwork and title media. These contain no
    library files or account data, and they publish `security: []`.
  - capability-authenticated redemptions:
    - consumer delivery grants (`/delivery/*/{token}`)
    - the admin download redemption (`/api/download-tickets/{token}`)
  - WebDAV, which authenticates with its own connection credentials
- **How redemptions work.** A short-lived opaque ticket is issued only after
  authenticated authorization. It binds an authorized snapshot and grants
  nothing beyond it. Ticket values never appear in logs or diagnostics. See
  [Downloads and exports](../architecture/downloads-and-exports.md).
- **OpenAPI publishes the security model.** Each document publishes the
  OAuth2 bearer scheme and per-operation security metadata. A test on each
  host enforces that runtime authorization and published security agree.

### Opaque Public Identity

- **No database ids on the wire.** Persisted-entity identity that crosses
  either public boundary is opaque: Sqid-encoded ints, or Guids where
  identity is already opaque, such as jobs. No int identity appears in public
  contracts or route templates.
- **Reference data uses natural keys.** Systems, companies and rating boards
  are identified by keys such as `systemKey`.
- **One canonical encoding.** Each identity has exactly one canonical string
  encoding. A non-canonical encoding is invalid input.
- **The wire form is always a string.** Opaque identity stays a string
  regardless of its internal representation, including in Sqid request
  collections and query parameters. The Sqid wrapper's internal value is
  never exposed.
- **Bounded identity collections publish their invariants.** Their
  cardinality and uniqueness rules appear in OpenAPI exactly as the endpoint
  enforces them.
- **Enums serialize as named strings, never integers.**

### Type-Faithful Numeric Wire Contracts

- **Strict number handling.** A numeric contract accepts only a JSON number
  token, and quoted numerics are invalid. OpenAPI describes one wire shape
  per field, so generated clients never contain `number | string` unions.
- **Bounded numbers are JSON numbers.** This covers bounded integers, floating
  point values and decimals.
- **`Int64` is a JSON number only when its producer guarantees a bound** no
  greater than JavaScript's `Number.MAX_SAFE_INTEGER`. Each such field is a
  reviewed exception with its rationale.
- **Other 64-bit values are canonical decimal strings.**
  - Signed `Int64` uses `^-?(?:0|[1-9]\d*)$`.
  - Unsigned `UInt64` uses `^(?:0|[1-9]\d*)$`.
  - This includes byte sizes and aggregates, free space and queue counts.
- **Byte quantities use the `ByteCount` value type.**
  - It is non-negative by construction.
  - It carries its canonical converter on the type.
  - It publishes a named string schema with the non-negative pattern.
  - Negative byte data is rejected or normalized at its producing boundary,
    so it never reaches the wire.
- **Converters travel with the contract type.** Canonical serialization
  therefore applies to every serialization path, not only host-configured
  responses.
- **A reflection ledger enforces this.** It holds every `Int64`, `UInt64` and
  `ByteCount` field across the contract assemblies (`Int64ContractPolicyTests`).
  A new field fails until its wire shape is reviewed, and stale entries fail
  too.

### Strict Validation

- **Invalid input is a 400.** Supplied-but-invalid input fails with a
  deterministic 400 in the shared envelope. This covers enum values, sort
  fields, filters, cursors, identities, natural keys, numeric ranges and
  malformed `If-Match` syntax.
- **Invalid input never selects a default.** An out-of-range `limit` is an
  error, not a clamp. An unknown filter key is an error, not an empty result.
- **Only absent input defaults.** Absent optional input may select a
  documented default.
- **Query names are closed.** An `/api` operation accepts exactly the query
  parameters it documents, matched by exact, case-sensitive name. A supplied name
  it does not document, or a single-valued parameter supplied more than once,
  is a 400 `Request.InvalidParameter` keyed by the name as sent; only a
  parameter documented as an array repeats. The check runs before any parameter
  binds or the body is read, so a misspelled filter never widens a result and
  an upload with one is refused before its body is processed. Every `/api`
  operation documents this 400.
- **Binding failures name the parameter.** A route, query or header parameter
  that fails to bind, fails a declared constraint or names an unknown system
  key as a filter answers 400 `Request.InvalidParameter`, with the
  parameter's wire name as its `errors` key. An unknown route system key is a
  404 `Systems.NotFound` when its stage has no 400 failure.

### One Error Envelope

- **One shape for every error body.** Every non-2xx response with a body, on
  either surface, is an RFC 9457 ProblemDetails served as
  `application/problem+json` with three ROMD extensions:
  - `errorCode`: a stable, machine-readable code derived from the `ErrorOr`
    error code
  - `errors`: a field-to-messages map that preserves every validation
    failure. Failures without field attribution are keyed by their error
    code. A request is validated in stages (query names, then binding, then declared parameter
    constraints and key resolution, then the operation's own validation), and
    `errors` preserves every failure of the stage that rejects it. Binding
    stops at the first parameter that fails to bind.
  - `traceId`: the request trace identifier
- **The rule has no exceptions.** It applies to 400, 404, 409, 412, 428, 500
  and binding failures alike.
- **Server errors are sanitized.** A 500 never includes exception messages
  or stack traces.
- **Operations declare their error responses.** Each operation documents
  every error status it returns by design, with the ProblemDetails schema.
  Framework-originated statuses (401 and 403 from authentication and
  authorization, the 404 for an unmatched route, 405, 413 and 415 from
  request handling, and the sanitized 500) are guaranteed by the shared
  contract configuration and are not documented per operation.

### Resource Model

- **One canonical resource per concept per surface.** Each concept has one
  canonical identity and collection on each surface, with clear mutation
  semantics. Admin and consumer may each serve their own projection at the
  same path, such as `/api/titles/{titleId}`.
  - Documented relationship collections, such as a collection's titles or a
    system's titles, preserve the related resources' canonical identities
    and contracts.
  - Relationship writes follow their documented membership or
    relationship metadata semantics; they do not introduce competing
    mutation paths for the related resource.
- **URL shape.**
  - Collections are plural kebab-case nouns.
  - Singletons are singular nouns, such as `/settings`.
  - Shallow nesting is the default limit:
    `/{collection}/{id}/{sub-collection}/{id}`.
  - Deeper relations use top-level collections with filters unless an
    explicit, reviewed exception reflects ownership and resource meaning.
- **Path parameters** are named after their resource: `{titleId}` for opaque
  ids, `{systemKey}` for natural keys. The same resource uses the same name
  on every route.
- **Unresolved identities are 404.** An unresolved path identity answers 404
  unless the operation's semantics make it a non-error, such as an idempotent
  DELETE or a check that answers for any id.
- **Entity-scoped operations live under the entity.** Operations on a
  title's own state and owned resources live under `/api/titles/{titleId}/…`:
  its artwork, provider matches and enrichment. Relationship collections
  follow the relationship semantics above.
  - Download tickets are one top-level collection, `/api/download-tickets`,
    whose request names a title, release, file or completed export job, because a ticket is a
    capability with its own address.
  - A free-text provider search lives under the provider, such as
    `/api/metadata-providers/{providerId}/games`; a suggestion derived from
    the title lives under the title.
  - Top-level function collections are reserved for cross-entity queues,
    workbenches, campaigns and settings, such as
    `/api/enrichment/workbench`.
  - Their rows link to the entity path; they do not duplicate it.
- **The host is the surface boundary.** No route carries an `/admin`
  segment.
- **Vocabulary.**
  - "System" means a gaming system.
  - Server and process state lives under `/server`.
  - `platform` does not appear in routes, parameters or operationIds.
  - Aggregates are a `stats` sub-resource of the thing they measure.
  - `/api/dashboard/*` holds only documented screen composites.
- **Self resources live under `/me` on both surfaces.**
  - `/api/me` is the caller.
  - `/api/me/password`, `/api/me/sessions`, `/api/me/settings` and
    `/api/me/connections` are the caller's own resources.
  - `/api/auth/*` holds only credential-flow endpoints.
- **Consumer library scope is implicit**, per
  [host plane ownership](plane-ownership.md#consumer-library-scope-is-implicit).
  - Browse resources sit at the root: `/api/catalog`, `/api/titles` and
    `/api/collections`.
  - Library-context projections sit under `/api/me/library`.
- **Stable representations per resource URL.** A resource has a stable,
  documented JSON shape; arbitrary competing JSON projections are not
  exposed at the same URL. Documented alternate media types, such as JSON
  and CSV, may represent the same resource through
  [content negotiation](https://www.rfc-editor.org/rfc/rfc9110.html#section-12).
  A projection becomes a named sub-resource only when it is a distinct
  concept, such as `/releases`. A different scope, behavior or operational
  cost can justify a separate export resource; two media types alone cannot.
- **Filters, not sibling lists.** A collection filtered by state is one
  endpoint with a filter: `/api/tracked-titles?status=missing`.
- **operationIds follow route semantics.**
  - `List`, `Get`, `Create`, `Replace`, `Update` and `Delete` plus the
    resource name, or the action's verb for a custom action.
  - Unique within each document.

### Method And Status Semantics

- **GET** is safe.
- **POST** does one of three things:
  - creates a member of a collection: 201 with `Location` and the
    representation
  - performs a custom action
  - runs a side-effect-free computation whose input needs a body, named
    `…/preview`
- **PUT** fully replaces or upserts a client-keyed resource. It is
  idempotent and never queues non-idempotent work.
- **PATCH** partially updates a resource.
- **DELETE** takes no request body. It returns 204, or 200 with the parent's
  representation when the deletion changes the parent.
- **Batch operations** are `POST /{collection}/batch-{verb}`.
- **Custom actions** are `POST /{collection}/{id}/{verb}`, reserved for
  transitions that are not "set this state": cancel, retry-failed, merge,
  materialize, rotate, test-connection, confirm. A custom action may follow
  a nested resource path; nesting limits count resources, not actions.
- **Setting a state value** (archived, hidden, rejected, pinned, empty,
  automatic) is a PUT or PATCH on the resource or sub-resource.
- **Status codes:**
  - 400 means the request is invalid regardless of server state.
  - 409 means a well-formed request conflicts with current state.
  - 412 and 428 are reserved for preconditions.

### Pagination

- **Keyset pagination with opaque cursors.** Every growable list pages this
  way.
- **Bounded page sizes.** `limit` has a documented maximum; out-of-range
  values fail per [Strict Validation](#strict-validation).
- **Cursors are bound to the query.** A cursor binds the sort field, the sort
  direction and a fingerprint of the active filter set. A malformed cursor,
  or one presented with an incompatible filter set, is a 400.
- **Full-text relevance exception.** Relevance-ranked full-text search may
  page by bounded offset internally behind the same opaque cursor, with a
  documented maximum exploration depth.
- **External artwork candidate exception.** Transient candidate browsing may
  follow the external provider's live ordering and continuation behind signed,
  opaque, short-lived cursors bound to the user, title, provider, game and all
  active filters. These pages offer no snapshot or complete enumeration
  guarantee; provider changes may repeat or omit candidates. Clients tolerate
  repeated candidate identities. This exception does not apply to ROMD's saved
  assets, rejections or other persisted lists, which retain keysets.
  - The response remains `Page<T>`, with no public page number or offset. The
    operation publishes its fixed provider page size and finite response and
    exploration budgets. Invalid or incompatible cursors fail with 400 before
    provider I/O.
  - SteamGridDB reads at most 50 raw records per page, 100 pages, 2 MiB per
    response and 30 seconds per request. At the final allowed page, more results
    or a full page without proof of exhaustion fails with 409,
    `Artwork.ProviderBrowseLimitExceeded`. Invalid upstream shape, count or
    contradictory continuation fails with 502, `Artwork.ProviderInvalidResponse`.
    Filtering may leave an empty page with a valid continuation.
  - IGDB candidate reads have a ROMD budget of 500 raw image entries across
    the requested image arrays and 2 MiB per response, within a finite request
    deadline. Count raw entries before validation or filtering and bound bytes
    while reading the stream. Overflow fails with 502,
    `Artwork.ProviderInvalidResponse`; it never truncates. These are adapter
    safety budgets, not a provider cardinality guarantee.
- **External game identity suggestions are explicitly bounded.** Provider
  autocomplete is an advisory query, not an enumerable catalog. It returns
  `ProviderGameSearchResultDto`: `items`, `completeness` and the fixed `limit`.
  This narrow exception exists because a provider may expose neither
  continuation nor a completeness guarantee. It does not apply to saved
  matches, reference data, facets or other ROMD-owned lists.
  - `Complete` means the provider response for the submitted query was
    exhausted. It promises neither a catalog-wide search nor a stable snapshot.
    `ProviderLimited` means the upstream offers no way to establish exhaustion;
    `BudgetLimited` means ROMD stopped before establishing exhaustion. An empty
    limited response does not establish that no matching game exists.
  - IGDB reads at most five requests of 100 raw records, 500 records per query,
    2 MiB per response and 10 seconds for the whole query. A full final batch
    returns `BudgetLimited`. SteamGridDB reads one autocomplete response of at
    most 100 raw records and 2 MiB within 30 seconds, and always returns
    `ProviderLimited`. Record or byte overflow and malformed identity evidence
    fail with 502; adapters never silently truncate or discard that evidence.
  - Unsupported continuation or retired mode flags are undeclared query names
    (see [Strict Validation](#strict-validation)), so they never silently
    select a different query.
  - Clients expose limited coverage and suggest refining the query or resolving
    an exact game id or URL. Exact resolution returns one game independently of
    suggestion limits. No cursor or last-page claim is manufactured for a
    provider that cannot continue its result set.
  - Automatic matching cannot infer a unique winner or an authoritative no-match
    from incomplete evidence, including a clipped set of alias queries. It
    retains candidates for review. An independently resolved trusted identity
    or bridge remains separate evidence.
- **Coherent reference snapshots use paged transfer.** A bounded manifest names
  the complete published catalog identity, the six family counts and its
  canonical content digest. Each family returns `Page<T>` pinned to that
  observed catalog revision. These pages come from one published state, never
  from independent live reference-list reads. A malformed pin is 400; a
  well-formed pin that is no longer current is 409. Current-only storage does
  not promise historical retrieval.
  - The manifest's `revision` and `ETag` validate its own representation.
    `catalogRevision` is a separate snapshot binding and installed-cache
    identity. A client never substitutes one for the other.
  - Clients preserve the published sequence, traverse every family, validate
    counts and the canonical complete-content digest, and discard all partial
    work on failure or revision change. A revision-change restart is bounded.
    No page, manifest or failed traversal replaces a complete local catalog.
  - Complete offline JSON and verified assets are promoted atomically. Existing
    persisted catalog identities retain their migration-aware rules. This
    transport creates no array exemption for growable reference collections.
- **Bounded-by-construction lists may return arrays.** This applies when the
  cardinality is bounded by the domain rather than by user, ingestion or
  curation activity: reference resources, configured providers, one title's
  artwork roles. The operation states the bound.
- **One page envelope.** Paged lists return `Page<T>` from
  `Romd.Contracts.Common`: `items`, `nextCursor` and `hasNextPage`.
  - Page-level extras such as counts and facets are sibling resources.
  - A non-list result that contains a page embeds a `Page<T>`.
- **Query vocabulary.**
  - Paging, search and sorting use `cursor`, `limit`, `query` (free text)
    and `sortBy`.
  - Filters use the public field name, such as `systemKey`.

### Async Operations And Jobs

- **Background work returns 202.** An operation whose requested effect is
  background work returns 202 Accepted with a job-reference body and a
  `Location` header pointing at `/api/jobs/{jobId}`.
- **A 202 carries no `ETag`.** The 202 answers before the work completes, so
  the client reads the resource for its current validator.
- **Synchronous changes with follow-up work return their normal success.** A
  synchronous state change that also schedules follow-up work returns the
  updated representation and its `ETag`, and exposes the follow-up as
  `pendingJobId` on both the mutation response and resource reads. `pendingJobId`
  refers only to follow-up work represented by a Jobs row; internal per-title
  convergence intents remain internal and do not acquire a job merely to expose
  an identifier. Under this policy, PUT and PATCH never return 202.
- **Jobs are canonical at `/api/jobs/{jobId}`.**
  - Job-type-specific details, item feeds and artifacts are sub-resources of
    the job.
  - Starting work stays under the owning domain.
- **Polling is always sufficient.** Polling the job resource always reveals
  completion. SignalR is a delivery optimization, never the only way to
  observe completion.
- **Internal semantics live elsewhere.** The in-process semantics behind this
  contract are owned by the
  [transactions and events decision](admin-use-case-transaction-event-boundaries.md).

### Idempotency And Concurrency

- **Idempotency.**
  - Mutations prefer naturally idempotent semantics and scheduler-level
    dedupe over protocol machinery.
  - There is no global `Idempotency-Key` requirement.
- **Concurrency is opt-in per resource.** It is introduced only where a real
  lost-update workflow exists.
- **Preconditions travel in headers.**
  - A concurrency-controlled resource returns `ETag` on GET and on every
    successful write that answers with its representation.
  - Every write that targets it sends `If-Match`: PUT, PATCH, DELETE and
    custom actions alike.
  - Request bodies never carry expected revisions, except in batch
    operations.
- **Validator format.**
  - The `ETag` value is the representation's `revision` string in exactly one
    pair of double quotes, with no `W/` prefix: `"revision": "42"` is sent as
    `ETag: "42"` and echoed as `If-Match: "42"`.
  - Representations expose `revision`, so a client can write a row from a
    list without fetching that row first.
  - Validators are opaque: clients echo them and never parse, order or
    construct them.
- **Preconditions are required.**
  - A write without `If-Match`, or with an empty value, fails with 428,
    `<Area>.PreconditionRequired`.
  - Malformed entity-tag syntax, or a nonempty list containing no entity
    tags, fails with 400 `Request.InvalidParameter`, with `If-Match` as its
    `errors` key. Input is validated before testing the condition, so
    invalid input is distinct from a valid condition that does not hold.
  - A syntactically valid tag list without a strong match, including a
    weak-only list or a stale tag, fails with 412,
    `<Area>.PreconditionFailed`.
  - `If-Match: *` does not prevent a lost update, so it gets 428.
    Combining `*` with any other list member is malformed syntax and gets
    400, even if another member is the current tag.
  - Multi-write flows pass along the `ETag` from each response to the next
    write.
- **The target URL is the unit of concurrency.**
  - Each independently versioned piece of state is an addressable resource
    with its own validator.
  - A parent validator may guard a family of sub-resources only when every
    write in the family bumps it. Every member then returns the parent's
    `ETag`.
  - A write to a member of such a family returns the family root's
    representation with the shared `ETag`, so the client receives the new
    validator together with the state it guards.
  - A create returns the created member with the shared `ETag`.
- **Comparison and reads.**
  - `If-Match` uses strong comparison (RFC 9110), so weak validators never
    satisfy it.
  - GET honors `If-None-Match` with 304 and `Cache-Control: private, no-cache`.
  - A representation that embeds state its validator does not cover, such as
    derived counts or other resources' state, uses its `ETag` for
    preconditions only and answers conditional reads with 200.
- **Batch exception.** A request that writes several independently versioned
  resources carries a `revision` per item. A mismatch fails with 409 and
  per-item `errors`, never 412.
- **Snapshot binding is not concurrency control.** Request fields that pin a
  computation to a published state the client observed stay in the request.
  A stale pin is never ignored: an operation that produces something from the
  pinned state returns 409, and a check that evaluates the pinned state
  reports the mismatch in its answer. Examples are disc-set `revision` and
  `observationId`, the catalog snapshot revision, and the DAT review
  `activeSha256` and `candidateSha256` pair.
- **Every request revision field is classified** as a precondition, a batch
  revision or a snapshot binding.
- **The mechanism is published.**
  - Concurrency-controlled operations declare a required `If-Match` header,
    plus 400, 412 and 428 responses.
  - Revalidating GETs declare the `ETag` response header and 304; others
    declare only the `ETag`.
  - CORS allows `If-Match` and exposes `ETag` and `Location`.

### Exports Are Complete Or Explicitly Partial

An export is either complete or explicitly partial, with a machine-readable
partial indicator in the response contract. Caps are never silent.

### Event Payload Compatibility

- **Same contract as HTTP.** Admin realtime payloads are part of the admin
  contract and follow the repo-atomic rule.
- **Payloads are versioned.** Each payload carries a type and version
  discriminator, per the outbox design in the
  [transactions and events decision](admin-use-case-transaction-event-boundaries.md).
- **Schema changes bump the version.** A dispatch-time translation for older
  pending payloads may exist only with explicit retirement conditions,
  recorded with the [admin realtime relay](../architecture/split-host-topology.md#admin-realtime-relay).

## Trade-offs

- **Same-commit pairing.** Before 1.0, a console built from an older commit
  may stop working against a newer server.
- **The console client moves by hand.** Every consumer contract change also
  updates the console's hand-written Dart client, which the generated-client
  drift gate cannot check.
- **No deprecation window.** Removing a route means migrating every call site
  in the same program of work.
- **Strictness surfaces client bugs as failures.** Input that lenient parsing
  would have mapped to a default fails with 400 instead.
- **Concurrency costs a validator round-trip.** A write to a
  concurrency-controlled resource needs that resource's current `ETag`, and a
  write without `If-Match` fails with 428.

## Rejected Alternatives

- **URL, header or media-type versioning.** Rejected for the same-commit or
  same-release pairing: parallel versions add maintenance without a
  supported version-skew requirement. Repo-atomic source changes do not
  update installed clients; the re-decision trigger requires revisiting
  compatibility before independent console distribution or 1.0.
- **External stability guarantees.** Rejected as speculative: they would
  freeze contract cleanup for a consumer that does not exist.
- **Indefinite shims or dual paths.** Rejected: migrate the call sites, then
  delete the old path.
- **Lenient parsing that maps invalid input to defaults.** Rejected: it masks
  client bugs and makes contract drift invisible.
- **Integer enums and raw int identity.** Rejected: brittle against
  reordering, unreadable in generated clients, and a leak of internal
  identity.
- **Offset pagination as the general mechanism.** Rejected except for
  full-text relevance and transient external artwork candidate browsing,
  under their narrow pagination exceptions behind opaque cursors.
- **SignalR as the only completion channel.** Rejected: polling the job
  resource is always sufficient.
- **A global `Idempotency-Key`, or blanket ETags on every resource.**
  Rejected as speculative machinery.
- **Revisions in request bodies, or optional preconditions.** Rejected: they
  duplicate HTTP conditional requests invisibly, or fall back to silent
  last-writer-wins.
- **Weak ETags for writes.** Rejected: RFC 9110 requires strong comparison
  for `If-Match`.
- **An `etag` body field alongside the header.** Rejected: one mechanism per
  direction. The body `revision` serves list reads; the header is the
  precondition.
- **Google-style `:verb` custom methods.** Rejected: `/{verb}` segments read
  as well in generated clients.
- **Hypermedia beyond `Location` and content URLs.** Rejected: every client
  is first-party and reviewed in this repository.
- **Reshaping `Page<T>`.** Rejected: it would break the console's parser for
  no gain.

## Guardrails

These checks enforce the policy mechanically:

- **Ratchets.** A new rule may start with an allowlist of existing
  violations; the allowlist only shrinks, and a stale entry fails.
- **OpenAPI drift.** A gate regenerates both OpenAPI documents and both
  generated clients, and fails on any difference.
- **Authorization.**
  - Each host has an inventory test and an OpenAPI security agreement test.
- **Contract-assembly scans:**
  - one for int-typed identity
  - the numeric wire ledger
- **A request revision ledger:** every `*revision` or `*revisions` field in a
  request body, query or header is either a reviewed batch or snapshot-binding
  field with a rationale, or it fails.
- **OpenAPI shape rules, over both documents' `/api` routes:**
  - no `/admin/` segment, no `platform` token in routes, parameters or
    operationIds, and kebab-case literal segments
  - DELETE declares no request body, and PUT and PATCH never document 202
  - every 202 documents `Location` and a job-reference schema, and no `ETag`
  - `If-Match`, 412 and 428 are declared together, with `If-Match` required
    and a 400 response for malformed header syntax
  - every 4xx and 5xx other than 401 and 403 is `application/problem+json`
    with the ProblemDetails schema, and every operation with a path parameter
    documents 404 unless a reviewed exemption records why an unresolved id is
    not an error
  - every operation with a request body documents 400 as
    `application/problem+json` with the ProblemDetails schema
  - every documented 201 declares a `Location` header
  - no `after`, `search` or `platformId` query parameters, and every `limit`
    declares its maximum
  - no offset paging in parameters, request bodies or responses, and every
    cursor-paged response is `Page<T>`
- **Closed query names.** On each host, every `/api` endpoint carries the query
  parameters its handler binds, equal to its OpenAPI operation's query
  parameters, with array parameters the only repeatable ones, and every `/api`
  operation documents 400. Synthetic endpoints cover the binding shapes the
  hosts do not use yet.
- **Protocol surfaces.** Outside `/api`, only the protocol surfaces exist.
- **Error responses.** Endpoints declare their error statuses through one
  shared metadata extension that publishes the ProblemDetails schema, and
  every error body comes from the shared envelope helpers. A source scan
  checks that endpoint code builds error bodies only through those helpers.
  One shared helper owns ETag emission and precondition checks.

Review enforces relationship semantics, nesting exceptions and the
compatibility re-decision checkpoint.
