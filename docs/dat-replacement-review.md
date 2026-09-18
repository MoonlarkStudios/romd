# Manual DAT replacement review

The Systems → Sources → Replace DAT flow validates and previews an extracted
Logiqx DAT/XML document before accepting a replacement job. The current catalog
remains active during preview. The browser retains the selected file; closing
the review or reloading discards the preview. Saved subscriptions use the same validation and review through the
[subscription flow](dat-subscription-trial.md). This manual-upload path keeps its
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
convergence is complete. Existing post-acceptance job retry behavior still applies;
this slice does not solve the #205 retry audit. In particular, rejected stale
activation does not automatically discard a previously ingested pending version.
Network ambiguity at acceptance requires checking Jobs before trying again.
The legacy replacement API remains available for existing callers; review is the
admin UI's replacement flow, not a new authorization boundary.

## Validation evidence

The implementation was exercised with backend tests for exact hash binding,
read-only preview, invalid input, deterministic/truncated diffs, unchanged content,
and a stale activation baseline against real PostgreSQL. The host test proves
preview row/job isolation, changed-file rejection, reviewed replacement on the
same source, and rejection of the superseded baseline. Web interaction tests cover
explicit approval, cancel, unchanged content, validation retry, and stale approval.

A local, uncommitted trial on 2026-09-06 UTC used the complete Redump PlayStation
baseline SHA-256
`0d5cffb7feb15aa4297ccaf722c62d2b08f17eb859d6ab7890088d481bf8a18e`
(10,914 disc entries / 60,168 track records) through the authenticated admin test
client and isolated PostgreSQL fixture. Unchanged preview took 0.985 seconds;
a candidate with one explicitly synthetic entry/file added took 0.790 seconds.
Preview created no catalog/claim/job rows. Reviewed activation produced 10,915
entries / 60,169 files on the same source and superseded the baseline. This is
one local measurement, not an upstream-update or throughput benchmark. The
fixture uses in-memory Hangfire transport; all four developer database exports
were removed. No upstream documents or trial harness were committed.

Required validation routes: `mise run test`, `mise run test:integration`, backend
build/OpenAPI generation, and `pnpm api:update`, `pnpm lint`, `pnpm test`,
`pnpm build` from `web/`, plus `git diff --check`. Generated scope is admin only.
The browser tool was unavailable during this run, so visual inspection and a
complete browser-to-live-server acceptance run remain outstanding. The automated
component and API tests are separate evidence.

The local fixture emitted its Data Protection unencrypted-key warning and
reported that TestServer does not implement the request-size feature. The parser's
independent byte limits remain enforced; real Kestrel request metadata is not
verified by that warning-producing fixture. Existing web auth-test `act(...)`
noise and Vite bundle-size advisories remain. No NAS deployment or public catalog
redistribution was performed.

## Bounded page requests

Manual replacement posts its retained file and query to `replacement-changes`.
Saved per-source and enrolled subscriptions use their `/changes` endpoint. Queries
carry `activeSha256`, `candidateSha256`, optional search (up to 200 characters),
Added/Removed/Changed filter, offset, and optional exact entry name for file details.
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
