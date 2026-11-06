# DAT Subscriptions

Related decisions: [neutral source identity](neutral-source-identity.md).
The publisher's wire format is proposed in the
[DAT catalog publication protocol](../design/dat-catalog-publication-protocol.md),
and current behavior is described in [DAT subscriptions](../features/dat-subscriptions.md).

## Context

Most ROMD libraries are built on No-Intro and Redump DATs, which change
upstream. Tracking those changes by hand is slow and error-prone. Neither
authority offers an API: one serves direct downloads, the other only a
website form. An instance that acquired catalogs itself would multiply load
on the authorities, carry form-driven adapter maintenance into every
installation, and blur who is responsible for redistribution.

One DAT update can change a large catalog at once. Applying it must preserve
curation, keep the active catalog usable throughout, and never surprise an
administrator with destructive changes. Trust in a downloaded catalog cannot
rest on the server that served it.

## Decisions

### A Companion Publisher Distributes Complete Catalogs

- **One default source.** The companion publisher, `romd-dat-catalogs`,
  acquires No-Intro and Redump catalogs and publishes them as ROMD's default
  subscription source. No-Intro and Redump remain the upstream authorities.
  Upstream attribution, document identity and acquisition provenance are
  preserved.
- **Complete catalogs.** The publisher applies no language, region, release
  or 1G1R filtering. Selection such as 1G1R is a ROMD library feature.
- **Qualified coverage only.** Coverage of a system or variant is never
  advertised until it has been qualified.
- **Instances never scrape.** A ROMD instance never contacts the upstream
  authorities, and no scraper ships inside one.
- **Restricted credentials stay private.** Restricted upstream credentials
  are never bundled, and never used to turn a restricted catalog into public
  mirror content.

### Publication Is Authenticated, Immutable And Versioned

- **One snapshot, three resources.** Each publication is a versioned snapshot
  of:
  - an index with stable authority, system and variant identifiers, current
    document references, provenance and acquisition health
  - immutable artifacts, each identified by the SHA-256 of its exact
    document bytes
  - a change feed with stable entry identities that links exact versions
- **Authenticated metadata.** It binds the snapshot and artifact hashes. A
  checksum served beside an artifact is not authentication. Artifacts are
  published and verified before any snapshot references them.
- **The index is the truth.** The feed announces changes, and the index
  defines current state. Instances reconcile the complete index on first
  subscription, periodically and after outages, so feed truncation never
  prevents catch-up.
- **Content identifies change.** Document hashes decide whether a catalog
  changed. Header versions, archive timestamps and publication dates never
  substitute for them.
- **Failures are isolated.** Each upstream and system succeeds or fails on
  its own. A failed acquisition never publishes an empty replacement or
  advances the last successful check. The last successful check and the last
  document change are reported separately.
- **Open format.** The format is open and versioned, and it allows
  alternative mirrors.

### ROMD Verifies With The Companion's Reader

- ROMD verifies publications with the companion's signed-metadata reader,
  run as a pinned binary, rather than with a second implementation of the
  trust protocol.
- Expired or unverifiable metadata yields no new candidate, while installed
  catalogs keep working.
- Why: one reviewed verifier is safer than two, and trust stays independent
  of the download site.

### Every Update Takes The Validated Replacement Path

- **Independent verification.** ROMD verifies each download and computes a
  deterministic, bounded diff tied to the exact active and candidate document
  hashes. Publisher summaries never substitute for local collection and
  library impact.
- **One path.** Automatic and reviewed activation use the same validated
  replacement path. A candidate is revalidated if its comparison baseline
  changes before activation.
- **The active catalog stays usable** during checks, preparation and upstream
  outages.
- **No content deletion.** A DAT update never authorizes deleting stored ROM
  content.

### Activation Policy

- **Automatic by default, exceptions paused.** Routine validated updates
  apply automatically, and exceptional changes pause for an actionable impact
  review. The exception thresholds are qualified against real updates. An
  explicit review-all policy is always available.
- **Rollback is a new activation.** Rolling back records a new activation of
  a retained version, and polling never reapplies a version the administrator
  rejected.

### Subscriptions Are Independent Of DAT Versions

- **Own identity.** A subscription is its own entity, bound to a stable
  source. It survives replacement of the DAT version it tracks, and it can
  exist before the first DAT.
- **Explicit binding.** Binding an already imported catalog preserves its
  source identity and history. An ambiguous binding is resolved explicitly,
  never guessed.
- **Truthful status.** Status distinguishes downloaded, awaiting review,
  activating and updating libraries, and reports up to date only when the
  required convergence completes. A persistent failure shows one actionable
  status, not a notification per retry.
- **Lineage survives.** Curation, source and version lineage, and history
  survive activation, disablement and rollback.

### Setup Is One Flow

- The common path is: keep my catalogs updated, accept No-Intro and Redump
  with the suggested systems, and enable updates.
- URLs, schedules and provider configuration are advanced settings.

## Trade-offs

- **A publisher dependency.** Default subscriptions depend on the companion
  publisher and its trust root. If it stops, installed catalogs keep working,
  but updates stop.
- **Large catalogs.** Complete catalogs are large, and filtering happens in
  each library rather than once at the publisher.
- **Local diffs every time.** Every activation computes its own diff, even
  when the publisher already summarized the change.
- **Lag.** Instances trail upstream by the publisher's cadence plus their own
  check interval.

## Rejected Alternatives

- **A scraper in each ROMD instance.** Rejected: it multiplies load on the
  authorities, puts form-driven adapter maintenance in every installation and
  blurs redistribution responsibility.
- **Publisher-side filtering or 1G1R.** Rejected: selection is a library
  concern, and complete catalogs keep provenance exact.
- **Checksums served beside artifacts as proof of integrity.** Rejected: a
  checksum from the same origin authenticates nothing.
- **Change detection from DAT headers or timestamps.** Rejected: only document
  content identifies a change.
- **A second implementation of the trust protocol inside ROMD.** Rejected: one
  reviewed verifier is safer than two.

## Guardrails

- **`SignedDatCatalogIndexTests`:**
  - a definition without a published artifact offers no subscription
  - inconsistent bindings reject the index
  - publisher health is kept
- **`DatCatalogEnrollmentServiceTests`:**
  - a retained artifact that fails verification fails closed
  - a failed refresh keeps the verified catalog for review
  - scheduled check failures back off, and the active catalog is kept
- **`DatSubscriptionTests`:** a check retains its candidate without creating
  claims, approval commits once, and a publisher failure leaves the active
  catalog in place.
- **`DatSubscriptionCheckJobTests`:** one failing check is recorded without
  stopping the others.
- **Review:** the companion repository's own tests cover acquisition,
  atomic publication and index and feed consistency.
