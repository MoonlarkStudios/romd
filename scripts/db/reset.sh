#!/usr/bin/env bash
# Return this checkout to first-run state without recreating the container:
# drop and recreate the per-checkout database, then clear the derived data
# directory. Equivalent to deleting the SQLite data directory pre-cutover.
# Snapshot databases (db:snapshot) are kept.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

romd_require_server

# Validate every destructive target before touching either resource. Deletion
# is constrained to a data directory inside this checkout's .data; anything
# else must be removed manually.
data_dir="${Romd__DataDirectory:-${ROMD_REPO_ROOT}/.data/romd}"
resolved_data_dir=""
if [[ -d "${data_dir}" ]]; then
  resolved_data_dir="$(cd "${data_dir}" && pwd -P)"
  case "${resolved_data_dir}" in
    "${ROMD_REPO_ROOT}/.data/"*) ;;
    *)
      echo "error: refusing to remove '${resolved_data_dir}': db:reset only clears a data directory under ${ROMD_REPO_ROOT}/.data — remove other locations manually" >&2
      exit 1
      ;;
  esac
fi

db="$(romd_db_name)"
if romd_db_exists "${db}"; then
  romd_terminate_connections "${db}"
  romd_psql -c "DROP DATABASE \"${db}\" WITH (FORCE)"
fi
romd_psql -c "CREATE DATABASE \"${db}\""
echo "recreated database ${db}"

if [[ -n "${resolved_data_dir}" ]]; then
  rm -rf "${resolved_data_dir}"
  echo "removed data directory ${resolved_data_dir}"
fi
