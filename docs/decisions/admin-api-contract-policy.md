# Admin API Contract And Compatibility Policy

Status: accepted

Related issues: #75, #76, #81, #83, #84, #89, #97, #98, #186

## Context

The admin API is the HTTP surface consumed by the ROMD admin SPA. It is
mapped unversioned under `/api` by the sole admin host
(`src/Romd.Admin.Host/Program.cs`), which calls the shared
`MapRomdAdminHttp()` composition
(`src/Romd.Hosting/Hosting/RomdAdminEndpointRouteExtensions.cs:7-29`).
That composition maps 18 admin endpoint classes in
`src/Romd.Hosting/Endpoints/` totaling roughly 108 HTTP operations. The
TypeScript client in `web/packages/romd-admin-api-client` is generated
from the committed OpenAPI document `web/schemas/admin-v1.json` via
`@hey-api/openapi-ts`
(`web/packages/romd-admin-api-client/openapi-ts.config.ts`), refreshed
by `pnpm api:update` (`web/package.json:22`).

The surface grew endpoint-by-endpoint without a contract policy, and the
inconsistencies are now measurable:

- No committed-snapshot enforcement: the CI web job runs lint, test, and
  build only (`.github/workflows/ci.yml:130-163`); nothing detects drift
  between the backend contract and `web/schemas/admin-v1.json`.
- No published security contract: `AddOpenApi` registers only a schema-fix
  document transformer and no security schemes or per-operation security
  metadata (`src/Romd.Hosting/Hosting/RomdHostRegistrationExtensions.cs:369-376`).
  `GET /api/catalog/filters` carries no authorization at all
  (`src/Romd.Hosting/Endpoints/CatalogEndpoints.cs:37-40`) while its
  sibling catalog routes require `AuthorizationPolicies.RequireUser`
  (lines 35 and 46 of the same file). No fallback authorization policy
  exists anywhere in `src/Romd.Hosting` or `src/Romd.Admin.Host`.
- Internal identity leaks: `PlatformAlias.Id` and `AliasDto.Id` are raw
  ints (`src/Romd.Contracts.Management/Models/PlatformAlias.cs:9`,
  `src/Romd.Contracts.Management/Taxonomy/AliasDto.cs:5`),
  `CoverMediaId` is `int?` in collection requests
  (`src/Romd.Contracts.Management/Collections/CreateCollectionRequest.cs:7`,
  `src/Romd.Contracts.Management/Collections/UpdateCollectionRequest.cs:7`),
  and three route templates constrain alias ids to int
  (`src/Romd.Hosting/Endpoints/PlatformEndpoints.cs:63`,
  `src/Romd.Hosting/Endpoints/TaxonomyEndpoints.cs:47` and `:79`). Some
  responses serialize enums as integer casts
  (`src/Romd.Hosting/Endpoints/CatalogEndpoints.cs:56` and `:62`).
- Silent-default parsing: unrecognized `sortBy`, `releaseCompleteness`, `tracked`,
  and `bios` values fall through to defaults instead of failing
  (`src/Romd.Hosting/Endpoints/CatalogEndpoints.cs:130-158`,
  `src/Romd.Hosting/Endpoints/SearchEndpoints.cs:79-96`), and
  out-of-range `limit` values are silently clamped
  (`src/Romd.Hosting/Endpoints/CatalogEndpoints.cs:87`).
- At least five error-body shapes coexist: `ToProblem()` emits
  ProblemDetails but surfaces only the first `ErrorOr` error and smuggles
  the error code into the `type` field
  (`src/Romd.Hosting/Endpoints/ErrorOrResultExtensions.cs:17-24`); raw
  string bodies (`src/Romd.Hosting/Endpoints/CollectionEndpoints.cs:176`),
  anonymous objects
  (`src/Romd.Hosting/Endpoints/ExportEndpoints.cs:95` and `:110`), and a
  raw exception-message leak in a 500 response
  (`src/Romd.Hosting/Endpoints/AdminEndpoints.cs:49-53`) all bypass it.
- Divergent 202 shapes: uploads return `Location` plus a typed body
  (`src/Romd.Hosting/Endpoints/UploadEndpoints.cs:83`,
  `src/Romd.Hosting/Endpoints/DatEndpoints.cs:247`), library export
  returns an anonymous `JobId` body without `Location`
  (`src/Romd.Hosting/Endpoints/ExportEndpoints.cs:112`), and enrichment,
  rematerialization, and force-materialize triggers return bodyless 202s
  with no job reference at all
  (`src/Romd.Hosting/Endpoints/EnrichmentEndpoints.cs:206` and `:224`,
  `src/Romd.Hosting/Endpoints/TitleEndpoints.cs:200`,
  `src/Romd.Hosting/Endpoints/LibraryEndpoints.cs:205`).
