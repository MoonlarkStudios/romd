# Reference catalog architecture

Status: stable-key resource APIs, ownership-aware editing, sparse overrides,
conditional writes, and a single current catalog snapshot are implemented.
All six reference families have specific relational entities and a typed import
component. Core facts, ownership, and supported presentation overrides use explicit columns.

ROMD owns `reference-data/catalog/`. Stable identities have one authoring
owner; different application releases can ship different catalog versions.
The worker installs built-in definitions and assets without contacting a provider.
The former external-sync development database is a fresh-start cutover; there is
no adoption or conflict-migration layer. Existing development data must be backed
up or recreated deliberately before switching. This implementation does not reset it.
Existing platform, region, and language rows retain database identities for
relationships and queries. Their canonical keys are independent of local names.

The existing `Platforms`, `Regions`, and `GameLanguages` rows retain their
relational identities. `Companies`, `RatingBoards`, and `Ratings` have stable
key identities. Each resource stores `Ownership`, `BuiltInVersion`, retirement,
and its core base facts in explicit columns. `SystemCompanies` and rating-board
relationships use restrictive foreign keys. Artwork base hashes also reference
`ReferenceAssets`. Region/language ordering and language codes remain relational.

Only systems and companies support installation-created identities and presentation
overrides. Ratings, rating boards, regions, and languages are ROMD-owned and
read-only outside the typed importer. JSON is used for published snapshots, not
reference definitions or overrides. There is no generic definition
table, universal definition record, or reference-entity base class. The domain
`ReferenceMetadata` value is composed from columns when needed. Unregistered operational platform rows have no canonical key, ownership,
version, or base facts. Unregistered region and language rows, which DAT ingest
creates for unrecognized tokens, carry a key minted once in the reserved `dat-`
namespace from the normalized token, and no ownership, version, or base facts.
Minting a key is not registration: ownership alone identifies a registered row,
and built-in keys never use the `dat-` prefix. A key is minted once and never
recomputed. Minting takes one PostgreSQL advisory lock shared by regions and
languages and holds it until the minting transaction commits, so minting is
refused outside a transaction. One lock serves both kinds because one DAT ingest
mints both in whatever order its games name them; a lock per collection would
let two ingests that mint in opposite orders deadlock. An ingest reads each
distinct token once, inside its transaction, and locks the row the token names
`FOR KEY SHARE`, as its links will. No host caches aliases between ingests, so
the admin host's merges and alias edits, and rows that a rolled-back ingest
created, reach the worker's next ingest. An unknown token takes the minting
lock, then is read again: the lock's previous holder has committed or rolled
back by then, in whichever process it ran. A read that waited for a merge of the
token's row misses although the merge moved the alias, so it reads again while
the alias exists. When another writer commits the alias while the ingest creates
the token's row, the ingest rolls its attempt back to a savepoint, leaving no
row, alias or audit of it, and uses that writer's row. An ingest waiting for the
lock has written no region, language or alias, so it holds nothing the holder's
minting needs. An alias add for a token that a running ingest has just
auto-created waits on the alias's unique index until that ingest ends, for at
most the alias write's lock timeout (below). Keys
follow natural-key semantics: a merge deletes the source row and frees its key.
A different token that shares the freed key's slug can mint it again, as `usa!`
can take `dat-usa`; the same token mints it again only after its alias leaves
the merge target, because until then the token resolves to the target. The
schema migration that introduced minted keys backfilled each existing row, in id
order, from its lowest-id alias. Rolling it back clears those keys; reapplying
it mints them again from the rows and aliases at that time, so a row can receive
a different key when its own aliases changed in between (a merge moved in a
lower-id alias, or its creating alias was removed), or when an earlier-minted
row was merged away and freed a candidate that the row now takes first. Audit
events name regions and languages, and their aliases, by key. Registered rows
require a key, ownership, and complete base facts. Database constraints enforce
these states; readers use ownership to identify registration. Registration never
infers a key from a display name.

