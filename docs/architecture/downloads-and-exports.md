# Download and export contracts

For portal actions and format choices, start with the
[download guide](../features/downloads-and-exports.md). This page describes the
authorization, transport and archive contracts.

Admin downloads use one ticket collection. An authenticated
`POST /api/download-tickets` names what to download in its body:
`{ "target": "Title" | "Release" | "Rom", "id": "<id>", "format": "original" }`.
It creates a ticket (201) whose `Location` is its redemption URL,
`/api/download-tickets/{token}`. An unknown `target` fails binding with a
`400`, as any request-body enum does; a malformed `id` and an unknown `format`
each fail as a `400` keyed by that field, both in one response. An unresolved
title, release or file is `404`.

A release ticket names the canonical release ID; it is not a DAT game ID or the
title's currently preferred release.
The application query authorizes and resolves files before issuing a ticket.
Contributors and above have catalog access; other users require the exact release
in their current persisted, published library. A missing library fails closed.

The ZIP uses canonical file requirement paths, including relative directories.
Unsafe paths and case-insensitive path collisions fail before a ticket is issued.
Partial releases export only locally available files and use an
`-available-files.zip` filename. No local files means no download. A file that
vanishes after planning fails the release download, rather than silently changing
its contents. Stream failures abort the HTTP response.

