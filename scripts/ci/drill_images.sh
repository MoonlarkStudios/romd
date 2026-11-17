#!/usr/bin/env bash
# Transfer the four images tested by source smoke to the drill runners.
set -euo pipefail

archive="$RUNNER_TEMP/production-drill-images.tar.gz"
images=(romd-admin:test romd-consumer:test romd-worker:test romd-player:test)

image_id_hash() {
  docker image inspect --format '{{.Id}}' "$@" | sha256sum | cut -d' ' -f1
}

case "${1:-}" in
  save)
    ids=$(image_id_hash "${images[@]}")
    docker image save "${images[@]}" | gzip -1 > "$archive"
    printf 'image-ids=%s\n' "$ids" >> "$GITHUB_OUTPUT"
    ;;
  load)
    test -n "$EXPECTED_IMAGE_IDS"
    gzip -dc "$archive" | docker image load
    if [[ "$(image_id_hash "${images[@]}")" != "$EXPECTED_IMAGE_IDS" ]]; then
      echo 'Loaded image IDs do not match the images verified by the source drill.' >&2
      exit 1
    fi
    rm "$archive"
    ;;
  *)
    echo 'usage: drill_images.sh save|load' >&2
    exit 2
    ;;
esac
