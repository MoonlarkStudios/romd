#!/usr/bin/env bash
# Fuzzes the pinned libchdr with libFuzzer (clang, ASan, UBSan) in a disposable native-architecture Linux container.
# Everything it writes lands in artifacts/fuzz/; see native/libchdr/FUZZING.md.
#
#   fuzz.sh run [seconds-per-target]   default 60; FUZZ_WORKERS=1|2 (default 1)
#   fuzz.sh replay <file>...           CHDR_FUZZ_VERBOSE=1 prints one summary line per input
#   fuzz.sh parity                     the harness and Moonlark.Libchdr must admit and reject every seed alike
#   fuzz.sh canary                     ASan and UBSan must fire in a separate canary build; the fuzzing build is canary-free
#   fuzz.sh gate [seconds-per-target]  canary, then parity, then run (default 1800), stopping at the first failure
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
cd "$root"

# Qualification runs natively: an emulated campaign does not qualify either RID.
case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) arch=amd64; rid=linux-x64 ;;
  Linux-aarch64 | Linux-arm64 | Darwin-arm64) arch=arm64; rid=linux-arm64 ;;
  *) echo "fuzz: unsupported host architecture" >&2; exit 2 ;;
esac
platform="linux/$arch"
image="romd-libchdr-fuzz:$arch"
out="artifacts/fuzz/$rid"
upstream=native/libchdr/upstream
source_snapshot=

check_pin() {
  local pinned head
  pinned="$(python3 -I -c 'import json, sys; print(json.load(open(sys.argv[1]))["commit"])' native/libchdr/libchdr.json)"
  head="$(git -C "$upstream" rev-parse HEAD 2>/dev/null)" \
    || { echo "fuzz: $upstream is not checked out (git submodule update --init $upstream)" >&2; exit 1; }
  [[ "$head" == "$pinned" ]] || { echo "fuzz: $upstream is at $head, the pin is $pinned" >&2; exit 1; }
  [[ -z "$(git -C "$upstream" status --porcelain --ignored)" ]] \
    || { echo "fuzz: $upstream has local changes or ignored files" >&2; exit 1; }
}

# Seeds: the synthetic fixtures and the C#-generated malformed corpus, regenerated on every run.
export_seeds() {
  local fixtures=artifacts/fixtures/libchdr-final count
  compgen -G "$fixtures/*.chd" >/dev/null \
    || { echo "fuzz: no fixtures in $fixtures (mise run native:test-inputs)" >&2; exit 1; }
  rm -rf "$out/seeds"
  mkdir -p "$out/seeds/fixtures"
  cp "$fixtures"/*.chd "$out/seeds/fixtures/"
  dotnet run --project tests/Moonlark.Libchdr.Tests -c Release -v quiet -- export-malformed-corpus "$out/seeds/malformed"
  count="$(find "$out/seeds/malformed" -type f -name '*.chd' | wc -l | tr -d ' ')"
  (( count > 0 )) || { echo "fuzz: the malformed corpus export wrote no seeds" >&2; exit 1; }
  echo "fuzz: seeds: $(find "$out/seeds/fixtures" -type f | wc -l | tr -d ' ') fixtures, $count malformed inputs"
}

in_container() {
  local identity engine_arch rootless user image_arch
  identity="$(docker info --format '{{json .}}' | python3 -I -c '
import json, sys
try:
    info = json.load(sys.stdin)
    arch = {"x86_64": "amd64", "amd64": "amd64", "aarch64": "arm64", "arm64": "arm64"}[info["Architecture"]]
    print(arch, int("name=rootless" in info.get("SecurityOptions", [])))
except (KeyError, TypeError, ValueError):
    sys.exit("fuzz: Docker must report a supported native architecture")')"
  read -r engine_arch rootless <<<"$identity"
  [[ "$engine_arch" == "$arch" ]] || { echo "fuzz: Docker architecture differs from the host; use a native $platform engine" >&2; exit 1; }
  user="$(id -u):$(id -g)"
  # Rootless Docker maps container root to the invoking host user. A numeric host
  # UID would map to a subordinate UID and could not write the owned output bind.
  [[ "$rootless" == 1 ]] && user=0:0
  if [[ -z "$source_snapshot" ]]; then
    mkdir -p "$out"
    source_snapshot="$(mktemp -d "$root/$out/source.XXXXXXXX")"
    cp -R "$root/native/libchdr/." "$source_snapshot/"
    echo "fuzz: frozen source ${source_snapshot#"$root/"}"
  fi
  docker build --quiet --platform "$platform" --tag "$image" "$source_snapshot/fuzz" >/dev/null
  image_arch="$(docker image inspect --format '{{.Architecture}}' "$image")"
  [[ "$image_arch" == "$arch" ]] || { echo "fuzz: built image architecture differs from $platform" >&2; exit 1; }
  mkdir -p "$out"
  echo "fuzz: $rid; output $out; native build recipe $(sed -n 's/.*BuildRecipeVersion = \([0-9]*\);.*/\1/p' tools/Moonlark.Native.Engineering/Native/NativePolicy.cs)"
  docker run --rm --platform "$platform" --memory 4g --cpus 2 --user "$user" \
    --network none --cap-drop ALL --security-opt no-new-privileges \
    --volume "$source_snapshot:/src:ro" --volume "$root/$out:/out" --env CHDR_FUZZ_VERBOSE --env "CHDR_FUZZ_OUTPUT=$out" \
    "$image" bash /src/fuzz/container.sh "$@"
}

