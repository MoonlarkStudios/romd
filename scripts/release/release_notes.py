#!/usr/bin/env python3
"""Render the operator-facing release page after the appropriate release gates."""
import argparse
import json
from pathlib import Path
import re
from urllib.parse import urlsplit

from evidence_archive import archive_name
from preflight import valid_tag

ROOT = Path(__file__).resolve().parents[2]


def render(repository, tag, revision, source_name, *, qualified=False):
    if (not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository)
            or not valid_tag(tag) or not re.fullmatch('[0-9a-f]{40}', revision)
            or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,199}', source_name)):
        raise ValueError('Invalid release page identity')
    project = 'https://github.com/' + repository
    download = project + '/releases/download/' + tag + '/'
    docs = project + '/blob/' + tag + '/docs/'
    bundle = f'romd-{tag}-deployment.tar.gz'
    evidence = archive_name(tag)
    status = ('**Qualified prerelease · Linux amd64 · Docker Compose**' if qualified
              else '**Unqualified prerelease · verification in progress**')
    qualification = (
        'This exact release passed packaged installation, authenticated browser playback, restart, '
        'backup/restore with preserved library identity and content, both injected-failure controls, '
        'image/SBOM security policy, provenance, anonymous downloads and tamper rejection. '
        'Stable promotion remains a separate maintainer checkpoint.'
        if qualified else
        'Qualification is still in progress. This build is available for evaluation and has not '
        'passed all release gates. Check the release workflow before installing it.')
    verification_links = (
        f'[Verification archive]({download}{evidence}) · [Archive checksum]({download}{evidence}.sha256) · '
        f'[CHD tooling source (GPL)]({download}{source_name})'
        if qualified else f'[CHD tooling source (GPL)]({download}{source_name})\n\n'
        'The verification archive will be attached after qualification.')
    return f"""ROMD is a self-hosted home for your personal game library.

{status}

## Download and install

**[Download the deployment bundle]({download}{bundle})** · [SHA-256 checksum]({download}{bundle}.sha256)

1. Download the bundle and its checksum, then verify and extract the archive.
2. Follow the [installation guide]({docs}self-hosting/production-deployment.md). The bundle includes the Compose configuration, pinned image references and operational tools.
3. [Create your first library]({docs}features/first-library.md), import your games and open the consumer portal.

## What you can do

- Import and verify games against DAT catalogs.
- Curate metadata and artwork, organize collections and share household libraries.
- Browse and download your games, with isolated browser playback for supported systems.

## Installation and support

This release targets **Linux amd64 with Docker** and establishes a **fresh-installation baseline**. Upgrades from previous experimental builds or release candidates are not qualified. Keep existing installations and backups until you have followed the recovery guidance.

Ottercade and native mount clients remain source-only and experimental. See the [playback compatibility guide]({docs}features/playback-compatibility.md) for the supported systems and runtime limits.

[Configuration]({docs}self-hosting/configuration.md) · [Backup, recovery and upgrades]({docs}self-hosting/production-deployment.md#backup-and-restore)

## Verification and source

{verification_links}

The verification archive contains the complete SBOMs, vulnerability reports, policy results, provenance attestations and packaged qualification evidence. Follow the [verification guide]({docs}maintainers/release-metadata.md) to check its contents and signatures. Official PostgreSQL findings remain visible as upstream-managed dependency information.

{qualification}

Built from [{revision[:7]}]({project}/commit/{revision}).
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('repository', 'tag', 'revision'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--qualified', action='store_true')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source = json.loads((ROOT / 'native/chdman/mame.json').read_text())
    source_name = Path(urlsplit(source['sourceUrl']).path).name
    args.output.write_text(render(args.repository, args.tag, args.revision, source_name,
                                 qualified=args.qualified))


if __name__ == '__main__':
    main()
