#!/usr/bin/env bash
# Open an interactive psql session against this checkout's database.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

romd_require_server
romd_create_db_if_missing "$(romd_db_name)"
exec "${ROMD_DB_COMPOSE[@]}" exec postgres psql -U romd -d "$(romd_db_name)" "$@"
