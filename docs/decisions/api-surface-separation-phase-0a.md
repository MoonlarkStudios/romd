# API Surface Separation Phase 0A Decisions

Status: accepted

Related issues: #1, #7, #8, #9, #10, #11, #12

## Context

Phase 0A exists to lock the decisions that shape ROMD's future admin,
consumer, and worker split before committing to consumer contracts or host
implementation.

The completed spikes cover browser emulator loading behavior, CAS delivery,
content-size distribution, SQLite write ownership, library scoping, and export
ownership.

## Decisions

### Consumer Library Scoping

Use implicit library scoping from the authenticated user/token.

Consumer routes do not include a library segment for the first split. The
consumer API exposes:

```http
GET /api/me
GET /api/me/library
```

`/api/me/library` returns a consumer `LibraryContext` projection only: display
name, available facets, counts, and other launch/browse context. It must not
return the admin `LibraryDto`, library configuration internals, materialization
flags, excluded DAT ids, or curation state.

Nested library routes can be introduced later if ROMD supports user-driven
library switching or browsing multiple libraries.

### Export Ownership

Exports are a dual-plane feature with different contracts.

Admin exports are operational and may target any library when authorized.
Consumer exports are self-service only: the caller may schedule exports for
their own ambient library or an accessible collection inside that library.

Both planes enqueue export work. Only the worker executes export profiles.
Adding an export target remains an `IExportProfile` implementation, not a new
endpoint family.

### Collection Ownership

Collections are admin-curated catalog constructs, not user-owned consumer data.

A collection defines a named set of titles for a Netflix-style browse
experience. Membership may come from explicit title selection, saved filters, or
both. Admins/contributors manage collections. Consumers read collections and
browse their titles, but they do not create, update, reorder, or delete
collections.

Consumer collection projections expose only navigation/display fields such as id,
name, description, cover/hero media, item count, and links. They must not expose
admin-only configuration or curation internals.

If ROMD later needs user-owned lists such as favorites, play-later, or personal
playlists, introduce a separate resource instead of overloading `Collection`.

### Browser Launch Model

The initial web-launch target should assume full-buffer browser loading.

EmulatorJS, Emularity/JSMESS, and similar browser cores generally fetch ROM
assets as URL-accessible files, read them into buffers or browser-managed
storage, and then write them into an Emscripten virtual filesystem. They do not
make bearer-header downloads a reliable launch contract.

The consumer API should expose launch assets or content-url minting links that
browser code can fetch without custom `Authorization` headers. Range support is
a capability of specific delivery artifacts, not a baseline requirement for all
consumer ROM URLs.

### ROM Content Delivery

Keep CAS as canonical storage, but do not promise random access from CAS.

Small/simple content is served as a full response from CAS. Compressed CAS
retrieval returns verified, decompressed, non-seekable streams, so direct CAS
delivery is always `200 OK` full-response delivery.

Large or seek-heavy content resolves to an uncompressed launch-cache artifact.
Only launch-cache artifacts advertise and serve range/`206 Partial Content`.

Default policy thresholds:

| Payload | Delivery mode |
| --- | --- |
| `<= 64 MiB` | full-buffer default |
| `> 64 MiB` and `<= 128 MiB` | conditional full-buffer for cartridge-style content |
| `> 128 MiB` | launch-cache preferred |
| `> 256 MiB` | range-capable artifact required |
| disk or unknown-size media | range/seek-heavy path |

The implementation should keep these configurable.

### Content-Size Reporting

Delivery planning should be based on game payload size, not only platform name.

The primary report unit is:

```text
DatGame payload bytes = SUM(DatRoms.Size) for that DatGame
```

Platform/media hints remain useful as fallbacks, especially when disk sizes are
unknown. `DatDisks` currently has no byte-size column, so disk manifests must be
classified as unknown-size seek-heavy content until the schema captures disk
size from sources that provide it.

### SQLite Write Ownership

SQLite remains a single-node deployment constraint.

Admin, Consumer, Worker, and Hangfire must not all run the same write-heavy
startup services or executors. The split is based on write ownership:

- Admin owns configuration, curation, user/role management, and management job
  request creation.
- Consumer is mostly read-only. Its write allow-list is intentionally narrow:
  export scheduling for the ambient library or accessible collection, and
  own-job archive/cancel after ownership checks.
- Worker owns ingestion, enrichment, materialization, export execution, cleanup,
  and CAS/file writes after request staging.
- Hangfire owns queue storage, recurring registration, and worker execution
  state in its own database.

Consumer login is read-only while lockout is disabled and there are no
persistent sessions or refresh tokens.

Postgres is required before horizontally scaling any API host or running
multiple write-capable instances.

## Implementation Consequences

- Phase 0B can proceed with consumer contracts using implicit library scope.
- Collections should remain admin-managed and consumer-read-only in the host
  split.
- `Romd.Contracts.Consumer` should include a launch/content capability shape
  rather than exposing admin ROM download DTOs.
- Host registrations must keep curation services, provider credentials, export
  execution, seeders, recurring registration, and Hangfire servers out of the
  consumer host.
- Signed content URLs should bind to method, path, ROM id, library id, file id,
  content hash/version, expiry, and delivery mode. Tokens must be redacted from
  logs.
- Launch-cache artifacts are sensitive cache files. They must live outside
  public static roots and never authorize access by existence alone.
- SQLite configuration needs explicit busy timeout and checkpoint ownership.

## Follow-Up Issues

Create or track follow-up work for:

- SQLite busy timeout and checkpoint ownership.
- Consolidating duplicate recurring job registration.
- Ownership enforcement for job archive/cancel before consumer exposure.
- Adding disk-size support for `DatDisks` if disc launch/reporting enters the
  near-term scope.
