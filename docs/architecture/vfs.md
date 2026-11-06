# Consumer virtual filesystem (WebDAV)

ROMD provides standard read-only WebDAV and device-named, revocable connections.
The [WebDAV decision](../decisions/vfs-webdav-connections.md) owns the policy;
[media status](media-status.md#network-client-scope) owns client qualification and
limitations. Stock rclone on Steam Deck is a qualification target, not a required
client or an already supported combination.

## Connection formats

Connections select `stored` (default) or `original`. The namespace uses the
selected delivered format: eligible verified whole-file RVZ/CHD content can
appear with its stored extension, while original view reconstructs original
bytes. The [media delivery decision](../decisions/media-delivery.md#vfs-formats)
owns portable-collision and complete-release descriptor/playlist fallback rules.
No old-path aliases, descriptor inspection or persisted reference index are added.

## Code-defined layouts

The application now defines `IVfsLayout` and a scoped `VfsLayoutRegistry`.
Layouts receive the current user's library scope and a decoded relative path,
and return bounded metadata without opening content. WebDAV owns URL encoding,
HTTP methods, conditions, and streaming. Duplicate or unsafe layout keys fail
endpoint startup.

The default implementation is `DefaultLibraryLayout`, selected by `library`
at `/vfs/library/`. No compatibility route is retained: the unkeyed `/vfs/`
root, old flat release URLs, and `/vfs/legacy/` return 404 for authenticated
reads. Tests, development mount tooling, and deployment probes use the keyed
route directly. Current library checks, companion filenames, enumeration
limits, and content validators remain in place. Unknown keys return 404.
The default layout exposes platform folders, release folders, and then the
original release-relative file tree:

```text
/vfs/library/<platform-sqid> <platform-name>/<release-sqid> <release-name>/<original-path>
```

Platform labels come from current platform catalog records; release names and
companion paths come from the DAT-derived release catalog. Folder labels retain
at most 80 Unicode scalar values and 200 UTF-8 bytes, replace unsafe filename
characters with `_`, and trim trailing spaces/dots. Empty labels use `Platform`
or `Release`. The public ID prefix disambiguates duplicate or sanitized names.
Original companion paths are validated and preserved, never sanitized.

Names and hierarchy are computed from current data on every request. Renames
and platform moves change path resolution immediately once visible in the
catalog/published library; stale paths return 404. No persisted namespace
assignments, reservations, or aliases are used. This does not add a new DAT
publication mechanism or promise path identity across independent databases.

Platform enumeration and platform-scoped release enumeration use keyset pages
of at most 200 items and existing library/platform indexes. Root listings do not
build every release tree. Directory limits apply independently to each level;
paging detects library reassignment, and platform listings reject inconsistent
platform metadata across pages. Content reads verify both current folder names
and release membership before opening CAS content.

No ES-DE layout is registered. Registered connection credentials and HTTP
admission are described below. CAS positional access is maintained in
[CAS random access](cas-random-access.md).

The [qualification matrix](media-status.md#network-client-scope) distinguishes
protocol/read checks from supported mount behavior. The parked
[ROMD Mount helper](../../clients/romd_mount/README.md) owns its experimental
credential, cache and mount-recovery procedures; its evidence does not establish
supported-client qualification.

## Current metadata resource bounds

Platform release pages are limited to 200 releases and 20,000 catalog file facts.
Requirement counts are read within the same authorization snapshot before paths
are loaded, using indexed counts capped at 10,001 rows per release. A page ends
before its next eligible release would exceed the fact budget. Releases
exceeding the 10,000-file limit remain unprojectable without
loading their paths. Cursors advance through omitted releases as well as visible
releases, so later valid releases remain reachable.

CHD content metadata, including artifacts and their recorded evidence, is loaded
in batches of at most 256 ROMs. Single and bulk catalog reads apply the same
evidence checks, and VFS verifies each descriptor against its authorized
content row before use. Native decoder admission is evaluated for the current
request. These bounds introduce no authorization or decoder-admission cache.

The opt-in sustained probe (`ROMD_VFS_SUSTAINED_REPORT`) checks continued progress
for every account in both halves of the workload. Successful queuing need not
produce overload responses. Separate HTTP scenarios hold admission slots to
verify queued grants, other-account progress, queue exhaustion, timeout,
cancellation and recovery with registered credentials. Sustained reports are
written on failure as well as success; latency percentiles are null when no
request succeeded.

## Design invariants

The directory tree is an application model, not an authorization capability. Its
content hashes must never become an unauthenticated content route. A cached tree
does not establish continued library access.

`IConsumerVfsRepository` is registered in consumer infrastructure and legacy
combined/test compositions that scan consumer handlers. The split admin host does
not register it. Neither query accepts a library id from the caller; the database
resolves assignment under `LiveConsumerLibraryQuery`'s repeatable-read snapshot.
File resolution loads the complete bounded release path facts and resolves the
selected file through its indexed catalog path; it does not construct a release
tree for each file request. Measure this cost through the transport before
introducing metadata caching; any cache must preserve live authorization checks
and catalog/content invalidation.

## WebDAV configuration and current limits

Enable only on the consumer host with `Romd__Vfs__Enabled=true`; the default is
false. Admission limits under `Romd__Vfs__` are:

| Setting | Default | Valid range |
| --- | --- | --- |
| `MaximumConcurrentRequests` | 8 | 1–128 |
| `MaximumConcurrentRequestsPerUser` | 4 | 1–128 |
| `MaximumConcurrentRequestsPerConnection` | 4 | 1–128 |
| `MaximumQueuedRequests` | 128 | 1–4096 |
| `MaximumQueuedRequestsPerUser` | 32 | 1–4096 |
| `MaximumQueuedRequestsPerConnection` | 16 | 1–4096 |
| `MaximumQueueWaitMilliseconds` | 10000 | 1–60000 |
| `MaximumDirectoryEntries` | 20000 | 1–100000 |
| `MaximumConcurrentMetadataRequests` | 2 | 1–32 |

PROPFIND, HEAD and OPTIONS use the separate metadata lane: two concurrent
requests globally by default, one per account and connection, under the same
bounded queue policy. The default active budget is therefore eight content
requests plus two metadata requests, which keeps listings progressing while
transfers hold every content slot.

Invalid limits fail startup. All limits apply together; a connection cannot
exceed its account or global allowance. These defaults are provisional pending
stock-rclone workload measurements. Limits and queues are per consumer-host
process, not distributed across replicas.

Requests wait in a bounded queue. Available slots go to the eligible account
least recently served, then its eligible connection least recently served, with
FIFO ordering inside a connection. A saturated connection does not block another
eligible connection. Account queue bounds prevent adding connections from filling
the entire queue under the defaults. Bearer development requests share one
connection bucket per account. Queue capacity and wait limits return 503 with
`Retry-After: 1` and `Cache-Control: private, no-store`; disconnected waiters are
removed and granted slots released even if cancellation races admission.

Registered app credentials are checked again after admission, including rotation,
revocation, account security-stamp changes and account disablement. Catalog and
content authorization run after waiting against current library state. Responses
already authorized may finish. Queues do not recall cached content. Tracking is
removed when an account/connection has no active or waiting requests.
Directory overflow returns 507 before sending a partial listing.
PROPFIND bodies are limited to 16 KiB; DTDs and external entities are prohibited.
Infinite-depth enumeration is rejected. Mutation methods return 405.

The default `/vfs/library/` namespace groups release folders under DAT-derived
platform folders, with public Sqids in folder names. Original file paths live
below each release folder. Listings use bounded
database pages and reject library reassignment between pages. Names and hrefs
are XML/URL encoded independently. Metadata reads do not open CAS objects.
GET/HEAD authorize before opening content or evaluating conditional headers.
File ETags are SHA-256 identities. Partial reads retain the CAS integrity contract;
they do not perform whole-object cryptographic verification.

`Romd.Vfs` publishes `romd.vfs.active_requests`, `romd.vfs.queued_requests`,
`romd.vfs.queue_wait` (seconds), `romd.vfs.admission_rejected` (bounded
`queue_full`/`timeout` reason tags), and `romd.vfs.request_duration` (seconds, bounded method/status tags). Combine those
with `Romd.Storage` meters when measuring client workloads. Request duration
includes post-admission authentication, metadata lookup and transfer; queue wait
is measured separately. It is not storage-read latency alone.
`romd.vfs.response_bytes` counts completed HTTP body writes, including metadata
responses. It does not count TCP acknowledgements or filesystem application
reads. Queue metrics identify their lane, and failure counters their reason.
Identifiers stay out of metric labels to keep cardinality bounded. Enable Debug
logging for `Romd.Consumer.Application.Vfs.ConsumerVfsService` to correlate
connection ids, ETags, `Content-Range`, bytes and outcomes when analyzing
repeated versus unique ranges. No app password or `Authorization` header is
recorded.

## Registered connections

The consumer API serves `/api/me/connections` (list, create, update,
rotate and revoke). Management requires the normal consumer bearer sign-in;
app credentials authenticate only `/vfs`, over HTTPS. The Connections page sits
under the account menu. It shows issuance secrets only in the one-time dialog,
which copies the standard WebDAV fields or downloads `romd.conf`.

`VfsConnections` stores a SHA-256 verifier of a 256-bit random password and the
account security stamp, never the password or the export. A stamp change
invalidates existing credentials without a second revocation list. An
invalidated connection can be rotated after signing in again; an explicitly
revoked connection cannot be restored. An account may hold up to 32 active
connections. Listing returns every active connection plus recent history,
bounded to 200 records. Last-used writes are throttled to once per five minutes.

Before issuing connections, enable `Romd:Vfs:Enabled` and set
`Romd:ConsumerHost:PublicUrl` to the HTTPS consumer origin. Exports use that
configured origin, never the incoming `Host` header. Management reads and
revocations stay available while VFS is disabled.

Schema 46 added the release/path lookup index. Schema 53 added each
connection's closed `stored`/`original` format preference; existing and new
connections default to `stored`. Creation accepts an optional `format`, and the
existing PUT updates the name and optional format; omitting `format` keeps the
saved choice, and changing it does not rotate credentials. Authentication
re-reads the choice after admission and requires the same connection and
account.

`DefaultLibraryLayout` validates each release's complete resulting path set
with the Domain portable-path rules on every listing and file read. It omits
releases it cannot project from platform listings and refuses file reads in
them; a platform whose releases are all unprojectable appears as an empty
folder. Companion paths are never sanitized, and rule changes need no
migration or catalog rebuild. Admin library-visibility diagnostics compute the
rejection reason on demand. Library membership comes from publication, while
names and file requirements come from the live catalog, matching consumer
download manifests.

The bearer mount helper requires `Romd:Vfs:AllowDevelopmentBearer=true`
(default `false`) and is for development only; release connection setup uses
app credentials.

## Qualification measurements

A qualification run first records the actual installed version of every
component, then verifies installation, FUSE availability and permissions,
mount-at-login and reboot behavior, and updates on the actual target. A run on
one Linux distribution makes no claim for others.

It records latency distributions, time to first launch,
transferred bytes, application bytes read, transfer amplification
(transferred bytes divided by application bytes), request counts, cache
occupancy, and relevant client and server resource use. It defines where each
byte counter is measured and distinguishes repeated reads from speculative
prefetch. Workload-specific acceptance thresholds are set before a pass is
declared.

Runs separate cold and warm caches, frontend scans and gameplay, file sizes,
client versions and network conditions. They measure real WAN bandwidth,
latency, loss and interruptions; an application delay is not a complete
network simulation. They cover large discs, positional and EOF reads, long
sessions and multiple connections, and measure amplification on each target
rather than assuming a known overhead or its absence.

The endpoint is excluded from OpenAPI because it serves WebDAV rather than a JSON
API. API client scope: consumer; generated clients and JSON contracts remain
unchanged. Production uses HTTPS, registered connection app passwords and
deliberate client cache settings. Access revocation cannot recall client
bytes already cached. Do not interpret the opt-in flag as a production-readiness
claim. Check the [client matrix](media-status.md#network-client-scope) before
advertising a mount/emulator combination; local protocol checks do not establish
realistic gameplay, WAN or long-soak behavior.
