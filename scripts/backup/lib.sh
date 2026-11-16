#!/usr/bin/env bash
# Shared helpers for ROMD backup sets (scripts/backup/backup.sh and restore.sh).
# Source this file; do not execute it.
#
# A backup set is one quiesced capture of a ROMD installation: the declarative
# role/schema/grant artifact, a PostgreSQL custom-format dump of the romd and
# hangfire schemas, a tar of the data directory (CAS content, keys, identity,
# data-protection keys), the generated certificate authority when the
# installation has one, and a manifest that binds them under one id with
# digests and schema versions. See docs/self-hosting/production-deployment.md.
#
# Modes (ROMD_BACKUP_MODE):
#   compose  (default) the Compose stack in this checkout: PostgreSQL commands
#            run through `docker compose exec -T postgres`, the data directory
#            is the selected project's romd-data volume,
#            and backup.sh stops/starts the ROMD hosts around the capture.
#   local    a directly reachable server and a host directory, for the restore
#            drill and bare-metal installs: ROMD_BACKUP_PG_EXEC is a command
#            prefix that runs a program against the server with stdin/stdout
#            piped (for example `docker exec -i <container>`), and
#            ROMD_BACKUP_DATA_DIR is the data directory. The caller quiesces.
set -euo pipefail

ROMD_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && /bin/pwd -P)"
ROMD_BACKUP_MODE="${ROMD_BACKUP_MODE:-compose}"
ROMD_BACKUP_COMPOSE="${ROMD_BACKUP_COMPOSE:-}"
ROMD_BACKUP_HELPER_IMAGE="${ROMD_BACKUP_HELPER_IMAGE:-}"
ROMD_BACKUP_PG_USER="${ROMD_BACKUP_PG_USER:-romd_bootstrap}"
ROMD_BACKUP_PG_DATABASE="${ROMD_BACKUP_PG_DATABASE:-romd}"
ROMD_BACKUP_ROLES_SCRIPT="${ROMD_BACKUP_ROLES_SCRIPT:-${ROMD_REPO_ROOT}/deploy/postgres/init/10-romd-roles.sh}"
ROMD_BACKUP_CONTAINER_DATA_DIR="/var/lib/romd"
# Where romd-init keeps the generated certificate authority; only it mounts that volume.
ROMD_BACKUP_CONTAINER_CA_DIR="/init/ca"
ROMD_BACKUP_HOSTS=(romd-worker romd-admin romd-consumer)
ROMD_BACKUP_MANIFEST_FORMAT=1

romd_backup_fail() {
  echo "error: $1" >&2
  exit "${2:-1}"
}

romd_backup_compose() {
  if [[ -n "$ROMD_BACKUP_COMPOSE" ]]; then
    # Optional command-prefix override; no eval or shell expansion of its contents.
    local -a command
    read -r -a command <<< "$ROMD_BACKUP_COMPOSE"
    "${command[@]}" "$@"
  else
    "${ROMD_REPO_ROOT}/scripts/deployment/compose.sh" "$@"
  fi
}

romd_backup_require_mode() {
  case "${ROMD_BACKUP_MODE}" in
    compose)
      if [[ -z "$ROMD_BACKUP_HELPER_IMAGE" ]]; then
        ROMD_BACKUP_HELPER_IMAGE="$(romd_backup_compose images --quiet postgres)"
        [[ -n "$ROMD_BACKUP_HELPER_IMAGE" ]] || romd_backup_fail "start PostgreSQL before backup/restore"
      fi
      ;;
    local)
      [[ -n "${ROMD_BACKUP_PG_EXEC:-}" || -n "${ROMD_BACKUP_CONTAINER_ID:-}" ]] || romd_backup_fail "ROMD_BACKUP_PG_EXEC or ROMD_BACKUP_CONTAINER_ID is required in local mode"
      [[ -n "${ROMD_BACKUP_DATA_DIR:-}" ]] || romd_backup_fail "ROMD_BACKUP_DATA_DIR is required in local mode"
      ;;
    *) romd_backup_fail "unknown ROMD_BACKUP_MODE '${ROMD_BACKUP_MODE}' (compose|local)" ;;
  esac
}

