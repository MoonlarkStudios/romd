#!/usr/bin/env bash
set -euo pipefail

brand_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo_dir="$(cd "$brand_dir/.." && pwd)"
kit_dir="$brand_dir/romd"
foundation_dir="$repo_dir/web/packages/romd-foundation/src"

# Application placement only: approved artwork and values originate in the kit.
mkdir -p "$foundation_dir/brand" "$foundation_dir/fonts"
cp "$kit_dir/assets/logos/symbol-primary/logo.svg" "$foundation_dir/brand/romd-mark.svg"
cp "$kit_dir/assets/logos/symbol-reversed/logo.svg" "$foundation_dir/brand/romd-mark-dark.svg"
cp "$kit_dir/assets/logos/wordmark-primary/logo.svg" "$foundation_dir/brand/romd-wordmark.svg"
cp "$kit_dir/assets/logos/wordmark-reversed/logo.svg" "$foundation_dir/brand/romd-wordmark-cream.svg"
cp "$kit_dir/assets/logos/horizontal-primary/logo.svg" "$foundation_dir/brand/romd-lockup-horizontal.svg"
cp "$kit_dir/assets/logos/horizontal-reversed/logo.svg" "$foundation_dir/brand/romd-lockup-horizontal-dark.svg"
cp "$kit_dir/assets/icons/standard/icon.svg" "$foundation_dir/brand/romd-icon.svg"
cp "$kit_dir/tokens.css" "$foundation_dir/brand/tokens.css"
cp "$kit_dir/fonts/archivo/"* "$foundation_dir/fonts/"
cp "$kit_dir/fonts/ibm-plex-mono/"* "$foundation_dir/fonts/"
for app in admin consumer player; do
  public_dir="$repo_dir/web/packages/romd-$app-app/public"
  mkdir -p "$public_dir"
  cp "$kit_dir/assets/icons/standard/icon.svg" "$public_dir/favicon.svg"
done
echo "ROMD web assets placed from brand/romd; source: brand/romd/SOURCE.md."
