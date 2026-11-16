#!/bin/sh
# Serve a renewed or replaced certificate without restarting the ingress.
# nginx reads its certificate when it starts or reloads; romd-init (or an
# operator's own renewal) can replace the files while it runs. The official
# image runs this from /docker-entrypoint.d before nginx starts.
set -eu
certificate=/etc/romd/tls/fullchain.pem
interval="${ROMD_CERTIFICATE_CHECK_SECONDS:-60}"
fingerprint() { sha256sum "$certificate" 2>/dev/null || true; }
(
  served="$(fingerprint)"
  while sleep "$interval"; do
    current="$(fingerprint)"
    if [ -n "$current" ] && [ "$current" != "$served" ]; then
      # Keep serving the old certificate if the new files do not form a valid configuration.
      if nginx -t >/dev/null 2>&1 && nginx -s reload >/dev/null 2>&1; then
        served="$current"
      fi
    fi
  done
) &
