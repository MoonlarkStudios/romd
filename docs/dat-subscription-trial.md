# PlayStation subscription trial

The system’s **Add source → Subscription** flow discovers actual published catalogs
from the authenticated index. Platform definitions alone do not advertise a DAT.
Choose **Subscribe and check**, review the saved candidate, and explicitly approve
**Import reviewed catalog**. Existing sources use the same review before replacement.
Checks use the ROMD mirror; the centralized publisher checks upstream daily.
ROMD checks eligible subscriptions daily; approval remains manual, without AutoApply.
See [system setup](system-setup.md) for pending-review and retry behavior.

The selected publisher system resolves by an explicit database canonical key, preserving
local platform identity and renamed short names. A deleted mapping is not recreated.
First-import preview creates no source claims or ingestion job. Approval commits the
existing upload job, durable dispatch, and subscription receipt in one transaction.
The completed source is resolved by exact retained file and mapped platform. Repeated
approval returns the accepted job receipt. A failed check retains the active catalog
and offers retry; it never silently accepts a stale unapproved candidate.

The older per-source **Track updates** control remains available. Ambiguous legacy
subscriptions are not merged or deleted automatically.

## Trust and deployment

ROMD invokes the companion repository's `distribution candidate` command using
`ProcessStartInfo.ArgumentList`, without a shell or request-controlled command,
URLs, root, or catalog identity. This reuses go-tuf/v2 verification rather than
introducing a second TUF implementation. The standard admin, admin-dev, and worker Docker
targets include the reader built from
[reviewed companion revision `8744b38`](https://github.com/MoonlarkStudios/romd-dat-catalogs/commit/8744b384443aa5cab60028aa1a0f5c1652f1dadd).
The Go build image, source archive, and original public trust root are pinned by
hash; Go 1.27.1 matches the companion's mise configuration. Only the static binary
and public root enter the admin and worker runtime images. The consumer image
does not include the reader. No signing credentials are included.

Standard images enable the official signed data site by default. They fetch
metadata during explicit checks and due worker checks; they never contact Redump directly. A check does not
automatically approve a DAT. To disable these checks, set
`DatSubscriptions__Enabled=false`.

For native hosts, build the reader using the companion's mise toolchain and
configure its executable and independently pinned public root explicitly:

```text
DatSubscriptions__Enabled=true
DatSubscriptions__ReaderPath=/opt/romd/catalog-reader
DatSubscriptions__RootPath=/etc/romd/catalog-trust/1.root.json
DatSubscriptions__Site=https://moonlarkstudios.github.io/romd-dat-data
```

The official mirror publishes the qualified complete standard PlayStation DAT.
Supply the pinned public root independently of the download site. The reader
retains TUF metadata under `Romd:DataDirectory/dat-subscriptions`; do not reset
that cache to bypass expiry or rollback rejection. Cache identity includes the
root bytes and publisher location. An OS lock serializes access; keep lock files.
The process has a two-minute deadline and downloaded candidates are size/hash
checked again before ROMD's semantic validation.

For a private local trial, set `DatSubscriptions__BundlePath` and clear
`DatSubscriptions__Site` (the standard image supplies a default).
It points at a complete local companion `distribution stage` bundle. The reader
uses the same TUF updater through a file-backed transport, with no network access;
local files still need valid signatures and unexpired metadata. Mount only the
bundle and the independently supplied public root. Do not mount private signing
keys. Local trial signing keys must be disposable and separate from production.
The bundle's synthetic GitHub release URLs are authenticated references mapped
to local assets, not a claim that those assets were publicly released.

## Persistence and activation

The `DatSubscriptions` table is independent of versioned DAT rows and binds the
stable source. Migration `20260906043825_DatSubscriptions` raises the expected
application schema version to 3. Enrollment migration
`20260906162538_DatCatalogEnrollment` adds nullable source binding and selected
shared identity, raising the version to 6. Migration `20260906202746_ScheduledDatChecks` adds
next-check times and failure counts at version 8; restart the worker first to migrate/provision.
Consumer APIs do not own these routes or the reader.

The persistence service implements the application subscription port. An explicit
check holds a source-scoped PostgreSQL transaction/advisory lock through the
bounded read/validation and candidate-reference commit. Overlapping operations
return a conflict rather than queueing more work. This intentionally simple
path is shared by scheduled checks, without a separate delivery framework.

A check creates no DAT version, source claim, or replacement job. A successfully
validated candidate is retained in CAS through a real file reference, recognized
by garbage collection and DAT storage attribution. Failure records an actionable
status and preserves installed data. Cancellation rolls back the attempt and
can be explicitly retried. A new successful check replaces the candidate reference;
normal unreferenced-file cleanup can reclaim the previous unowned blob. Explicit
source deletion also removes the subscription in its existing deletion transaction.

Preview and approval use the existing exact-document review/activation contract.
Approval atomically commits the replacement job, stable dispatch intent, and a
subscription job receipt. Repeating the same approved hash pair returns the same
job. A queued job retains the reviewed active version fence. This adds no new
job family, custom recovery scheduler, or pipeline-resumability claim. Existing
replacement job failure/retry behavior and the #205 audit still apply.

## Local verification, 2026-09-06 UTC

- Companion race tests, vet, formatting, workflow validation, builds, and smoke
  passed. New cases cover signed candidate selection, wrong identity/root,
  tampered bytes, inconsistent references, unhealthy publishers, exact output
  bytes, and refusal to overwrite a candidate. Existing TUF expiry/rollback
  tests remain part of that suite.
- Backend safety: 2,236 tests passed. Host integration: 445 passed without all
  four developer connection-string exports. An initial integration run found
  the hard-coded two-migration assertion; updating it to three fixed that failure.
- Web lint, 471 tests, and production web builds passed. Generated client scope
  is admin only; consumer contracts did not change. The ordinary Biome formatter
  reported the supplied files as ignored under its exclusion-only includes list.
  An explicit-inclusion temporary configuration using the same project rules
  linted/formatted all five touched UI files; no broad configuration change was made.
- Local Docker admin + worker + PostgreSQL exercised the actual Go reader,
  signed real PSX candidate, exact-hash approval, repeated approval returning one
  job, activation, and unchanged recheck. Tampering with timestamp metadata
  produced Check failed while the active version remained intact; restoring it
  allowed retry. A signed synthetic one-disc addition then produced the expected
  one-addition diff and was left unapplied for user review.
- The upstream baseline has 10,914 disc entries / 60,168 tracks and SHA-256
  `0d5cffb7feb15aa4297ccaf722c62d2b08f17eb859d6ab7890088d481bf8a18e`.
  Exact document: 12,756,749 bytes; gzip level 9: 3,973,980 bytes. This is one
  size measurement. Repeated identical probes do not establish change frequency.

The browser tool reported no available browser; no automated visual QA is
claimed. Existing web auth `act(...)`/bundle-size advisories and the installed
EF tool/runtime version mismatch remain. No upstream DATs, private keys, demo
credentials, or operator secrets-manager identifiers belong in Git. No NAS or
public source publication was performed.

## Next gates

Observe repeated real publisher updates and failure frequency before expanding
coverage or adding unattended activation. The data repository stores complete DATs
at immutable commit URLs and publishes signed indexes through Pages. No routine
release/recovery archive or automatic history trimming is required. Measure repository
growth before adding retention machinery. ROMD retains its active document locally.

Large-diff browsing is #210. Full #145 also includes first-source enrollment,
provider capabilities/credentials, history/rollback UX, backoff policy, and
unattended activation on the same validated path; this trial does not close it.

## Current publisher evidence

[Data publication run 34046841023](https://github.com/MoonlarkStudios/romd-dat-data/actions/runs/34046841023)
published the complete standard PlayStation catalog on 2026-09-06. An independent
reader using the existing root and rollback cache verified publication 3007:
12,833,217 extracted bytes, 10,974 disc records, 60,444 track records, SHA-256
`62572360b7abe18df80886283e39782c2c4a325e48c6134fc179d589936fe41d`.
The signed index points to data commit `1a199860d51a5f6611d6e970028976fd4958f8fa`;
the public RSS includes that document's update event.

[Source and redistribution qualification](https://github.com/MoonlarkStudios/romd-dat-catalogs/blob/8744b384443aa5cab60028aa1a0f5c1652f1dadd/docs/redump-psx-qualification.md)
is scoped to the current official standard PSX DAT. This live evidence proves
acquisition and distribution, not unattended reliability or hardware acceptance.
The host enrollment test uses synthetic documents and real worker jobs; distinguish
it from importing the real published document in the demo.
