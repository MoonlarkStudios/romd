#!/usr/bin/env python3
"""Prepare the bundled compose.yaml with pinned images and release metadata.

The source compose.yaml requires each ROMD_*_IMAGE; a bundle carries the exact
digest-pinned reference as that variable's default instead, so plain
`docker compose` works from the extracted bundle. The variables still override
the defaults, and `normalize` restores the required image variables so two
bundles can be compared apart from their image references.
"""
import re
import sys

KEYS = tuple(f'ROMD_{name}_IMAGE' for name in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER', 'POSTGRES'))
DEFAULT = re.compile(r'\$\{(' + '|'.join(KEYS) + r'):-([^}\s]*)\}')
REFERENCE = re.compile(r'[^\s{}$]+@sha256:[0-9a-f]{64}')
MANIFEST_MOUNT_MARKER = '      # Release bundles mount their deployment manifest here.\n'
MANIFEST_MOUNT = '''      - type: bind
        source: ./manifest.json
        target: /etc/romd/deployment-manifest.json
        read_only: true
        bind:
          create_host_path: false
'''


def required(key):
    return '${%s:?set %s from the release bundle}' % (key, key)


def render(text, images):
    if set(images) != set(KEYS) or any(not REFERENCE.fullmatch(value) for value in images.values()):
        raise ValueError('Image map must hold a digest-pinned reference for each release image')
    for key in KEYS:
        if required(key) not in text:
            raise ValueError(f'compose.yaml does not require {key}')
        text = text.replace(required(key), '${%s:-%s}' % (key, images[key]))
    return text


def images_in(text):
    """The image defaults a bundled compose.yaml carries; every use of a key must agree."""
    if any(required(key) in text for key in KEYS):
        raise ValueError('Bundled compose.yaml still requires an image variable')
    found = {}
    for key, value in DEFAULT.findall(text):
        if found.setdefault(key, value) != value or not REFERENCE.fullmatch(value):
            raise ValueError(f'Bundled compose.yaml has conflicting or mutable defaults for {key}')
    if set(found) != set(KEYS):
        raise ValueError('Bundled compose.yaml must default every release image')
    return found


def normalize(text):
    return DEFAULT.sub(lambda match: required(match.group(1)), text)


def main(argv):
    if len(argv) != 3:
        print('usage: compose_images.py <compose.yaml> <images.env>', file=sys.stderr)
        return 2
    compose, images = argv[1], argv[2]
    entries = dict(line.strip().split('=', 1) for line in open(images) if line.strip() and not line.startswith('#'))
    with open(compose) as source:
        text = render(source.read(), entries)
    if text.count(MANIFEST_MOUNT_MARKER) != 1:
        raise ValueError('compose.yaml must mark exactly one deployment manifest mount')
    with open(compose, 'w') as target:
        target.write(text.replace(MANIFEST_MOUNT_MARKER, MANIFEST_MOUNT))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