Title and release ZIPs accept `original`, `rvz` and `chd`. Original selects
original bytes for every member. An encoded format selects that format for each
member where it is available, and original for other members. CHD is available
when retained integrity evidence matches the exact artifact and the host's
decoder tuple, and the host can decode the evidenced kind; see
[CHD decoder upgrades](../self-hosting/production-deployment.md#chd-decoder-upgrades). Selection
happens before writing, and the complete renamed path set must pass portable
path and collision checks. Each entry binds its selected format, filename,
length and SHA-256; redemption does not substitute another format. This can
produce a ZIP containing RVZ or CHD discs alongside other original files.

The title representation's `archiveFormats`, and each release's, list the
formats that ZIP offers the caller: `original` first, then each format whose ZIP
would bind at least one member in it. Each entry carries the wire `format` and
its display `label` from the server's format catalog; the admin portal shows an
encoded label as "{label} where available" and a single download when only
`original` is listed. The list comes from the same members, platform context,
kind resolver, planner and CHD admission as the ZIP: the title list reads the caller's title
ticket selection, and a release list reads the release ticket's files, every
release on the page in one query, with member content read in batches. A ZIP
the caller cannot download, one over the ticket limit and a release without a
canonical ID list only `original`, as does every ZIP when member content cannot
be described; requesting such a ZIP still fails. Every title response, reads and
writes, carries the lists, as does the release page. A title read therefore also
reads the title ticket's selection, the registered platform keys, the page's release
files in one query and member content in batches, plus each CD file set a
member belongs to. A request for an unlisted format still succeeds, falling back
to each member's original.

Media kind resolution is shared and computed from loaded evidence. Title and
release ZIPs carry their own platform context; per-ROM downloads, file tickets
and all-catalog exports resolve under each matched entry's platform and
aggregate the results. Runtime applicability comes only from a unique kind's
descriptor. An ambiguous or contradicted kind makes no runtime claim, but does
not hide held CHD bytes admitted by the host's decoder evidence. Admission is
rechecked when the target is read; a decoder change or failed native check
refuses it. A CD member exposes its authorized reconstructed member only,
never the entire multi-output CHD. Consumer manifests follow the same byte
admission rule when catalog platform and DVD evidence contradict: original and
CHD remain available without runtime applicability or a per-format runtime.
VFS serves the decoded original in that case, without a stored CHD projection.

A library's `exportFormats` list `original`, then the stored delivery format of
each system its configuration allows (every system the delivery policy covers
when it allows all; only `original` when the configuration is invalid). They
come from policy without reading members, so an export of a listed format still
falls back to originals where members lack it.

Advertised hashes always describe the selected bytes. Original uses logical
checksums; RVZ uses immutable artifact checksums measured during staging,
encoding or convergence re-verification. Unknown checksums are omitted, never
filled from the logical identity. The same rule applies to VFS checksum properties.

A title ticket uses the caller's existing authorized scope: catalog access
selects distinct locally stored title files, while library access selects the
title's published default release. The title ticket accepts at most 10,000
files; larger selections require a background export.

A file ticket (`"target": "Rom"`) returns one complete file. Every ticket's
optional `format` accepts exactly `original`, `rvz` or `chd`; omission selects
`original`. ROM detail metadata lists available formats and their delivered
filenames, lengths and hashes, with each format's display `label` and, for an
encoded format, its `fileExtension`, both from the server's format catalog. The
admin portal labels its per-file download buttons with `label` and does not yet
display `fileExtension`. The direct authenticated
`/api/roms/{romId}/download` endpoint accepts the same values as its `format`
query. Invalid values return `400`; an unavailable format returns `409` without
a ticket or payload.

Single-file tickets bind the authorized ROM, format, delivered filename, SHA-256
and length. Original follows a replacement representation only while its logical
identity still matches. RVZ and CHD open only their bound artifact;
disappearance fails the download rather than substituting another artifact or
original bytes. A CHD ticket also binds the evidence it was admitted under, so
one issued before a decoder upgrade stays refused.

Convergence keeps the retained representation readable until atomic publication
of its verified replacement. Original tickets keep their logical-identity rule;
RVZ tickets remain bound to the selected artifact. The Storage page offers bounded
GameCube and Wii convergence runs. See
[durable convergence](../self-hosting/media-processing.md#durable-convergence) for source custody,
admin bulk runs and recovery.

## Transport and capability lifetime

`@romd/delivery` provides browser-managed saving and a separate credential-free
fetch transport. Browser saving follows a same-origin capability URL; it does not
fetch a JavaScript Blob, and the app reports only download handoff, not completion.
The browser handles progress, disk storage, and errors after handoff. An expired
or failed capability can be retried by requesting a fresh ticket.

Admin tickets are opaque random 256-bit capabilities held in a bounded in-memory
cache in the Admin host. They contain an immutable, authorized file snapshot and
expire after two minutes. They are reusable until expiry so browser retries work.
Restart or eviction invalidates them. Authorization changes do not revoke an
already issued ticket before expiry. They never contain an application bearer
token and cannot be redeemed by the Consumer host. Responses are private/no-store.
Do not log or share capability URLs. Multiple Admin replicas would require a
shared ticket store or sticky routing; the current deployment is single-node.

Library exports start with `POST /api/export/library`. A successful request
returns 202 with a `JobReference` body (`jobId`) and
`Location: /api/jobs/{jobId}`, without an `ETag`. Poll the job until it completes
before requesting the download ticket. An export already in progress returns
409 `Export.AlreadyInProgress`.

Completed background exports use the same authenticated
`POST /api/download-tickets` collection with `target: "ExportJob"` and the
canonical, nonempty job GUID as `id`. The ticket redeems at
`/api/download-tickets/{token}`. Issuance checks the existing creator,
library and materialization-generation authorization, then binds the completed
ZIP path. Omit `format` or send null: any nonnull override is invalid because the archive
already contains the format chosen when its job was created. A missing completed artifact returns
`404`. The browser uses a fresh ticket for each download action.

Consumer saving uses signed content grants for the chosen manifest-item format.
The signed payload binds its filename, SHA-256 and length. Older grants without
a format claim retain original semantics. Consumer authorization and grant
lifetime remain independent of Admin tickets. Playback still fetches original
bytes and verifies size and SHA-256 before producing the Blob used by EmulatorJS;
browser-managed saving does not perform client-side hash verification.

## Reuse for larger exports

`IExportArchiveWriter` lives in Admin Application/Export; its Infrastructure
implementation writes authorized file entries to any destination stream.
It owns portable path validation, ZIP creation, cancellation and entry progress,
not access control or file selection. All title/library/release packaging uses it.
ZIP entries use no compression to keep CPU cost predictable. A bounded 64 KiB
metadata adapter handles .NET 10 synchronous entry-close writes without enabling
synchronous HTTP I/O; payload bytes go directly through asynchronous writes.

Release downloads stream directly to HTTP. The existing library export worker
writes to its attempt-specific temporary artifact, with existing job checkpoints,
download authorization and cleanup. Its explicit skip-missing behavior is retained;
interactive release downloads are strict. A future collection exporter should
build its own authorized entry plan and use the same background artifact flow.

Library export POSTs to `/api/export/library` carry `libraryId` and optional
`format` (`original`, `rvz` or `chd`). The job persists its choice in
`Jobs.ExportFormat`; a null selection means original. This field and CHD choices
are part of the current fresh baseline. A restarted attempt
keeps that choice and authorization scope, collects current members and binds
fresh targets before writing a new attempt artifact. Missing selected artifacts
are recorded and skipped, without changing their selected format. The job's
format and progress are returned with its status. The library workspace offers
all three choices and links the queued job.

Collection scope, advanced export settings, resumable/range delivery and durable
multi-host ticket storage are separate changes.

Logical ROM downloads and single-file ticket redemptions join the host's bounded
decoder FIFO when all slots are occupied, waiting up to 5 seconds by default.
A full queue or expired admission wait returns `503` with `Retry-After: 15`. ZIP exports
wait up to 30 seconds per logical file for a slot and honor cancellation. A wait
that expires during a streaming ZIP aborts that download; a failed background
export removes its partial artifact. Original byte representations and raw RVZ
delivery need no decoder slot. The delivery formats are defined in the
[media delivery decision](../decisions/media-delivery.md#delivery-formats).
