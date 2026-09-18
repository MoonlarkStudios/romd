#!/usr/bin/env bash
# Snapshot this checkout's database via CREATE DATABASE ... TEMPLATE.
# Usage: snapshot.sh <name>   (no argument lists existing snapshots)
# Restoring a snapshot resets state without paying fixture regeneration,
# including 100k/1M scale-harness fixtures.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

romd_require_server
db="$(romd_db_name)"

if [[ $# -eq 0 ]]; then
  romd_psql -tA -c "SELECT datname FROM pg_database WHERE datname LIKE '${db}\\_\\_snap\\_\\_%' ORDER BY datname" \
    | sed "s/^${db}__snap__//"
  exit 0
fi

romd_validate_snapshot_name "$1"
snapshot_db="$(romd_snapshot_db_name "$1")"
if romd_db_exists "${snapshot_db}"; then
  echo "error: snapshot '$1' already exists" >&2
  exit 1
fi
if ! romd_db_exists "${db}"; then
  echo "error: database ${db} does not exist; run 'mise run db:up' first" >&2
  exit 1
fi

romd_terminate_connections "${db}"
romd_psql -c "CREATE DATABASE \"${snapshot_db}\" TEMPLATE \"${db}\""
echo "created snapshot '$1' (${snapshot_db})"
