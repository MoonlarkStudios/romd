#!/usr/bin/env python3
"""Build a local, unsigned macOS ARM64 ROMD Mount bundle; never install it."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
RCLONE_VERSION = '1.75.1'
RCLONE_ARCHIVE = f'rclone-v{RCLONE_VERSION}-osx-arm64.zip'
RCLONE_SHA256 = 'c61d7a371c62bcbbe882c3423aa4b8bf63485c248dd0f692997b8f0c3f6d0c6f'
RCLONE_URL = f'https://downloads.rclone.org/v{RCLONE_VERSION}/{RCLONE_ARCHIVE}'
MAX_ARCHIVE_BYTES = 128 * 1024 * 1024
DOTNET_RUNTIME_VERSION = '10.0.10'


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def verify(directory):
    entries = list(directory.rglob('*'))
    if any(p.is_symlink() for p in entries):
        raise ValueError('Bundle contains symbolic links')
    manifest = json.loads((directory / 'manifest.json').read_text())
    expected = manifest['files']
    actual = {p.relative_to(directory).as_posix() for p in entries if p.is_file()}
    if actual != set(expected) | {'manifest.json'}:
        raise ValueError('Bundle inventory differs from manifest')
    for name, info in expected.items():
        path = directory / name
        if path.is_symlink() or not path.is_relative_to(directory) or '..' in Path(name).parts:
            raise ValueError('Invalid bundle path')
        if digest(path) != info['sha256'] or bool(path.stat().st_mode & 0o111) != info['executable']:
            raise ValueError(f'Bundle file differs: {name}')
    return manifest


def build(output, archive):
    if output.exists() or output.is_symlink():
        raise ValueError('Output must not already exist')
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.romd-mount-build-', dir=output.parent) as temporary:
        stage = Path(temporary)
        package = stage / 'package'
        package.mkdir()
        if archive is None:
            archive = stage / RCLONE_ARCHIVE
            with urllib.request.urlopen(RCLONE_URL, timeout=60) as response, archive.open('xb') as target:
                count = 0
                while block := response.read(1024 * 1024):
                    count += len(block)
                    if count > MAX_ARCHIVE_BYTES:
                        raise ValueError('rclone archive exceeds size limit')
                    target.write(block)
        if digest(archive) != RCLONE_SHA256:
            raise ValueError('rclone archive checksum mismatch')
        project = ROOT / 'clients/romd_mount'
        lib = package / 'lib'
        subprocess.run(['dotnet', 'publish', str(project / 'Romd.Mount.csproj'), '-c', 'Release',
                        '-r', 'osx-arm64', '--self-contained', 'true', '-o', str(lib),
                        '-p:RuntimeFrameworkVersion=' + DOTNET_RUNTIME_VERSION,
                        '-p:PublishTrimmed=false', '-p:PublishSingleFile=false'], check=True)
        notices = package / 'notices'
        notices.mkdir()
        with zipfile.ZipFile(archive) as source:
            prefix = RCLONE_ARCHIVE.removesuffix('.zip') + '/'
            # Extract only these exact regular file payloads, never archive paths.
            for name, target in [('rclone', lib / 'rclone'), ('README.txt', notices / 'rclone-README.txt')]:
                with source.open(prefix + name) as src, target.open('xb') as dst:
                    shutil.copyfileobj(src, dst)
        (lib / 'rclone').chmod(0o755)
        # Retain the release's complete upstream README, including its license.
        if 'Permission is hereby granted' not in (notices / 'rclone-README.txt').read_text():
            raise ValueError('rclone license missing from archive README')
        runtime = json.loads((lib / 'Romd.Mount.runtimeconfig.json').read_text())['runtimeOptions']['includedFrameworks']
        version = next(item['version'] for item in runtime if item['name'] == 'Microsoft.NETCore.App')
        assets = json.loads((project / 'obj/project.assets.json').read_text())
        candidates = [Path(p) / 'microsoft.netcore.app.runtime.osx-arm64' / version for p in assets['packageFolders']]
        runtime_package = next(p for p in candidates if (p / 'LICENSE.TXT').is_file())
        for name in ['LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT']:
            shutil.copy2(runtime_package / name, notices / ('dotnet-' + name))
        shutil.copy2(ROOT / 'LICENSE', package / 'LICENSE')
        shutil.copy2(project / 'README.md', package / 'DEVELOPMENT.md')
        sources = package / 'source'
        for path in project.rglob('*'):
            relative = path.relative_to(project)
            if any(part in {'bin', 'obj'} for part in relative.parts):
                continue
            if path.is_file() and (path.suffix in {'.cs', '.csproj'} or path.name == 'README.md'):
                target = sources / 'clients/romd_mount' / relative
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(path, target)
        (sources / 'scripts/mount').mkdir(parents=True)
        for script in (ROOT / 'scripts/mount').glob('*.py'):
            shutil.copy2(script, sources / 'scripts/mount' / script.name)
        shutil.copy2(ROOT / 'LICENSE', sources / 'LICENSE')
        launcher = package / 'romd-mount'
        launcher.write_text('''#!/bin/bash
set -euo pipefail
bundle="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
if [[ "${1-}" == mount ]]; then
  explicit=false
  for argument in "$@"; do
    if [[ "$argument" == --rclone ]]; then explicit=true; fi
  done
  if [[ "$explicit" == false ]]; then
    exec "$bundle/lib/Romd.Mount" "$@" --rclone "$bundle/lib/rclone"
  fi
fi
exec "$bundle/lib/Romd.Mount" "$@"
''')
        launcher.chmod(0o755)
        (package / 'README.md').write_text('''# ROMD Mount local validation bundle

Apple Silicon macOS only. No installed .NET runtime is required. Run
`./romd-mount --help`; the `mount` command uses the bundled rclone unless
`--rclone` explicitly selects another executable. Keep the entire folder together.
The launcher expects standard macOS system utilities on PATH.

This is an unsigned, unnotarized development artifact. It does not install
macFUSE, modify trust stores, configure a mount, or grant local-user isolation.
Do not describe it as a signed production release. See DEVELOPMENT.md for
backend prerequisites, authentication, mount lifecycle and remaining limitations.

`manifest.json` records file hashes and executable bits. Verify it with
`python3 source/scripts/mount/bundle.py --verify .`. This detects changes relative
to the manifest; it is not an authenticated signature. Source is under source/;
ROMD's license is LICENSE; bundled runtime and rclone notices are under notices/.
Rebuild with .NET 10 and Python 3.11+ using the included bundle script.
''')
        files = {p.relative_to(package).as_posix(): {'sha256': digest(p), 'executable': bool(p.stat().st_mode & 0o111)}
                 for p in sorted(package.rglob('*')) if p.is_file()}
        manifest = {'format': 1, 'runtimeIdentifier': 'osx-arm64', 'dotnetRuntimeVersion': version,
                    'rcloneVersion': RCLONE_VERSION, 'rcloneArchiveUrl': RCLONE_URL,
                    'rcloneArchiveSha256': RCLONE_SHA256, 'signedRelease': False, 'files': files}
        (package / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
        verify(package)
        os.rename(package, output)
    print(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--output', type=Path)
    mode.add_argument('--verify', type=Path)
    parser.add_argument('--rclone-archive', type=Path, help='Optional cached archive; pinned checksum is still required')
    args = parser.parse_args()
    if args.verify:
        manifest = verify(args.verify.resolve())
        print(f'Verified {len(manifest["files"])} files; signed release: {manifest["signedRelease"]}')
    else:
        build(args.output.absolute(), args.rclone_archive)


if __name__ == '__main__':
    main()
