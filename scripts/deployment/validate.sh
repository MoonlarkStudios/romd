#!/usr/bin/env bash
# Render without printing secrets and validate the effective production contract.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$here/compose.sh" config --format json | python3 "$here/validate.py"
