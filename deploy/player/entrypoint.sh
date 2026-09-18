#!/bin/sh
set -eu

DEFAULTS_FILE=/etc/romd/player-defaults.env
CONFIG_ROOT=/usr/share/nginx/html
NGINX_TEMPLATE=/etc/romd/default.conf.template
NGINX_CONFIG=/etc/nginx/conf.d/default.conf

fail() {
  printf 'romd-player: %s\n' "$1" >&2
  exit 1
}

contains_control_or_header_chars() {
  case "$1" in
    *';'*|*'"'*|*"'"*|*\\*) return 0 ;;
  esac
  printf '%s' "$1" | LC_ALL=C grep -q '[[:cntrl:]]'
}

is_http_origin() {
  printf '%s' "$1" | grep -Eq '^https?://([a-z0-9.-]+|\[[0-9a-f:]+\])(:[0-9]{1,5})?$'
}

is_valid_data_path() {
  case "$1" in
    *[[:space:]?#%]*|//*) return 1 ;;
    /*/) path_part=$1 ;;
    http://*/*/|https://*/*/)
      without_scheme=${1#*://}
      path_part=/${without_scheme#*/}
      ;;
    *) return 1 ;;
  esac
  printf '%s' "$1" | LC_ALL=C grep -q '[^ -~]' && return 1
  lower_path=$(printf '%s' "$path_part" | tr '[:upper:]' '[:lower:]')
  ! printf '%s' "$lower_path" | grep -Eq '(^|/)(\.{1,2}|latest|stable)(/|$)'
}

is_valid_csv() {
  case "$1" in
    ,*|*,|*,,*) return 1 ;;
    *) return 0 ;;
  esac
}

json_array=''
append_json_string() {
  if [ -n "$json_array" ]; then
    json_array="$json_array,"
  fi
  json_array="$json_array\"$1\""
}

[ -r "$DEFAULTS_FILE" ] || fail "missing build defaults: $DEFAULTS_FILE"
# The build writes only validated single-quoted literals to this root-owned file.
# shellcheck disable=SC1090
. "$DEFAULTS_FILE"

case "${ROMD_PLAYER_PIN_VERSION:-}" in
  ''|*latest*|*stable*) fail 'invalid pinned EmulatorJS version' ;;
esac
[ -n "${ROMD_PLAYER_PIN_CORES:-}" ] || fail 'the pinned core list is empty'

source=${ROMD_PLAYER_EMULATORJS_SOURCE:-cdn}
case "$source" in
  cdn) ;;
  *) fail "ROMD_PLAYER_EMULATORJS_SOURCE must be 'cdn'" ;;
esac

data_path=${ROMD_PLAYER_EMULATORJS_DATA_PATH:-}
if [ -z "$data_path" ]; then
  data_path="https://cdn.emulatorjs.org/${ROMD_PLAYER_PIN_VERSION}/data/"
fi
contains_control_or_header_chars "$data_path" && fail 'invalid characters in ROMD_PLAYER_EMULATORJS_DATA_PATH'
[ "${#data_path}" -le 2048 ] || fail 'ROMD_PLAYER_EMULATORJS_DATA_PATH is too long'
is_valid_data_path "$data_path" || fail 'ROMD_PLAYER_EMULATORJS_DATA_PATH must be a root-relative or absolute HTTP(S) directory ending in /'

case "$data_path" in
  http://cdn.emulatorjs.org/*|https://cdn.emulatorjs.org/*|http://cdn.emulatorjs.org:*/*|https://cdn.emulatorjs.org:*/*)
    [ "$data_path" = "https://cdn.emulatorjs.org/${ROMD_PLAYER_PIN_VERSION}/data/" ] \
      || fail 'the official EmulatorJS CDN data path must use the pinned version'
    ;;
esac

asset_origin=''
case "$data_path" in
  http://*|https://*)
    asset_origin=$(printf '%s' "$data_path" | sed -E 's#^(https?://([^/]+)).*$#\1#')
    is_http_origin "$asset_origin" || fail 'ROMD_PLAYER_EMULATORJS_DATA_PATH contains an invalid origin'
    ;;
esac

