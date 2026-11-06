#!/usr/bin/env python3
"""Fail when committed OpenAPI schemas or generated web clients drift from backend sources.

Runs `pnpm api:update` from web/ and compares the result with HEAD. Refuses to start
when those paths already have uncommitted changes, so regeneration never overwrites
developer work. Requires the .NET SDK, Node and installed web dependencies.
"""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DRIFT_PATHS = (
    'web/schemas/admin-v1.json',
    'web/schemas/consumer-v1.json',
    'web/packages/romd-admin-api-client/src/generated',
    'web/packages/romd-consumer-api-client/src/generated',
)
REMEDIATION = ("Remediation: run 'pnpm api:update' from web/, review the diff, and commit the "
               'regenerated schemas and clients in the same change as the backend/OpenAPI source.')


def status(root):
    """Porcelain status (modified, staged, deleted and untracked) for the generated paths."""
    return subprocess.run(['git', 'status', '--porcelain', '--untracked-files=all', '--', *DRIFT_PATHS],
                          cwd=root, check=True, capture_output=True, text=True).stdout


def main(root=ROOT, regenerate=None):
    before = status(root)
    if before:
        print('OpenAPI drift check refused: generated paths already have uncommitted changes, and regeneration '
              'would overwrite them:', file=sys.stderr)
        print(before, end='', file=sys.stderr)
        print('Commit, or move aside your changes to these paths, then rerun. Generated contracts are derived '
              'artifacts; change backend/OpenAPI sources and regenerate instead of hand-editing them.',
              file=sys.stderr)
        return 2
    if not (root / 'web/node_modules').is_dir():
        print('OpenAPI drift check: web dependencies are missing; run pnpm install --frozen-lockfile in web/.',
              file=sys.stderr)
        return 2
    command = regenerate or ['pnpm', 'api:update']
    print(f'Regenerating OpenAPI schemas and clients: {" ".join(command)} (cwd=web)', flush=True)
    try:
        result = subprocess.run(command, cwd=root / 'web')
    except OSError as error:
        print(f'OpenAPI drift check: cannot run {command[0]}: {error}', file=sys.stderr)
        return 2
    if result.returncode:
        print('OpenAPI regeneration failed; fix the backend build or generator error above.', file=sys.stderr)
        return result.returncode
    after = status(root)
    if after:
        print('Stale generated OpenAPI artifacts (committed files differ from regenerated output):', file=sys.stderr)
        print(after, end='', file=sys.stderr)
        print(subprocess.run(['git', 'diff', '--stat', '--', *DRIFT_PATHS], cwd=root,
                             capture_output=True, text=True).stdout, end='', file=sys.stderr)
        print(REMEDIATION, file=sys.stderr)
        print('The regenerated files are left in the working tree for review.', file=sys.stderr)
        return 1
    print('OpenAPI schemas and generated clients match backend sources.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
