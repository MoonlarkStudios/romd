# DAT subscription distribution and one-click setup

Status: accepted product direction, 2026-09-05; implementation started.

The companion repository now exists at
<https://github.com/JackSkylark/romd-dat-catalogs>. Initial commit
`02d300c2593383535f9d3ab571089aa08e74d2e3` implements an unsigned local
publisher with synthetic fixtures, immutable objects, index/RSS output, and
22 passing tests. CI passed on Python 3.11 and 3.13. This is not upstream
acquisition, authenticated production publication, or ROMD integration;
#145 and unattended beta acceptance remain incomplete.

Language decision: use Go for the companion publisher (user approved after
the Python prototype). [Go port PR #1](https://github.com/JackSkylark/romd-dat-catalogs/pull/1)
replaces Python with a standard-library CLI and token-based XML validation.
It preserves document hashes and existing publication/event state. Semantic
catalog comparison remains a separate follow-up; no measured speedup over
Python or full-catalog performance acceptance is claimed by the port.

Related: [#145](https://github.com/JackSkylark/romd/issues/145).

## Decision

A companion repository will publish complete No-Intro and Redump source
catalogs for ROMD's default one-click subscriptions. It is ROMD's default
distribution source; No-Intro and Redump remain the upstream authorities.
Preserve upstream attribution, document identity, and acquisition provenance.

Start with complete catalogs for the supported systems and variants. Do not
apply language, region, release, or 1G1R filtering in the publisher. Future
1G1R selection can be a ROMD-side library feature and is outside #145.
Do not advertise complete upstream coverage until its systems and variants
have been qualified.

Fresh1G1R inspires scheduled acquisition and convenient published artifacts:
<https://github.com/UnluckyForSome/Fresh1G1R>. Its automation is a reference,
not evidence that upstream acquisition or redistribution is already qualified.
No browser scraper ships inside a ROMD instance.

## Publication contract

Publish three resources from the same versioned snapshot:

- A complete catalog index with stable authority/system/variant identifiers,
  current document references, provenance, and acquisition health.
- Immutable DAT artifacts with exact document hashes and authenticated release
  metadata. Publish and verify artifacts before exposing their snapshot.
- An RSS feed with stable entry identities, readable change summaries, and
  links to exact versions rather than mutable latest-download URLs.

RSS announces changes; the index defines current state. Instances poll with
conditional requests, jitter, and bounded backoff, and reconcile the index on
initial subscription, periodically, and after outages. Feed truncation must
not prevent an instance from catching up. Document content determines change;
header versions, archive timestamps, and publication dates do not substitute
for content hashes.

The format is open and versioned, with support for alternative mirrors.
Publisher failures are isolated per upstream/system. Failed acquisition never
publishes an empty replacement or advances the successful-check timestamp.
Expose last successful check separately from last document change.

## ROMD responsibilities and UX

The common flow is: Keep my catalogs updated, accept No-Intro and Redump with
suggested systems, enable automatic updates. URLs, schedules, and provider
configuration belong in advanced settings. Recognize existing imported
catalogs and preserve their source identity and history when binding them.
Resolve ambiguous bindings explicitly rather than guessing.

Automatic application is the primary beta experience. Routine validated
updates apply quietly; exceptional changes pause for an actionable impact
review. Exact exception thresholds need qualification against real updates.
Always retain an explicit review-all policy.

ROMD independently verifies each download and computes a deterministic,
bounded diff tied to the exact active and candidate document hashes. Both
automatic and reviewed activation use the same validated replacement path.
Publisher summaries cannot substitute for local collection/library impact.
Revalidate a candidate if its comparison baseline changes before activation.

Keep the active catalog usable during preparation and upstream outages.
Distinguish downloaded, awaiting review, activating, and updating libraries;
report up to date only when required convergence completes. Persistent failures
need an actionable status, not repeated notifications for each retry.

Preserve curation, source/version lineage, and intelligible history through
activation, disablement, and rollback. Rollback records a new activation of a
retained version; polling must not immediately reapply the rejected version.
DAT updates do not authorize deleting stored ROM content.

Subscriptions remain independent entities and can exist before their first
DAT. Preserve #145's DirectUrl, Mirror, NotifyOnly, and Manual capabilities,
secret-backed user credentials, matching manual-upload behavior, and
per-subscription health. Restricted upstream credentials are never bundled
or used to turn restricted catalogs into public mirror content.

## Implementation sequence

1. Qualify acquisition and distribution for public No-Intro and Redump
   catalogs: stable identity, system/variant coverage, failure behavior,
   upstream access conditions, and artifact retention. Choose the companion
   repository name, hosting, and release authentication/key rotation design.
2. Specify the versioned index/artifact/feed contract with fixtures for a new
   subscription, multiple changes, a truncated feed, and a failed acquisition.
   Build the publisher with independent source health and atomic publication.
3. Implement subscription persistence and the provider adapter over existing
   durable jobs, CAS, replacement, and activation boundaries. Add exact-version
   diffs, review policy, automatic application, history, and rollback together
   on the shared validated path.
4. Implement and exercise setup, healthy overview, exceptional review, and
   history UI. Regenerate clients from backend contracts before UI call sites.
5. Qualify unattended No-Intro and Redump updates end to end before hardware
   beta. Review-gated operation alone does not complete this acceptance.

## Risk and verification plan

Persisted contracts include subscription identity, source binding, version
lineage, candidate comparison baseline, policy, and rejected-version state.
Use PostgreSQL migrations with the expected schema version increment. Worker
scheduling must preserve #129's caller-owned acceptance transaction, stable
job identity, dispatch intent, execution fencing, and replay rules. Keep
#193's tested queue counts; subscriptions do not authorize retuning them.

Verify publisher parsing, hashes, authentication, partial acquisition failures,
atomic snapshot publication, and feed/index consistency. The companion
repository's concrete test commands will be selected with its implementation.

Backend tests must cover duplicate delivery, missed-feed catch-up, interrupted
activation after durable effects, invalid or mismatched artifacts, provider
outages, credential boundaries, bounded diffs, curation preservation, changed
review baselines, and rollback without automatic reapplication. Run
`mise run test`, `mise run test:integration`, and `mise run build` for the
implementation. Check Docker and host disk first; consult
[known validation issues](../known-issues.md) before interpreting failures.
Host fixture changes also require the documented run without developer
connection-string exports.

For endpoint/UI changes, use the web API client skill, then run
`pnpm api:update`, `pnpm lint`, `pnpm test`, and `pnpm build` from `web/`.
Exercise the actual setup and update states in the browser. No backend, UI,
publisher, or upstream qualification pass is claimed by this decision record.

Parallel agent work: none. Library export #132, ROMD-side 1G1R, hardware
deployment, and a private instance activity feed are separate follow-ups.