# Runs a program against the PostgreSQL server with stdin and stdout piped.
romd_backup_pg() {
  if [[ "${ROMD_BACKUP_MODE}" == "compose" ]]; then
    romd_backup_compose exec -T postgres "$@"
  elif [[ -n "${ROMD_BACKUP_CONTAINER_ID:-}" ]]; then
    python3 "${ROMD_REPO_ROOT}/scripts/containers/runtime.py" engine exec -i "${ROMD_BACKUP_CONTAINER_ID}" "$@"
  else
    # shellcheck disable=SC2086
    ${ROMD_BACKUP_PG_EXEC} "$@"
  fi
}

# Scalar query against the ROMD database as the maintenance user.
romd_backup_scalar() {
  romd_backup_pg psql -v ON_ERROR_STOP=1 -qtA \
    -U "${ROMD_BACKUP_PG_USER}" -d "${ROMD_BACKUP_PG_DATABASE}" -c "$1" </dev/null
}

romd_backup_sha256() {
  python3 -I - "$1" <<'PY'
import hashlib, sys
digest = hashlib.sha256()
with open(sys.argv[1], 'rb') as source:
    for block in iter(lambda: source.read(1024 * 1024), b''):
        digest.update(block)
print(digest.hexdigest())
PY
}

romd_backup_bytes() {
  wc -c <"$1" | tr -d ' '
}

# Resolve the selected project's worker, including a stopped container during restore.
romd_backup_worker_container() {
  local id
  id="$(romd_backup_compose ps --all --quiet romd-worker)"
  [[ -n "$id" && "$id" != *$'\n'* ]] || romd_backup_fail "expected one worker container; create it before restore"
  printf '%s\n' "$id"
}

# Emits the data directory as a tar stream on stdout. Job staging, CAS staging and
# logs are reproducible and excluded; everything else (CAS content, keys, identity,
# data-protection keys, exports) is part of the installation.
romd_backup_data_export() {
  if [[ "${ROMD_BACKUP_MODE}" == "compose" ]]; then
    # Data includes private keys owned by the ROMD application user. These
    # one-shot volume helpers need root independently of the image's default
    # database identity; the PostgreSQL server continues running as postgres.
    # stdout is a binary archive, not application logs. Docker's default logger
    # otherwise duplicates it (and JSON escaping can greatly inflate disk use).
    docker run --rm --user 0:0 --log-driver none --volumes-from "$(romd_backup_worker_container)" "${ROMD_BACKUP_HELPER_IMAGE}" \
      tar -C "${ROMD_BACKUP_CONTAINER_DATA_DIR}" -cf - --exclude ./temp --exclude ./logs --exclude ./content/.staging .
  else
    tar -C "${ROMD_BACKUP_DATA_DIR}" -cf - --exclude ./temp --exclude ./logs --exclude ./content/.staging .
  fi
}

# The romd-init container, which alone mounts the certificate authority volume. Empty when
# the installation has none (for example a stack that predates it, or local mode).
romd_backup_init_container() {
  romd_backup_compose ps --all --quiet romd-init 2>/dev/null | head -n 1
}

# Runs where the authority directory is visible: prints its tar stream, prints nothing when the
# installation has no authority, and fails for anything in between. Only absence is optional:
# an authority with one file missing, or one that cannot be read, must fail the backup.
ROMD_BACKUP_CA_EXPORT='
  set -eu
  [ -d "$1" ] || exit 0
  cd "$1"
  [ -e ca.key ] || [ -e ca.crt ] || exit 0
  [ -f ca.key ] && [ -f ca.crt ] || { echo "certificate authority is incomplete: expected both ca.key and ca.crt" >&2; exit 3; }
  tar -cf - ca.key ca.crt
'

# Emits the generated certificate authority as a tar stream on stdout, or nothing when the
# installation has none (an operator-supplied certificate, or ROMD_BACKUP_CA_DIR unset in
# local mode). Devices trust this authority, so a restore must bring it back.
romd_backup_ca_export() {
  if [[ "${ROMD_BACKUP_MODE}" == "compose" ]]; then
    local container
    container="$(romd_backup_init_container)"
    [[ -n "$container" ]] || return 0
    docker run --rm --user 0:0 --log-driver none --volumes-from "$container" "${ROMD_BACKUP_HELPER_IMAGE}" \
      sh -c "${ROMD_BACKUP_CA_EXPORT}" ca-export "${ROMD_BACKUP_CONTAINER_CA_DIR}"
  elif [[ -n "${ROMD_BACKUP_CA_DIR:-}" ]]; then
    sh -c "${ROMD_BACKUP_CA_EXPORT}" ca-export "${ROMD_BACKUP_CA_DIR}"
  fi
}