`ReferenceAssetEntity`, `ReferenceAssetOwnerEntity`, and
`ReferenceCatalogStateEntity` are separate infrastructure entities. Publication
state remains one application-maintained JSON snapshot; no PostgreSQL materialized
view or historical snapshot store is introduced.

Built-in facts and explicit local overrides remain distinguishable. Installing
new base facts preserves overrides. An identity collision fails rather than
taking over a registration. Publishers cannot create or reassign identities.

Built-in presentation artwork lives in `reference-data/assets/presentation/`,
next to its canonical catalog definitions, and is embedded in the server.
Client packages contain no duplicate system/rating artwork or per-system fallback
maps. Web rendering consumes the effective catalog's URLs. Flutter retains a
complete downloaded snapshot and verified artwork per server for offline use;
without that data it shows generic text/icons. An explicit null icon remains
hidden and cannot be replaced by a client-bundled default.

## Resource model and stable identity

Use **systems** consistently in public routes, contracts, filters, and client
terminology. A system key such as `snes` is immutable and independent of a
database ID, display name, compact label, or editable alias. Database IDs remain
internal relationship keys. Public system relationships and emulator runtime
mappings use the stable system key. Installation-owned keys are stable within
their installation; offline clients retain the server instance identity as well.

| Reference resource | Collection route | Item route | Facts |
| --- | --- | --- | --- |
| Systems | `/api/systems` | `/api/systems/{systemKey}` | Names, compact labels, manufacturer relationships, artwork |
| Companies | `/api/companies` | `/api/companies/{companyKey}` | Manufacturer/publisher identities and names |
| Regions | `/api/regions` | `/api/regions/{regionKey}` | Names, ordering, aliases |
| Languages | `/api/languages` | `/api/languages/{languageKey}` | Names, language codes, aliases |
| Rating boards | `/api/rating-boards` | `/api/rating-boards/{board}` | Board identity, name, description |
| Ratings | `/api/rating-boards/{board}/ratings` | `/api/rating-boards/{board}/ratings/{ratingCode}` | Rating identity, description, classification facts, artwork |

