#!/usr/bin/env bash
# Builds and checks ROMD's pinned native code for this host under artifacts/.
# macOS arm64 builds natively. Linux builds in the Dockerfile's AlmaLinux native
# stages, which hold the glibc <= 2.31 policy; use the selected container engine.
#
#   build.sh libchdr | chdman | fixtures | generation-check | test-inputs
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$root"

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) rid=osx-arm64 ;;
  Linux-x86_64) rid=linux-x64 ;;
  Linux-aarch64 | Linux-arm64) rid=linux-arm64 ;;
  *) echo "native: unsupported host $(uname -s)-$(uname -m)" >&2; exit 1 ;;
esac

container() { python3 "$root/scripts/containers/runtime.py" engine "$@"; }

engineering=tools/Moonlark.Native.Engineering
# The CLI refuses unrecorded GIT_* overrides; a caller's shell settings such as
# GIT_EDITOR are not build inputs, so the CLI runs without them.
cli() {
  local name clear=()
  for name in $(compgen -e); do [[ "$name" == GIT_* && "$name" != GIT_PAGER ]] && clear+=(-u "$name"); done
  env ${clear[@]+"${clear[@]}"} dotnet "$engineering/bin/Release/net10.0/Moonlark.Native.Engineering.dll" "$@"
}
build_cli() { dotnet build "$engineering" -c Release -nologo -v quiet; }

libchdr() {
  local out="artifacts/native/libchdr/$rid"
  if [[ "$rid" == linux-* ]]; then
    rm -rf "$out"
    container build --target libchdr-artifacts --output "type=local,dest=$out" .
    return
  fi
  if [[ -f "$out/build-manifest.json" ]] && cli native verify --manifest "$out/build-manifest.json" >/dev/null 2>&1; then
    echo "native: libchdr $rid is current"
    return
  fi
  init_submodule
  cli native build --rid "$rid"
  cli native verify --manifest "$out/build-manifest.json"
}

init_submodule() { [[ -e native/libchdr/upstream/.git ]] || git submodule update --init native/libchdr/upstream; }

# Prints "tag url bytes sha256" from the MAME pin.
mame_pin() {
  python3 -I -c 'import json, sys; p = json.load(open(sys.argv[1])); print(p["tag"], p["sourceUrl"], p["sourceBytes"], p["sourceSha256"])' \
    native/chdman/mame.json
}

chdman_recipe() {
  sed -n 's/.*internal const int RecipeVersion = \([0-9][0-9]*\);.*/\1/p' "$engineering/Chdman/ChdmanBuild.cs"
}

# ChdmanBuild.RecipeSha256: the digest of the sorted `sha256  path` listing of the recipe sources.
chdman_recipe_sha256() {
  python3 -I - "$engineering/Chdman" <<'PY'
import hashlib, sys
from pathlib import Path
sources = sorted(Path(sys.argv[1]).rglob('*.cs'), key=lambda path: path.as_posix().encode())
listing = ''.join(hashlib.sha256(path.read_bytes()).hexdigest() + '  ' + path.as_posix() + '\n'
                  for path in sources)
print(hashlib.sha256(listing.encode()).hexdigest())
PY
}

# Keeps only the binary, its record and the source-input inventory, not the extracted MAME source.
copy_chdman() {
  rm -rf "$2" && mkdir -p "$2"
  cp -R "$1/build-manifest.json" "$1/native" "$1/source-inputs.json" "$2/"
}

# A chdman build is current when its record names this RID, the pinned tarball, the
# recipe version and these recipe sources, and its binary still has the recorded digest.
chdman_current() {
  local out=$1 sha=$2 recipe=$3 recipe_sha=$4
  [[ -f "$out/build-manifest.json" && -f "$out/native/chdman" ]] || return 1
  python3 -I - "$out" "$rid" "$sha" "$recipe" "$recipe_sha" <<'PY'
import hashlib, json, sys
out, rid, sha, recipe, recipe_sha = sys.argv[1:]
m = json.load(open(f"{out}/build-manifest.json"))
binary = hashlib.sha256(open(f"{out}/native/chdman", "rb").read()).hexdigest()
ok = (m.get("rid") == rid and str(m.get("recipeVersion")) == recipe and m.get("recipeSha256") == recipe_sha
      and m.get("pin", {}).get("sourceSha256") == sha and m.get("binary", {}).get("sha256") == binary)
sys.exit(0 if ok else 1)
PY
}

