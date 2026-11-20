#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SIM_BIN="$REPO_ROOT/simulator/build/tick_simulator"

if [[ ! -x "$SIM_BIN" ]]; then
  cmake -S "$REPO_ROOT/simulator" -B "$REPO_ROOT/simulator/build" -DCMAKE_BUILD_TYPE=Release
  cmake --build "$REPO_ROOT/simulator/build" -j
fi

exec "$SIM_BIN" \
  --probe \
  --offline \
  --data-dir "$REPO_ROOT/data" \
  "$@"
