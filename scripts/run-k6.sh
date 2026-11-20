#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

if ! command -v k6 >/dev/null 2>&1; then
  echo "k6 binary not found in PATH. Install Grafana k6 first." >&2
  exit 1
fi

VUS="${1:-50}"
DURATION="${2:-60s}"
BASE_URL="${3:-http://localhost:8080}"

exec k6 run \
  -e VUS="$VUS" \
  -e DURATION="$DURATION" \
  -e BASE_URL="$BASE_URL" \
  "$REPO_ROOT/loadtests/user-simulation.js" \
  "${@:4}"
