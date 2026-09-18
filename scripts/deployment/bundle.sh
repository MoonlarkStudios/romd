#!/usr/bin/env bash
# Build a source-free deployment bundle using a release's digest-pinned image map.
set -euo pipefail
[[ $# -eq 2 ]] || { echo 'usage: bundle.sh <images.env> <output.tar.gz>' >&2; exit 2; }
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
images="$1"
out="$2"
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
cp "$root/docs/production-deployment.md" "$stage/docs/"
cp "$root/deploy/README.md" "$stage/README.md"
# Keep relative notice links usable in the extracted bundle, without copying assets.
for notice in LICENSE THIRD_PARTY_NOTICES.md TRADEMARKS.md \
  brand/Funnel-OFL.txt \
  web/packages/romd-foundation/src/fonts/Archivo-OFL.txt \
  web/packages/romd-foundation/src/fonts/IBMPlexMono-OFL.txt \
  reference-data/assets/presentation/platforms/ATTRIBUTION.md \
  reference-data/assets/presentation/ratings/ATTRIBUTION.md; do
  mkdir -p "$stage/$(dirname "$notice")"
  cp "$root/$notice" "$stage/$notice"
done
# macOS tar otherwise adds AppleDouble metadata files to portable release bundles.
COPYFILE_DISABLE=1 tar -czf "$out" -C "$stage" .
(cd "$(dirname "$out")" && shasum -a 256 "$(basename "$out")") > "$out.sha256"
