#!/usr/bin/env python3
"""Stage once-built web assets and verify their exact MSBuild static-asset binding."""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import shutil

ROOT = Path(__file__).resolve().parents[2]
HOSTS = {'admin': 'Romd.Admin.Host', 'consumer': 'Romd.Consumer.Host'}


def contained(root, path):
    if not path.resolve().is_relative_to(root.resolve()):
        raise ValueError('Frontend asset path escapes this checkout')
    for parent in (path, *path.parents):
        if parent == root:
            break
        if parent.is_symlink():
            raise ValueError('Frontend asset path is linked')


def inventory(directory):
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError('Frontend asset directory is missing or linked')
    result = {}
    for path in directory.rglob('*'):
        if path.is_symlink():
            raise ValueError('Linked frontend assets are prohibited')
        if path.is_file():
            result[path.relative_to(directory).as_posix()] = hashlib.sha256(path.read_bytes()).digest()
    if 'index.html' not in result:
        raise ValueError('Frontend build lacks index.html')
    return result


def assets(root, stage=False):
    for role, host_name in HOSTS.items():
        source = root / f'web/packages/romd-{role}-app/dist'
        host = root / 'src' / host_name
        target = host / 'wwwroot'
        contained(root, source)
        contained(root, target)
        expected = inventory(source)
        if target.is_symlink():
            raise ValueError('Host asset destination is linked')
        if stage:
            if target.exists():
                shutil.rmtree(target)
            shutil.copytree(source, target)
        if inventory(target) != expected:
            raise ValueError('Host assets differ from the current frontend build')
        if not stage:
            manifest_path = host / 'obj/Release/net10.0/staticwebassets.build.json'
            contained(root, manifest_path)
            manifest = json.loads(manifest_path.read_text())
            primary = {Path(item['Identity']).resolve(): item for item in manifest['Assets']
                       if item.get('AssetRole') == 'Primary'}
            for relative, digest in expected.items():
                item = primary.get((target / relative).resolve(), {})
                if (item.get('Integrity') != base64.b64encode(digest).decode()
                        or item.get('CopyToPublishDirectory') not in ('Always', 'PreserveNewest')):
                    raise ValueError('MSBuild does not publish the current frontend asset bytes')
        print(f'{role}: {len(expected)} current frontend assets {"staged" if stage else "bound for publication"}')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', action='store_true')
    args = parser.parse_args()
    assets(ROOT, args.stage)


if __name__ == '__main__':
    main()
