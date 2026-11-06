#!/usr/bin/env bash
# Runs inside the disposable fuzz container that scripts/native/fuzz.sh starts. Sources are mounted
# read-only at /src (native/libchdr); builds, corpora, findings and logs go to /out (artifacts/fuzz).
#
#   container.sh run <seconds-per-target> <workers> | replay <file>... | parity | canary
set -euo pipefail
output_label=${CHDR_FUZZ_OUTPUT:-artifacts/fuzz}

targets=(chd_walk_fuzzer)
build=/out/build
seeds=(/out/seeds/fixtures /out/seeds/malformed)
# rss_limit also caps single allocations. max_len admits every seed the harness bounds let through,
# the largest being the 1.9 MB v4 self-reference chains; only the 16 MiB-hunk seeds are truncated.
max_len=2097152
budget=(-rss_limit_mb=1536 -timeout=10)
limits=("${budget[@]}" -max_len="$max_len")
canary_macros='CHDR_FUZZ_(SKIP_MAP_GUARD|UBSAN_CANARY)'

fail() { echo "container: $*" >&2; exit 1; }

build_targets() {
  clang --version | head -n 1
  cmake -S /src/fuzz -B "$build" -G Ninja -DCMAKE_C_COMPILER=clang -DCMAKE_EXPORT_COMPILE_COMMANDS=ON \
    -DCHDR_FUZZ_CANARY:BOOL=OFF >/dev/null
  cmake --build "$build" -- -j 2
  check_canary_free
}

# The fuzzing build must hold none of the canary: the option off in its cache, no canary macro in any compile
# command, and no canary code in the binary. Every run, replay, parity and canary command checks this.
check_canary_free() {
  local commands=$build/compile_commands.json symbols
  grep -qx 'CHDR_FUZZ_CANARY:BOOL=OFF' "$build/CMakeCache.txt" || fail "the fuzzing build's cache does not set CHDR_FUZZ_CANARY off"
  grep -q 'chd_walk_fuzzer\.c' "$commands" || fail "no compile command for chd_walk_fuzzer.c in $commands"
  if grep -Eq "$canary_macros" "$commands"; then fail "a fuzzing-build compile command defines a canary macro"; fi
  [[ ! -e "$build/chd_walk_canary" ]] || fail "the fuzzing build holds a chd_walk_canary binary"
  symbols="$(llvm-nm "$build/${targets[0]}")"
  if [[ "$symbols" == *chd_fuzz_ubsan_canary* ]]; then fail "the fuzzing binary contains the UBSan canary"; fi
}

run_target() {
  local target=$1 seconds=$2 workers=$3 dir="/out/$1" stamp
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  mkdir -p "$dir/corpus" "$dir/findings" "$dir/logs/$stamp"
  echo "== $target: every seed once (log: $output_label/$target/logs/$stamp/seeds.log)"
  "$build/$target" "${limits[@]}" -runs=0 -artifact_prefix="$dir/findings/" "${seeds[@]}" \
    >"$dir/logs/$stamp/seeds.log" 2>&1 || { tail -n 40 "$dir/logs/$stamp/seeds.log"; return 1; }
  grep -E 'seed corpus|INITED|Done' "$dir/logs/$stamp/seeds.log" || true
  echo "== $target: fuzzing for ${seconds}s with $workers worker(s) (logs: $output_label/$target/logs/$stamp)"
  local args=("${limits[@]}" -len_control=0 -max_total_time="$seconds" -print_final_stats=1
    -artifact_prefix="$dir/findings/" "$dir/corpus" "${seeds[@]}")
  local status=0 log
  if (( workers > 1 )); then
    # Each job logs to fuzz-<job>.log; the parent's own output, which repeats those logs, goes to jobs.log.
    (cd "$dir/logs/$stamp" && "$build/$target" -jobs="$workers" -workers="$workers" "${args[@]}") \
      >"$dir/logs/$stamp/jobs.log" 2>&1 || status=$?
  else
    "$build/$target" "${args[@]}" >"$dir/logs/$stamp/fuzz-0.log" 2>&1 || status=$?
  fi
  for log in "$dir/logs/$stamp"/fuzz-*.log; do summarize "$log" "$status"; done
  echo "== $target: corpus $(find "$dir/corpus" -type f | wc -l) files; findings $(find "$dir/findings" -type f | wc -l) files"
  return "$status"
}

# The last coverage line and the final stats, or the sanitizer report when the run failed.
summarize() {
  echo "-- ${1#/out/}"
  if (( $2 != 0 )); then tail -n 60 "$1"; return; fi
  grep -E 'cov:' "$1" | tail -n 1
  grep -E '^(Done [0-9]+ runs|stat::)' "$1"
}

