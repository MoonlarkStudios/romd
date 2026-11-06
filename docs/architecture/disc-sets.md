# Disc sets, layouts and published delivery

The server groups releases, retains descriptors and publishes exact audience
selections. Clients install those snapshots and own emulator adaptation.
[Original disc-set decisions](../decisions/original-disc-sets.md) own the rules
and rationale; [console acquisition](../../clients/romd_console/docs/original-disc-sets.md)
owns local install/repair. [Media architecture](game-media.md) owns reconstruction
and compression. Grouping and byte delivery establish no gameplay qualification;
see [media status](media-status.md#qualification-status).

## Grouping and retained decisions

`DiscSets` has a stable integer/Sqid identity scoped to a system and catalog
source, a GUID revision, a versioned proposal and an independent pinned correction.
Catalog rebuilds upsert by source/system and a digest of the parser's retained
edition key. Region, language, revision and edition qualifiers stay significant;
unknown totals are never inferred from the highest ordinal. Ambiguous names,
contradictory totals, duplicate ordinals and unusual sibling roles require review.

Accept, reject, correct and reset decisions pin ordered source entry keys, title
IDs and release-content fingerprints. Rebuild-owned numeric row IDs do not supply
that identity. Unchanged natural keys/content reconnect after DAT replacement;
renames, changed requirements and title conflicts remain unresolved. Manual
corrections do not expand when a proposal gains discs. Rejection survives refresh;
retained decisions keep their orphaned title reachable rather than cascading away.

Proposal refresh shares the catalog convergence transaction/savepoint. Rebuilds
and admin decisions take the catalog topology fence. Stale decisions conflict;
committed decisions rotate revision and record actor, time and reason. They
create neither ownership nor consumer publication.

Admin endpoints require Admin:

| Method/path | Contract |
| --- | --- |
| `GET /api/disc-sets?titleId=...&cursor=...&limit=100` | Common Page of proposals, decisions and member-resolution issues; query-bound opaque keysets, maximum 100 |
| `PATCH /api/disc-sets/{discSetId}` | `If-Match`-conditional accept/reject/correct/reset, with reason and optional corrected name, expected count and entry-key/ordinal membership |

Corrected ordinals are unique and within 1–99. All members resolve to distinct
releases of one title in the same source/system. Missing slots stay explicit.

## Original descriptor intake and repair

Catalog-matched CUEs follow ordinary ROM ingestion. An unmatched original CUE is
intercepted before generic unidentified-ROM admission, retained byte for byte,
and recorded as a descriptor attachment. It does not create a DAT requirement.
Tracked-only imports reject unmatched descriptors; rejected copy/move sources
remain preserved. Unsupported GDI/CCD do not enter the CUE lane.

Intake accepts bounded UTF-8 CUEs up to 1 MiB and 100 FILE dependencies. Paths
resolve relative to the uploaded descriptor with portable path/alias/collision
checks. Unsupported encoding and missing or malformed references remain visible
review issues. Parsing describes dependencies; it verifies neither CD sectors
nor emulator compatibility.

Uploads, archives and path imports share the upload journal. Descriptor storage,
attachment and successful outcome publish atomically. After sibling outcomes
commit, job-relative names locate candidates; exact content identities and
fingerprints establish associations. Import order and similar filenames do not.
A descriptor-only upload can later bind stored catalog-matched content explicitly.

In **Disc descriptors** under Sources & Files, or the title's Discs link, inspect
pending/accepted/rejected/detached/stale layouts. Confirm an exact release and
every required dependency by path, hash and length. Required catalog companions
must be satisfied; arbitrary uncataloged sidecars are not admitted. Confirmation
pins system, release fingerprint, original descriptor and exact file bindings.
Replacing a layout requires its observed ID/revision and validates the replacement
before detach. Reject/detach retain audit and file custody. Catalog drift requires
repair rather than reassignment to a similarly named edition.

Admin-only descriptor routes use keyset Pages with `systemKey`, `cursor`, `limit`:

- `GET /api/original-disc-layouts`
- `GET /api/original-disc-layouts/{layoutId}`
- `GET /api/original-disc-layouts/{layoutId}/candidates`
- `PATCH /api/original-disc-layouts/{layoutId}`

Decisions carry observed revision, action, exact release/dependency choices,
optional observed replacement ID/revision and reason. `IOriginalDiscLayoutReader`
returns retained identities within the caller's consistent authorization snapshot;
it grants no independent permission.

Descriptor records, historical dependencies, decisions and explicit file roots
are retained without automatic expiry/purge. Per-intake limits do not bound total
retained storage. Backups include database and shared content; baseline downgrades
are unsupported. A future retention policy must preserve the decision/audit ledger.

## Library selection and publication

The Library configuration's `selectedDiscSetIds` stores explicit choices.
**Games → Disc sets** offers accepted, policy-eligible candidates. A saved Library
retains its own unresolved selections for repair/removal; new or cross-Library
invalid selections are rejected. Other rule edits and selection-mode changes
preserve these choices. Group updates do not discard unsaved form values.

Save then **Refresh library**. Publication atomically snapshots selected IDs,
ordered pinned definitions/revisions and release projection. Consumers read that
publication, not live corrections or editable rules. Group edits mark configured
and published selections pending through the Library materialization revision.
No grouping or selection action automatically publishes access.

Every known resolved member needs eligible/exposed metadata in the same current
published Library. An excluded member hides the group; partial choice cannot
bypass it or combine Libraries. Ownership/bytes are separate: policies allowing
missing games may expose an authorized missing-media status but no absent bytes.
Unpublished/invalid Libraries fail closed; inconsistent publication is unavailable.

## Status and explicit selection

Authenticated consumer routes are:

| Method/path | Purpose |
| --- | --- |
| `GET /api/disc-sets?titleId=...&cursor=...&limit=100` | Status Page with query-bound opaque keysets; follow `nextCursor`, not visible row count |
| `GET /api/disc-sets/{discSetId}/status` | Exact v1 observed group state |
| `POST /api/disc-sets/{discSetId}/selection-manifest` | Explicit ordered selected delivery |
| `POST /api/disc-sets/{discSetId}/access/preview` | Body-required installed-snapshot access check |
| `POST /api/disc-sets/{discSetId}/manifest?revision=...` | All-known convenience path; no implicit partial consent |

V1 status binds contract version, server instance, set/revision, title/system,
name and `observationId`, expected count or null, missing ordinals and ordered
known members. Members identify release/original ordinal, known layout ID/version,
and `ready`, `missing-media`, `descriptor-required`, `unsupported-layout` or
`stale-layout`. Consumer status excludes restricted identities and admin repair data.

Selection carries version, observed revision/observation, canonical ordered
release IDs, explicit `allowPartial` and optional `requiredSnapshotId`. Empty,
duplicate, foreign, out-of-order and stale choices fail. All known ready discs
with an unknown total need no partial consent; an omitted known disc or declared
missing slot does. A one-disc partial install is valid only with explicit consent
and that disc's complete dependency closure. Members are never renumbered.

Responses distinguish selection kind/completeness, selected IDs, omitted release
IDs with original ordinals and missing expected slots. Each selected disc has an
entry point, layout identity/version and complete paths, measured hashes/lengths
and grants. One repeatable-read authorization snapshot binds Library assignment,
publication, set revision, requirements, ownership and byte-target selection.
Unknown/denied/unresolved sets disclose no partial grants; unavailable media or
inconsistent publication conflicts rather than creating permission.

## Original and derived bytes

Supported layouts are one cataloged CUE or explicitly bound descriptor per disc
with its complete dependency closure, or a single ISO/IMG. Multiple CUEs,
GDI/CCD, nested playlists and inferred entry points fail closed. Retained catalog
CD bindings can also deliver independently measured regenerated descriptors:
DAT-exact CUEs are originals; other CUEs remain `Derived` with their actual hash.
The [media architecture](game-media.md#compressed-only-input-and-descriptors)
defines that preservation boundary. No arbitrary blob or invented ROM identity
is used to fill a missing original.

Original ROM grants retain their existing scope. Descriptor capabilities under
`/api/delivery/original-layout/{token}` separately bind owner Library, release,
layout/version, file identity, hash and length. Every complete layout is checked
before issuing grants. Expiry, tamper and cross-lane checks remain enforced.

Portable path-set validation rejects unsafe paths, aliases, duplicates and
file/directory collisions. Bounded CUE reads verify exact FILE references within
the authorized path set, including case, containment and completeness. Separate
`disc-01/`, `disc-02/` directories retain authored relative references while
allowing the same filenames on different discs.

The derived `playlist.m3u` is UTF-8 without BOM: ordered relative entry points
and one trailing LF. It carries exact text, length, SHA-256 and
`m3u-sha256:{digest}` identity. Clients verify/materialize it alongside each
granted file; it is not a catalog ROM or archival equivalent.

## Snapshot renewal and access

`deliverySnapshotId` binds server/set revision, selected and omitted membership,
count expectation, layout identity/version, ordered paths/hashes/lengths and
playlist hash. It excludes expiring grants and transient readiness. A revision
alone does not pin bytes. Renewal with `requiredSnapshotId` issues fresh grants
only for that exact plan under current policy; changed plans require replanning.

Access echoes exact server/set/revision/snapshot/selection/layout identities.
Only exact authoritative allow can establish cached authority. Policy denial or
authoritative identity change blocks the old installation. Transient unavailability
or inconsistent publication returns 409 and neither revokes nor creates cached
permission. Check known layout identity before byte readiness: detach/replacement
invalidates an old snapshot even when current ROM bytes are missing; unknown
bindings remain unavailable. Client compare-and-set binds active profile/server
and installed snapshot. Download URLs never authorize launch.

Issued capabilities retain their normal bounded lifetime. There is no immediate
revocation protocol or durable server resume store. Clients own installation,
repair, local verification and runtime adaptation; group ZIP/export/VFS projection,
browser group play, other descriptor families and production disc-switch/shared-
save acceptance remain separate scope. See [console acquisition](../../clients/romd_console/docs/original-disc-sets.md).

Changes need the [backend/host and generated-client validation routes](../../CONTRIBUTING.md#validating):
manual publication, stale observations, exact renewal, partial consent, dependency
closure, grant scope, retention/rollback and unavailable-versus-revoked behavior.
