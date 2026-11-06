#!/usr/bin/env bash
set -euo pipefail

brand_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo_dir="$(cd "$brand_dir/.." && pwd)"
kit_dir="$brand_dir/romd"
output="$repo_dir/docs/assets/social-preview.png"

# Compose existing canonical artwork; do not redraw or alter the source kit.
magick -size 1280x640 'canvas:#272A25' \
  \( "$kit_dir/assets/logos/wordmark-reversed/logo.png" -resize 610x160 \) \
  -gravity NorthWest -geometry +92+126 -composite \
  -font "$kit_dir/fonts/archivo/Archivo-Variable.ttf" -fill '#F4EFE4' \
  -pointsize 48 -annotate +92+365 'A self-hosted home for' \
  -annotate +92+424 'your personal game library.' \
  -font "$kit_dir/fonts/ibm-plex-mono/IBMPlexMono-Regular.ttf" \
  -pointsize 22 -annotate +92+552 'Moonlark Studios / Public pre-release' \
  -strip "$output"

echo "ROMD social preview exported: $output"