# Replays every seed once with one disposition line each, for the managed comparison in scripts/native/fuzz.sh parity.
# Inputs are whole (no -max_len), because the managed side opens the same complete files; the seeds the campaign
# truncates are listed instead.
parity() {
  local log=/out/parity/harness.log inputs long
  mkdir -p /out/parity
  cd /out/seeds
  inputs=(fixtures/*.chd malformed/*.chd)
  CHDR_FUZZ_VERBOSE=1 "$build/${targets[0]}" "${budget[@]}" "${inputs[@]}" >"$log" 2>&1 \
    || { tail -n 40 "$log"; fail "the harness failed a seed during the parity replay (log: $output_label/parity/harness.log)"; }
  echo "parity: the harness replayed ${#inputs[@]} seeds whole (log: $output_label/parity/harness.log)"
  long="$(find fixtures malformed -type f -name '*.chd' -size +"$max_len"c | LC_ALL=C sort)"
  [[ -z "$long" ]] || { echo "parity: the campaign's -max_len=$max_len truncates these seeds to their first $max_len bytes:"; echo "$long" | sed 's/^/  /'; }
}

# Runs one canary input; the binary must fail and its output must contain every expected pattern. The exit status and
# the first line matching each pattern go to the canary's summary.
expect_report() {
  local name=$1 binary=$2 input=$3 log=$4 status=0 pattern line
  shift 4
  "$binary" "${budget[@]}" -artifact_prefix=/out/canary/ "$input" >"$log" 2>&1 || status=$?
  (( status != 0 )) || fail "the $name canary exited 0 (log: $output_label/${log#/out/})"
  echo "$name: exit $status (log: $output_label/${log#/out/})" >>"$summary"
  for pattern in "$@"; do
    line="$(grep -m 1 -F -- "$pattern" "$log")" || fail "the $name canary report lacks '$pattern' (log: $output_label/${log#/out/})"
    echo "$name: $line" >>"$summary"
  done
  echo "canary: $name fired (exit $status): $(grep -m 1 -F -- "$1" "$log")"
}

# Shows that ASan and UBSan fire, in a canary build configured from scratch in its own directory, and that the
# fuzzing build, checked canary-free first, rejects both canary inputs before any read. The summary is renamed to
# summary.txt only when every check passed.
canary() {
  local dir=/out/canary mini=/out/seeds/malformed/crafted-v4-mini-hunk-of-1.chd
  local binary=$dir/build/chd_walk_canary trigger=$dir/ubsan-trigger.bin summary=/out/canary/summary.txt.partial
  [[ -f "$mini" ]] || fail "missing seed $mini"
  rm -rf "$dir"
  mkdir -p "$dir"
  { echo "canary run: $(date -u +%Y-%m-%dT%H:%M:%SZ)"; clang --version | head -n 1; } >"$summary"
  cmake -S /src/fuzz -B "$dir/build" -G Ninja -DCMAKE_C_COMPILER=clang -DCMAKE_EXPORT_COMPILE_COMMANDS=ON \
    -DCHDR_FUZZ_CANARY:BOOL=ON >/dev/null
  cmake --build "$dir/build" -- -j 2
  grep -Eq "$canary_macros" "$dir/build/compile_commands.json" || fail "the canary build defines no canary macro"
  printf 'ROMD-UBSAN-CANARY' >"$trigger"
  expect_report ASan "$binary" "$mini" "$dir/asan.log" 'ERROR: AddressSanitizer: heap-buffer-overflow' \
    'put_bigendian_uint64_t' 'chd_walk: canary build'
  expect_report UBSan "$binary" "$trigger" "$dir/ubsan.log" 'runtime error: signed integer overflow' \
    'ubsan_canary.c' 'chd_walk: canary build'
  CHDR_FUZZ_VERBOSE=1 "$build/${targets[0]}" "${limits[@]}" "$mini" "$trigger" >"$dir/control.log" 2>&1 \
    || fail "the fuzzing build failed a canary input (log: $output_label/canary/control.log)"
  grep -q 'chd_walk: reject stage=validation .* reason=map: MINI entry in a hunk under 8 bytes$' "$dir/control.log" \
    || fail "the fuzzing build did not reject the MINI seed at its map guard (log: $output_label/canary/control.log)"
  grep -q 'chd_walk: reject stage=envelope .* reason=header signature$' "$dir/control.log" \
    || fail "the fuzzing build did not reject the UBSan trigger at its envelope check (log: $output_label/canary/control.log)"
  grep -h 'chd_walk: reject' "$dir/control.log" | sed 's/^/control (fuzzing build): /' >>"$summary"
  echo "canary: the fuzzing build is canary-free and rejects both inputs before reading a hunk"
  sha256sum "$build/${targets[0]}" "$binary" | sed "s|/out/|$output_label/|" | tee -a "$summary"
  echo "canary: passed" >>"$summary"
  mv "$summary" "$dir/summary.txt"
  echo "canary: passed (summary: $output_label/canary/summary.txt); never fuzz or replay with $output_label/canary/build"
}

case "${1:-}" in
  run)
    build_targets
    for target in "${targets[@]}"; do run_target "$target" "$2" "$3"; done
    ;;
  replay)
    shift
    build_targets
    "$build/${targets[0]}" "${limits[@]}" "$@"
    ;;
  parity) build_targets; parity ;;
  canary) build_targets; canary ;;
  *) echo "usage: $0 run <seconds> <workers> | replay <file>... | parity | canary" >&2; exit 2 ;;
esac
