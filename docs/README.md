# ROMD documentation

Welcome! Pick the section that matches what you're doing. Each surface also
keeps its own docs next to its code:
[web apps](../web/README.md), [Ottercade console](../clients/romd_console/README.md),
[native libraries](../native/libchdr/README.md) and
[reference data](../reference-data/README.md).

## Start here

| Your goal | First stop |
| --- | --- |
| Install or operate a server | [Self-hosting](self-hosting/README.md) |
| Bring your games into ROMD and share them | [Your first library](features/first-library.md) |
| Use your household's library | [Browsing and playing](features/browsing-and-playing.md) |
| Develop or contribute | [Development setup](development/getting-started.md), then [Contributing](../CONTRIBUTING.md) |
| Prepare a release | [Release readiness](maintainers/release-readiness.md) |

## Self-hosting

For people installing and running a ROMD server.

- [Start here](self-hosting/README.md): installation path, release scope and
  routes for upgrades, backups and recovery.
- [Production deployment](self-hosting/production-deployment.md): install a
  release bundle, HTTPS ingress, first start, backups, restore and upgrades.
- [Configuration](self-hosting/configuration.md): installation settings,
  environment-variable names, defaults and links to specialized references.
- [Media processing](self-hosting/media-processing.md): disc-image inspection
  and conversion, native binaries and known limits.

## Feature guides

How to use ROMD. Start with [your first library](features/first-library.md), or
[browse and play](features/browsing-and-playing.md) as a household member.

| Area | Guides |
| --- | --- |
| Systems and catalogs | [Systems and catalog sources](features/system-setup.md), [DAT subscriptions](features/dat-subscriptions.md), [manual DAT replacement](features/dat-replacement-review.md) |
| Importing ROMs | [Archive uploads and path imports](features/archive-imports.md), [tracked-title imports](features/tracked-title-imports.md), [disc compression](features/disc-compression.md) |
| Libraries and collections | [Audience libraries, game rules and tracking](features/library-audience-workspace.md), [collections](features/collection-workspace.md) |
| Artwork and metadata | [Artwork completion](features/unified-artwork-completion.md), [artwork collection and curation](features/artwork-curation.md) |
| Getting games out | [Downloads and exports](features/downloads-and-exports.md) |
| Playing games | [Playback compatibility](features/playback-compatibility.md), [browsing and playing](features/browsing-and-playing.md), [Ottercade setup](../clients/romd_console/docs/using-ottercade.md) |

## Development

For contributors working from a source checkout.

- [Getting started](development/getting-started.md): prerequisites, container
  engine, running the stack, tests and worktrees.
- [.NET development standards](development/dotnet-development.md): backend
  boundaries, functional policies, encapsulation, errors and meaningful tests.
- [Catalog capacity qualification](development/catalog-capacity.md): complete
  candidate/projection and export-planning memory measurements, scope and limits.
- [Demo data](development/demo-data.md): a repeatable private `.demo/` workspace.
- [Application brand snapshots](../brand/README.md): canonical assets, retained
  runtime compatibility files, and local export commands.
- [Web branding](../web/docs/branding.md): the manually copied ROMD kit and
  foundation placement workflow.
- [Console branding](../clients/romd_console/docs/branding.md): the checked-in
  Ottercade kit and native asset placement workflow.
- [Agentic development](development/agentic-development.md): working with
  Codex or Claude Code in this repository.
- [Known issues](development/known-issues.md) and the
  [resolved-issue archive](development/known-issues-archive.md): search these by
  command or error text when something fails unexpectedly.
- [Contributing](../CONTRIBUTING.md): change scope, validation and pull requests.
- Tools: [DAT preview](../tools/Romd.Dat.Preview/README.md) (read-only disc
  grouping preview) and [media probe](../tools/Romd.Media.Probe/README.md).
- API and generated clients: the
  [API client workflow](../.agents/skills/romd-web-api-client/SKILL.md).

## Architecture

How the system works and the invariants to preserve.

| Topic | Documents |
| --- | --- |
| Hosts and data | [Split-host topology](architecture/split-host-topology.md), [backend mutations](architecture/backend-mutations.md), [API contract status](architecture/api-contract-status.md), [admin statistics](architecture/admin-statistics.md) |
| Audience publication | [Library publication](architecture/library-publication.md) |
| Storage | [CAS random access](architecture/cas-random-access.md), [temporary staging](architecture/temporary-staging.md) |
| Game media | [Media architecture](architecture/game-media.md), [qualification matrix](architecture/media-status.md), [media processing](self-hosting/media-processing.md) |
| Delivery and playback | [Disc sets and layouts](architecture/disc-sets.md), [consumer delivery](architecture/consumer-delivery.md), [download and export contracts](architecture/downloads-and-exports.md), [browser player](architecture/browser-player.md), [WebDAV virtual filesystem](architecture/vfs.md) |
| Artwork and enrichment | [Artwork pipeline](architecture/artwork-pipeline.md) |
| Catalog data | [Reference catalog](architecture/shared-reference-data.md), [content ratings](architecture/content-ratings.md) |

## Decisions and designs

[Decision records](decisions/README.md) state the rules ROMD holds itself to
and the reasons behind them. Every record is in force; that page is the
style guide and the index of server and console records.
[`design/`](design/) holds unbuilt proposals, currently the
[DAT catalog publication protocol](design/dat-catalog-publication-protocol.md).
Implemented media and disc-set contracts live in the architecture guides above;
current source and tests establish shipped behavior.

## Maintainers

Release and repository operations.

- [Release readiness](maintainers/release-readiness.md): the checklist for a
  release candidate.
- [Public source cutover](maintainers/public-source-cutover.md): local snapshot
  preparation, publication approval and verification on the new repository.
- [Packaged release qualification](maintainers/packaged-release-qualification.md)
  and [release metadata](maintainers/release-metadata.md): bundle qualification,
  SBOMs and provenance.
- [Dependency updates](maintainers/dependency-updates.md): advisory checks and
  reviewed upgrades.
- [Continuous integration](maintainers/continuous-integration.md): early checks,
  CHD drill selection, frontend build reuse and weekly security maintenance.
- [Publication audit](maintainers/publication-audit.md) and
  [repository settings](maintainers/repository-settings.md): what was checked and
  configured before publishing.
- [CHD end-to-end harness](../scripts/e2e/README.md).

## Keeping docs healthy

- Update the relevant guide in the same change as the behavior it describes.
- Decisions record intent and rationale. Accepting a decision does not mean it
  is fully implemented.
- Don't commit dated validation logs, raw diagnostics or agent handoffs. Put
  evidence in the pull request and update the guide's conclusion instead.
- When a plan lands, fold its lasting rules into the guide or decision it
  affects and delete the plan. Git history keeps the original.
- Add an index entry here when a new document becomes the starting point for a
  recurring task.