The consumer host serves registered regions and languages only, as arrays bounded
by the reference catalog. The admin host serves every region and language row,
registered or minted, as a keyset-paged collection. Its `query` matches each
row's normalized name, key (and a language's code) and every alias, so a search
finds aliases a row does not embed; the cursor is bound to the query. Each row
embeds its first ten aliases in id order as a `Page`, and
`GET /api/regions/{regionKey}/aliases` continues them with a cursor bound to the
row. The row's validator covers exactly what it embeds, so an alias past the
embedded page leaves its `ETag` unchanged. A merge moves aliases with their ids,
so a traversal running across a merge into the row never repeats an alias but
may miss one; reading again from the first page sees them all. Merge
(`POST /api/regions/{regionKey}/merge`, with a side-effect-free
`…/merge/preview`) and alias writes (`/api/regions/{regionKey}/aliases/{aliasId}`)
live under the row, and the same under languages. A merge locks its source
exclusively, because it deletes it. Its target and alias writes take the weakest
lock that serializes against a merge, so the key-share locks a running ingest
holds on every row it has resolved never block them (an alias add can still wait
on the alias's unique index, as above); merges into one target, and removals of
one row's aliases, still take turns. An ingest takes its key-share locks in the
order its tokens arrive, not in id order, and that is safe: an ingest waits only
for a merge's source, and a merge waits for an ingest only while it takes its
own source, so a cycle through an ingest needs at least one more merge. PostgreSQL
aborts one participant of such a three-way cycle: an aborted ingest is deferred
as a capacity failure and runs again, and an aborted merge retries. An alias write
on a row that a merge removed answers 404, and an alias added first moves with
the merge. A catalog rebuild deletes a platform's release links and inserts them
again, so a merge can deadlock with it, and PostgreSQL aborts one of the two. A
merge that loses runs once more and waits for the rebuild; it answers 409 if it
loses again, or if the rebuild outlasts the merge's 10-second lock timeout. A
rebuild that loses leaves the catalog as it was and the platform Dirty, and the
recovery dispatcher rebuilds it. A merge also bounds each lock wait at 10
seconds, so one queued behind a running ingest that has resolved its source
answers the same retryable 409 when the wait times out. While it waits, it holds
its target when the target's id is the lower one, because it locks both rows in
id order, so merges into that target and removals of its aliases queue behind
it. Alias adds and removals bound their lock waits at the same 10 seconds: one
queued behind a merge of its row, behind another removal on it, or on the unique
index behind an ingest's uncommitted equal alias, answers a retryable 409
`Taxonomy.AliasConflict`, rolls back and changes nothing. It is not retried
internally, because the work it waited for may still run; sent again, it meets
that work's outcome. The published snapshot stays registered-only, so DAT ingest
never republishes it.

Ratings are identified by their board and stable code together. Human-readable
labels are never used to infer or reassign an identity. Aliases and provider
mappings remain explicit relationships, for example
`/api/systems/{systemKey}/aliases`, with creation and deletion on those resources.

Reference definitions and library membership are different resources:

- `GET /api/systems` lists effective definitions, including systems with no
  titles in the user's library.
- `GET /api/me/library/systems` lists systems represented in the current user's
  authorized library, including library-specific counts.
- Related system resources such as titles, DATs, and BIOS use the same stable
  key and retain their existing host and access-policy boundaries.

`/api/systems` exposes effective reference resources on both hosts. Consumer
library projections and counts live at `/api/me/library/systems`. System
relationships and filters use stable keys; relational integers stay internal.

## Client contract and snapshots

Both API hosts expose the same effective reference facts. Consumer routes remain
read-only; administrative writes belong to the admin host. Resource reads return
the resolved representation regardless of whether its origin is ROMD-managed or
installation-owned. Clients do not need to know whether the server resolves that
representation from static definitions, database rows, or another implementation.

Retain an aggregate versioned snapshot for efficient startup and consistent
offline caching. Individual resource reads and aggregate snapshots must resolve
the same effective facts at the same revision; they are not separate sources of
truth. Assets and snapshots support the reference resources rather than becoming
additional taxonomies. Clients follow returned immutable artwork URLs instead
of constructing them from keys or assuming a storage layout.

Schema version describes the complete catalog wire shape. Built-in version
identifies the shipped source catalog. `catalogRevision` retains the existing
published content identity, including overrides and installation registrations.
The transfer manifest has its own `revision`, used by its `ETag`; it is not the
catalog identity. `contentHash` verifies the complete assembled catalog using
the canonical profile below, independently of the publisher's existing revision
hash algorithm.

Names, compact labels, rating descriptions, asset URL/hash/media type, and the
monochrome artwork property are facts. Clients retain typography, layout,
accessibility, rendering and error fallbacks. Unknown system keys remain strings.
Unknown images or media types fall back to text. No catalog entry grants library
access or establishes emulator support; access policy and runtime capability
remain separate authorities.

Web pages use effective facts embedded in resource responses; shared rating
presentation also consumes the completely assembled catalog. Both the admin
provider and Flutter fetch the manifest and every family page at its observed
`catalogRevision`. They validate page envelopes, natural-key uniqueness, strict
progress, family counts and the complete content hash before publishing a result.
An empty nonterminal page, cursor cycle, excess count or inconsistent terminal
state fails the transfer. Ratings use the `(board, code)` identity.

A page's pin, family and continuation must agree. A stale current-only pin returns
409 `ReferenceCatalog.SnapshotChanged`; clients discard all accumulated families
and restart from a fresh manifest at most once. A second conflict or any other
failure retains the previous complete catalog. Ordinary reference-list traversal
does not provide this snapshot guarantee.

Flutter retains its existing complete schema-1 JSON and hash-verified asset files
under the server instance identity. The assembled document's `revision` remains
`catalogRevision`; neither a manifest nor a page is written as a complete cache.
It promotes its current pointer only after all assets are available and the
current server generation still owns the refresh. Existing schema-1 offline
files and server-instance directory identities remain readable. Refresh failures
retain the last complete snapshot. The scope sits above navigation; cached metadata works after restart
without a session. Tokens never enter this cache. Flutter currently renders
raster system icons; unsupported artwork formats retain readable labels.

The server can replace its storage implementation without changing the client
contract. A new system needs facts and supported-format artwork, not new client
code. A new rendering capability or incompatible schema still requires a client
change.

### Canonical complete-content digest

`contentHash` is SHA-256 of UTF-8 `romd-reference-json-v1` encoding of the complete
schema-1 catalog, including its real published `revision`, all six family arrays
and every returned field, null and default. The profile operates on JSON values:

- Object member names are unique and sorted by ordinal UTF-16 code-unit order.
  Arrays retain their exact published sequence; clients never reorder rows to
  compute the digest.
- Strings escape quote and backslash, use `\b`, `\t`, `\n`, `\f` and `\r` for
  those controls, and lowercase `\u00xx` for the remaining U+0000–U+001F controls.
  Forward slash and all other valid Unicode are literal UTF-8. Lone surrogates
  are invalid.
- Null and booleans use their JSON literals. Numbers must be exact safe integer
  values from -9007199254740991 through 9007199254740991 and use plain decimal
  integer notation. Integral decimal/exponent input has the same value; negative
  zero encodes as `0`. Fractional, nonfinite and unsafe values are invalid.
- Encoding has no insignificant whitespace. Default C#, JavaScript or Dart
  object serialization is not a substitute for this profile.

The shared vectors at `tests/Fixtures/ReferenceCatalogCanonicalization.json`
cover Unicode and surrogate-pair ordering, all controls, null/default fields,
complete-family content, safe-integer edges and invalid values. The publisher's
existing opaque revision hashing and persisted snapshot JSON remain unchanged.

## Resource editing and overrides

For systems and companies, edit the resource directly. Storage ownership determines how the server persists
the edit, without changing the public editing contract:

- Editing a ROMD-managed resource records explicit field overrides. It never
  rewrites the built-in definition, and overrides survive built-in updates.
- Editing an installation-owned resource updates its owned definition.
- Both return the effective resource representation. Administrative reads also
  need ownership/provenance information so an editor can explain inheritance
  and offer appropriate reset and deletion controls.

Apply this method pattern where the resource supports the operation:

| Method and route | Semantics |
| --- | --- |
| `GET /resources` | List effective resources |
| `GET /resources/{key}` | Read one effective resource |
| `POST /resources` | Create an installation-owned resource; return `201 Created` and its `Location` |
| `PATCH /resources/{key}` | Change only explicitly supplied editable fields; return the effective resource |
| `DELETE /resources/{key}` | Delete an installation-owned resource when relationship constraints permit; return `204 No Content` |

Prefer `PATCH` for ordinary editing. An omitted field is unchanged; an explicit
null clears a nullable value. Unknown fields, immutable identity fields, and
invalid nulls are rejected. Editing a compact label must not pin an unchanged
description or icon as an override. If `PUT` is added later, it means complete
replacement of the editable representation, not partial updating.

Reject occupied keys and identity reassignment. Ordinary resource deletion cannot
delete a ROMD-managed definition. Installation-owned deletion must respect
references and cannot silently cascade through library or imported catalog data.

Use the common single-word subresource **overrides** for inspecting and resetting
ROMD-managed edits:

| Method and example | Semantics |
| --- | --- |
| `GET /api/systems/snes/overrides` | Read only explicitly overridden fields |
| `DELETE /api/systems/snes/overrides` | Remove all overrides and inherit built-in defaults |
| `DELETE /api/systems/snes/overrides/compactLabel` | Remove one field override and inherit its built-in value |

The same pattern applies to companies. The other reference types have no editing or override endpoints. Resetting overrides
is not resource deletion. Installation-owned resources have no built-in baseline
to restore; their editors must not offer a misleading reset-to-ROMD operation.

For example, `PATCH /api/systems/snes` with `{"icon": null}` explicitly hides the
icon. `DELETE /api/systems/snes/overrides/icon` restores inheritance. Resetting a
field is a distinct operation from assigning null, including when future built-in
versions change the inherited value.

Expose ETags on resource reads and require `If-Match` on edits, override resets,
and deletions. A stale precondition returns `412 Precondition Failed`; a missing
required precondition returns `428 Precondition Required`. Both whole and
per-field override resets use the ETag returned by `GET .../overrides`.
The server must check the
precondition and persist the mutation atomically; serializing writes alone does
not prevent a stale editor from overwriting a newer decision.

Define writable fields and permissions per resource. Presentation facts,
provider identity mappings, and classification facts used by access policy need
separate validation and authorization. The common resource pattern does not
make rating ages or policy behavior editable through a cosmetic metadata update.
Creating new rating boards/codes requires explicit backend support beyond the
current closed rating-policy model.

Mutations publish a new effective snapshot atomically with their persisted
changes. Only the current snapshot JSON is retained. Artwork URLs are content-addressed and immutable, with the bounded retention described below.

## Current implementation and cutover

The implemented snapshot and artwork endpoints are:

- `GET /api/catalog-snapshot`: authenticated bounded transfer manifest, with
  its own representation `revision` and ETag revalidation (`private, no-cache`).
- `GET /api/catalog-snapshot/{family}`: six typed page resources (`systems`,
  `companies`, `regions`, `languages`, `rating-boards`, `ratings`). Requests
  require `revision` equal to the manifest's `catalogRevision`; `limit` defaults
  to 100 and accepts 1–100. Cursors bind the family, catalog identity and forward
  published sequence. Pages answer 200 without a validator and never re-read
  live reference tables to build their facts.
- `GET /api/assets/{hash}`: immutable public display artwork.
- Admin `POST /api/assets`: PNG/WebP upload, at most 2 MiB, returning a hash and
  immutable asset Location.

Both hosts expose list/item reads for systems, companies, regions, languages,
rating boards, and board-scoped ratings. Systems and companies use bounded keyset
Pages on both hosts, ordered by `key:asc`; `limit` defaults to 100 and accepts
1–100. Query filters and the fixed order are cursor-bound. Explicit C collation makes
key comparison deterministic; this does not establish use of an existing
default-collation index or a query-plan qualification on another installation. Changed collection
operations reject unsupported names and repeated scalar query parameters.
Each system/company resource exposes its own required `revision`, agreeing with
its GET/write ETag; it is independent of the catalog or manifest identity.
System manufacturer relationships remain bounded at sixteen members.

Admin `/api/system-setup` also pages before resolving its row status/counts and
manufacturer facts. Search includes names and name aliases on the server; setup
rows no longer embed every alias. Explicit alias browsing remains available on
`/api/systems/{systemKey}/aliases`. Status polling does not fetch all aliases.

Snapshot family reads parse exactly one current published JSON row per request.
The response is bounded, but this storage implementation still reads and parses
O(complete catalog) data per page; it is not an indexed historical snapshot
store or a large-installation performance qualification.

Only systems and companies expose admin
creation, PATCH, override inspection/reset, and ETag-conditional deletion.
System creation retains the existing manager permission; other reference writes
require admin. The old `POST /api/platforms` and independent creation command are
removed. The Inbox creates through `POST /api/systems` and selects registered company keys.

Systems and companies have resource-specific application commands and repository
ports. Handlers own mutation policy and conditional writes; repositories stage
persistence changes. `IReferenceMutationSession` coordinates flushing, the catalog
lock, publication, and commit. All six families have focused readers and specific
DTOs. There is no universal resource DTO or kind-dispatched persistence service.

Required name/compact-label override columns use null for inheritance. Nullable
values have explicit presence flags: `HasDescriptionOverride` plus
`DescriptionOverride`, and for systems `HasArtworkOverride` plus
`ArtworkOverrideHash`. A true flag with a null value explicitly clears the field;
a false flag inherits. Reset clears both the flag and stored value. Each supported
resource's pure `Overrides.Apply` function owns effective-value semantics, shared
by reads, embedded summaries, and publication. Override response DTOs expose these
values and flags directly. Classification, identity, provider mappings, and
manufacturer relationships are never presentation overrides.

The typed catalog migration preserves IDs, relationships, and supported system/
company overrides, including explicit clears. Persisted installation-owned
regions/languages/boards/ratings or any overrides for those categories cause a
transactional migration conflict listing their identities. Operators must export
and explicitly resolve that data before retrying; the migration never silently
drops it. Unregistered operational taxonomy rows and local import aliases remain
separate and are preserved.

`ReferenceCatalogPublisher` reads typed effective facts directly. It produces
both the snapshot and compatibility projections without constructing endpoint
services or consuming their DTOs. `ReferenceArtworkOwnershipIndex` is explicitly
a derived index over base and override asset references. Its polymorphic owner
key has no foreign key; writes are confined to atomic publication, and acceptance
coverage verifies that a complete rebuild repairs missing or stale owners.

System responses include `manufacturers: [{ key, name }]`, resolving effective
company names through the relationship. Snapshot systems include the same
summaries alongside manufacturer keys. Company renames, resets, and built-in
updates therefore affect dependent system representations and ETags. Stale
conditional writes fail even when the changed fact belongs to a related company.

The queryable columns `Company.Name`, `Platform.Name`, and `Platform.Manufacturer` are derived.
Region/language names and ordering are likewise effective compatibility columns.
Snapshot publication refreshes these in the same transaction for every reference
mutation and built-in installation. The legacy `PlatformRepository.ToDomain`
path consumes those projections; consumer library system queries and admin
system setup resolve manufacturer names through `SystemCompanies` directly.
No reference command accepts manufacturer display text as an independent fact.
Legacy unregistered imported rows are outside the reference registry and cannot
silently acquire canonical ownership.

Deletion checks dependencies before removing an installation-owned identity.
Company foreign keys restrict deletion while systems refer to it; system
deletion explicitly removes its owned company links only after checking all
other mapped dependencies. Built-in identities require explicit retirement in
the catalog. Conditional checks and publication are serialized by the catalog
transaction; deletion also uses serializable isolation.

`TypedSystemsAndCompanies` moves existing definitions into their authoritative
typed storage, preserving platform IDs, keys, ownership, overrides (including
explicit null), company links, and CAS owners. It requires an existing canonical
key match and never infers one from a name. This is an ordinary schema migration,
not the removed reference-asset upgrade helper.

`TypedReferenceCatalog` completes that separation, moving remaining definitions
into their resource tables and flattening system/company facts. It copies values
before dropping old columns/table, preserving existing relational IDs, explicit
null overrides, and artwork owners. It does not reset the development library.

### Typed import and relationship ownership

`RomdCatalogInput` parses and validates resource-specific input models before
mutation. `RomdCatalogImporter` coordinates one transaction and catalog lock:
version/identity/relationship validation, dependency-ordered typed upserts,
explicit retirement, relationship reconciliation, artwork ownership, and effective
snapshot publication. An exception rolls back all database changes and clears
tracked mutations. Older catalog versions and missing managed definitions are
rejected; retirement must remain explicit in the bundle.

Aliases/provider mappings record nullable `ReferenceOwnership`: `Romd` belongs
to the importer, `Installation` is an explicit local addition, and null means
historical provenance is unresolved. Imports prune obsolete ROMD-owned rows,
then add desired rows; pruning across the entire catalog permits deliberate
alias moves independent of import order. Local additions retain ownership,
including identical entries. A local provider mapping for the same system and
provider takes precedence over a bundled value by explicit local authority.

Existing pre-provenance relationships migrate to unresolved, not local and not
ROMD-owned. A changed bundled provider mapping with unresolved ownership raises
a specific conflict with the stored and requested values, rolling back import.
It cannot report successful installation while silently keeping the old mapping.
Worker startup reports unresolved counts even when no value conflicts exist.
An operator must explicitly classify or replace an unresolved relationship;
the importer never infers provenance from value equality. A local name alias
that resolves to another identity fails validation before storing artwork.

Deleting a ROMD-owned alias does not create a persistent suppression: the next
import restores a relationship still declared by the bundle. An admin-created
replacement is installation-owned. This development migration policy does not
claim that missing historical provenance can be recovered automatically.

Consumer title cards/details, collection summaries/entries, and recent activity
embed the shared `SystemSummaryDto`: `key`, `name`, `compactLabel`, and `icon`.
A batched server projection resolves effective facts and sparse overrides; clients
can render the response without joining the reference catalog. Internal database
IDs remain separate application query fields. The web player selects runtime
mappings by explicit system key, independently of editable display names.
Consumer catalog/collection filters accept `systemKey`. Reference resources use
`/api/systems/{systemKey}`; library-specific counts and browse projections use
`/api/me/library/systems` and `/api/me/library/systems/{systemKey}`. Related
system resources, BIOS requests, management filters, library restrictions and
release manifests carry stable keys. `/api/platforms` and snapshot `platformId`
are removed. Database integer foreign keys remain internal. Generated web
clients and Flutter consume the same key contract.

Consumer title and catalog responses also embed effective rating presentation:
board identity and display name, code, label, description, and immutable artwork
reference. The server resolves these facts in batches, using the same overrides
as reference resource reads. Consumer web pages render their resource responses
without fetching or joining a catalog snapshot. Snapshot synchronization remains
available for offline clients and catalog-wide editor workflows; shared UI
components accept presentation facts directly.

## Identity namespaces and catalog lifecycle

Installation-created systems and companies must use a
`local-` key (for example `local-my-console`), with a nonempty suffix. Built-in
catalog definitions may not use this prefix. Display names and language codes
are not unique identities. A local alias pointing at another identity is a
conflict requiring explicit resolution; the importer neither skips it silently
nor reassigns it.

Installing an official definition for the same real-world system creates a
separate identity. There is no name-based adoption. A future explicit adoption
operation must reconcile dependent records transactionally, let the admin keep
selected presentation overrides, and preserve old-key resolution through an
explicit alias or redirect. That workflow is not part of this cutover.

Increment `catalogVersion` whenever shipped definitions or their artwork change.
Catalog installation is serialized and transactional. Before any writes, the
worker rejects a catalog version older than the installed built-in version.
Application rollback therefore requires a compatible catalog version, or a
coordinated restore of the database and assets from a matching backup. Running
an older worker against newer definitions does not overwrite them.

Removal is explicit: retain the key and definition in the repository with
`"retired": true`. Omitting an installed built-in fails validation before writes.
Retirement preserves relational records, references, effective labels and
artwork, and is exposed in resource reads and snapshots. Retired systems cannot
be newly enrolled in a DAT subscription or accept a reviewed DAT candidate.
Existing library content remains displayable; retirement grants no new runtime
capability or access. Reinstatement is an explicit later catalog revision.

Built-in updates replace base facts while retaining installation overrides.
Resetting an override exposes the current built-in default. Snapshot revisions
and artwork URLs remain immutable; clients can retain them for offline use.

Installation-owned systems accept registered `manufacturerKeys` on creation and
PATCH. An empty array clears the links; omitted leaves them unchanged; null and
unresolved keys are rejected. Built-in relationships remain catalog-owned and
cannot be changed through presentation overrides. A dedicated reference editor
and explicit custom-system adoption remain follow-ups.

## DAT and MAME boundaries

The DAT publisher owns acquisition, signed catalog definitions and DAT updates.
It vendors ROMD's system-key export for authoring validation. A published system
unknown to a receiving ROMD version remains visible in discovery; enrollment
requires an explicitly registered matching identity. It is never silently mapped
by display name or discarded by a generated system list.

MAME metadata belongs to versioned imported catalogs. Machines, devices, BIOS
dependencies, software lists, items, and parts are distinct records with explicit
upstream-to-ROMD system mappings. They do not expand `PlatformIds` into a machine
registry. This change establishes that boundary; it does not implement a MAME
importer or imply MAME runtime support.

## Generation and verification

The Roslyn generator produces well-known constants and closed rating policy
vocabularies during compilation. The companion CLI shares its parser and emits
validated seed/export artifacts. OpenAPI generates web contracts. Generated
system constants are conveniences, never exhaustive runtime validation.

Run `mise run test`, `mise run test:integration`, web `pnpm lint/test/build`,
and console `mise run analyze/test`. Publisher changes have their own
`mise run check` and immutable reader pin in ROMD's Dockerfile. No deployment
or signed feed is published by generating or installing reference definitions.

Acceptance coverage for the resource cutover must prove: stable-key resolution
across resource relationships and runtime lookup; identical effective facts in
resource and snapshot reads; unfamiliar entries rendering without a rebuild;
partial edits preserving inheritance of untouched fields; null versus reset
semantics; overrides surviving built-in updates; conditional writes rejecting
stale edits; deletion respecting ownership and references; and Flutter retaining
the matching metadata and assets offline. Preserve the existing metadata UI.


## Reference artwork lifecycle

Reference artwork uses the shared content-addressable store (CAS), with the existing
`/api/assets/{hash}` URLs. Database metadata records the media type and `Files`
relationship; it does not duplicate image bytes. Storage reporting counts these
files as media. Deduplication can share bytes with other file owners.

Only the current effective catalog snapshot is stored. Its revision remains an
ETag/content hash; previous revisions have no historical retrieval endpoint.
Publication updates facts, ownership, and the current JSON in one transaction.
Both base and override artwork have explicit ownership, including defaults hidden
by an override, so reset never depends on a retention window.

Unattached uploads reserve their artwork for seven days. When artwork loses its
last reference owner, a fresh seven-day reservation protects recently rendered
pages and in-flight synchronization. Reuploading the same bytes renews that
reservation. The daily orphan cleanup job releases expired, unowned reference
metadata before the existing CAS collector checks every file owner and removes
unreferenced bytes. Files shared with other features remain protected. This is a
reference-specific reservation, not a universal `UnreferencedSince` field or a
change to ROM, DAT, or game-artwork retention.

Flutter keeps its complete hash-verified offline cache. If an asset returns 404
during synchronization, it fetches the current catalog once and retries only if
the revision changed. Failure preserves the last complete local snapshot.

The fresh application baseline creates the CAS relationships,
explicit artwork ownership, and current snapshot directly. Worker provisioning
performs ordinary EF migrations; reference seeding stores bundled artwork through
CAS afterward. There is no legacy byte-copy path or historical snapshot schema.
Downgrade requires a matching database and CAS backup.

## Consistent reads and taxonomy capabilities

Standalone system GET and list reads use a PostgreSQL repeatable-read transaction
covering platform, manufacturer/company and artwork queries through resource
and ETag construction. Reads inside a mutation reuse its transaction and catalog
lock. Deterministic concurrency tests cover coherent GET and list results.

Admin region and language contracts expose `canMerge` using the server's
registered-identity predicate. Aliases expose Romd, Installation or Unresolved
ownership. The UI hides invalid merge actions and explains source authority;
local and unresolved aliases retain their permitted removal operations.

Current reference tables and artwork relationships are installed by the fresh
application baseline. Retired reference migration chains and developer-database
reconciliation procedures are not supported upgrade paths. See the
[database boundary](../self-hosting/configuration.md#database-and-installation-boundary).
