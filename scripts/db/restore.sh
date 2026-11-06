#!/usr/bin/env bash
# Replace this checkout's database with a snapshot taken by snapshot.sh.
# Usage: restore.sh <name>
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

romd_require_server
romd_validate_snapshot_name "${1:-}"
db="$(romd_db_name)"
snapshot_db="$(romd_snapshot_db_name "$1")"

if ! romd_db_exists "${snapshot_db}"; then
  echo "error: snapshot '$1' not found; list snapshots with 'mise run db:snapshot'" >&2
  exit 1
fi

if romd_db_exists "${db}"; then
  romd_terminate_connections "${db}"
  romd_psql -c "DROP DATABASE \"${db}\" WITH (FORCE)"
fi
romd_terminate_connections "${snapshot_db}"
romd_psql -c "CREATE DATABASE \"${db}\" TEMPLATE \"${snapshot_db}\""
echo "restored snapshot '$1' into ${db}"
