#!/usr/bin/env bash

set -euo pipefail

CONSOLE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
STYLE_SURFACES="$CONSOLE_ROOT/lib/src"

COMMON_GLOBS=(
  --glob '*.dart'
  --glob '!romd_skins.dart'
)

failures=0

check() {
  local label="$1"
  local pattern="$2"
  shift 2
  local matches
  local status
  set +e
  matches="$(rg -n "$pattern" "$STYLE_SURFACES" "${COMMON_GLOBS[@]}" "$@")"
  status=$?
  set -e
  if (( status > 1 )); then
    echo "FAIL: invalid $label governance expression."
    exit "$status"
  fi
  if [[ -n "$matches" ]]; then
    echo "FAIL: $label must be supplied by the active theme:"
    printf '%s\n' "$matches" | sed "s#^$CONSOLE_ROOT/##"
    failures=$((failures + 1))
  fi
}

# Persisted profile accent ARGB values are user identity content, not skin
# colors. That deliberately separate store is the only presentation exemption.
check \
  'raw visual colors' \
  "Color\\(0x|Color\\.from\\(|Colors\\.(black|white|red|green|blue|grey|amber|orange|purple|pink|cyan|teal)" \
  --glob '!profile_identity_palette.dart'

# ProfileAvatar topology is persisted identity artwork, like the profile accent
# itself. Ambient alpha-zero endpoints are renderer mechanics; every non-zero
# ambient intensity is supplied by ConsoleAmbientRenderSpec.
check \
  'inline derived color intensity' \
  "withValues\\(alpha:[[:space:]]*[0-9]|Color\\.lerp\\([^\n]*,[[:space:]]*0\\.[0-9]" \
  --glob '!profile_avatar.dart' \
  --glob '!console_ambient_background.dart'

check \
  'raw typography values' \
  "FontWeight\\.|fontSize:[[:space:]]*[0-9]|fontFamily:[[:space:]]*['\"]|letterSpacing:[[:space:]]*-?[0-9]|height:[[:space:]]*[0-9]+\\.[0-9]+"

check \
  'raw shape or elevation values' \
  'BorderRadius\.circular\([0-9]|Radius\.circular\([0-9]|elevation:[[:space:]]*[0-9]'

check \
  'raw interaction geometry' \
  'scale:[[:space:]]*[01]\.[0-9]+|Tween<double>\(begin:[[:space:]]*0\.[0-9]+|^[[:space:]]*[?:][[:space:]]*0\.[0-9]+'

if (( failures > 0 )); then
  echo 'Use ColorScheme, TextTheme, or a classified ThemeExtension role.'
  exit 1
fi

echo 'Widget style literals: 0 (identity-content exemption documented).'