requested_cores=${ROMD_PLAYER_CORES:-$ROMD_PLAYER_PIN_CORES}
[ -n "$requested_cores" ] || fail 'ROMD_PLAYER_CORES must not be empty'
contains_control_or_header_chars "$requested_cores" && fail 'invalid characters in ROMD_PLAYER_CORES'
[ "${#requested_cores}" -le 1024 ] || fail 'ROMD_PLAYER_CORES is too long'
is_valid_csv "$requested_cores" || fail 'ROMD_PLAYER_CORES must be a comma-separated list without empty entries'

json_array=''
seen_cores=','
core_count=0
old_ifs=$IFS
IFS=','
for core in $requested_cores; do
  printf '%s' "$core" | grep -Eq '^[a-z0-9][a-z0-9_-]*$' || fail "invalid EmulatorJS core: $core"
  case ",$ROMD_PLAYER_PIN_CORES," in
    *",$core,"*) ;;
    *) fail "EmulatorJS core is not in the image pin: $core" ;;
  esac
  case "$seen_cores" in
    *",$core,"*) fail "duplicate EmulatorJS core: $core" ;;
  esac
  seen_cores="$seen_cores$core,"
  core_count=$((core_count + 1))
  [ "$core_count" -le 64 ] || fail 'ROMD_PLAYER_CORES contains too many entries'
  [ "${#core}" -le 64 ] || fail "EmulatorJS core is too long: $core"
  append_json_string "$core"
done
IFS=$old_ifs
cores_json="[$json_array]"

allowed_parents=${ROMD_PLAYER_ALLOWED_PARENTS:-}
[ -n "$allowed_parents" ] || fail 'ROMD_PLAYER_ALLOWED_PARENTS must contain at least one origin'
contains_control_or_header_chars "$allowed_parents" && fail 'invalid characters in ROMD_PLAYER_ALLOWED_PARENTS'
[ "${#allowed_parents}" -le 8192 ] || fail 'ROMD_PLAYER_ALLOWED_PARENTS is too long'
is_valid_csv "$allowed_parents" || fail 'ROMD_PLAYER_ALLOWED_PARENTS must be a comma-separated list without empty entries'

json_array=''
frame_ancestors=''
seen_parents=','
IFS=','
for parent in $allowed_parents; do
  [ "${#parent}" -le 2048 ] || fail 'an allowed parent origin is too long'
  is_http_origin "$parent" || fail "invalid allowed parent origin: $parent"
  case "$seen_parents" in
    *",$parent,"*) fail "duplicate allowed parent origin: $parent" ;;
  esac
  seen_parents="$seen_parents$parent,"
  append_json_string "$parent"
  frame_ancestors="$frame_ancestors $parent"
done
IFS=$old_ifs
parents_json="[$json_array]"

asset_sources=''
if [ -n "$asset_origin" ]; then
  asset_sources=" $asset_origin"
fi
ROMD_PLAYER_CSP="default-src 'none'; script-src 'self'${asset_sources} blob: 'wasm-unsafe-eval' 'unsafe-eval'; style-src 'self'${asset_sources} 'unsafe-inline'; img-src 'self'${asset_sources} blob: data:; connect-src 'self'${asset_sources} blob: data:; media-src 'self' blob: data:; worker-src 'self' blob:; frame-ancestors${frame_ancestors}; base-uri 'none'; form-action 'none'"
export ROMD_PLAYER_CSP

config_tmp="$CONFIG_ROOT/player-config.json.tmp"
printf '%s\n' "{\"schemaVersion\":1,\"protocol\":1,\"emulatorJs\":{\"version\":\"$ROMD_PLAYER_PIN_VERSION\",\"source\":\"$source\",\"dataPath\":\"$data_path\"},\"cores\":$cores_json,\"allowedParents\":$parents_json}" > "$config_tmp"
mv "$config_tmp" "$CONFIG_ROOT/player-config.json"

[ -r "$NGINX_TEMPLATE" ] || fail "missing nginx template: $NGINX_TEMPLATE"
nginx_tmp="$NGINX_CONFIG.tmp"
envsubst '${ROMD_PLAYER_CSP}' < "$NGINX_TEMPLATE" > "$nginx_tmp"
mv "$nginx_tmp" "$NGINX_CONFIG"
