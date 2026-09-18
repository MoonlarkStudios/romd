# Artwork curation

Artwork curation separates Discover (identify a provider game), Preview (inspect
remote candidates), and Apply (retain selected content in ROMD). Preview must
not mutate title state, establish provider associations, or retain gallery
assets. Only explicit Apply creates retained content and selection intent.

## Presentation contract

| Role | Intended use | Preferred shape/reference size | Initial delivery |
| --- | --- | --- | --- |
| Poster | Default library card | 2:3, 600×900 | Implemented first |
| Landscape card | Wide library card | 92:43, 920×430 | Deferred |
| Banner (`Hero` in API/storage) | Compact wide header | 96:31, 1920×620 | PNG variants |
| Logo | Transparent title art | Natural proportions | PNG variants |
| Backdrop | Cinematic game-detail background | 16:9, 1920×1080 or larger | WebP variants up to 3840px |
| Screenshot | Gameplay gallery | Original proportions | Existing media semantics |

Roles are presentation choices, not replacements for persisted media types.
Original box art remains `Cover`; it is not migrated to Poster. New artwork is
retained separately from legacy TitleMedia so provider refreshes and the legacy
one-media-per-type/source upsert cannot replace pinned originals.

The shared mapping lives in Application.Common, which references the dependency-free
Contracts.Common assembly as well as Domain. It has no reference to management
contracts or host/infrastructure code. Exact project-reference and anonymous-route
architecture assertions include this explicit shared contract and the versioned
`/artwork/{assetId}/{variantName}/{contentVersion}` delivery route. Provider browsing
and mutation routes remain authenticated Admin operations.

Both clients consume centrally resolved artwork. A resolution identifies the
role, selected asset/content version, source pixel dimensions, delivery variants,
fit, and fallback reason. Null dimensions mean unknown historical dimensions,
not the preferred role dimensions. Clients choose a suitable bounded variant
and control their layout; they never stretch images. Resolved artwork is the primary title artwork contract. There are no active
deployments requiring a compatibility rollout; client adoption can remove old
cover-only paths together. Generic media remains meaningful for box art and
screenshots.

Poster uses retained eligible poster artwork, then the existing primary cover
contained in the card, then a local placeholder. Hero uses eligible hero artwork,
then a local placeholder; historical banners and covers are not implicitly
reclassified or cropped into heroes. Automatic preference is independent of
metadata field precedence. Among eligible retained assets: an explicit pin wins,
then user artwork, then configured role-specific provider priority; creation
time and asset ID provide stable tie-breaks. Automatic never initiates a provider
request. Disabled providers' downloaded content remains eligible.

## Backdrop automation and composition

Schema version 25 adds `Backdrop` without renumbering roles or reclassifying
existing `Hero` selections. Run the worker migration before restarting the API
hosts. Rollback requires resolving any Backdrop assets, selections, preferences,
and import jobs first; the downgrade does not delete them automatically.

IGDB artworks supply backdrop candidates. Screenshots can be manually assigned
from the gallery but are excluded from automatic selection. SteamGridDB's current
hero categories supply wide banners; this adapter does not advertise Backdrop.
Logos remain a separate SteamGridDB role. A landscape image is not necessarily a
clean backdrop: edition branding and baked-in text require a visual review.

`FillBackdrops` and `ReviewBackdrops` default to true. Fill missing artwork uses
the trusted IGDB match and reports `NeedsReview` when suitable dimensions exist;
it does not download or select that backdrop. **Review backdrop** opens candidates
with the landscape filter enabled. Review both desktop and mobile compositions,
set the focal point, and save. Turning review off explicitly opts into geometric
selection: minimum 1200×720, aspect ratio 1.3–2.1, closest to 16:9 first. Existing
eligible artwork, pins, and pending manual requests are preserved in either mode.
Missing/untrusted provider matches and unavailable candidates stay explicit outcomes.
The preview filter can be disabled for deliberate manual exceptions.

Backdrop delivery preserves the original proportions and never upscales. Width
bounds are 480, 960, 1920, 2880, and 3840 pixels, with duplicate dimensions omitted.
The consumer uses native `srcset`/`sizes`, eager high-priority hero loading, and
browser caching rather than the separate recent-artwork blob cache for this role.
Only ROMD delivery URLs are rendered. Full-resolution originals are not requested.

Consumer detail uses Backdrop plus optional Logo. Missing/failed Backdrop switches
to the poster composition; missing/failed Logo shows the game name. Missing Poster
uses the shared monogram treatment. Banners are not stretched into backgrounds.
On mobile the scene sits above the copy. Details, Releases, and available Media
use tabs; `tab` and `release` query parameters preserve selection in deep links.
Playback capability and download-verification checks remain enforced.

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
superseded, without introducing a title-to-job lock dependency. Downgrading the
pipeline migration removes artwork-import jobs because the earlier model cannot
read that job discriminator; retained artwork belongs to the foundation schema.

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

Artwork publication acquires database-scoped hash locks in stable order before
writing CAS files, holding them through the selection and job-outcome commit.
Both file-cleanup paths share those locks and recheck references after acquiring
them. Orphan snapshots only nominate candidates for deletion. This prevents
cleanup from removing newly published artwork. Legacy file writers that do not
participate in this protocol retain their existing concurrency limitations.
If CAS deletion fails, cleanup rolls back its metadata deletion and propagates
the failure. A rollback after deleting an unreferenced blob can leave an
unreferenced file row for a later cleanup attempt; referenced artwork is protected.

## Delivery plan and verification

Runtime configuration is available to administrators in Settings. The API key is
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

1. Foundation: domain selections/resolver, additive persistence, resolved
   contracts, storage-reference safety and generated clients.
2. Pipeline: durable import, variants, revision/execution fencing, cleanup and
   controlled-provider integration tests.
3. Provider/Admin: runtime SteamGridDB configuration, discovery, transient
   previews in actual layouts, import-and-pin and Return to automatic.
4. Clients: consumer web and console rendering, cache identities, durable device
   caching and controller-first navigation. Console migrations only as needed.

Offline guarantees require retained device bytes, not merely server-local files
or persisted URLs. Client delivery must specify the cached title set and eviction
policy and verify restart while disconnected. Live-provider validation is
reported separately from deterministic controlled-provider tests.

Acceptance includes preview non-mutation; only chosen imports retained; arbitrary
URLs and invalid references rejected; failure preserving effective art; retries
deduplicated; Apply A → Apply B and Apply A → Automatic rejecting stale writes;
crash replay after durable effects; pins surviving restarts and refresh;
pre-existing libraries working without provider settings; and consistent client
resolution including offline fallback. Migration tests must preserve historical
media and prove artwork files are excluded from orphan cleanup.

Validation: `mise run test`, `mise run test:integration`, a direct integration
run with all four development connection-string exports removed; from `web/`,
`pnpm api:update`, `pnpm lint`, `pnpm test`, `pnpm build`; from the console,
database generation if needed, `mise run analyze`, `mise run test`; final
`mise run build`, rendered UI checks, independent review, and PR CI. Read
`docs/known-issues.md` before interpreting unexpected failures. These are gates,
not claims that the implementation or validation is complete.

Metadata Compare/apply, animated assets, additional providers, and destructive
crop editing are deferred. Focal-point composition is supported. Reusable provider
associations must be deliberate and separate from browsing selection.
