#!/usr/bin/env python3
"""Conservatively omit the expensive CHD drill only for unrelated pull requests."""
import argparse
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
OID = re.compile(r'[0-9a-f]{40}')
DOCUMENTS = {'README.md', 'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md', 'SECURITY.md',
             'THIRD_PARTY_NOTICES.md', 'ROADMAP.md', 'AGENTS.md', '.github/pull_request_template.md'}


def unrelated(path):
    name = PurePosixPath(path)
    if name.is_absolute() or '..' in name.parts or not name.parts:
        return False
    return (path in DOCUMENTS or path.startswith(('docs/', 'brand/', 'clients/romd_console/',
                                                 '.github/ISSUE_TEMPLATE/'))
            or (len(name.parts) >= 5 and name.parts[:2] == ('web', 'packages')
                and name.parts[3] == 'src'))


def requires_chd(event, paths):
    # Main and reusable release invocations remain full, even for documentation.
    return event != 'pull_request' or not paths or any(not unrelated(path) for path in paths)


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
    required = requires_chd(args.event, paths)
    if os.environ.get('GITHUB_OUTPUT'):
        with open(os.environ['GITHUB_OUTPUT'], 'a') as output:
            output.write(f'chd={str(required).lower()}\n')
    print(json.dumps({'chdRequired': required, 'event': args.event,
                      'reason': 'full verification' if required else 'only unrelated PR paths',
                      'changedPaths': paths}))


if __name__ == '__main__':
    main()
