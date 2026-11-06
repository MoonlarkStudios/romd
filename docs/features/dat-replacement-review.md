# Manual DAT replacement review

The **Systems → Catalog sources → Upload new version** flow validates and previews an extracted
Logiqx DAT/XML document before accepting a replacement job. The current catalog
remains active during preview. The browser retains the selected file; closing
the review or reloading discards the preview. Saved subscriptions use the same validation and review through the
[subscription flow](dat-subscriptions.md). This manual-upload path keeps its
candidate in the browser.

## Review contract

Preview reads the original CAS document and candidate without creating catalog,
claim, storage, or job rows. It hashes exact document bytes and compares parsed
entries using ordinal entry names and file kind/name within each entry. Renames
appear as removals/additions. Header ordering or formatting can change the
original-document hash without changing the supported entry comparison.

The response names both SHA-256 hashes, entry and file additions/removals/changes,
changed existing-file checksums, BIOS entry counts, and current/candidate file
counts. The summary retains its bounded compatibility sample; the UI uses the full
searchable, filtered change API with pages of 50 entries or files. Every page binds
to both exact reviewed document hashes and rejects a stale baseline or candidate.
Entry expansion shows metadata changes and paginated file details, with exact
before/after strings including Int64 byte sizes. Ordering is ordinal and stable.
Percentages use the current entry/file counts; first imports explicitly have no
existing baseline. These are catalog entry/file counts, not unique
titles or projected library impact. Unknown fields outside the parser's model
are not included in semantic comparison. There is no automatic acceptance policy.

Each original/candidate document is limited to 32 MiB, 100,000 entries, and
500,000 files. XML must have the supported structure, identity header, named
entries/files, valid declared checksums and ROM sizes, and unambiguous names.
Dumped files need a supported checksum; `nodump` files may omit it. Empty
catalogs and mismatched header names are rejected. ZIP extraction, other DAT
formats, platform reassignment, and semantic ROM-content verification are outside
this manual review path. Saved subscriptions also support first-import review
against an explicit empty baseline. Existing additive upload is unchanged.

Approval resubmits the browser's file and both reviewed hashes. ROMD repeats
validation and verifies the current baseline and exact candidate bytes. Identical
content creates no job. A changed file/baseline requires a new preview. The
replacement uses the existing durable job/dispatch contract; its `ExistingDatId`
is the persisted baseline version. Both pending ingestion and activation take the existing catalog topology
fence and reject a different active version, including a change while queued.
A replay of an already-active candidate remains a no-op.

Approval starts a replacement job, not an instantaneous declaration that library
publication is complete. Check the job outcome before refreshing affected
libraries. A rejected stale activation does not automatically discard a
previously ingested pending version.
Network ambiguity at acceptance requires checking Jobs before trying again.
The replacement collection is `POST /api/dats/{datId}/replacements`. Multipart
approval requires `file`, `activeSha256` and `candidateSha256`; the hashes bind
the reviewed snapshot, with a changed pair returning 409. Acceptance returns
202 with only `jobId` and `Location: /api/jobs/{jobId}`, without an `ETag`.
Preview uses `POST /api/dats/{datId}/replacements/preview`; changes use
`POST /api/dats/{datId}/replacements/changes` and its `/{entryId}` detail route.
The former unreviewed PUT and the former replacement action URLs are retired.

## Bounded page requests

Manual replacement posts its retained file and page request as form fields to
`/api/dats/{datId}/replacements/changes`; a saved subscription reads the same
page request as query parameters from
`GET /api/dat-subscriptions/{subscriptionId}/changes`. A page request carries
`activeSha256`, `candidateSha256`, an optional `query` (up to 200 characters), an
optional Added/Removed/Changed `change` filter, and an opaque `cursor` with a
`limit` of at most 50. Pages are keyset pages (`items`, `nextCursor`,
`hasNextPage`) in ordinal entry-name order, so the browser keeps the cursors it
visited to page back; no total is returned, and the preview summary carries the
counts. A cursor is bound to the reviewed hash pair and filters, and any other
pair or filter set rejects it as invalid input. Each entry has an opaque `id`;
`…/changes/{entryId}` returns that entry's metadata changes with keyset pages of
its changed files, and an unchanged or unknown entry is not found. The hash pair
is a snapshot binding: a stale pair is a conflict, and the browser asks for a new
preview only for that stale-review error code. A manually uploaded file that fails
validation is invalid input; a saved subscription's candidate and installed
document are held by ROMD, so one that fails validation there is a conflict.

No remote URLs or commands are accepted. Requests revalidate bounded original
and candidate documents; they create no jobs, claims, or storage rows. There is
no separately persisted diff index. A manual upload resends its file for each page;
a saved subscription reads the already retained local candidate.

The parser's supported fields define the semantic comparison. Structural validity
is not an automatic safety decision. A changed document with zero supported entry
changes can reflect header metadata, formatting, or fields outside the comparison.
Removals and existing-file checksum changes deserve review; applying the DAT does
not delete stored ROM files. Disc/track counts do not claim unique titles or
uncomputed owned-library impact.
