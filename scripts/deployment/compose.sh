#!/usr/bin/env bash
# Same entry point for a source checkout and an extracted release bundle.
# COMPOSE_FILE / COMPOSE_PROJECT_NAME are standard Compose overrides.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
env_file="${ROMD_ENV_FILE:-$root/.env}"
# setup.sh records the selected overlays and project name in the env file.
# Export them here so every tool sees the same project on any Compose release,
# from any working directory: env-file paths are relative to the bundle root.
# Values already exported by the caller keep Compose's own precedence.
if [[ -f "$env_file" ]]; then
  if [[ -z "${COMPOSE_PROJECT_NAME:-}" ]]; then
    name="$(sed -n 's/^COMPOSE_PROJECT_NAME=//p' "$env_file" | tail -n 1)"
    [[ -z "$name" ]] || export COMPOSE_PROJECT_NAME="$name"
  fi
  if [[ -z "${COMPOSE_FILE:-}" ]]; then
    files="$(sed -n 's/^COMPOSE_FILE=//p' "$env_file" | tail -n 1)"
    if [[ -n "$files" ]]; then
      resolved=()
      IFS=: read -r -a entries <<< "$files"
      for entry in "${entries[@]}"; do
        [[ "$entry" == /* ]] && resolved+=("$entry") || resolved+=("$root/$entry")
      done
      COMPOSE_FILE="$(IFS=:; printf '%s' "${resolved[*]}")"
      export COMPOSE_FILE
    fi
  fi
fi
args=(--project-directory "$root")
[[ ! -f "$root/images.env" ]] || args+=(--env-file "$root/images.env")
args+=(--env-file "$env_file")
exec docker compose "${args[@]}" "$@"