# Succeeds when a captured authority archive holds exactly the two files a restore needs.
romd_backup_ca_archive_is_complete() {
  [[ "$(tar -tf "$1" 2>/dev/null | LC_ALL=C sort | tr '\n' ' ')" == "ca.crt ca.key " ]]
}

# Replaces the destination's certificate authority with a tar stream from stdin. romd-init
# then reissues the server certificate under it on the next start.
romd_backup_ca_import() {
  if [[ "${ROMD_BACKUP_MODE}" == "compose" ]]; then
    local container
    container="$(romd_backup_init_container)"
    [[ -n "$container" ]] || romd_backup_fail "the backup set holds a certificate authority but this stack has no romd-init service"
    docker run --rm --user 0:0 -i --volumes-from "$container" "${ROMD_BACKUP_HELPER_IMAGE}" \
      sh -c 'mkdir -p "$1" && rm -f "$1/ca.key" "$1/ca.crt" && tar -C "$1" -xf -' ca-import "${ROMD_BACKUP_CONTAINER_CA_DIR}"
  else
    [[ -n "${ROMD_BACKUP_CA_DIR:-}" ]] || romd_backup_fail "the backup set holds a certificate authority; set ROMD_BACKUP_CA_DIR to restore it"
    mkdir -p "${ROMD_BACKUP_CA_DIR}"
    rm -f "${ROMD_BACKUP_CA_DIR}/ca.key" "${ROMD_BACKUP_CA_DIR}/ca.crt"
    tar -C "${ROMD_BACKUP_CA_DIR}" -xf -
  fi
}

# Restores a tar stream from stdin into the data directory.
romd_backup_data_import() {
  if [[ "${ROMD_BACKUP_MODE}" == "compose" ]]; then
    docker run --rm --user 0:0 -i --volumes-from "$(romd_backup_worker_container)" "${ROMD_BACKUP_HELPER_IMAGE}" \
      tar -C "${ROMD_BACKUP_CONTAINER_DATA_DIR}" -xf -
  else
    mkdir -p "${ROMD_BACKUP_DATA_DIR}"
    tar -C "${ROMD_BACKUP_DATA_DIR}" -xf -
  fi
}

# Succeeds when the data directory holds nothing but empty scaffolding and the signing
# secrets romd-init generates for a new installation; the restored set replaces those.
romd_backup_data_is_empty() {
  local listing
  if [[ "${ROMD_BACKUP_MODE}" == "compose" ]]; then
    listing="$(docker run --rm --user 0:0 --volumes-from "$(romd_backup_worker_container)" "${ROMD_BACKUP_HELPER_IMAGE}" \
      find "${ROMD_BACKUP_CONTAINER_DATA_DIR}" -mindepth 1 -type f \
        -not -path '*/logs/*' -not -path '*/temp/*' -not -path '*/secrets/*' 2>/dev/null | head -n 1)"
  else
    [[ -d "${ROMD_BACKUP_DATA_DIR}" ]] || return 0
    listing="$(find "${ROMD_BACKUP_DATA_DIR}" -mindepth 1 -type f \
      -not -path '*/logs/*' -not -path '*/temp/*' -not -path '*/secrets/*' | head -n 1)"
  fi
  [[ -z "${listing}" ]]
}

romd_backup_stop_hosts() {
  if [[ "${ROMD_BACKUP_MODE}" == "compose" ]]; then
    romd_backup_compose stop "${ROMD_BACKUP_HOSTS[@]}"
  fi
}

romd_backup_start_hosts() {
  if [[ "${ROMD_BACKUP_MODE}" == "compose" ]]; then
    romd_backup_compose start "${ROMD_BACKUP_HOSTS[@]}"
  fi
}

# Reads a top-level string or number field from a one-field-per-line manifest.
romd_backup_manifest_field() {
  sed -nE "s/^  \"$2\": \"?([^\",]*)\"?,?$/\1/p" "$1" | head -n 1
}

# Reads the recorded sha256 of an artifact from the manifest.
romd_backup_manifest_sha256() {
  sed -nE "/^    \"$2\": \\{/,/\\}/s/^      \"sha256\": \"([0-9a-f]{64})\",?$/\1/p" "$1" | head -n 1
}
