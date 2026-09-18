#!/usr/bin/env bash
# Read-only application checks after Compose has reached healthy state.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
for service in romd-admin romd-consumer; do
  "$here/compose.sh" exec -T "$service" curl --fail --silent --max-time 10 http://localhost:8080/health/ready
  "$here/compose.sh" exec -T "$service" curl --fail --silent --max-time 10 http://localhost:8080/ > /dev/null
  printf '\n%s ready and serving its portal\n' "$service"
done
"$here/compose.sh" exec -T romd-player wget -q -O /dev/null http://127.0.0.1:8080/player-config.json
printf 'Player configuration available. Verify HTTPS discovery/login through your ingress separately.\n'
