#!/usr/bin/env bash

set -euo pipefail

CONSOLE_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
EXTENSIONS="$CONSOLE_ROOT/lib/src/presentation/theme/console_theme_extensions.dart"
REGISTRY="$CONSOLE_ROOT/tool/theme_token_registry.tsv"

if [[ ! -f "$REGISTRY" ]]; then
  echo 'FAIL: theme token registry is missing.'
  exit 1
fi

TOKENS="$({
  awk '
    /^final class (ConsoleColors|Console[A-Za-z]+Theme|Console[A-Za-z]+Metrics|Console[A-Za-z]+Spec|Console[A-Za-z]+Palette)/ {
      class_name = $3
    }
    /^  final / {
      field_name = $NF
      sub(/;.*/, "", field_name)
      print class_name "." field_name
    }
  ' "$EXTENSIONS"
})"

failures=0
while IFS= read -r token; do
  matches=0
  while IFS=$'\t' read -r pattern category rationale; do
    [[ -z "$pattern" || "$pattern" == \#* ]] && continue
    if [[ ! "$category" =~ ^(semantic|component|unique)$ ]]; then
      echo "FAIL: invalid registry category '$category' for pattern $pattern"
      failures=$((failures + 1))
      continue
    fi
    if [[ -z "$rationale" ]]; then
      echo "FAIL: missing rationale for registry pattern $pattern"
      failures=$((failures + 1))
      continue
    fi
    [[ "$token" =~ $pattern ]] && matches=$((matches + 1))
  done < "$REGISTRY"

  if (( matches == 0 )); then
    echo "FAIL: unclassified theme token: $token"
    echo 'Add a semantic/component/unique registry rule with a rationale.'
    failures=$((failures + 1))
  elif (( matches > 1 )); then
    echo "FAIL: theme token matches multiple registry rules: $token"
    failures=$((failures + 1))
  fi
done <<< "$TOKENS"

if (( failures > 0 )); then
  exit 1
fi

EXTENSION_COUNT="$(
  printf '%s\n' "$TOKENS" | awk 'NF && $0 !~ /(Spec|Palette)\./ { count++ } END { print count + 0 }'
)"
SPEC_COUNT="$(
  printf '%s\n' "$TOKENS" | awk 'NF && $0 ~ /(Spec|Palette)\./ { count++ } END { print count + 0 }'
)"
printf 'Theme token registry: %s extension fields + %s nested spec fields classified exactly once.\n' \
  "$EXTENSION_COUNT" "$SPEC_COUNT"
