#!/usr/bin/env python3
"""Fail clearly when local, declared and hosted toolchains disagree."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
import tomllib

ROOT = Path(__file__).resolve().parents[2]


def check(root, runtime=True):
    tools = tomllib.loads((root / '.mise.toml').read_text())['tools']
    sdk = json.loads((root / 'global.json').read_text())['sdk']
    package = json.loads((root / 'web/package.json').read_text())['packageManager']
    errors = []
    if sdk != {'version': tools['dotnet'], 'rollForward': 'disable', 'allowPrerelease': False}:
        errors.append('global.json must select the exact stable mise SDK')
    if package != 'pnpm@' + tools['pnpm']:
        errors.append('mise pnpm must match web/package.json packageManager')
    workflow = (root / '.github/workflows/ci.yml').read_text()
    workflows = '\n'.join(path.read_text() for path in sorted((root / '.github/workflows').glob('*.y*ml')))
    if f'DOTNET_VERSION: "{tools["dotnet"]}"' not in workflow:
        errors.append('CI SDK disagrees with mise')
    for version in re.findall(r'^\s*dotnet-version:\s*(.+)$', workflows, re.MULTILINE):
        if version.strip().strip('\'"') not in (tools['dotnet'], '${{ env.DOTNET_VERSION }}'):
            errors.append('CI step SDK disagrees with mise')
    if set(re.findall(r'^\s*node-version:\s*[\'"]?([^\'"\s]+)', workflows, re.MULTILINE)) != {tools['node']}:
        errors.append('CI Node disagrees with mise')
    if 'dotnet-quality: preview' in workflows:
        errors.append('CI selects a preview SDK')
    flutter = tomllib.loads((root / 'clients/romd_console/.mise.toml').read_text())['tools']['flutter']
    if set(re.findall(r'^\s*flutter-version:\s*[\'"]?([^\'"\s]+)', workflow, re.MULTILINE)) != {flutter}:
        errors.append('CI Flutter disagrees with the console mise pin')
    actionlint = re.findall(r'actionlint/releases/download/v([^/]+)/actionlint_([^_]+)_linux_amd64\.tar\.gz', workflow)
    if not actionlint or set(actionlint) != {(tools['actionlint'], tools['actionlint'])}:
        errors.append('CI actionlint download disagrees with mise')
    dockerfile = (root / 'Dockerfile').read_text()
    if f'FROM mcr.microsoft.com/dotnet/sdk:{tools["dotnet"]}-' not in dockerfile:
        errors.append('release container SDK disagrees with strict global.json selection')
    archive_versions = re.findall(r'https://builds\.dotnet\.microsoft\.com/dotnet/Sdk/([^/]+)/dotnet-sdk-([^-]+)-linux-\$sdk\.tar\.gz', dockerfile)
    if archive_versions != [(tools['dotnet'], tools['dotnet'])]:
        errors.append('native builder SDK archive disagrees with strict global.json selection')
    if runtime:
        for command in ('dotnet', 'node', 'pnpm'):
            actual = subprocess.check_output([command, '--version'], text=True).strip().removeprefix('v')
            print(f'{command}: {actual} (expected {tools[command]})')
            if actual != tools[command]:
                errors.append(f'{command}: run mise install and mise exec; selected version is {actual}')
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--configuration-only', action='store_true')
    args = parser.parse_args()
    errors = check(ROOT, not args.configuration_only)
    for error in errors:
        print(error, file=sys.stderr)
    return bool(errors)


if __name__ == '__main__':
    sys.exit(main())