check_run_arguments() {
  [[ "$1" =~ ^[1-9][0-9]*$ ]] || { echo "fuzz: seconds must be a positive integer" >&2; exit 2; }
  [[ "$2" =~ ^[12]$ ]] || { echo "fuzz: FUZZ_WORKERS must be 1 or 2 (the container has 2 CPUs)" >&2; exit 2; }
}

run() {
  local seconds=${1:-60} workers=${FUZZ_WORKERS:-1}
  check_run_arguments "$seconds" "$workers"
  check_pin
  export_seeds
  in_container run "$seconds" "$workers"
}

# The M5 gate in order, stopping at the first failure: the canary, the parity check, then the campaign.
gate() {
  local seconds=${1:-1800}
  check_run_arguments "$seconds" "${FUZZ_WORKERS:-1}"
  echo "== gate 1/3: sanitizer canary"
  canary
  echo "== gate 2/3: guard parity"
  parity
  echo "== gate 3/3: fuzzing for ${seconds}s per target"
  run "$seconds"
}

# Runs each input once. The container sees artifacts/fuzz as /out; other inputs are copied to artifacts/fuzz/replay.
replay() {
  (( $# > 0 )) || { echo "usage: $0 replay <file>..." >&2; exit 2; }
  check_pin
  mkdir -p "$out/replay"
  local file absolute names=()
  for file in "$@"; do
    [[ -f "$file" ]] || { echo "fuzz: $file is not a file" >&2; exit 2; }
    absolute="$(cd "$(dirname "$file")" && pwd -P)/$(basename "$file")"
    if [[ "$absolute" == "$root/$out/"* ]]; then
      names+=("/out/${absolute#"$root/$out/"}")
    else
      cp "$file" "$out/replay/"
      names+=("/out/replay/$(basename "$file")")
    fi
  done
  in_container replay "${names[@]}"
}

# The harness replays every seed whole in the container, then Moonlark.Libchdr opens the same files on this host. The
# managed comparison is kept in artifacts/fuzz/parity/report.txt; pipefail keeps its exit status.
parity() {
  rm -f "$out/parity/report.txt"
  check_pin
  export_seeds
  in_container parity
  dotnet run --project tests/Moonlark.Libchdr.Tests -c Release --no-build -v quiet -- guard-parity "$out/parity/harness.log" "$out/seeds" \
    | tee "$out/parity/report.txt"
}

# The ASan input is a malformed-corpus seed, so the seeds are exported first. A previous verdict is removed before
# anything can fail, so summary.txt only ever reports this run.
canary() {
  rm -f "$out/canary/summary.txt" "$out/canary/summary.txt.partial"
  check_pin
  export_seeds
  in_container canary
}

case "${1:-}" in
  run) shift; run "$@" ;;
  replay) shift; replay "$@" ;;
  parity) parity ;;
  canary) canary ;;
  gate) shift; gate "$@" ;;
  *) echo "usage: $0 run [seconds-per-target] | replay <file>... | parity | canary | gate [seconds-per-target]" >&2; exit 2 ;;
esac
