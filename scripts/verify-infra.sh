#!/usr/bin/env bash
set -euo pipefail

DOCKER_CMD="docker"
if ! command -v docker &> /dev/null; then
  if command -v docker.exe &> /dev/null; then
    DOCKER_CMD="docker.exe"
  else
    echo "Error: docker binary not found." >&2
    exit 1
  fi
fi

if [ -f .env ]; then
  # shellcheck disable=SC2046
  export $(grep -v '^#' .env | xargs)
fi

PG_USER="${POSTGRES_USER:-postgres}"
PG_DB="${POSTGRES_DB:-trading_db}"

echo "=== Starting TimescaleDB Service ==="
$DOCKER_CMD compose up -d timescaledb

echo "=== Waiting for Database Healthcheck ==="
MAX_RETRIES=30
RETRY_COUNT=0
until $DOCKER_CMD compose ps timescaledb | grep -E -q "\(healthy\)| healthy "; do
  RETRY_COUNT=$((RETRY_COUNT + 1))
  if [ $RETRY_COUNT -ge $MAX_RETRIES ]; then
    echo " Timeout waiting for database healthcheck." >&2
    $DOCKER_CMD compose ps timescaledb
    exit 1
  fi
  sleep 2
  echo -n "."
done
echo " Healthy!"

echo "=== Verifying Hypertable in TimescaleDB ==="
$DOCKER_CMD compose exec -T timescaledb psql -U "$PG_USER" -d "$PG_DB" -c "
SELECT hypertable_name, num_dimensions 
FROM timescaledb_information.hypertables 
WHERE hypertable_name = 'trading_windows';
"

echo "=== Testing Sample Window Record Insertion ==="
$DOCKER_CMD compose exec -T timescaledb psql -U "$PG_USER" -d "$PG_DB" -c "
INSERT INTO trading_windows (symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal)
VALUES ('TEST.SYM', '2021-11-13 00:00:00+00', '2021-11-13 00:05:00+00', 100.5, 99.8, 101.2, true, false)
ON CONFLICT (symbol_id, window_start) DO NOTHING;
"

echo "=== Verifying Inserted Record ==="
$DOCKER_CMD compose exec -T timescaledb psql -U "$PG_USER" -d "$PG_DB" -c "
SELECT symbol_id, window_start, ema_38, ema_100, buy_signal FROM trading_windows WHERE symbol_id = 'TEST.SYM';
"

echo "=== Infrastructure Verification Succeeded ==="
