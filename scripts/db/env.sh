#!/usr/bin/env bash
# Derive this checkout's dev database connection strings for the ROMD hosts.
#
# Sourced by mise (`[env] _.source` in .mise.toml), so a mise-activated shell,
# `mise run`, and `mise exec` all carry them after `mise run db:up`. Executed
# directly (`mise run db:env`) it prints the same values as `export` lines for
# shells without mise activation: eval "$(mise run db:env)".
#
# The dev server (compose.db.yaml) uses loopback trust auth and has no romd_*
# roles, so every host and the worker's schema provisioners connect as `romd`.
# Production credentials are owned by compose.yaml and deploy/postgres/init.

# Runs in a subshell so lib.sh's strict mode never leaks into the sourcing shell.
romd_dev_db_connection_string() (
  set -euo pipefail
  source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

  # Compose resolves ROMD_DB_PORT from the environment, then the repo-root .env.
  # Mirror that precedence without invoking Docker: the server may be down.
  port="${ROMD_DB_PORT:-}"
  if [[ -z "${port}" && -f "${ROMD_REPO_ROOT}/.env" ]]; then
    port="$(sed -nE 's/^ROMD_DB_PORT=["'"'"']?([0-9]+).*$/\1/p' "${ROMD_REPO_ROOT}/.env" | tail -n 1)"
  fi

  printf 'Host=127.0.0.1;Port=%s;Database=%s;Username=romd' "${port:-15432}" "$(romd_db_name)"
)

export ConnectionStrings__Romd="$(romd_dev_db_connection_string)"
export ConnectionStrings__RomdProvisioning="${ConnectionStrings__Romd}"
export ConnectionStrings__Hangfire="${ConnectionStrings__Romd}"
export ConnectionStrings__HangfireProvisioning="${ConnectionStrings__Romd}"

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  printf 'export ConnectionStrings__Romd=%q\n' "${ConnectionStrings__Romd}"
  printf 'export ConnectionStrings__RomdProvisioning=%q\n' "${ConnectionStrings__RomdProvisioning}"
  printf 'export ConnectionStrings__Hangfire=%q\n' "${ConnectionStrings__Hangfire}"
  printf 'export ConnectionStrings__HangfireProvisioning=%q\n' "${ConnectionStrings__HangfireProvisioning}"
fi
