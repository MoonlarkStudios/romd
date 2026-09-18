#!/usr/bin/env bash
# Shared helpers for the local PostgreSQL development workflow (mise db:* tasks).
# Source this file; do not execute it.
set -euo pipefail

# The external pwd (getcwd) returns the on-disk spelling; bash's builtin keeps the
# caller's case, which on case-insensitive macOS would derive different database
# names for `cd ~/Development/romd` and `cd ~/Development/Romd`.
ROMD_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && /bin/pwd -P)"
ROMD_DB_COMPOSE=(docker compose -f "${ROMD_REPO_ROOT}/compose.db.yaml")

# Derive a per-checkout database name from the checkout path, mirroring how
# Romd__DataDirectory already varies per checkout. Parallel worktrees with
# divergent migration histories share one server without colliding. Not
# overridable: the derivation is the ownership boundary between checkouts,
# and its restricted charset is what makes the SQL interpolation below safe.
romd_db_name() {
  local slug hash name
  slug="$(basename "${ROMD_REPO_ROOT}" | tr '[:upper:]' '[:lower:]' | tr -c 'a-z0-9' '_' | cut -c1-24)"
  slug="${slug%_}"
  hash="$(printf '%s' "${ROMD_REPO_ROOT}" | shasum -a 256 | cut -c1-8)"
  name="romd_${slug}_${hash}"
  if [[ ! "${name}" =~ ^romd_[a-z0-9_]+_[0-9a-f]{8}$ ]]; then
    echo "error: derived database name '${name}' failed validation" >&2
    exit 1
  fi
  printf '%s' "${name}"
}

romd_snapshot_db_name() {
  printf '%s__snap__%s' "$(romd_db_name)" "$1"
}

# Snapshot names become part of a PostgreSQL identifier (63-byte limit).
romd_validate_snapshot_name() {
  local name="${1:-}"
  if [[ ! "${name}" =~ ^[a-z0-9_]{1,16}$ ]]; then
    echo "error: snapshot name must match [a-z0-9_]{1,16}, got '${name}'" >&2
    exit 1
  fi
}

# Non-interactive psql against the maintenance database.
romd_psql() {
  "${ROMD_DB_COMPOSE[@]}" exec -T postgres psql -U romd -d postgres -v ON_ERROR_STOP=1 -q "$@"
}

romd_db_exists() {
  [[ "$(romd_psql -tA -c "SELECT 1 FROM pg_database WHERE datname = '$1'")" == "1" ]]
}

romd_require_server() {
  if ! romd_psql -c "SELECT 1" >/dev/null 2>&1; then
    echo "error: dev database server is not running; start it with 'mise run db:up'" >&2
    exit 1
  fi
}

romd_create_db_if_missing() {
  local db="$1"
  if ! romd_db_exists "${db}"; then
    romd_psql -c "CREATE DATABASE \"${db}\""
    echo "created database ${db}"
  fi
}

# CREATE DATABASE ... TEMPLATE and DROP DATABASE both require the source or
# target to have no other connections.
romd_terminate_connections() {
  romd_psql -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$1' AND pid <> pg_backend_pid()" >/dev/null
}
