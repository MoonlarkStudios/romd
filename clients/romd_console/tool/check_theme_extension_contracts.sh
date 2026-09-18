#!/usr/bin/env bash

set -euo pipefail

CONSOLE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXTENSIONS="$CONSOLE_ROOT/lib/src/presentation/theme/console_theme_extensions.dart"

extension_source() {
  local class_name="$1"
  awk -v target="$class_name" '
    $0 ~ "^final class " target " extends ThemeExtension" {
      active = 1
    }
    active {
      print
      line = $0
      opens = gsub(/{/, "{", line)
      closes = gsub(/}/, "}", line)
      depth += opens - closes
      if (depth == 0) exit
    }
  ' "$EXTENSIONS"
}

failures=0
for class_name in \
  ConsoleColors \
  ConsoleLayoutTheme \
  ConsoleElevationTheme \
  ConsoleMotionTheme \
  ConsoleArtworkTheme; do
  source="$(extension_source "$class_name")"
  compact="$(printf '%s' "$source" | tr -d '[:space:]')"
  snap_lerp=0
  [[ "$compact" == *'returnt<0.5?this:other;'* ]] && snap_lerp=1

  fields="$(printf '%s\n' "$source" | awk '
    /^  final / {
      field_name = $NF
      sub(/;.*/, "", field_name)
      print field_name
    }
  ')"

  while IFS= read -r field; do
    [[ -z "$field" ]] && continue
    if [[ "$compact" != *"${field}??this.${field}"* ]]; then
      echo "FAIL: $class_name.copyWith does not preserve/replace $field"
      failures=$((failures + 1))
    fi
    if (( snap_lerp == 0 )) && [[ "$compact" != *"other.${field}"* ]]; then
      echo "FAIL: $class_name.lerp does not read other.$field"
      failures=$((failures + 1))
    fi
  done <<< "$fields"
done

if (( failures > 0 )); then
  echo 'Every ThemeExtension field must participate in copyWith and lerp.'
  exit 1
fi

echo 'ThemeExtension contracts: copyWith/lerp cover every declared field.'
