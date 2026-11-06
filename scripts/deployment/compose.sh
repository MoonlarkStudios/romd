#!/usr/bin/env bash
# Same entry point for a source checkout and an extracted release bundle.
# COMPOSE_FILE / COMPOSE_PROJECT_NAME are standard Compose overrides.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
args=(--project-directory "$root")
[[ ! -f "$root/images.env" ]] || args+=(--env-file "$root/images.env")
args+=(--env-file "${ROMD_ENV_FILE:-$root/.env}")
exec docker compose "${args[@]}" "$@"
