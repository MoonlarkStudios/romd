#!/usr/bin/env python3
"""Confirm a just-published release is immutable and attests the verified local assets.

Completion publishes the draft only after every gate passes. With release
immutability enabled, GitHub then locks the tag and assets and signs a release
attestation over them. This check reads the published release state and uses
`gh release verify` / `gh release verify-asset` to prove that the public assets
are exactly the bytes the workflow verified. A release published without
immutability fails here and can never be promoted.
"""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
import time


def gh(*args, runner=subprocess.run):
    result = runner(['gh', *map(str, args)], capture_output=True, text=True, check=False)
    if result.returncode:
        raise ValueError(f'gh {args[0]} {args[1]} failed: {result.stderr.strip()}')
    return result.stdout


def confirm(repository, tag, assets, source_name, runner=subprocess.run, sleep=time.sleep, attempts=6):
    if (not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository)
            or not re.fullmatch(r'v[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?', tag)
            or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,199}', source_name)):
        raise ValueError('Invalid repository, tag or source asset name')
    state = json.loads(gh('release', 'view', tag, '--repo', repository, '--json',
                          'isDraft,isImmutable,isPrerelease,assets', runner=runner))
    if state.get('isDraft') is not False or state.get('isPrerelease') is not True:
        raise ValueError('Release is not a published prerelease')
    if state.get('isImmutable') is not True:
        raise ValueError('Release was published without immutability. Enable release immutability in the '
                         'repository settings; this release is public but unlocked and cannot be promoted.')
    published = {asset.get('name'): asset.get('size') for asset in state.get('assets', [])}
    expected = {path.name: path.stat().st_size for path in assets}
    if set(published) != set(expected) | {source_name} or any(published[name] != size for name, size in expected.items()):
        raise ValueError('Published release assets differ from the verified asset set')
    # GitHub signs the release attestation as the release is published.
    for attempt in range(attempts):
        try:
            gh('release', 'verify', tag, '--repo', repository, runner=runner)
            break
        except ValueError:
            if attempt == attempts - 1:
                raise
            sleep(10)
    for path in assets:
        gh('release', 'verify-asset', tag, path, '--repo', repository, runner=runner)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--repository', required=True)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--asset', type=Path, action='append', required=True, help='Locally verified asset')
    parser.add_argument('--source-name', required=True, help='GPL source asset uploaded by the publisher')
    args = parser.parse_args()
    try:
        confirm(args.repository, args.tag, args.asset, args.source_name)
    except (ValueError, OSError, json.JSONDecodeError) as error:
        parser.exit(1, (str(error) if type(error) is ValueError else 'Publication could not be confirmed') + '\n')
    print(f'{args.tag} is published, immutable, and attests {len(args.asset)} verified assets')
    return 0


if __name__ == '__main__':
    sys.exit(main())
