#!/usr/bin/env bash

set -euo pipefail

CONSOLE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

current_importers() {
  find "$CONSOLE_ROOT/lib" "$CONSOLE_ROOT/test" -name '*.dart' -type f \
    -exec grep -lE "^import .*console_(palette|design_tokens)\.dart';" {} + \
    2>/dev/null \
    | sed "s#^$CONSOLE_ROOT/##" \
    | sort -u \
    || true
}

current_references() {
  find "$CONSOLE_ROOT/lib" "$CONSOLE_ROOT/test" -name '*.dart' -type f \
    -exec grep -lE 'Console(Palette|Type|Spacing|Radii|Motion|State)\.' {} + \
    2>/dev/null \
    | sed "s#^$CONSOLE_ROOT/##" \
    | sort -u \
    || true
}

count_lines() {
  awk 'NF { count++ } END { print count + 0 }'
}

CURRENT="$(current_importers)"
CURRENT_COUNT="$(printf '%s\n' "$CURRENT" | count_lines)"
REFERENCES="$(current_references)"
REFERENCE_COUNT="$(printf '%s\n' "$REFERENCES" | count_lines)"

if (( CURRENT_COUNT > 0 )); then
  echo 'FAIL: console code imports legacy primitive tokens:'
  printf '%s\n' "$CURRENT"
  echo 'Read the active values from Theme.of(context) instead.'
  exit 1
fi

if (( REFERENCE_COUNT > 0 )); then
  echo 'FAIL: console code references legacy primitive tokens:'
  printf '%s\n' "$REFERENCES"
  echo 'Read the active values from Theme.of(context) instead.'
  exit 1
fi

echo 'Legacy primitive-token imports/references: 0 (hard ban active).'
