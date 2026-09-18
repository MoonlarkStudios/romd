#!/usr/bin/env bash
# Start the shared local PostgreSQL server and ensure this checkout's database exists.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

"${ROMD_DB_COMPOSE[@]}" up -d --wait postgres
romd_create_db_if_missing "$(romd_db_name)"
# Ask Compose for the effective binding: ROMD_DB_PORT may come from .env,
# which Compose reads but this shell does not.
echo "dev database ready: $(romd_db_name) on $("${ROMD_DB_COMPOSE[@]}" port postgres 5432)"
