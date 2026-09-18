#!/usr/bin/env bash
# Stop the shared local PostgreSQL server. Data persists in the romd-db-data
# volume. Note: the server is shared by every checkout on this machine.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

"${ROMD_DB_COMPOSE[@]}" down
