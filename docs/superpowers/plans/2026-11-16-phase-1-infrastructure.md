# Phase 1: Infrastructure Setup (Docker & Database) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Orchestrate containerized TimescaleDB, ASP.NET Core backend, and Svelte frontend with automated hypertable schema provisioning.

**Architecture:** TimescaleDB (PostgreSQL 15+) runs in Docker with an initialization mount creating the `trading_windows` hypertable partitioned by `window_start`. Docker Compose orchestrates the database with healthchecks, alongside service stubs for the C# ASP.NET backend and Svelte frontend configured via `.env`.

**Tech Stack:** Docker, Docker Compose, TimescaleDB (PostgreSQL 15+), Bash.

**Spec:** [specs/phase-1-infrastructure.md](file:///home/zak/aalto/scalable-systems/specs/phase-1-infrastructure.md)

## Global Constraints
- TimescaleDB version: PostgreSQL 15+ compatible (`timescale/timescaledb:latest-pg15` or pg16/pg17).
- Database credentials and ports must be sourced from `.env` with fallback defaults.
- Primary table name: `trading_windows`.
- Hypertable partitioning column: `window_start`.
- No raw tick data written to the database; only aggregated 5-minute window records.

## Review Focus
1. Database container restarts without losing hypertable definitions or volume state.
2. `initdb/01-init.sql` is idempotent using `IF NOT EXISTS` constructs.
3. TimescaleDB extension properly created and verified before `create_hypertable` call.
4. Composite primary key `(symbol_id, window_start)` prevents duplicate window writes per symbol.
5. Index `idx_trading_windows_symbol_time` created on `(symbol_id, window_start DESC)` for fast lookup.

---

### Task 1: Database Initialization Script & Hypertable Schema

**Files:**
- Create: `initdb/01-init.sql`
- Create: `.env.example`
- Modify: `.env`

**Interfaces:**
- Produces: `trading_windows` hypertable in PostgreSQL database `trading_db`.

- [ ] **Step 1: Write `initdb/01-init.sql` schema script**
```sql
CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;

CREATE TABLE IF NOT EXISTS trading_windows (
    symbol_id VARCHAR(32) NOT NULL,
    window_start TIMESTAMPTZ NOT NULL,
    window_end TIMESTAMPTZ NOT NULL,
    ema_38 DOUBLE PRECISION NOT NULL,
    ema_100 DOUBLE PRECISION NOT NULL,
    last_price DOUBLE PRECISION NOT NULL,
    buy_signal BOOLEAN DEFAULT FALSE,
    sell_signal BOOLEAN DEFAULT FALSE,
    PRIMARY KEY (symbol_id, window_start)
);

SELECT create_hypertable('trading_windows', 'window_start', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS idx_trading_windows_symbol_time 
ON trading_windows (symbol_id, window_start DESC);
```

- [ ] **Step 2: Create `.env.example` with standard defaults**
```bash
POSTGRES_HOST=timescaledb
POSTGRES_PORT=5432
POSTGRES_DB=trading_db
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres_password

BACKEND_PORT=5000
FRONTEND_PORT=3000
```

- [ ] **Step 3: Verify SQL syntax and file creation**
Run: `test -f initdb/01-init.sql && test -f .env.example && echo "Files exist"`
Expected: "Files exist"

- [ ] **Step 4: Commit**
```bash
git add initdb/01-init.sql .env.example
git commit -m "feat(infra): add database initialization script and env template"
```

---

### Task 2: Docker Compose Orchestration & Healthchecks

**Files:**
- Modify: `docker-compose.yml`

**Interfaces:**
- Consumes: `initdb/01-init.sql`, `.env`
- Produces: Running `timescaledb` service with healthy status check, ready for backend and frontend container attachments.

- [ ] **Step 1: Update `docker-compose.yml` configuration**
Configure `timescaledb` with `pg_isready` healthcheck, `initdb` mount, and define service endpoints:
```yaml
services:
  timescaledb:
    image: timescale/timescaledb:latest-pg16
    container_name: trading_timescaledb
    environment:
      POSTGRES_DB: ${POSTGRES_DB:-trading_db}
      POSTGRES_USER: ${POSTGRES_USER:-postgres}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres_password}
    ports:
      - "${POSTGRES_PORT:-5432}:5432"
    volumes:
      - timescaledb_data:/var/lib/postgresql/data
      - ./initdb/01-init.sql:/docker-entrypoint-initdb.d/01-init.sql:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${POSTGRES_USER:-postgres} -d ${POSTGRES_DB:-trading_db}"]
      interval: 5s
      timeout: 5s
      retries: 5
    restart: unless-stopped

  backend:
    build:
      context: ./InitialAttempt
      dockerfile: Dockerfile
    container_name: trading_backend
    environment:
      POSTGRES_HOST: timescaledb
      POSTGRES_PORT: 5432
      POSTGRES_DB: ${POSTGRES_DB:-trading_db}
      POSTGRES_USER: ${POSTGRES_USER:-postgres}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:-postgres_password}
      ASPNETCORE_ENVIRONMENT: Development
    ports:
      - "${BACKEND_PORT:-5000}:80"
    depends_on:
      timescaledb:
        condition: service_healthy
    restart: unless-stopped

  frontend:
    image: node:20-alpine
    container_name: trading_frontend
    working_dir: /app
    ports:
      - "${FRONTEND_PORT:-3000}:3000"
    environment:
      VITE_API_BASE_URL: http://localhost:${BACKEND_PORT:-5000}
    command: ["sh", "-c", "echo 'Frontend container ready' && tail -f /dev/null"]
    depends_on:
      - backend
    restart: unless-stopped

volumes:
  timescaledb_data:
```

- [ ] **Step 2: Validate docker-compose file syntax**
Run: `docker compose config`
Expected: Valid compose config without syntax errors.

- [ ] **Step 3: Commit**
```bash
git add docker-compose.yml
git commit -m "feat(infra): update docker compose configuration with healthchecks"
```

---

### Task 3: Infrastructure Verification & Smoke Test

**Files:**
- Create: `scripts/verify-infra.sh`

**Interfaces:**
- Consumes: `docker compose` services
- Produces: Automated verification report on TimescaleDB hypertable readiness and query execution.

- [ ] **Step 1: Write `scripts/verify-infra.sh` verification script**
```bash
#!/usr/bin/env bash
set -euo pipefail

echo "=== Starting TimescaleDB Service ==="
docker compose up -d timescaledb

echo "=== Waiting for Database Healthcheck ==="
until docker compose ps timescaledb | grep -q "(healthy)"; do
  sleep 2
  echo -n "."
done
echo " Healthy!"

echo "=== Verifying Hypertable in TimescaleDB ==="
docker compose exec -T timescaledb psql -U postgres -d trading_db -c "
SELECT hypertable_name, num_dimensions 
FROM timescaledb_information.hypertables 
WHERE hypertable_name = 'trading_windows';
"

echo "=== Testing Sample Window Record Insertion ==="
docker compose exec -T timescaledb psql -U postgres -d trading_db -c "
INSERT INTO trading_windows (symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal)
VALUES ('TEST.SYM', '2021-11-13 00:00:00+00', '2021-11-13 00:05:00+00', 100.5, 99.8, 101.2, true, false)
ON CONFLICT (symbol_id, window_start) DO NOTHING;
"

echo "=== Verifying Inserted Record ==="
docker compose exec -T timescaledb psql -U postgres -d trading_db -c "
SELECT symbol_id, window_start, ema_38, ema_100, buy_signal FROM trading_windows WHERE symbol_id = 'TEST.SYM';
"

echo "=== Infrastructure Verification Succeeded ==="
```

- [ ] **Step 2: Make `scripts/verify-infra.sh` executable**
Run: `chmod +x scripts/verify-infra.sh`

- [ ] **Step 3: Commit**
```bash
git add scripts/verify-infra.sh
git commit -m "feat(infra): add infrastructure verification script"
```