chdman() {
  local out="artifacts/tools/chdman/$rid" tag url bytes sha recipe recipe_sha
  read -r tag url bytes sha < <(mame_pin)
  recipe="$(chdman_recipe)"
  [[ -n "$recipe" ]] || { echo "native: chdman recipe version not found" >&2; exit 1; }
  recipe_sha="$(chdman_recipe_sha256)"
  if chdman_current "$out" "$sha" "$recipe" "$recipe_sha"; then
    echo "native: chdman $rid is current"
    return
  fi
  if [[ "$rid" == linux-* ]]; then
    local jobs; jobs="$(nproc)"; (( jobs > 4 )) && jobs=4
    rm -rf "$out"
    container build --target chdman-artifacts --build-arg "CHDMAN_JOBS=$jobs" --output "type=local,dest=$out" .
  else
    local cache="${ROMD_NATIVE_CACHE:-$HOME/Library/Caches/romd}"
    local archive="$cache/mame/$sha/$tag.tar.gz" built="$cache/chdman/$rid-$sha-r$recipe-${recipe_sha:0:16}"
    if [[ ! -f "$archive" ]]; then
      mkdir -p "$(dirname "$archive")"
      curl --fail --location --proto '=https' --proto-redir '=https' --max-time 1800 --output "$archive.partial" "$url"
      [[ "$(stat -f %z "$archive.partial")" == "$bytes" ]] || { echo "native: MAME archive size differs from the pin" >&2; exit 1; }
      echo "$sha  $archive.partial" | shasum -a 256 -c - >/dev/null
      mv "$archive.partial" "$archive"
    fi
    if chdman_current "$built" "$sha" "$recipe" "$recipe_sha"; then
      copy_chdman "$built" "$out"
    else
      local jobs; jobs="$(sysctl -n hw.ncpu)"; (( jobs > 32 )) && jobs=32
      cli chdman build --archive "$archive" --jobs "$jobs" --log artifacts/chdman-build.log
      copy_chdman "$out" "$built"
    fi
  fi
  chdman_current "$out" "$sha" "$recipe" "$recipe_sha" || { echo "native: chdman $rid record does not match the pin" >&2; exit 1; }
}

fixtures() {
  chdman
  local dir=artifacts/fixtures/libchdr-final
  if [[ -d "$dir" ]] && cli fixtures verify --directory "$dir" >/dev/null 2>&1; then
    echo "native: libchdr fixtures are current"
    return
  fi
  cli fixtures generate --chdman "artifacts/tools/chdman/$rid/native/chdman" \
    --manifest "artifacts/tools/chdman/$rid/build-manifest.json" --log artifacts/fixtures-generate.log
  cli fixtures verify --directory "$dir"
}

generation_check() {
  init_submodule
  dotnet tool restore >/dev/null
  if [[ "$rid" == linux-* ]]; then
    rm -rf artifacts/generation/clang-resource
    container build --target clang-resources --output type=local,dest=artifacts/generation/clang-resource .
    cli generate --check --clang-resources artifacts/generation/clang-resource
  else
    cli generate --check
  fi
}

build_cli
case "${1:-}" in
  libchdr) libchdr ;;
  chdman) chdman ;;
  fixtures) fixtures ;;
  generation-check) generation_check ;;
  test-inputs) libchdr; fixtures; generation_check ;;
  *) echo "usage: $0 libchdr|chdman|fixtures|generation-check|test-inputs" >&2; exit 2 ;;
esac
