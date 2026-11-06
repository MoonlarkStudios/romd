#!/usr/bin/env python3
"""Verify all portable and Linux image NuGet locks; use --update for reviewed dependency changes."""
import argparse
import hashlib
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[2]
IMAGE_PROJECTS = ('src/Romd.Admin.Host/Romd.Admin.Host.csproj',
                  'src/Romd.Consumer.Host/Romd.Consumer.Host.csproj',
                  'src/Romd.Worker.Host/Romd.Worker.Host.csproj',
                  'tools/Romd.Media.Probe/Romd.Media.Probe.csproj')
IMAGE_RIDS = ('linux-x64', 'linux-arm64')


def restore_commands(projects, solution, update=False):
    mode = ['--force-evaluate', '-p:RestoreLockedMode=false'] if update else ['--locked-mode']
    base = ['dotnet', 'restore', '--use-lock-file', '--disable-parallel', '--disable-build-servers', '-m:1', *mode]
    # Restore portable targets last so subsequent --no-restore builds use their graph.
    commands = [[*base, project, '--runtime', rid, '--lock-file-path', f'packages.{rid}.lock.json']
                for rid in IMAGE_RIDS for project in IMAGE_PROJECTS]
    return commands + [[*base, target] for target in ['Romd.sln', *sorted(projects - solution)]]


def lock_snapshot(root, projects):
    return {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for project in projects for path in (root / project).parent.glob('packages*.lock.json')}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--update', action='store_true', help='Regenerate locks for a reviewed dependency change')
    args = parser.parse_args()
    projects = set(subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard',
                                            '*.csproj'], cwd=ROOT, text=True).splitlines())
    solution = {p.replace('\\', '/') for p in re.findall(r'= "[^"]+", "([^"]+\.csproj)"',
                                                       (ROOT / 'Romd.sln').read_text())}
    if not solution <= projects or not set(IMAGE_PROJECTS) <= projects:
        parser.error('NuGet project inventory is incomplete')
    if not args.update and any(not (ROOT / project).with_name('packages.lock.json').is_file() for project in projects):
        parser.error('A project lacks its portable lockfile; run nuget_locks.py --update and review the diff')
    before = lock_snapshot(ROOT, projects)
    for command in restore_commands(projects, solution, args.update):
        subprocess.run(command, cwd=ROOT, check=True)
    if not args.update and before != lock_snapshot(ROOT, projects):
        parser.exit(1, 'Locked restore changed or created a lockfile; review and commit the complete inventory\n')


if __name__ == '__main__':
    main()
