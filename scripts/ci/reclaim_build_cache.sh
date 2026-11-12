#!/usr/bin/env bash
# Only the disposable hosted runner may reclaim cache; never local or self-hosted engines.
set -euo pipefail
if [[ "${GITHUB_ACTIONS:-}" != true || "${RUNNER_ENVIRONMENT:-}" != github-hosted ]]; then
  echo 'Refusing build-cache cleanup outside GitHub-hosted Actions.' >&2
  exit 1
fi

romd_images=(romd-worker:test romd-admin:test romd-consumer:test romd-player:test romd-postgres:test)
romd_image_ids_before=$(docker image inspect --format '{{.Id}}' "${romd_images[@]}")
df -h "${RUNNER_TEMP:?}"
# Keep runtime images and all volumes. The following harness retains its disk-space guard.
docker builder prune --all --force
romd_image_ids_after=$(docker image inspect --format '{{.Id}}' "${romd_images[@]}")
if [[ "$romd_image_ids_before" != "$romd_image_ids_after" ]]; then
  echo 'Runtime image IDs changed during build-cache cleanup.' >&2
  exit 1
fi
df -h "$RUNNER_TEMP"
