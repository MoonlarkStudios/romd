#!/usr/bin/env bash
# Restore one ROMD backup set into an EMPTY installation.
#
#   scripts/backup/restore.sh /mnt/backups/romd/<backup-set-id>
#
# Refuses (exit 2) a set whose manifest is missing, whose artifacts are missing
# or fail their recorded digest, or whose roles artifact differs from this
# checkout's deploy/postgres/init/10-romd-roles.sh: those are incomplete or
# mixed-generation sets. Refuses (exit 3) a destination that already holds
# tables or data files. Roles, schemas, and default privileges must already
# exist on the destination (the PostgreSQL init script creates them on first
# boot); the dump is restored into those schemas owned by romd_owner, and the
# worker re-applies grants and republishes the schema versions on its next
# start. Hosts are left stopped: start the worker first, then the API hosts.
set -euo pipefail
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ $# -eq 1 ]] || romd_backup_fail "usage: restore.sh <backup-set-dir>"
romd_backup_require_mode
set_dir="$1"
manifest="${set_dir}/manifest.json"
[[ -f "${manifest}" ]] || romd_backup_fail "no manifest.json in ${set_dir}: not a complete backup set" 2
[[ "$(romd_backup_manifest_field "${manifest}" format)" == "${ROMD_BACKUP_MANIFEST_FORMAT}" ]] \
  || romd_backup_fail "unsupported manifest format" 2
backup_set_id="$(romd_backup_manifest_field "${manifest}" backupSetId)"
romd_schema_version="$(romd_backup_manifest_field "${manifest}" romdSchemaVersion)"
[[ -n "${backup_set_id}" && "${romd_schema_version}" =~ ^[0-9]+$ ]] \
  || romd_backup_fail "manifest is missing its id or schema version" 2

for artifact in roles.sh romd.dump data.tar; do
  [[ -f "${set_dir}/${artifact}" ]] || romd_backup_fail "backup set ${backup_set_id} is missing ${artifact}" 2
  expected="$(romd_backup_manifest_sha256 "${manifest}" "${artifact}")"
  actual="$(romd_backup_sha256 "${set_dir}/${artifact}")"
  [[ -n "${expected}" && "${expected}" == "${actual}" ]] \
    || romd_backup_fail \
      "${artifact} does not match the manifest of backup set ${backup_set_id}: incomplete or mixed-generation set" 2
done
[[ -f "${ROMD_BACKUP_ROLES_SCRIPT}" ]] || romd_backup_fail "roles artifact not found: ${ROMD_BACKUP_ROLES_SCRIPT}"
[[ "$(romd_backup_sha256 "${set_dir}/roles.sh")" == "$(romd_backup_sha256 "${ROMD_BACKUP_ROLES_SCRIPT}")" ]] \
  || romd_backup_fail \
    "roles artifact in backup set ${backup_set_id} differs from ${ROMD_BACKUP_ROLES_SCRIPT}: mixed-generation set" 2

romd_backup_stop_hosts

role_count="$(romd_backup_scalar "SELECT count(*) FROM pg_roles \
  WHERE rolname IN ('romd_owner','romd_provisioner','romd_worker','romd_admin','romd_consumer')")"
[[ "${role_count}" == "5" ]] \
  || romd_backup_fail \
    "destination is missing the romd_* roles; initialise PostgreSQL with deploy/postgres/init first" 3
schema_count="$(romd_backup_scalar "SELECT count(*) FROM pg_namespace WHERE nspname IN ('romd','hangfire')")"
[[ "${schema_count}" == "2" ]] || romd_backup_fail "destination is missing the romd or hangfire schema" 3
table_count="$(romd_backup_scalar \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema IN ('romd','hangfire')")"
[[ "${table_count}" == "0" ]] \
  || romd_backup_fail \
    "destination database already holds ${table_count} tables; restore only into an empty installation" 3
romd_backup_data_is_empty \
  || romd_backup_fail "destination data directory is not empty; restore only into an empty installation" 3

# Schemas already exist and carry the default privileges the roles script
# registered, so the dump's SCHEMA entries are filtered out of the TOC instead
# of dropped and recreated. Objects are created as romd_owner; explicit ACLs
# are skipped because the worker's provisioner re-applies them.
romd_backup_pg sh -c '
  set -e
  dump="$(mktemp)"; toc="$(mktemp)"
  cat >"$dump"
  pg_restore -l "$dump" | grep -vE "^[0-9]+; [0-9]+ [0-9]+ SCHEMA " >"$toc"
  pg_restore -U "$1" -d "$2" --role=romd_owner --no-owner --no-privileges \
    --exit-on-error --single-transaction -L "$toc" "$dump"
  rm -f "$dump" "$toc"
' restore-dump "${ROMD_BACKUP_PG_USER}" "${ROMD_BACKUP_PG_DATABASE}" <"${set_dir}/romd.dump"

romd_backup_data_import <"${set_dir}/data.tar"

restored_version="$(romd_backup_scalar "SELECT version FROM romd.romd_schema WHERE singleton")"
[[ "${restored_version}" == "${romd_schema_version}" ]] \
  || romd_backup_fail \
    "restored romd.romd_schema version ${restored_version} does not match the manifest (${romd_schema_version})"

echo "backup set ${backup_set_id} restored (romd schema version ${restored_version})."
echo "Start Romd.Worker.Host first so it re-applies grants and republishes the schema versions," \
  "then start the API hosts."
