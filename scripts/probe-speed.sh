#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIM_BIN="$REPO_ROOT/simulator/build/tick_simulator"

if [[ ! -x "$SIM_BIN" ]]; then
  cmake -S "$REPO_ROOT/simulator" -B "$REPO_ROOT/simulator/build" -DCMAKE_BUILD_TYPE=Release
  cmake --build "$REPO_ROOT/simulator/build"
fi

for arg in "$@"; do
  if [[ "$arg" == "--offline" ]]; then
    exec "$SIM_BIN" \
      --probe \
      --data-dir "$REPO_ROOT/data" \
      "$@"
  fi
done

CONCURRENCY="4"
if [[ $# -ge 1 && "$1" != -* ]]; then
  CONCURRENCY="$1"
  shift
fi

PROBE_TICKS="20000"
if [[ $# -ge 1 && "$1" != -* ]]; then
  PROBE_TICKS="$1"
  shift
fi

exec "$SIM_BIN" \
  --probe \
  --data-dir "$REPO_ROOT/data" \
  --url "${SIM_URL:-http://localhost:8080/api/ingest}" \
  --concurrency "$CONCURRENCY" \
  --probe-ticks "$PROBE_TICKS" \
  "$@"
