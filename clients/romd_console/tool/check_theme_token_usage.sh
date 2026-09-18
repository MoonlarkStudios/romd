#!/usr/bin/env bash

set -euo pipefail

CONSOLE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXTENSIONS="$CONSOLE_ROOT/lib/src/presentation/theme/console_theme_extensions.dart"

# A registry entry answers why a token exists; this check also requires every
# declared field to have a real consumer outside its declaration/copy/lerp
# implementation. Constructor mappings in romd_skins.dart do not count because
# they use `field:` rather than `.field`.
FIELDS="$({
  awk '
    /^  final / {
      field_name = $NF
      sub(/;.*/, "", field_name)
      print field_name
    }
  ' "$EXTENSIONS" | sort -u
})"

failures=0
while IFS= read -r field; do
  [[ -z "$field" ]] && continue
  if ! rg -q "\\.${field}\\b" "$CONSOLE_ROOT/lib" \
    --glob '*.dart' \
    --glob '!console_theme_extensions.dart'; then
    echo "FAIL: declared theme field has no consumer: $field"
    failures=$((failures + 1))
  fi
done <<< "$FIELDS"

if (( failures > 0 )); then
  echo 'Delete unused fields or add a real non-factory consumer.'
  exit 1
fi

echo 'Theme token usage: every declared field has a non-definition consumer.'
