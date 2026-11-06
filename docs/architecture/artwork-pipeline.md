# Artwork and enrichment architecture

User workflows live in [artwork completion](../features/unified-artwork-completion.md),
[curation](../features/artwork-curation.md) and the
[saved artwork library](../features/artwork-curation.md#collect-images). This reference owns
identity, acquisition, publication, retention and client-cache contracts.
Provider credentials and defaults are in [configuration](../self-hosting/configuration.md).

## Durable runs

The server previews missing role counts and snapshots matching title IDs and
names when accepting an operation. All-tracked and filtered runs are not limited
to loaded browser pages. API selections accept up to 10,000 explicit IDs.

A dedicated `artwork-completion` job has a durable item ledger and typed results
for every title and role. Publication and its role result commit together under
the existing worker ownership fence. Each completed title checkpoints progress.
Redelivery skips completed items; interrupted titles retain published outcomes.
Overlapping runs cannot replace existing choices or consume another run's
results. Titles removed after acceptance remain in historical results; titles
untracked before processing are skipped for tracked scopes.

Recent runs come from `GET /api/jobs?jobType=artwork-completion`. The canonical
job is cheap; durable result pages use `/api/jobs/{jobId}/artwork-items`, and
expensive role aggregates use `/api/jobs/{jobId}/artwork-summary`.
`/enrichment?activity=<id>` opens saved results in another browser; **Jobs → Open
this run** also links to them. Legacy `/catalog?artworkJob=<id>` links redirect to
Enrichment. Results support keyset pagination,
match/image review, cancellation, and retries. Retrying unresolved titles also
includes work not reached before cancellation. Retry scopes preserve tracking
requirements. Confirming a provider match accepts a durable follow-up in the
same transaction; pending work coalesces, but an active older run does not consume
the follow-up. Historical results link to the resumed run.

Existing eligible images, legacy covers, pins, pending manual choices, hidden
candidates, and roles explicitly left empty remain preserved. Image review opens
the composition-first title artwork workspace. Provider match review exposes
persisted candidates, local evidence, supporting signals, and conflicts.

## Identity decisions

`ProviderIdentityPolicy` is a dependency-free, versioned policy used by the shared
application `ProviderIdentityService`. Metadata enrichment and artwork consume
the same accepted provider associations. Provider adapters retrieve evidence;
they do not decide trust. Old numeric IGDB confidence alone no longer authorizes
metadata or artwork. Existing unconfirmed links are audited and never silently
replaced with another game. Manual confirmations and explicit unlink suppression
remain authoritative.

Inputs are the local title name/system plus names and years from source-neutral
catalog release evidence. Provider-enriched dates are deliberately excluded.
IGDB supplies alternative names and platform-specific release years. For release
identities, exact names require a corroborating system or independent year;
provider aliases require both. Known platform/year contradictions veto acceptance.
Cross-platform artwork identities use edition-preserving names; matching dates
can break ties, while a different port's date does not veto the name. These rules
can authorize an automatic match only when discovery evidence is complete. Equal
candidates, edition conflicts and uncorroborated aliases remain reviewable. Release
metadata retains the stricter platform/year rules. Scores rank evidence and are
not probabilities.
Normalization preserves diacritics, sequel numbers, plus signs, hack markers,
remake/remaster and edition text; known region/dump annotations may be removed.
Normalization also recognizes subtitle separators, a trailing indexed “The”, and
spaced “&”/“and” equivalence. Discovery uses up to six distinct, deterministic
primary/alias queries per provider. All successful query results are combined
before assessing ambiguity. A clipped query set or a provider-limited or
budget-limited response requires review, even if it returns one plausible game
or no games. Partial failures cannot authorize a match. Candidates with
contradictory evidence remain visible but cannot displace a valid winner.
Policy v5 invalidates earlier automatic trust until reevaluation; it does not
invalidate explicit confirmations or remove retained artwork.

The match picker presents bounded suggestions and their coverage. IGDB searches
explore at most 500 raw results; SteamGridDB autocomplete has no continuation or
completeness guarantee. Refine a limited search or switch to an exact game id or
page URL. SteamGridDB suggestions alone therefore require curator confirmation;
an independently trusted bridge remains usable. Editing the query or switching
provider or lookup mode discards the previous suggestions and selection.

Decisions retain candidates, reasons, policy version, input fingerprint, and any
trusted bridge dependency. Input/policy changes invalidate automatic trust;
bridge trust requires the source game and association revision to remain current.
Negative discovery caches last six hours; temporary provider failures retry after
one minute. Rate limits are not cached as negative identity decisions; retry hints
are returned per provider so a healthy alternative can still fill the role.
Trusted associations are checked again after metadata retrieval and
inside artwork publication. Metadata payloads retain the accepted provider game
ID without duplicating the identity service's association write.

## Acquisition and architecture

The application completion service validates operation intent.
`QueueArtworkCompletionCommandHandler` owns the transaction, resolves its catalog
scope through narrow persistence ports, stages the job, snapshot and dispatch,
then flushes and commits them together.
The worker executor handles per-title execution and failure isolation. The same
job dispatch, claim, lease, cancellation and checkpoint infrastructure used by
other job families owns execution; no second scheduler is introduced.

The production `LinkedProviderArtworkService` runs adapters through one role
loop. Most roles fetch progressively, stopping as soon as a usable asset is retained.
Backdrops compare the bounded three-page set before choosing a crop fallback.
Limits are three pages and eight attempted assets per provider/role, with sixteen
attempted assets per role across providers. A bad primary provider therefore
leaves a bounded opportunity for fallback. Transient unavailable responses retry
twice with exponential delay; rate limits instead use durable deferral below.
Empty image searches are cached in worker memory for ten minutes, keyed by title,
role, provider identity, association/selection revisions and settings revision.
Individual download/decode failures use a separate five-minute cache bounded to
2,048 entries per process. This temporary cache does not modify curator exclusions.
SteamGridDB skips unsafe or malformed individual rows, retains valid rows and raw
pagination, and still rejects malformed response envelopes. Its shared request
gate permits at most four simultaneous network operations per process, including
streamed bodies and redirects. Database operations and publication stay serialized
within each job; image decoding retains its existing process-wide bound.

`ArtworkAcquisitionPolicy` shares provider ordering with retained resolution:
configured entries first, then supported defaults (SteamGridDB then IGDB for
posters/banners, IGDB for backdrops, SteamGridDB for logos). Dimension and aspect
ratio checks are suitability gates. Within each page, official-style logos are
preferred; otherwise provider order is preserved rather than ranking by small
crop differences, resolution or asset ID. Decoded dimensions are rechecked;
automatic logos require transparency and visible content. These checks do not establish artistic quality. SteamGridDB
heroes are banners, never backdrops. See [backdrop curation](../features/artwork-curation.md#backdrop-automation-and-composition)
for automatic crop suitability and opt-in review.

A completed search with no suitable backdrop or logo satisfies that optional
requirement without creating an explicit empty selection. The presentation uses
its fallback, and a later enrichment run may search again. Unattempted searches,
provider errors, active work and identity decisions remain outstanding.

Existing image validation, host/redirect checks, credential isolation, CAS
storage, hash locks, and publication transactions remain in place. Publication
rechecks title, selection and provider association revisions under the title lock
and execution fence. A provider failure can fall through without obscuring a
successful role. Successful publication outcomes survive retry within a run.

## Provider cooldown recovery

If a missing role cannot be filled because a required provider is throttled, the
job remains unfinished. The worker checkpoints progress and atomically releases
its execution claim while rearming the existing dispatch at `NextAttemptAtUtc`.
Retry hints are bounded from one second to one hour, defaulting to one minute.
The database rejects execution claims before the persisted due time, including
stale scheduler deliveries after restart. Completed titles and earlier successful
role results remain retained; cancellation remains authoritative while waiting.
No new scheduler or terminal “Deferred” phase is introduced for artwork jobs.

Management routes require Manager authorization; provider credential settings
remain Admin-only. Consumer/console resolved-artwork contracts are unchanged.

api clients: admin - completion scopes/history/results and provider identity
review evidence; generated with `pnpm api:update`. Consumer generation unchanged.

## Permanent enrichment workspace

`/enrichment` is a standing workspace for tracked titles, independent of any run.
The default view contains actual decisions: ambiguous provider associations,
artwork choices, and core metadata that remains absent after discovery. Automatic,
processing, and blocked views expose the other kinds of outstanding work. A title
can need a decision while another requirement is processing; those facts are not
collapsed into one mutually exclusive status. Completed requirements disappear
from the queue when their current facts change. The selected editor remains open
so a curator can see the result and deliberately advance.

Workspace and title actions launch durable execution and return to this workspace.
Automatic scope is resolved on the server from current eligibility. The launcher
offers only the scopes available at its entry point. Jobs retain
execution history, cancellation, retry, and dispatch ownership; they no longer own
whether a title is complete. Legacy task links retain their activity context and
selected title in the permanent workspace.

`EnrichmentAssessmentPolicy` classifies requirement facts in the domain layer.
The persistence query evaluates current provider trust, effective metadata,
artwork selections/assets, operational availability, and active work. It filters
and counts in PostgreSQL, uses keyset pagination, then hydrates only the requested
page. Counts and page IDs share one materialized assessment evaluation and database
snapshot. EF generates the assessment SQL and bound parameters; the persistence
reader retains retry, cancellation, and connection lifetime handling. The catalog exposes the same assessment separately from the historical
metadata-attempt status. Core metadata requirements are Description, Genre, and
ReleaseDate; optional credits and ratings do not create a permanent review queue.

Accepted metadata omissions are persisted per title and field. They are explicit
curator decisions, can be reversed, and use the title revision to reject stale
writes. Artwork continues to use its existing explicit empty selection. Provider
confirmation and unlink suppression remain authoritative. Metadata fetched for a
previous provider game is distinguished from current enrichment.

Provider discovery and database assessment share the current-input fingerprint
and recursive trust predicate. Automatic trust depends on active, non-BIOS local
catalog evidence, local name/system, explicit confirmations, matcher version, and
current bridge association revisions. Provider-enriched dates are excluded.
Discovery reads facts and fingerprints in a consistent database snapshot. Import
publication checks trust directly in its existing mutation transaction. The new
fingerprint encoding invalidates legacy automatic decisions for reassessment;
explicit confirmations survive.

Overlapping compatible completion requests share active title work through the
existing durable item ledger. Automatic scope excludes active legacy work as well;
explicit manual selections can still overlap legacy single/bulk enrichment jobs. Full enrichment can satisfy an artwork request;
artwork-only work cannot satisfy full enrichment. A provider decision changed
while work is already running receives a pending follow-up, so an old snapshot
cannot consume the new request. Dynamic tracked/filter/automatic scopes are
resnapshotted for each request rather than hiding newly eligible titles behind a
previous scope signature.

Each issue exposes a typed `state` independent of its available `actions`. The
API names are Satisfied, Automatic, NeedsReview, Blocked, and Processing; satisfied
requirements are omitted from the issue list. Queue presentation selects issues
by state, while action availability controls the offered operations. This is an
admin contract; it does not persist another assessment status.

The review pane reuses the provider evidence picker, metadata editor, and artwork
editor. Its primary content addresses the selected decision. Consumer preview is
a secondary disclosure outside artwork review. Navigation preserves the selected
title and protects unsaved edits; successful mutations invalidate the assessment
and catalog queries. Selected assessments refresh every 30 seconds while idle and
every 3 seconds while processing. Pagination checks the current query fetch state
and does not cancel a background cursor-chain refresh. Pending Next navigation
cannot overwrite a newer user selection. A failed background title-detail refresh
keeps cached content and mounted drafts, with a retry notice.

The workspace uses the existing scheduler and providers; consumer contracts
remain separate.

api clients: admin - canonical workspace, omission mutation, and catalog
assessment contracts; regenerated with `pnpm api:update`.


## Persistence and publication

Retained assets carry provider game and asset identifiers, attribution when
available, immutable original file identity, actual dimensions, and bounded
delivery variants. Reimporting the same provider asset and content revision is
idempotent; CAS deduplicates identical bytes independently of provenance.
File cleanup must account for both originals and variants. Artwork-only curation
also retains a title if its final catalog source disappears. When titles merge,
retained source assets move to the target (identical provider/content revisions
are deduplicated). A target pin or pending request wins; otherwise a source pin
transfers. Source pending requests do not transfer, preventing old jobs from
publishing onto another title. Artwork mutations lock their title before changing
assets or selections; multi-title operations take these locks in ascending ID order.
Job title and outcome IDs are historical snapshots without cascading foreign keys.
Deleting a title preserves job history and causes pending imports to finish as
superseded, without introducing a title-to-job lock dependency. Artwork-import job state and retained artwork are part of the current fresh
baseline; baseline downgrades are unsupported.

A per-title/per-role selection stores effective Automatic/Pinned state,
revision, and pending request identity separately. Apply advances the revision
when accepted, but keeps the previous effective artwork until the replacement
original and required variants are usable. Publication atomically verifies the
request revision and worker execution fence. Return to automatic also advances
the revision, invalidating older pending requests. Failed/stale jobs cannot
replace the current selection. Historical `IsPrimary` is not evidence of a pin.

Publication outcomes are monotonic across worker checkpoints. If the database
commits publication but its acknowledgement is lost, a stale retry checkpoint
must preserve the retained asset or superseded outcome. Before exhausting retries,
the worker reconciles against a fresh durable outcome under its execution fence;
an unavailable read remains retryable. Admin status follows the terminal phase,
so a successful retry is still successful even when its error history is retained.

Import acceptance and durable job dispatch share one transaction. An accepted
job persists trusted provider identity and does not depend on a short-lived
preview cache entry surviving a restart. Candidate references and opaque cursors
are scoped and validated; caller-supplied asset URLs are never import inputs.
Adapter downloads validate hosts and redirects and bound bytes, decoded pixels,
time, and supported static formats. Secrets and upstream error details remain
server-side. Consumer composition exposes delivery, never provider management.

SteamGridDB browsing advertises supported dimensions and styles for each role;
it does not advertise a language filter the provider cannot enforce. Candidate
references bind title, curator, provider identity, source location, attribution,
and a ten-minute expiry. Cursors also bind the search filters. Accepted jobs
retain the validated source location independently of the preview cache; the
downloader revalidates its HTTPS host, path and redirects. Provider credentials
are never sent to the artwork CDN. Disabling the provider prevents new browsing
and acceptance; already accepted jobs can finish their selected CDN download.

Preview fetches only bounded thumbnails and keeps processed images in a dedicated
64 MiB, 512-entry cache for five minutes. It creates no database or CAS records.
Retained originals are complete static JPEG, PNG or WebP, limited to 32 MiB,
40 million decoded pixels and 16,384 pixels per dimension. PNG delivery variants
preserve proportions and orientation, never upscale, and fit within 300×450 and
600×900 for posters or 960×310 and 1920×620 for heroes.

Every file writer, artwork included, uses one path: the file ledger
(`IFileStorageService`). Ordinary request and worker paths stage and compress
content before taking database locks or opening the publication transaction.
The catalog bootstrap retains its reviewed
[locked CAS publication exception](backend-mutations.md#locked-cas-publication).
Inside the caller's transaction, ordinary writers publish that transaction's files
in one batch, taking database-scoped hash locks in one global order and staging
file rows. The owner conditionally flushes pending receipt identities before
reading them and adding references; the locks remain held until commit.
Artwork stages the original and its variants together and publishes them after
its selection checks. Writers of title media and artwork
lock the title before publishing, so they cannot deadlock on shared image bytes.
ROM and DAT ingest update titles after publishing, but their bytes never coincide
with title media, and PostgreSQL would detect and abort such a deadlock. Deletion commits
the row deletion first, then deletes the blob only if no row claims it, rechecking
under the lock. Orphan snapshots only nominate candidates for deletion. This
prevents cleanup from removing newly published content, and every file row keeps
its blob. If blob deletion fails after the row deletion commits, the blob remains
as an orphan for the orphan sweep. `FileLedgerBoundaryArchitectureTests` fails if
anything else takes file locks, writes the content store, or creates file rows.

## Provider configuration and client caching

Runtime provider configuration is available to administrators in **Integrations**;
automatic acquisition preferences are in **Settings**. The API key is
encrypted in persistence and is never returned to clients. Deployments can supply
`Providers__SteamGridDb__ApiKey` to both Admin and worker hosts instead; the presence
of that setting makes configuration deployment-managed, including an explicit
empty value, which disables access. Do not add a default empty environment value
to deployment templates: that would prevent runtime configuration. Consumer hosts
do not register provider settings, browsing, import or credential services.

Consumer web artwork caching retains at most 64 recently viewed images and 32 MiB
in total, with a 4 MiB per-image limit. Entries are scoped by account, server and
versioned delivery identity. Immutable artwork is cache-first; legacy media is
network-first with a cached fallback. Cached artwork can render without the
network when the title data is available. This does not add offline authentication,
metadata persistence or a cold-start offline web application.

Console artwork caching follows successful installations. It stores bounded
delivery bytes and a manifest under the application support artwork directory,
scoped by server and release, outside verified ROM directories. Installed titles
can read these files after restart without network access. Failed refreshes retain
the previous role; an explicit cleared role removes it. Uninstallation removes
the release's artwork. This requires no Drift schema change.


## Retention and contracts

`POST /api/titles/{titleId}/artwork-candidates/{candidateId}/import` accepts a
server-issued candidate reference for that candidate. A candidate's id is a stable
digest of its source URL; hiding uses the same id with
`PUT /api/titles/{titleId}/hidden-artwork-candidates/{candidateId}`. A hidden
candidate stays hidden: no route lists or offers it again. The reference is
title/user scoped, expires, and is bound to the provider match revision. The new media belongs to the title, so the import
requires the title's ETag in `If-Match` and answers 201 with the created media and
the title's new ETag; a repeat of the same original answers 200 and writes
nothing. Imports validate downloaded image bytes and retain the original,
attribution, and source page. A second match validation occurs inside the
application-owned transaction before committing the append.

Deleting a title media item (`DELETE /api/titles/{titleId}/media/{mediaId}`) also
removes it from the artwork: its source URL is excluded so enrichment and automatic
fills never acquire it again, and, when it was the title's last media row for that
file, assets of the file are rejected and roles showing it return to automatic.
While another media row of the title still shows the same file, those assets and
roles stay as they are.

Manually collected provider media uses `gallery:<providerId>` as its persisted
source identity. This keeps it separate from enrichment-owned media. Do not strip
that prefix in persistence, merge, or rematerialization code. UI labels display
the provider's readable name. Ordinary uploads retain source `user`.


## Schema and validation

The current fresh application baseline installs artwork selections, assets,
identity decisions, completion ledgers, accepted omissions and durable cooldown
state. Follow the [database boundary](../self-hosting/configuration.md#database-and-installation-boundary)
and worker-first deployment procedure; historical migrations 20–40 are not an
upgrade chain for current releases.

Use the [validation routes](../../AGENTS.md#validation) for affected surfaces.
Controlled-provider tests cover identity, preview non-mutation, URL/reference
validation, revision/execution fences, atomic publication, retry and retained
file ownership. Review actual provider matches, image quality and client
composition separately. Offline claims require retained device bytes and
restart testing; a stored URL alone does not establish offline operation.
