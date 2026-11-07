# ROMD roadmap

ROMD is a public pre-release for people who want to curate and share their
personal game library at home. Source evaluation is available today; the first
supported server release is pending qualification. Ottercade remains an
experimental, source-only console client.

This roadmap describes priorities, not release dates or promises of platform
support. The [release notes](https://github.com/MoonlarkStudios/romd/releases)
will identify each version's accepted support and upgrade scope.

## What you can explore today

- Import and verify your collection against DAT catalogs, with
  [archive and path imports](docs/features/archive-imports.md) and
  [reviewed catalog subscriptions](docs/features/dat-subscriptions.md).
- Curate metadata, artwork, collections and
  [household libraries](docs/features/library-audience-workspace.md).
- [Browse and download](docs/features/browsing-and-playing.md), play compatible
  titles in the browser, and explore Ottercade. Consult
  [playback compatibility](docs/features/playback-compatibility.md) for limits.

ROMD includes no commercial games or BIOS files. Bring your own lawfully
obtained collection. To evaluate the project, start with
[source development](docs/development/getting-started.md).

## Next priorities

| Priority | What completion means |
| --- | --- |
| Public source launch | An audited source snapshot, working contributor CI, private security reporting and reviewed asset provenance |
| First qualified server release | A downloaded Linux amd64 candidate passes installation, sign-in, browser playback, restart and backup/restore qualification, with verified security metadata |
| Predictable upgrades | Each release states a tested upgrade path or an explicit fresh-install requirement, with a documented recovery procedure |
| Ottercade qualification | Packaged console builds pass their claimed OS, architecture, emulator and physical-controller scenarios before those combinations are advertised as supported |
| Client compatibility | A published server/console compatibility policy before the console is distributed independently or ROMD reaches 1.0 |

The [maintainer release checklist](docs/maintainers/release-readiness.md) defines
the evidence behind those milestones. Hardware and packaged-release claims
follow completed qualification rather than the presence of an implementation.

## Help shape the project

[Report a reproducible bug or propose a use case](https://github.com/MoonlarkStudios/romd/issues/new/choose).
External pull requests require an explicit maintainer invitation on a specific
issue; see the [contribution guide](CONTRIBUTING.md#pull-request-policy).
Report vulnerabilities privately through [Security](SECURITY.md).
