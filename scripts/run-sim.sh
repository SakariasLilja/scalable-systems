#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIM_BIN="$REPO_ROOT/simulator/build/tick_simulator"

if [[ ! -x "$SIM_BIN" ]]; then
  cmake -S "$REPO_ROOT/simulator" -B "$REPO_ROOT/simulator/build" -DCMAKE_BUILD_TYPE=Release
  cmake --build "$REPO_ROOT/simulator/build"
fi

SPEED="1.0"
if [[ $# -ge 1 && "$1" != -* ]]; then
  SPEED="$1"
  shift
fi

CONCURRENCY="16"
if [[ $# -ge 1 && "$1" != -* ]]; then
  CONCURRENCY="$1"
  shift
fi

exec "$SIM_BIN" \
  --data-dir "$REPO_ROOT/data" \
  --url "${SIM_URL:-http://localhost:8080/api/ingest}" \
  --speed "$SPEED" \
  --concurrency "$CONCURRENCY" \
  "$@"