- Pagination gaps: `CompositeCursor` binds sort field, sort value, id,
  and offset but neither direction nor the active filter set
  (`src/Romd.Application.Common/Common/Pagination/CompositeCursor.cs:37-41`);
  FTS relevance ranking uses offset pagination because bm25 cannot be
  keyset-paged
  (`src/Romd.Infrastructure/Persistence/Repositories/SearchRepository.cs:55-60`);
  `GET /platforms/{platformId}/titles` returns an unbounded list
  (`src/Romd.Hosting/Endpoints/PlatformEndpoints.cs:76`); `JobItemPage`
  signals `HasMore` without providing a cursor to continue
  (`src/Romd.Contracts.Management/Models/JobItem.cs:44-48`).
- Silent export truncation: the job-item export path caps results at
  100,000 rows with no indicator to the caller
  (`src/Romd.Infrastructure/Persistence/Repositories/JobItemRepository.cs:14`
  and `:68`).

This decision fixes the contract policy that the implementation issues
(#81, #83, #84, #97) enforce. It does not implement anything.

## Decisions

### First-Party Contract Audience And Repo-Atomic Compatibility

The admin API is a first-party contract owned by the admin SPA. Its
compatibility unit is the repo-atomic change: the backend contract, the
regenerated TypeScript client in `web/packages/romd-admin-api-client`,
and all app call sites change together in one accepted change.

External third-party stability is explicitly out of scope until an
external consumer is a real product requirement. No stability promise is
made to any consumer other than the admin SPA in this repository. If an
external consumer becomes a requirement, that is a new decision with its
own ADR, not a silent broadening of this one.

Guardrail: a change that alters the admin contract without regenerating
the client and updating call sites in the same change is incomplete and
is rejected in review; the drift gate in #83 makes this mechanical.

### No URL Versioning

Admin routes remain unversioned. There is no `/api/v2`, no version
header, and no version media type. The compatibility mechanism is the
admin-v1 OpenAPI document, a deterministic committed snapshot of it, and
a CI drift gate (#83) that fails when the backend contract and the
committed snapshot disagree.

Breaking changes are permitted only as repo-atomic changes per the
previous decision. Deprecation means: migrate all call sites, then
delete the old path, in the same program of work under a named issue.
Indefinite dual paths are prohibited. A compatibility shim may exist
only with an open retirement issue attached to it.

Migration path for a contract-breaking change: change the backend
contract, run `pnpm api:update` to regenerate `web/schemas/admin-v1.json`
and the generated client, update admin app call sites, and land all of
it as one accepted change that passes the drift gate.

### Protected By Default

Every admin route carries an explicit authorization decision. A
group-level fallback policy on the `/api` group enforces default-deny
for any route that fails to declare one. Intentionally anonymous routes
must both declare `AllowAnonymous` explicitly and appear in an
authorization-inventory test that enumerates every permitted anonymous
route; an anonymous route absent from the inventory fails the test.

`GET /api/catalog/filters`, currently unprotected
(`src/Romd.Hosting/Endpoints/CatalogEndpoints.cs:37-40`), becomes
protected consistent with its sibling catalog routes.

The OpenAPI document publishes the OAuth2/bearer security scheme and
per-operation security metadata, and a test enforces that runtime
authorization metadata and OpenAPI security metadata agree, so the
generated client and the published contract cannot silently diverge from
enforcement. Issue #81 implements this decision.

### Opaque Public Identity

All persisted-entity identity crossing the public admin boundary is
opaque: Sqid-encoded ints, or Guids where the identity is already opaque
(jobs, e.g. `src/Romd.Contracts.Management/Models/Job.cs:17`). This
explicitly includes the platform and taxonomy alias ids that are ints
today (`src/Romd.Contracts.Management/Models/PlatformAlias.cs:9`,
`src/Romd.Contracts.Management/Taxonomy/AliasDto.cs:5`), `CoverMediaId`
in collection requests, and the three `{aliasId:int}` route parameters.

No int identity appears in public contracts or admin route templates. A
guardrail test scans the contract assemblies for int-typed identity
fields so regressions fail deterministically.

Enums serialize as named strings, never integers. The integer-cast enum
fields in the rating-board catalog response
(`src/Romd.Hosting/Endpoints/CatalogEndpoints.cs:56` and `:62`) are
non-conforming. Issue #84 implements this decision.

### Type-Faithful Numeric Wire Contracts

Admin and consumer JSON inputs use strict number handling: a numeric contract
accepts a JSON number token only. Quoted numerics are invalid rather than being
silently coerced. OpenAPI describes one wire shape per field; generated clients
must contain no `number | string` numeric unions.

Bounded integers (`int`), floating point values, and decimals are JSON numbers.
An `Int64` is a JSON number only when its producer has a machine-enforced upper
bound no greater than JavaScript's `Number.MAX_SAFE_INTEGER`. The reviewed
numeric exceptions are the four elapsed-second diagnostics
`WedgedReplaceDatJobDto.StalledForSeconds`,
`StrandedBulkEnrichmentJobDto.StrandedForSeconds`,
`OutboxDiagnosticsDto.OldestPendingAgeSeconds`, and
`HangfireServerDiagnosticDto.HeartbeatAgeSeconds`. They are derived from the
bounded `DateTimeOffset` domain; its complete representable span in seconds is
far below JavaScript's safe-integer ceiling.

Every other public signed `Int64` is a canonical decimal string matching
`^-?(?:0|[1-9]\\d*)$`; unsigned `UInt64` fields use the non-negative equivalent
`^(?:0|[1-9]\\d*)$`. This includes every byte size and aggregate, filesystem
free space, and Hangfire queue counts. Byte-semantic fields use the `ByteCount`
value type: it enforces a nonnegative value at construction, its type
declaration carries the canonical converter, and it serializes with the
non-negative pattern. Negative byte data is handled at its producing boundary:
DAT parsers refuse to parse signed size declarations and normalize them to
zero, the existing fallback for malformed sizes. Non-byte canonical-string fields
(the Hangfire queue counts) declare the signed canonical converter on the DTO
property. Both mechanisms travel with the contract type, so canonical
serialization also applies to standalone serialization paths such as job-item
JSON export, not only host-configured responses, and rejects numeric or
non-canonical string input. In OpenAPI output, `ByteCount` publishes as a named
string schema with the non-negative canonical pattern that properties
reference, giving generated clients a semantic `ByteCount` type alias. A
field-by-field reflection ledger tests both contract assemblies against three
reviewed wire shapes — bounded JSON number, signed canonical string, and
`ByteCount` — so a new `Int64`, `UInt64`, or `ByteCount` fails until its exact
wire policy and rationale are reviewed, a byte field cannot silently regress to
an annotated raw `Int64`, and stale ledger entries fail as well.

Opaque public identity remains a string regardless of its internal numeric
representation. This includes Sqid request collections and query parameters;
the public contract never exposes the Sqid wrapper's internal `value` property.
Bounded identity collections publish the same cardinality and uniqueness
invariants in OpenAPI that their endpoint enforces at runtime.

### Strict Validation

Supplied-but-invalid input — enum values, sort fields, filters, cursors,
sqids, and numeric ranges — fails with a deterministic 400 using the
shared error envelope. Invalid input never silently selects a default.
Absent optional input may select a documented default.

The silent-default parsers in `CatalogEndpoints` and `SearchEndpoints`
(`src/Romd.Hosting/Endpoints/CatalogEndpoints.cs:130-158`,
`src/Romd.Hosting/Endpoints/SearchEndpoints.cs:79-96`) and the silent
limit clamp (`src/Romd.Hosting/Endpoints/CatalogEndpoints.cs:87`) are
non-conforming. Issue #84 implements this decision.

### One Error Envelope

Every non-2xx admin response with a body conforms to RFC 9457
ProblemDetails plus three documented ROMD extensions:

- `errorCode`: a stable machine-readable code derived from `ErrorOr`
  error codes.
- `errors`: a field-to-messages map for validation failures, preserving
  all failures, not just the first. Failures without field attribution
  (application-layer `ErrorOr` validation errors carry a code, not a
  field) are keyed by their stable error code instead.
- `traceId`: the request trace identifier.

This applies to 400, 404, 409, and 500 alike. 500 details are sanitized;
the current exception-message leak in
`src/Romd.Hosting/Endpoints/AdminEndpoints.cs:49-53` is non-conforming.
Raw-string bodies
(`src/Romd.Hosting/Endpoints/CollectionEndpoints.cs:176`) and anonymous
objects (`src/Romd.Hosting/Endpoints/ExportEndpoints.cs:95` and `:110`)
are non-conforming. The first-error-only behavior of `ToProblem()`
(`src/Romd.Hosting/Endpoints/ErrorOrResultExtensions.cs:17-24`) is
non-conforming for validation failures. Issue #84 implements this
decision.

### Pagination

Keyset pagination with opaque cursors is the rule for every list
endpoint. Every list endpoint has a required bounded page size with a
documented maximum; unbounded lists such as
`GET /platforms/{platformId}/titles`
(`src/Romd.Hosting/Endpoints/PlatformEndpoints.cs:76`) and
cursor-less `HasMore` pages such as `JobItemPage`
(`src/Romd.Contracts.Management/Models/JobItem.cs:44-48`) are
non-conforming.

Cursors bind the sort field, the sort direction, and a fingerprint of
the active filter set. A malformed cursor, or a cursor presented with an
incompatible filter set, is a 400 per the strict-validation decision.
The current `CompositeCursor`
(`src/Romd.Application.Common/Common/Pagination/CompositeCursor.cs:37-41`)
binds neither direction nor filters and is non-conforming.

One named exception: FTS relevance-ranked search may use bounded offset
paging internally behind the same opaque-cursor contract, with a
documented maximum exploration depth, because bm25 ranking cannot be
keyset-paged
(`src/Romd.Infrastructure/Persistence/Repositories/SearchRepository.cs:55-60`).
The cursor remains opaque so the implementation may change without a
contract change. Issue #97 implements this decision; #98 measures it at
scale.

### Async Operations

Any operation that queues background work returns 202 Accepted with a
body containing a public opaque job reference and a `Location` header
pointing at the job resource. Polling the job resource is always
sufficient to observe completion. SignalR is a delivery optimization,
never the only way to observe completion.

Bodyless 202s — the enrichment, rematerialization, and force-materialize
triggers (`src/Romd.Hosting/Endpoints/EnrichmentEndpoints.cs:206` and
`:224`, `src/Romd.Hosting/Endpoints/TitleEndpoints.cs:200`,
`src/Romd.Hosting/Endpoints/LibraryEndpoints.cs:205`) — and the
anonymous `JobId` body without `Location`
(`src/Romd.Hosting/Endpoints/ExportEndpoints.cs:112`) are
non-conforming.

This decision aligns with the job model in the companion ADR
`docs/decisions/admin-use-case-transaction-event-boundaries.md` (#89).

### Idempotency And Concurrency

Mutations prefer naturally idempotent semantics and scheduler-level
dedupe over protocol machinery. There is no global `Idempotency-Key`
header requirement.

Optimistic concurrency (a version- or updatedAt-based ETag) is
introduced per-resource only when a real lost-update workflow exists —
expected in Phase 3 curation. When introduced, it follows this policy's
envelope and header shapes: precondition failures use the shared error
envelope, and validators/ETags use standard HTTP headers.

### Exports Are Complete Or Explicitly Partial

An export is either complete or explicitly, machine-readably partial via
a structured partial indicator in the response contract. Silently
applied caps — the current 100,000-row job-item export cap
(`src/Romd.Infrastructure/Persistence/Repositories/JobItemRepository.cs:14`
and `:68`) — are non-conforming. Issue #97 implements this decision.

### Event Payload Compatibility

Admin realtime event payloads are part of the same first-party contract
as the HTTP surface. Payload schema changes follow the same repo-atomic
rule: backend payload, client consumption, and app call sites change
together in one accepted change. Payloads carry a type and version
discriminator per the outbox design in the companion ADR
`docs/decisions/admin-use-case-transaction-event-boundaries.md` (#89).

`LibraryUpdated` schema version 2 replaces its raw integer library identity
with an opaque Sqid. Pending version-1 outbox rows are translated to the
version-2 public payload at dispatch, and failed delivery or acknowledgement
retries the same deterministic translation. Issue #187 owns removal of that
compatibility path only after the #116 release is the minimum supported direct
upgrade baseline, the processed-row retention interval has elapsed, and every
supported deployment reports zero unprocessed version-1 `LibraryUpdated` rows.

### Canonical Admin Host

`Romd.Admin.Host` is the sole admin executable and canonical OpenAPI source.
The compatibility executable was retired by #171; the decision and migration
boundary live in `docs/decisions/compatibility-admin-host-retirement.md`.

## Rejected Alternatives

- URL versioning (`/api/v1`, `/api/v2`) or header/media-type versioning.
  Rejected: the only consumer lives in this repository and moves
  atomically with the backend, so parallel versions buy nothing and cost
  dual-path maintenance forever.
- External API stability guarantees now. Rejected as speculative: no
  external consumer exists, and promising stability to a hypothetical
  one would freeze contract cleanup (#84, #97) that the SPA needs.
- Indefinite compatibility shims or dual code paths for deprecated
  routes. Rejected: every shim requires a retirement issue; migrate call
  sites, then delete, within the same program of work.
- A global `Idempotency-Key` header requirement on all mutations.
  Rejected as speculative machinery; natural idempotency and
  scheduler-level dedupe cover current workflows.
- Blanket ETag/optimistic-concurrency on every resource. Rejected: it is
  introduced per-resource only when a real lost-update workflow exists.
- Lenient parsing that maps invalid input to defaults. Rejected: it
  masks client bugs and makes contract drift invisible; the existing
  silent-default parsers demonstrate the failure mode.
- Integer enum serialization. Rejected: brittle against enum reordering
  and unreadable in generated clients; named strings are the contract.
- Raw int identity in public contracts and route templates. Rejected:
  leaks internal database identity and contradicts the established
  Sqid boundary used everywhere else on the surface.
- Offset pagination as the general list mechanism. Rejected except for
  the single named FTS-relevance exception, which stays hidden behind an
  opaque cursor with a documented maximum exploration depth.
- SignalR as the only completion channel for background work. Rejected:
  polling the job resource is always sufficient; realtime delivery is an
  optimization.
- Deciding compatibility-host retirement inside this ADR. Rejected as scope
  creep; it was resolved by the dedicated host-topology decision and #171.

## Implementation Consequences

- #81 adds the group-level fallback authorization policy on `/api`, the
  explicit `AllowAnonymous` plus inventory-test requirement, protection
  for `GET /api/catalog/filters`, the OAuth2/bearer security scheme and
  per-operation security metadata in the OpenAPI document, and the test
  that runtime authorization metadata and OpenAPI security metadata
  agree.
- #84 converts alias ids, `CoverMediaId`, and the `{aliasId:int}` route
  templates to Sqid; adds the contract-scan guardrail test against int
  identity; switches enum serialization to named strings; replaces
  silent-default parsers with strict 400s; and unifies every non-2xx
  body on the ProblemDetails envelope with `errorCode`, `errors` (all
  validation failures preserved), and `traceId`, with sanitized 500s.
- #83 commits a deterministic snapshot of the admin-v1 OpenAPI document
  and adds the CI drift gate; the current CI web job
  (`.github/workflows/ci.yml:130-163`) gains a contract check.
- #97 binds cursors to sort field, direction, and filter fingerprint;
  bounds every list endpoint with a documented maximum page size;
  replaces `JobItemPage.HasMore` with a real cursor; documents the FTS
  exploration depth; and replaces the silent job-item export cap with a
  structured partial indicator.
- Bodyless and anonymous 202 responses are normalized to a job reference
  body plus `Location` header following the job model in
  `docs/decisions/admin-use-case-transaction-event-boundaries.md`.
- Because compatibility is repo-atomic, every backend contract change
  regenerates `web/schemas/admin-v1.json` and
  `web/packages/romd-admin-api-client` via `pnpm api:update` and updates
  admin app call sites in the same change. Generated client files remain
  derived artifacts and are never hand-edited.
- `Romd.Admin.Host` is the sole admin OpenAPI source; contract work continues
  to land once in the shared `Romd.Hosting` composition.

## Follow-Up Issues

- #81: protected-by-default authorization and OpenAPI security metadata.
- #84: opaque IDs, strict validation, and the unified ProblemDetails
  envelope.
- #83: committed OpenAPI snapshot and CI drift gate.
- #97: pagination hardening and streaming/partial-aware exports.
- #98: scale evidence for the pagination and export decisions.
- #102 / #171: compatibility admin host retired before the application
  database PostgreSQL cutover.

### Reference artwork exception

`GET /api/assets/{hash}` intentionally allows anonymous access on
both hosts. These are immutable public system/rating display assets; they contain
no library files or account data. Upload and reference mutations remain admin-only,
and catalog snapshots require authentication. The route is explicitly inventoried
in authorization tests and publishes `security: []` in OpenAPI.
