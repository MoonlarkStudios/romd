#!/usr/bin/env python3
"""Add a release's version tag to its qualified ROMD image digests.

Candidates are pushed only under a `sha-<commit>` staging tag and promotions
reuse existing digests, so no image carries a version tag until completion runs
this tool, after every gate has passed and immediately before the release is
published. Every version tag is checked before any is written: a tag that
already names a different digest blocks the release, and a tag that already
names the qualified digest (from an interrupted attempt) is kept as is.
"""
import argparse
from pathlib import Path
import re
import sys
import urllib.error

from preflight import valid_tag
from qualify import image_map
from registry import Registry, image_repositories


def tag_images(images_path, repository, tag, registry=Registry):
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository) or not valid_tag(tag):
        raise ValueError('Expected a repository and a valid v<SemVer> release tag')
    repositories = image_repositories(image_map(images_path), repository.split('/')[0].lower())
    clients = {service: registry(name) for service, (name, _) in repositories.items()}
    existing = {service: clients[service].tagged_digest(tag) for service in repositories}
    for service, (_, digest) in repositories.items():
        if existing[service] not in (None, digest):
            raise ValueError(f'romd-{service}:{tag} already names a different digest; it cannot be replaced')
    for service, (_, digest) in repositories.items():
        if existing[service] is None:
            clients[service].tag(digest, tag)
    return {service: digest for service, (_, digest) in repositories.items()}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--images', type=Path, required=True)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--tag', required=True)
    args = parser.parse_args()
    try:
        tagged = tag_images(args.images, args.repository, args.tag)
    except (ValueError, OSError, KeyError, urllib.error.URLError) as error:
        parser.exit(1, (str(error) if type(error) is ValueError else 'Version tagging failed') + '\n')
    print(f'Tagged {len(tagged)} qualified images as {args.tag}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
