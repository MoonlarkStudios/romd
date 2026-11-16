#!/usr/bin/env bash
# Create .env for a new installation: prompts for your choices, generates every secret.
set -euo pipefail
exec python3 "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/setup.py" "$@"
