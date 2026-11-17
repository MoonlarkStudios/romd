#!/usr/bin/env python3
"""Select the expensive packaged-rehearsal and CHD drills a pull request must run."""
import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
OID = re.compile(r'[0-9a-f]{40}')
# Image builds, the Compose model and the CI machinery itself feed both drills.
SHARED = ('.github/workflows/', 'scripts/ci/', 'scripts/validation/')
SHARED_FILES = {'Dockerfile', '.dockerignore', 'compose.yaml'}
PACKAGED = ('deploy/', 'scripts/deployment/', 'scripts/release/', 'scripts/backup/', 'scripts/security/',
            'scripts/containers/')
# The independently required advisory gate covers these source-only inputs.
# Shared policy and image/SBOM scanner inputs still select the packaged drill.
SOURCE_ADVISORY_ONLY = {
    'scripts/security/advisory_exceptions.py',
    'scripts/security/source-advisory-exceptions.json',
    'scripts/security/source_advisories.py',
    'scripts/security/test_advisory_exceptions.py',
    'scripts/security/test_javascript_advisories.py',
    'scripts/security/test_source_advisories.py',
}
CHD = ('native/', 'scripts/e2e/')
CHD_PROJECTS = ('Romd.Media', 'Moonlark.')


def drills(path):
    """The drills one changed path requires on a pull request."""
    name = PurePosixPath(path)
    if name.is_absolute() or '..' in name.parts or not name.parts:
        return {'chd', 'packaged'}
    if path in SHARED_FILES or path.startswith(SHARED):
        return {'chd', 'packaged'}
    if path in SOURCE_ADVISORY_ONLY:
        return set()
    required = set()
    if path.startswith(PACKAGED):
        required.add('packaged')
    if path.startswith(CHD) or 'chd' in path.lower() or any(part.startswith(CHD_PROJECTS) for part in name.parts):
        required.add('chd')
    return required


def required_drills(event, paths):
    # Main and reusable invocations always run both, so a pull request that skipped one is still
    # verified before any release: publication requires the successful main run for its commit.
    if event != 'pull_request' or not paths:
        return {'chd', 'packaged'}
    return set().union(*(drills(path) for path in paths))


def changed_paths(root, base):
    if not OID.fullmatch(base):
        raise ValueError('PR scope requires a full base commit')
    subprocess.run(['git', 'merge-base', '--is-ancestor', base, 'HEAD'], cwd=root, check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    data = subprocess.check_output(['git', 'diff', '--name-only', '--no-renames', '-z', base, 'HEAD'], cwd=root)
    # --no-renames includes both sides of a rename; deleting a relevant file cannot skip the drill.
    return [os.fsdecode(path) for path in data.split(b'\0') if path]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--event', default=os.environ.get('ROMD_CI_EVENT', ''))
    parser.add_argument('--base', default=os.environ.get('ROMD_CI_BASE', ''))
    args = parser.parse_args()
    paths = changed_paths(ROOT, args.base) if args.event == 'pull_request' else []
    required = required_drills(args.event, paths)
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write(''.join(f'{drill}={str(drill in required).lower()}\n' for drill in ('chd', 'packaged')))
    print(json.dumps({'chdRequired': 'chd' in required, 'packagedRequired': 'packaged' in required,
                      'event': args.event, 'changedPaths': paths}))

if __name__ == '__main__':
    main()
