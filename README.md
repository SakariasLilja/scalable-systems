# Financial Tick Data Breakout Detector (CS-E4780)

Real-time market tick processing and EMA-38/100 breakout detection system running on Kubernetes (Minikube).

## Architecture

- **Tick Simulator (`simulator/`):** C++20 tool that streams CSV tick data over HTTP to Traefik, setting `X-Market-Shard` via FNV-1a hash of `symbol_id`.
- **Traefik (`k8s/07-08`):** Routes `/api/ingest` by `X-Market-Shard` to `worker-0..2`, and `/` (REST, SSE, Svelte UI) to `server` with `trading_session` sticky cookies.
- **Worker (`services/worker`):** 3-pod StatefulSet (ASP.NET Core). Aggregates ticks into 5-minute tumbling windows, computes EMA-38/100 and buy/sell crossovers, writes to TimescaleDB, and publishes updates to Redis Pub/Sub.
- **Storage & Broker:** 2-shard TimescaleDB StatefulSet (consistent hashing by `symbol_id`) + Redis (Pub/Sub for live fan-out and TTL read cache).
- **Server & Frontend (`services/server`, `frontend/`):** HPA-autoscaled ASP.NET Core API serving the Svelte + Chart.js UI, paged DB history, and live SSE streams.

## Prerequisites

- Linux / WSL2 with **Docker**, **Minikube**, and **kubectl**
- **CMake** and a **C++20 compiler** (for building the tick simulator)
- Tick CSV files placed in `./data/`
- *(Optional)* Root `.env` with `POSTGRES_USER`, `POSTGRES_PASSWORD`, and `POSTGRES_DB` (synced to K8s secrets on deploy; defaults are used if omitted)

## Run the Cluster

```bash
minikube start
./scripts/k8s-build.sh
./scripts/k8s-deploy.sh
```

Expose Traefik locally (pick one):

```bash
# Port-forward to http://localhost:8080
kubectl port-forward -n trading-system svc/traefik 8080:80

# Or via Minikube service URL
minikube service traefik -n trading-system --url
```

*(Optional)* Monitor pods, HPA, and resources live:

```bash
./scripts/k8s-status.sh
```

## Start the Tick Simulator

Builds `simulator/build/tick_simulator` automatically on first run and streams `./data/*.csv` to `SIM_URL` (default `http://localhost:8080/api/ingest`):

```bash
# Usage: ./scripts/run-sim.sh [speed] [concurrency] [extra flags...]
# speed=1.0 (real-time), speed=100 (100x), speed=0 (unbounded)
./scripts/run-sim.sh 100

# If using a custom Traefik URL:
SIM_URL="http://127.0.0.1:<port>/api/ingest" ./scripts/run-sim.sh 0
```

## Run k6 Load Test Inside the Cluster

Runs `grafana/k6:latest` in a temporary pod targeting `http://traefik.trading-system.svc.cluster.local` (avoids host port-forward bottlenecks):

```bash
# Usage: ./scripts/run-k6-in-cluster.sh [vus] [duration]
./scripts/run-k6-in-cluster.sh 500 5m
```

## Tear down the Cluster

```bash
./scripts/k8s-teardown.sh
```
