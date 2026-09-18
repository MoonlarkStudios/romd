#!/usr/bin/env bash
set -euo pipefail

if ! command -v sqlite3 >/dev/null 2>&1; then
  echo "sqlite3 is required" >&2
  exit 1
fi

writers="${WRITERS:-4}"
iterations="${ITERATIONS:-40}"
payload_bytes="${PAYLOAD_BYTES:-750000}"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/romd-sqlite-contention.XXXXXX")"

cleanup() {
  rm -rf "$work_dir"
}
trap cleanup EXIT

run_case() {
  local name="$1"
  local busy_timeout_ms="$2"
  local db="$work_dir/$name.db"
  local failures="$work_dir/$name.failures"

  sqlite3 "$db" "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=1000; CREATE TABLE writes (id INTEGER PRIMARY KEY, writer TEXT NOT NULL, iteration INTEGER NOT NULL, created_at TEXT NOT NULL);" >/dev/null

  run_writer() {
    local writer="$1"
    local iteration
    local sql
    local output

    for iteration in $(seq 1 "$iterations"); do
      sql="PRAGMA busy_timeout=$busy_timeout_ms; BEGIN IMMEDIATE; INSERT INTO writes (writer, iteration, created_at) VALUES ('writer-$writer', $iteration, datetime('now')); SELECT length(randomblob($payload_bytes)); COMMIT;"
      if ! output="$(sqlite3 "$db" "$sql" 2>&1 >/dev/null)"; then
        printf 'writer=%s iteration=%s error=%s\n' "$writer" "$iteration" "$output" >> "$failures"
      fi
    done
  }

  local writer
  for writer in $(seq 1 "$writers"); do
    run_writer "$writer" &
  done
  wait

  local expected_rows=$((writers * iterations))
  local actual_rows
  local failure_count=0
  actual_rows="$(sqlite3 "$db" "SELECT COUNT(*) FROM writes;")"

  if [[ -f "$failures" ]]; then
    failure_count="$(wc -l < "$failures" | tr -d ' ')"
  fi

  printf '%s: writers=%s iterations=%s busy_timeout_ms=%s rows=%s/%s failures=%s\n' \
    "$name" "$writers" "$iterations" "$busy_timeout_ms" "$actual_rows" "$expected_rows" "$failure_count"

  if [[ "$failure_count" != "0" ]]; then
    head -5 "$failures" | sed 's/^/  /'
  fi
}

run_case "no-timeout" 0
run_case "with-timeout" 5000
