#!/usr/bin/env python3
"""Render the operator-facing release page after the appropriate release gates."""
import argparse
import json
from pathlib import Path
import re
from urllib.parse import urlsplit

from preflight import valid_tag
from verify_release import attestations_name

ROOT = Path(__file__).resolve().parents[2]


def render(repository, tag, revision, source_name, *, qualified=False, promoted_from=None):
    if (not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository)
            or not valid_tag(tag) or not re.fullmatch('[0-9a-f]{40}', revision)
            or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,199}', source_name)):
        raise ValueError('Invalid release page identity')
    if promoted_from is not None and (not valid_tag(promoted_from) or not promoted_from.startswith(tag + '-rc.')):
        raise ValueError('Invalid promotion source')
    project = 'https://github.com/' + repository
    download = project + '/releases/download/' + tag + '/'
    docs = project + '/blob/' + tag + '/docs/'
    bundle = f'romd-{tag}-deployment.tar.gz'
    attestations = attestations_name(tag)
    # Releases stay drafts until qualified, so only maintainers see the unqualified page.
    status = ('**Qualified prerelease · Linux amd64 · Docker Compose**' if qualified
              else '**Draft · qualification in progress**')
    qualification = (
        'This exact release passed packaged installation, authenticated browser playback, restart, '
        'backup/restore with preserved library identity and content, both injected-failure controls, '
        'image security policy and provenance verification. Its assets are immutable. '
        'Stable promotion remains a separate maintainer checkpoint.'
        if qualified else
        'Qualification is still in progress. This draft has not passed all release gates and '
        'is published only after it does.')
    promotion = (f'\n\nPromoted without rebuilding from [{promoted_from}]({project}/releases/tag/{promoted_from}): '
                 'the same image digests and deployment files, with only the release tag changed. '
                 f'Image labels keep the candidate version that built them.' if promoted_from else '')
    return f"""ROMD is a self-hosted home for your personal game library.

{status}

## Download and install

**[Download the deployment bundle]({download}{bundle})** · [SHA-256 checksum]({download}{bundle}.sha256)

1. Download the bundle and its checksum, then verify and extract it: `sha256sum --check {bundle}.sha256 && tar -xzf {bundle}` creates `romd-{tag}/`.
2. In that directory, follow the [quick start]({docs}self-hosting/production-deployment.md#quick-start): put your server's host name and the first administrator in `.env` and start the stack. ROMD generates its own secrets and HTTPS certificate. The bundle also includes the Compose configuration, pinned image references and operational tools.
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

[Attestations]({download}{attestations}) · [CHD tooling source (GPL)]({download}{source_name})

The attestations file holds the signed provenance and SBOM for the bundle and every image it installs. Verify them with the [GitHub CLI](https://cli.github.com/); no GitHub login is needed:

```bash
gh attestation verify {bundle} --bundle {attestations} --repo {repository}
gh attestation verify oci://IMAGE@sha256:DIGEST --bundle {attestations} --repo {repository}
```

Run the second command for each image in the bundle's `images.env`. See the [verification guide]({docs}maintainers/release-metadata.md#attestation-and-public-verification).

{qualification}{promotion}

Built from [{revision[:7]}]({project}/commit/{revision}).
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('repository', 'tag', 'revision'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--qualified', action='store_true')
    parser.add_argument('--publisher-state', type=Path,
                        help='Same-run publisher state; records a promotion source when present')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    source = json.loads((ROOT / 'native/chdman/mame.json').read_text())
    source_name = Path(urlsplit(source['sourceUrl']).path).name
    promoted_from = None
    if args.publisher_state is not None:
        state = json.loads(args.publisher_state.read_text())
        if state.get('releaseTag') != args.tag or state.get('sourceCommit') != args.revision:
            raise SystemExit('Publisher state belongs to a different release')
        promoted_from = state.get('promotedFrom')
    args.output.write_text(render(args.repository, args.tag, args.revision, source_name,
                                 qualified=args.qualified, promoted_from=promoted_from))


if __name__ == '__main__':
    main()
