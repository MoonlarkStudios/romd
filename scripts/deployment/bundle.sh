#!/usr/bin/env bash
# Build a source-free deployment bundle using a release's digest-pinned image map.
set -euo pipefail
[[ $# -eq 4 ]] || { echo 'usage: bundle.sh <images.env> <output.tar.gz> <full-source-commit> <release-tag>' >&2; exit 2; }
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
images="$1"
out="$2"
revision="$3"
tag="$4"
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { echo "full source commit required" >&2; exit 2; }
[[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$ ]] || { echo "release tag required" >&2; exit 2; }
python3 - "$images" <<'PY'
import re, sys
entries = dict(line.strip().split('=', 1) for line in open(sys.argv[1]) if line.strip() and not line.startswith('#'))
for name in ('ADMIN', 'CONSUMER', 'WORKER', 'PLAYER', 'POSTGRES'):
    value = entries.get(f'ROMD_{name}_IMAGE', '')
    if not re.fullmatch(r'[^\s]+@sha256:[0-9a-f]{64}', value):
        sys.exit(f'ROMD_{name}_IMAGE must be digest-pinned')
PY
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/deploy" "$stage/scripts" "$stage/docs"
cp "$root/compose.yaml" "$stage/"
cp "$images" "$stage/images.env"
cp "$root/deploy/.env.example" "$stage/.env.example"
cp -R "$root/deploy/postgres" "$root/deploy/examples" "$stage/deploy/"
cp -R "$root/scripts/backup" "$stage/scripts/"
mkdir -p "$stage/scripts/deployment"
for tool in compose.sh validate.sh validate.py verify.sh; do
  cp "$root/scripts/deployment/$tool" "$stage/scripts/deployment/"
done
cp "$root/docs/self-hosting/production-deployment.md" "$root/docs/self-hosting/media-processing.md" \
  "$root/docs/self-hosting/configuration.md" "$stage/docs/"
cp "$root/deploy/README.md" "$stage/README.md"
# Keep relative notice links usable in the extracted bundle, without copying assets.
for notice in LICENSE THIRD_PARTY_NOTICES.md TRADEMARKS.md \
  brand/fonts/Fraunces-OFL.txt \
  web/packages/romd-foundation/src/fonts/DMSans-OFL.txt \
  web/packages/romd-foundation/src/fonts/Archivo-OFL.txt \
  web/packages/romd-foundation/src/fonts/IBMPlexMono-OFL.txt \
  reference-data/assets/presentation/platforms/ATTRIBUTION.md \
  reference-data/assets/presentation/ratings/ATTRIBUTION.md; do
  mkdir -p "$stage/$(dirname "$notice")"
  cp "$root/$notice" "$stage/$notice"
done
# Preserve native notice links without introducing application source into the bundle.
mkdir -p "$stage/native/libchdr" "$stage/native/chdman" "$stage/notices/Moonlark.Libchdr"
cp -R "$root/native/libchdr/notices" "$stage/native/libchdr/"
cp -R "$root/native/chdman/notices" "$stage/native/chdman/"
cp "$root/src/Moonlark.Libchdr/LICENSE" "$stage/notices/Moonlark.Libchdr/LICENSE"
python3 - "$stage/THIRD_PARTY_NOTICES.md" <<'PYNOTICES'
from pathlib import Path
import sys
path = Path(sys.argv[1])
path.write_text(path.read_text().replace('src/Moonlark.Libchdr/LICENSE', 'notices/Moonlark.Libchdr/LICENSE'))
PYNOTICES
# Preserve local operational links and pin omitted source references before hashing.
python3 "$root/scripts/agents/doc_links.py" --bundle "$root" "$stage" "$revision"
# Bind every installed byte and published image to the approved release source.
python3 - "$stage" "$revision" "$tag" <<'PYMANIFEST'
import hashlib, json, sys
from pathlib import Path
stage = Path(sys.argv[1])
files = {}
for path in sorted(stage.rglob('*')):
    if path.is_symlink():
        raise SystemExit('Release bundle cannot contain symlinks')
    # The source-free bundle is public. Its assets must be usable by non-root
    # container users regardless of the publisher's private umask.
    if path.is_dir():
        path.chmod(0o755)
    if path.is_file():
        path.chmod(0o755 if path.stat().st_mode & 0o111 else 0o644)
        files[path.relative_to(stage).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
images = dict(line.split('=', 1) for line in (stage / 'images.env').read_text().splitlines()
              if line and not line.startswith('#'))
(stage / 'manifest.json').write_text(json.dumps(dict(schemaVersion=1, sourceCommit=sys.argv[2],
    releaseTag=sys.argv[3], platform='linux/amd64', files=files, images=images), indent=2) + '\n')
(stage / 'manifest.json').chmod(0o644)
PYMANIFEST
# macOS tar otherwise adds AppleDouble metadata files to portable release bundles.
COPYFILE_DISABLE=1 tar -czf "$out" -C "$stage" .
python3 -I - "$out" > "$out.sha256" <<'PYCHECKSUM'
import hashlib, sys
from pathlib import Path
archive = Path(sys.argv[1])
digest = hashlib.sha256()
with archive.open('rb') as source:
    for block in iter(lambda: source.read(1024 * 1024), b''):
        digest.update(block)
print(f'{digest.hexdigest()}  {archive.name}')
PYCHECKSUM
