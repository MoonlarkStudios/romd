#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
client_dir="$(cd "$script_dir/.." && pwd)"
repo_dir="$(cd "$client_dir/../.." && pwd)"
brand_dir="$repo_dir/brand"

inkscape_bin="${INKSCAPE_BIN:-}"
if [[ -z "$inkscape_bin" ]]; then
  if [[ -x /Applications/Inkscape.app/Contents/MacOS/inkscape ]]; then
    inkscape_bin=/Applications/Inkscape.app/Contents/MacOS/inkscape
  else
    inkscape_bin="$(command -v inkscape || true)"
  fi
fi

magick_bin="${MAGICK_BIN:-$(command -v magick || true)}"
if [[ -z "$inkscape_bin" || -z "$magick_bin" ]]; then
  echo "Ottercade exports require Inkscape and ImageMagick (magick)." >&2
  exit 1
fi

temporary_dir="$(mktemp -d "${TMPDIR:-/tmp}/ottercade-brand.XXXXXX")"
trap 'rm -rf "$temporary_dir"' EXIT

export_svg() {
  local source_path="$1"
  local output_path="$2"
  local width="$3"
  mkdir -p "$(dirname "$output_path")"
  "$inkscape_bin" \
    --export-filename="$output_path" \
    --export-width="$width" \
    --export-area-page \
    "$source_path"
}

assets_dir="$client_dir/assets/brand"
mkdir -p "$assets_dir"
export_svg "$brand_dir/ottercade-romp.svg" \
  "$assets_dir/ottercade_romp.png" 1024
export_svg "$brand_dir/ottercade-wordmark.svg" \
  "$assets_dir/ottercade_wordmark_ink.png" 1600
"$magick_bin" "$assets_dir/ottercade_wordmark_ink.png" \
  -channel RGB -fill '#fff9ee' -colorize 100 -strip \
  -define png:exclude-chunk=date,time \
  "$assets_dir/ottercade_wordmark_cream.png"

# macOS receives a square, unmasked source. The system owns the corner mask.
mac_icon_dir="$client_dir/macos/Runner/Assets.xcassets/AppIcon.appiconset"
for size in 16 32 64 128 256 512 1024; do
  export_svg "$brand_dir/ottercade-icon-square.svg" \
    "$mac_icon_dir/app_icon_${size}.png" "$size"
done

# Android launch and adaptive foreground sources are supplied at xxxhdpi.
android_xxxhdpi="$client_dir/android/app/src/main/res/drawable-xxxhdpi"
export_svg "$brand_dir/ottercade-romp.svg" \
  "$android_xxxhdpi/ottercade_launch_mark.png" 880
export_svg "$brand_dir/ottercade-icon-adaptive-foreground.svg" \
  "$android_xxxhdpi/ottercade_launcher_foreground.png" 432

# Legacy Android launchers still consume density-specific mipmaps.
declare -a android_densities=(mdpi hdpi xhdpi xxhdpi xxxhdpi)
declare -a android_sizes=(48 72 96 144 192)
for index in "${!android_densities[@]}"; do
  density="${android_densities[$index]}"
  size="${android_sizes[$index]}"
  export_svg "$brand_dir/ottercade-icon-plated.svg" \
    "$client_dir/android/app/src/main/res/mipmap-$density/ic_launcher.png" \
    "$size"
done

# Windows ICO files need exact small rasters rather than one scaled bitmap.
windows_icon_dir="$client_dir/windows/runner/resources"
windows_pngs=()
for size in 16 24 32 48 64 128 256; do
  output_path="$temporary_dir/windows_${size}.png"
  export_svg "$brand_dir/ottercade-icon-plated.svg" "$output_path" "$size"
  windows_pngs+=("$output_path")
done
"$magick_bin" "${windows_pngs[@]}" "$windows_icon_dir/app_icon.ico"

# Linux installs this plated source next to the relocatable bundle data.
export_svg "$brand_dir/ottercade-icon-plated.svg" \
  "$client_dir/linux/runner/resources/ottercade.png" 512

echo "Ottercade brand exports updated."
