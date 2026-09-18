#!/usr/bin/env bash
# Capture one ROMD backup set into <output-root>/<backup-set-id>/.
#
#   scripts/backup/backup.sh /mnt/backups/romd
#
# Compose mode stops the ROMD hosts (never PostgreSQL) for the capture and
# starts them again afterwards. Local mode expects the caller to have stopped
# every ROMD writer already. The manifest is written last, so a set without a
# manifest is an aborted capture and restore.sh refuses it.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ $# -eq 1 ]] || romd_backup_fail "usage: backup.sh <output-root>"
romd_backup_require_mode
[[ -f "${ROMD_BACKUP_ROLES_SCRIPT}" ]] || romd_backup_fail "roles artifact not found: ${ROMD_BACKUP_ROLES_SCRIPT}"

backup_set_id="$(date -u +%Y%m%dT%H%M%SZ)-$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
set_dir="$1/${backup_set_id}"
mkdir -p "${set_dir}"

romd_backup_stop_hosts
restart_hosts() { romd_backup_start_hosts; }
trap restart_hosts EXIT

cp "${ROMD_BACKUP_ROLES_SCRIPT}" "${set_dir}/roles.sh"
romd_schema_version="$(romd_backup_scalar "SELECT version FROM romd.romd_schema WHERE singleton")"
hangfire_schema_version="$(romd_backup_scalar "SELECT version FROM hangfire.romd_schema WHERE singleton")"
server_version="$(romd_backup_scalar "SHOW server_version")"
[[ "${romd_schema_version}" =~ ^[0-9]+$ ]] \
  || romd_backup_fail "romd.romd_schema has no published version; the worker never provisioned this database"
[[ "${hangfire_schema_version}" =~ ^[0-9]+$ ]] || romd_backup_fail "hangfire.romd_schema has no published version"

romd_backup_pg pg_dump -U "${ROMD_BACKUP_PG_USER}" -d "${ROMD_BACKUP_PG_DATABASE}" \
  -Fc --schema=romd --schema=hangfire \
  </dev/null >"${set_dir}/romd.dump"
romd_backup_data_export >"${set_dir}/data.tar"

{
  printf '{\n'
  printf '  "format": %s,\n' "${ROMD_BACKUP_MANIFEST_FORMAT}"
  printf '  "backupSetId": "%s",\n' "${backup_set_id}"
  printf '  "createdAtUtc": "%s",\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  printf '  "romdSchemaVersion": %s,\n' "${romd_schema_version}"
  printf '  "hangfireSchemaVersion": %s,\n' "${hangfire_schema_version}"
  printf '  "postgresServerVersion": "%s",\n' "${server_version}"
  printf '  "artifacts": {\n'
  for artifact in roles.sh romd.dump data.tar; do
    separator=","
    [[ "${artifact}" == "data.tar" ]] && separator=""
    printf '    "%s": {\n      "sha256": "%s",\n      "bytes": %s\n    }%s\n' \
      "${artifact}" "$(romd_backup_sha256 "${set_dir}/${artifact}")" \
      "$(romd_backup_bytes "${set_dir}/${artifact}")" "${separator}"
  done
  printf '  }\n}\n'
} >"${set_dir}/manifest.json"

echo "backup set ${backup_set_id} written to ${set_dir}"
