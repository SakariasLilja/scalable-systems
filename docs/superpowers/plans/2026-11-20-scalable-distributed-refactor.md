# Scalable Distributed Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the single-process Financial Tick Data Breakout Detector into a Kubernetes-native distributed system (C++20 host simulator, C# Worker, Redis Pub/Sub, TimescaleDB, horizontally scaled C# Server + Svelte pods behind Traefik CRDs, and stock Grafana k6 load tests) across 5 verifiable phases.

**Architecture:** The host C++20 simulator streams 24 GB of raw DEBS 2022 CSV files from `data/` chronologically across the week, sending 1 raw JSON row per HTTP POST through Traefik to the C# Worker. The Worker filters trades, computes 5-minute CEST tumbling windows and $EMA^{38}$/$EMA^{100}$ breakouts, batch-writes to TimescaleDB, and publishes finalized windows to Redis Pub/Sub. Horizontally auto-scaled Server pods (serving baked Svelte `dist/` + stateful SSE + history/symbol DB queries + Clear DB reset) subscribe to Redis and receive sticky user sessions via Traefik `sticky.cookie` + `least-connections`.

**Tech Stack:** .NET 10 (ASP.NET Core, `Npgsql`, `StackExchange.Redis`, xUnit), Svelte + Vite + Chart.js, C++20 + CMake + `libcurl`, Kubernetes (Minikube + `metrics-server` + Traefik v3 CRDs), TimescaleDB, Redis 7, Grafana k6 (stock binary).

**Spec:** [2026-11-20-scalable-distributed-refactor-design.md](file:///home/zak/aalto/scalable-systems/docs/superpowers/specs/2026-11-20-scalable-distributed-refactor-design.md)

## Global Constraints
- Follow `AGENTS.md`: no XML docs / JSDoc / over-commenting; C# `PascalCase` classes/methods and `camelCase` variables/parameters; JS/Svelte `snake_case` methods/variables; secrets in `.env` / K8s `Secret`; global values in `Constants.cs` / `constants.js`.
- Keep `InitialAttempt/` intact as reference; build new services under `services/`, `simulator/`, `k8s/`, `loadtests/`, `scripts/`.
- Never copy `data/` CSV files into containers or PersistentVolumes.
- Every Kubernetes container must set explicit CPU and memory `requests` and `limits`.
- Always run `dotnet test` with `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp` (and `--no-restore` after initial package restore).
- Testing strategy per `AGENTS.md`: unit test logic-heavy code (EMA math, tumbling windows, resilient JSON parsing, async warning throttling, reset state clearing, C++ CSV/day-traversal logic); skip trivial CRUD/UI rendering unit tests.

## Review Focus
1. **Corrupted JSON types or malformed body on `POST /api/ingest`** (e.g., array/number where string expected, invalid decimal, unparseable date/time, truncated JSON) — Worker never throws unhandled exceptions, skips/returns cleanly, and keeps ingesting valid ticks. *(Pinned in Task 2)*
2. **Warning flood on large blocks of malformed rows** — Worker's bounded drop-newest async warning logger never blocks `POST /api/ingest` and tracks dropped warning counts. *(Pinned in Task 2)*
3. **`DELETE /api/data` between simulation runs** — Truncating `trading_windows` also clears Worker `WindowProcessor` EMA state so next run starts cold (`EMA=0`) and broadcasts SSE `reset` to clear live charts. *(Pinned in Task 1 & Task 4)*
4. **C++ Simulator EOF -> next day transition with overnight/weekend gap or `::` timestamp** — Automatically advances to next chronological CSV file, re-reads header, and caps inter-event delay so playback never stalls. *(Pinned in Task 7)*
5. **Same-origin routing & Traefik `sticky.cookie` affinity** — Frontend uses relative `api_base_url = ''` so `trading_session` cookie attaches to all REST + SSE calls, and k6 VUs honor cookie jar across HPA scale-out. *(Pinned in Task 5 & Task 9)*

---

## Phase 1: Shared Domain Library & Worker Service

### Task 1: Shared Domain Library & Core Math / Reset Tests

**Files:**
- Create: `services/ScalableTrading.slnx`
- Create: `services/shared/Trading.Shared.csproj`
- Create: `services/shared/Models/MinimalTradingEntry.cs`
- Create: `services/shared/Models/FinalizedWindowRecord.cs`
- Create: `services/shared/Utils/Constants.cs`
- Create: `services/shared/Services/EmaCalculator.cs`
- Create: `services/shared/Services/WindowProcessor.cs`
- Test: `services/tests/Trading.Tests.csproj`
- Test: `services/tests/WindowAndEmaTests.cs`

**Interfaces:**
- Produces (`Trading.Shared`):
  - `MinimalTradingEntry` (plain POCO — no `CsvHelper` attributes): `string Id`, `decimal? Last`, `DateOnly? Date`, `TimeOnly? Time`, `DateOnly? TradingDate`, `TimeOnly? TradingTime`, `EffectiveDate`, `EffectiveTime`.
  - `FinalizedWindowRecord(string SymbolId, DateTime WindowStart, DateTime WindowEnd, double Ema38, double Ema100, double LastPrice, bool BuySignal, bool SellSignal)`.
  - `Constants`: EMA periods/alphas (`38`, `100`), `WindowDurationMinutes = 5`, `RedisWindowsChannel = "ema:windows"`, `RedisResetChannel = "system:reset"`, `IngestChannelCapacity = 100000`, `WarningLogChannelCapacity = 1024`, `DbBatchFlushIntervalMs = 5000`, `DbBatchMaxCapacity = 1000`, `DbBatchMaxBufferSize = 5000`, `DbBatchRetryDelayMs = 2000`, `SubscriptionHidden/Tracked/Active`, `DefaultSymbolSearchLimit = 20`.
  - `EmaCalculator`: `CalculateShortEma(double closePrice, double prevEma)`, `CalculateLongEma(double closePrice, double prevEma)`, `EvaluateBreakout(double ema38, double ema100, double prevEma38, double prevEma100)`.
  - `IWindowProcessor`: `FinalizedWindowRecord? ProcessTick(MinimalTradingEntry tick)` and `void Reset()`.

- [ ] **Step 1: Scaffold `services/shared/Trading.Shared.csproj`, `services/tests/Trading.Tests.csproj`, and `services/ScalableTrading.slnx`**
  Target `net10.0`, add `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` to `Trading.Tests.csproj`.

- [ ] **Step 2: Write failing tests in `services/tests/WindowAndEmaTests.cs`**
  Include tests:
  - `CalculateShortEma_AndLongEma_MatchSpecFormulas` (verify $\alpha_{38}=2/39$, $\alpha_{100}=2/101$, first window initializes from `prevEma = 0`).
  - `EvaluateBreakout_DetectsBuyAndSellCrossovers`.
  - `ProcessTick_FinalizesFiveMinuteWindow_AndResetClearsEmaState` (process 2 windows to build non-zero EMA, call `processor.Reset()`, process fresh window and assert EMA computes from `prevEma = 0`, not stale pre-reset EMA).

- [ ] **Step 3: Run test to verify it fails**
  Run: `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests`
  Expected: FAIL (missing `EmaCalculator` / `WindowProcessor` / `Reset()`).

- [ ] **Step 4: Implement `MinimalTradingEntry`, `FinalizedWindowRecord`, `Constants`, `EmaCalculator`, and `WindowProcessor` (with `public void Reset() => states.Clear();`)**
  Port exact window alignment (5-min CEST UTC+2) and EMA math from `InitialAttempt/Services/`.

- [ ] **Step 5: Run tests to verify pass**
  Run: `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests --no-restore`
  Expected: PASS (all tests green).

- [ ] **Step 6: Commit**
  ```bash
  git add services/ScalableTrading.slnx services/shared services/tests
  git commit -m "feat: add shared domain library with ema, window processor, and reset"
  ```

---

### Task 2: Worker Resilient Raw-JSON Tick Parser & Non-Blocking Async Warning Logger

**Files:**
- Create: `services/worker/Trading.Worker.csproj`
- Create: `services/worker/Services/AsyncWarningLogger.cs`
- Create: `services/worker/Services/RawTickParser.cs`
- Test: `services/tests/RawTickParserTests.cs`

**Interfaces:**
- Consumes: `MinimalTradingEntry`, `Constants.WarningLogChannelCapacity` from `Trading.Shared`.
- Produces:
  - `IAsyncWarningLogger`: `void EnqueueWarning(string message)`, `long DroppedCount { get; }`, plus `BackgroundService` loop draining bounded `Channel<string>(FullMode = DropWrite)` and emitting `ILogger.LogWarning` + periodic dropped summary.
  - `RawTickParser`: `static bool TryParseTradeTick(JsonElement root, IAsyncWarningLogger? warningLogger, out MinimalTradingEntry? entry)` — extracts `ID`, `Last`, `Date`, `Time`, `Trading date`, `Trading time` (case-insensitive key lookup). Tolerates *any* missing, empty, `"::"`, wrong-JSON-type (array/object/bool/number), or unparseable date/time/decimal field without throwing. Returns `false` for quote rows (missing/empty `Last`) or missing `ID`; logs non-blocking warning on corrupted non-empty field values.

- [ ] **Step 1: Create `services/worker/Trading.Worker.csproj` and reference from `Trading.Tests.csproj`**
  Add `Npgsql` (`10.0.3`) and `StackExchange.Redis` (`2.8.16` or latest compatible) to `Trading.Worker.csproj`, project reference to `Trading.Shared.csproj`.

- [ ] **Step 2: Write failing tests in `services/tests/RawTickParserTests.cs`**
  Include tests:
  - `TryParseTradeTick_ValidTradeJson_ReturnsMinimalTradingEntry`
  - `TryParseTradeTick_QuoteWithEmptyLast_ReturnsFalseWithoutWarning`
  - `TryParseTradeTick_CorruptedFieldsAndWrongJsonTypes_NeverThrowsAndLogsWarning` (test `"::"`, `"not-a-decimal"`, `Last: [1,2]`, `Trading time: 99999`, `Date: "99-99-9999"` — asserts no exception and warning enqueued).
  - `AsyncWarningLogger_WhenFloodedBeyondCapacity_DropsWithoutBlockingAndTracksDroppedCount` (enqueue `Capacity + 500` warnings without starting consumer, assert non-blocking return and `DroppedCount >= 500`).

- [ ] **Step 3: Run tests to verify failure**
  Run: `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests --filter "FullyQualifiedName~RawTickParserTests"`
  Expected: FAIL.

- [ ] **Step 4: Implement `AsyncWarningLogger.cs` and `RawTickParser.cs`**
  Use `System.Text.Json` `JsonElement` inspection (`ValueKind` checks before reading), `CultureInfo.InvariantCulture` for decimal parsing, `DateOnly.TryParseExact(..., "dd-MM-yyyy")` and `TimeOnly.TryParseExact(..., ["HH:mm:ss.fff", "HH:mm:ss"])`.

- [ ] **Step 5: Run tests to verify pass**
  Run: `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests --no-restore`
  Expected: PASS.

- [ ] **Step 6: Commit**
  ```bash
  git add services/worker/Trading.Worker.csproj services/worker/Services services/tests
  git commit -m "feat: add resilient raw json tick parser and non-blocking warning logger"
  ```

---

### Task 3: Worker Ingestion Pipeline, DB Batch Writer, Redis Publisher & Dockerfile

**Files:**
- Create: `services/worker/Services/TickIngestionService.cs`
- Create: `services/worker/Services/DbBatchWriter.cs`
- Create: `services/worker/Services/RedisWindowPublisher.cs`
- Create: `services/worker/Controllers/IngestController.cs`
- Create: `services/worker/Program.cs`
- Create: `services/worker/Dockerfile`
- Test: `services/tests/WorkerPipelineTests.cs`

**Interfaces:**
- Consumes: `IWindowProcessor`, `RawTickParser`, `IAsyncWarningLogger`, `Constants`.
- Produces:
  - `ITickIngestionService`: `ValueTask EnqueueAsync(MinimalTradingEntry tick, CancellationToken ct)` (bounded `Channel` with `BoundedChannelFullMode.Wait`), `void Reset()`.
  - `IWindowPublisher`: `Task PublishWindowAsync(FinalizedWindowRecord record)` (implemented by `RedisWindowPublisher` using `IConnectionMultiplexer.GetSubscriber().PublishAsync(Constants.RedisWindowsChannel, json)`).
  - `IDbBatchWriter`: `void Enqueue(FinalizedWindowRecord record)`, `void ClearBuffer()`.
  - HTTP Endpoints:
    - `POST /api/ingest` — reads raw JSON body (`JsonDocument.ParseAsync`), catches `JsonException` (logs async warning, returns `200`/`400` without crashing), runs `RawTickParser.TryParseTradeTick`, enqueues valid trades via `ITickIngestionService.EnqueueAsync`.
    - `POST /internal/reset` — calls `tickIngestionService.Reset()`, `windowProcessor.Reset()`, `dbBatchWriter.ClearBuffer()`, returns `200 OK`.
    - `GET /health` — returns `200 OK`.

- [ ] **Step 1: Write failing test in `services/tests/WorkerPipelineTests.cs`**
  Test `IngestController_PostMalformedJson_DoesNotThrowAndLogsWarning` and `WorkerReset_ClearsWindowProcessorAndAllowsFreshEma`.

- [ ] **Step 2: Run test to verify failure**
  Run: `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests --filter "FullyQualifiedName~WorkerPipelineTests"`
  Expected: FAIL.

- [ ] **Step 3: Implement `TickIngestionService.cs`, `DbBatchWriter.cs`, `RedisWindowPublisher.cs`, `IngestController.cs`, `Program.cs`, and `services/worker/Dockerfile`**
  Port `DbBatchWriter` parameterized upsert SQL from `InitialAttempt/Services/DbBatchWriter.cs`, adding `ClearBuffer()`. Configure Kestrel on port `8080` inside container.

- [ ] **Step 4: Run all tests & verify Worker builds**
  Run: `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests --no-restore`
  Expected: PASS.

- [ ] **Step 5: Commit**
  ```bash
  git add services/worker services/tests services/ScalableTrading.slnx
  git commit -m "feat: implement worker ingestion pipeline, redis publisher, and reset endpoint"
  ```

---

## Phase 2: Server Service & Frontend Refactor

### Task 4: Server Service (Redis Subscriber, SSE, History/Symbols DB Queries, Clear DB Reset)

**Files:**
- Create: `services/server/Trading.Server.csproj`
- Create: `services/server/Services/SseService.cs`
- Create: `services/server/Services/RedisWindowSubscriber.cs`
- Create: `services/server/Services/DatabaseQueryService.cs`
- Create: `services/server/Controllers/SseController.cs`
- Create: `services/server/Controllers/HistoryController.cs`
- Create: `services/server/Controllers/SymbolsController.cs`
- Create: `services/server/Controllers/DataController.cs`
- Create: `services/server/Program.cs`
- Create: `services/server/Dockerfile`
- Test: `services/tests/ServerResetAndSseTests.cs`

**Interfaces:**
- Consumes: `Trading.Shared`, Redis channels `ema:windows` and `system:reset`, TimescaleDB `trading_windows`, Worker `POST {WORKER_URL}/internal/reset`.
- Produces HTTP Endpoints:
  - `GET /api/sse/stream?clientId=<id>` — SSE stream (`connected`, `advisory_alert`, `active_update`, `tracked_update`, `reset`).
  - `POST /api/subscription/set-state` — `{ clientId, symbol, state }`.
  - `GET /api/history/{symbolId}?limit=50` — historical windows from TimescaleDB ordered chronologically.
  - `GET /api/symbols/search?q=<query>&limit=20` — searches merged in-memory live symbol set + TimescaleDB `SELECT DISTINCT symbol_id FROM trading_windows`.
  - `DELETE /api/data` — truncates `trading_windows`, invokes `POST {WORKER_URL}/internal/reset`, clears local live symbol cache, and publishes `"reset"` to Redis `system:reset` (all Server pods clear their symbol caches and broadcast SSE `reset` event).
  - `GET /health` — returns `200 OK`.
  - Static files from `wwwroot/` (`app.UseDefaultFiles(); app.UseStaticFiles(); app.MapFallbackToFile("index.html");`).

- [ ] **Step 1: Scaffold `services/server/Trading.Server.csproj` (with `Npgsql`, `StackExchange.Redis`, reference to `Trading.Shared.csproj`) and add to `ScalableTrading.slnx`**

- [ ] **Step 2: Write failing test in `services/tests/ServerResetAndSseTests.cs`**
  Test `DataController_DeleteData_TruncatesDbResetsWorkerAndPublishesRedisReset` (with fake `IDatabaseQueryService`, `IWorkerResetClient`, and `IRedisResetPublisher`, asserting all 3 are called in order).

- [ ] **Step 3: Run test to verify failure**
  Run: `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests --filter "FullyQualifiedName~ServerResetAndSseTests"`
  Expected: FAIL.

- [ ] **Step 4: Implement `SseService`, `RedisWindowSubscriber`, `DatabaseQueryService`, Controllers (`SseController`, `HistoryController`, `SymbolsController`, `DataController`), `Program.cs`, and multi-stage `services/server/Dockerfile`**
  Multi-stage `Dockerfile` (context = repo root): Stage 1 `node:20-alpine` runs `npm ci && npm run build` in `frontend/`; Stage 2 `mcr.microsoft.com/dotnet/sdk:10.0` publishes `services/server`; Stage 3 `mcr.microsoft.com/dotnet/aspnet:10.0` copies published DLLs + `frontend/dist` into `/app/wwwroot`.

- [ ] **Step 5: Run all tests**
  Run: `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests --no-restore`
  Expected: PASS.

- [ ] **Step 6: Commit**
  ```bash
  git add services/server services/tests services/ScalableTrading.slnx
  git commit -m "feat: add server service with redis sse fan-out, db queries, and clear-db reset"
  ```

---

### Task 5: Frontend Refactor (Same-Origin API, Admin Status Bar & Clear DB Button)

**Files:**
- Modify: `frontend/src/constants.js`
- Modify: `frontend/src/lib/sse_service.js`
- Create: `frontend/src/components/SystemStatusBar.svelte`
- Modify: `frontend/src/App.svelte`
- Delete or leave unused: `frontend/src/components/SimulatorControls.svelte` (remove import from `App.svelte`)

**Interfaces:**
- Consumes: Server HTTP/SSE endpoints (`/api/sse/stream`, `/api/subscription/set-state`, `/api/history/{symbol}`, `/api/symbols/search`, `DELETE /api/data`).
- Produces:
  - `constants.js`: `api_base_url = import.meta.env.VITE_API_BASE_URL ?? ''` (same-origin default so Traefik `sticky.cookie` attaches to every fetch + EventSource).
  - `sse_service.js`: adds `reset` event listener in `connect_sse` and `export async function clear_database()` calling `DELETE ${api_base_url}/api/data`; removes obsolete monolith simulator control functions.
  - `SystemStatusBar.svelte`: displays SSE connection status (`Connected` / `Disconnected`), latest simulation timestamp received, and a **"Clear DB"** button with inline confirm step calling `clear_database()`.
  - `App.svelte`: handles `event_type === 'reset'` by clearing `symbol_data`, `latest_active_tick`, and closing/clearing `active_modal_symbol`.

- [ ] **Step 1: Update `frontend/src/constants.js` and `frontend/src/lib/sse_service.js`**
  Preserve `snake_case` naming per `AGENTS.md`, no JSDoc/comments.

- [ ] **Step 2: Create `frontend/src/components/SystemStatusBar.svelte` and wire into `frontend/src/App.svelte`**
  Replace `SimulatorControls` usage with `SystemStatusBar`; wire SSE `reset` event to wipe live chart and symbol state.

- [ ] **Step 3: Verify frontend production build**
  Run: `npm --prefix frontend run build`
  Expected: `dist/` built with 0 errors.

- [ ] **Step 4: Commit**
  ```bash
  git add frontend/src
  git commit -m "feat: refactor frontend for same-origin sse, status bar, and clear-db button"
  ```

---

## Phase 3: Kubernetes & Traefik Cluster

### Task 6: Kubernetes Manifests, Traefik CRDs (Sticky Cookie + Least-Connections), HPA & Minikube Scripts

**Files:**
- Create: `k8s/00-namespace-config.yaml`
- Create: `k8s/01-timescaledb.yaml`
- Create: `k8s/02-redis.yaml`
- Create: `k8s/03-worker.yaml`
- Create: `k8s/04-server.yaml`
- Create: `k8s/05-server-hpa.yaml`
- Create: `k8s/06-traefik-rbac-crds.yaml`
- Create: `k8s/07-traefik-deployment.yaml`
- Create: `k8s/08-traefik-routes.yaml`
- Create: `scripts/k8s-build.sh`
- Create: `scripts/k8s-deploy.sh`
- Create: `scripts/k8s-teardown.sh`

**Interfaces:**
- Produces:
  - Namespace `trading-system` with `ConfigMap` (`REDIS_CONNECTION`, `WORKER_URL`) and `Secret` (`POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `ConnectionStrings__TimescaleDb`).
  - Capped CPU & RAM `requests`/`limits` on every container:
    - `worker`: `250m/256Mi` req, `1000m/512Mi` limit.
    - `server`: `200m/256Mi` req, `500m/512Mi` limit (`HPA`: `minReplicas: 1`, `maxReplicas: 10`, target CPU `70%`).
    - `redis`: `100m/128Mi` req, `500m/256Mi` limit.
    - `timescaledb`: `250m/512Mi` req, `1000m/1Gi` limit (mounts `01-init.sql` via `ConfigMap` into `/docker-entrypoint-initdb.d/`).
    - `traefik`: `100m/128Mi` req, `500m/256Mi` limit.
  - Traefik v3 `IngressRoute` + `TraefikService` (`k8s/08-traefik-routes.yaml`):
    - Route `PathPrefix('/api/ingest')` -> `worker` Service (`port 8080`).
    - Route `PathPrefix('/')` -> `TraefikService` pointing to `server` Service (`port 8080`) with `strategy: p2c` (Traefik v3 power-of-two-choices least-connections) and `sticky.cookie.name: trading_session`, `httpOnly: true`.
  - `scripts/k8s-build.sh`: builds `trading-worker:local` and `trading-server:local` directly inside Minikube Docker daemon (`eval $(minikube docker-env)`).
  - `scripts/k8s-deploy.sh`: enables `metrics-server`, applies `k8s/*.yaml`, waits for rollouts, prints `localhost` access command (`minikube service` / `kubectl port-forward svc/traefik 8080:80`).

- [ ] **Step 1: Author `k8s/00-namespace-config.yaml` through `k8s/08-traefik-routes.yaml`**
  Verify all manifests include `imagePullPolicy: IfNotPresent` for local images, readiness/liveness probes on `/health`, and explicit CPU/RAM limits.

- [ ] **Step 2: Author `scripts/k8s-build.sh`, `scripts/k8s-deploy.sh`, `scripts/k8s-teardown.sh` (`chmod +x`)**

- [ ] **Step 3: Validate YAML syntax with `kubectl apply --dry-run=client`**
  Run: `kubectl apply --dry-run=client -f k8s/00-namespace-config.yaml -f k8s/01-timescaledb.yaml -f k8s/02-redis.yaml -f k8s/03-worker.yaml -f k8s/04-server.yaml -f k8s/05-server-hpa.yaml`
  Expected: manifests parse cleanly (no syntax/schema errors).

- [ ] **Step 4: Commit**
  ```bash
  git add k8s scripts/k8s-*.sh
  git commit -m "feat: add kubernetes manifests, traefik sticky crds, hpa, and minikube scripts"
  ```

---

## Phase 4: C++20 Tick Simulator

### Task 7: C++20 Simulator Core (Multi-Day Traversal, Raw CSV->JSON, Pacing & Unit Tests)

**Files:**
- Create: `simulator/CMakeLists.txt`
- Create: `simulator/src/csv_json.hpp`
- Create: `simulator/src/day_discovery.hpp`
- Create: `simulator/src/timestamp_pacer.hpp`
- Test: `simulator/tests/simulator_tests.cpp`

**Interfaces:**
- Produces:
  - `discover_trading_days(const std::string& data_dir, const std::string& single_day, const std::string& start_day)` -> chronologically sorted `std::vector<std::string>` of `.csv` file paths (ignores `:Zone.Identifier` and non-CSV files). Default: all days from first day (`08-11-21`) through last (`14-11-21`).
  - `csv_row_to_json(const std::vector<std::string>& headers, std::string_view line)` -> raw JSON object string mapping every CSV column to its uncleaned string value (proper JSON escaping for quotes/backslashes) + extracts raw `ID` string for sharding without modifying payload.
  - `TimestampPacer`: parses `Trading date`/`Date` (`dd-MM-yyyy`) and `Trading time`/`Time` (`HH:mm:ss.fff`), handles `::`/empty/corrupted values as 0 delta, caps inter-event delay (e.g., `2000ms` max across overnight/day transitions), computes scaled sleep duration for `--speed <N>` or `0` for `--speed 0`.

- [ ] **Step 1: Write `simulator/CMakeLists.txt` (C++20, `enable_testing()`) and failing `simulator/tests/simulator_tests.cpp`**
  Test:
  - `test_discover_trading_days_chronological_and_filters_zone_identifier`
  - `test_csv_row_to_json_preserves_raw_values_and_escapes_quotes`
  - `test_timestamp_pacer_handles_colons_and_caps_day_transition_gap`

- [ ] **Step 2: Build & run tests to verify failure**
  Run: `cmake -S simulator -B simulator/build && cmake --build simulator/build`
  Expected: FAIL (headers not implemented).

- [ ] **Step 3: Implement `csv_json.hpp`, `day_discovery.hpp`, `timestamp_pacer.hpp`**

- [ ] **Step 4: Build & run CTest to verify pass**
  Run: `cmake -S simulator -B simulator/build && cmake --build simulator/build && ctest --test-dir simulator/build --output-on-failure`
  Expected: 100% tests passed.

- [ ] **Step 5: Commit**
  ```bash
  git add simulator/CMakeLists.txt simulator/src simulator/tests
  git commit -m "feat: add c++20 simulator core with multi-day discovery, raw json, and pacing"
  ```

---

### Task 8: C++20 Sharded HTTP/1.1 Keep-Alive Sender, CLI (`--probe`), and `.sh` Launchers

**Files:**
- Create: `simulator/src/http_sender.hpp`
- Create: `simulator/src/main.cpp`
- Modify: `simulator/CMakeLists.txt` (link `libcurl` if available or POSIX TCP keep-alive socket sender + `Threads::Threads`)
- Create: `scripts/run-sim.sh`
- Create: `scripts/probe-speed.sh`

**Interfaces:**
- Consumes: `csv_json.hpp`, `day_discovery.hpp`, `timestamp_pacer.hpp`.
- Produces CLI binary `simulator/build/tick_simulator`:
  - Flags: `--data-dir <dir>` (default `./data`), `--url <url>` (default `http://localhost:8080/api/ingest`), `--speed <double>` (default `1.0`; `0` = max speed), `--concurrency <int>` (default `4`), `--day <dd-mm-yy>`, `--file <path>`, `--start-day <dd-mm-yy>`, `--probe` (blasts `--speed 0` over `--probe-ticks <N>` per day, default `20000`, prints per-day req/s & `sim_time/wall_time` speedup and overall minimum sustained speedup).
  - Shards rows across `concurrency` worker threads by `std::hash<std::string>{}(symbol_id) % concurrency`; each thread owns a bounded queue and persistent HTTP/1.1 keep-alive connection.
  - Traverses files chronologically; on EOF immediately advances to next day's file and re-parses headers.
  - Prints live throughput stats every 2 seconds (current file, ticks sent, req/s, HTTP errors, effective speedup).

- [ ] **Step 1: Implement `simulator/src/http_sender.hpp` (persistent HTTP/1.1 keep-alive POSIX socket or `libcurl` easy handle with auto-reconnect) and `simulator/src/main.cpp`**
  Check whether `libcurl` dev headers exist; if not, implement clean POSIX TCP keep-alive HTTP/1.1 `POST` client so zero extra apt installs are required.

- [ ] **Step 2: Build simulator and re-run CTest**
  Run: `cmake -S simulator -B simulator/build -DCMAKE_BUILD_TYPE=Release && cmake --build simulator/build && ctest --test-dir simulator/build --output-on-failure`
  Expected: `tick_simulator` compiles cleanly with `-O3` and all CTest cases pass.

- [ ] **Step 3: Create `scripts/run-sim.sh` and `scripts/probe-speed.sh` (`chmod +x`)**
  Auto-builds `simulator/build/tick_simulator` if missing, passes through speed/concurrency/CLI overrides.

- [ ] **Step 4: Commit**
  ```bash
  git add simulator scripts/run-sim.sh scripts/probe-speed.sh
  git commit -m "feat: add c++20 sharded keep-alive simulator cli, probe mode, and launcher scripts"
  ```

---

## Phase 5: Grafana k6 Load Testing & End-to-End Verification

### Task 9: Stock k6 Load Tests & Full Cluster E2E Verification

**Files:**
- Create: `loadtests/user-simulation.js`
- Create: `scripts/run-k6.sh`

**Interfaces:**
- Consumes: Traefik `localhost` endpoint (`http://localhost:8080`), Server endpoints (`/api/symbols/search`, `/api/subscription/set-state`, `/api/history/{symbol}`, `/api/sse/stream`).
- Produces:
  - `loadtests/user-simulation.js`: stock `k6` script with per-VU cookie jar (`http.cookieJar()`), ramping stages (configurable via `VUS` / `DURATION` env vars), searching symbols, setting 1 active + 3 tracked stocks, fetching history, and executing bounded-timeout SSE reads (`timeout: '10s'`), asserting `trading_session` cookie persistence and HTTP 2xx rates.
  - `scripts/run-k6.sh`: quick-start wrapper accepting `[vus] [duration] [base_url]`.

- [ ] **Step 1: Author `loadtests/user-simulation.js` and `scripts/run-k6.sh` (`chmod +x`)**
  Follow `snake_case` JS conventions per `AGENTS.md`.

- [ ] **Step 2: Run full test suites (C# + C++ + Frontend)**
  Run:
  - `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test services/tests --no-restore`
  - `ctest --test-dir simulator/build --output-on-failure`
  - `npm --prefix frontend run build`
  Expected: 100% pass across all 3 stacks.

- [ ] **Step 3: Commit Phase 5**
  ```bash
  git add loadtests scripts/run-k6.sh
  git commit -m "feat: add stock k6 load testing suite and runner script"
  ```

- [ ] **Step 4: End-to-End Minikube Verification (when cluster is running)**
  1. Run `./scripts/k8s-build.sh && ./scripts/k8s-deploy.sh` and verify all pods in `trading-system` are `Running` with capped CPU/RAM (`kubectl describe pod`).
  2. Run `./scripts/probe-speed.sh` against Traefik `http://localhost:8080/api/ingest` and verify ticks flow into `trading_windows` and Redis.
  3. Run `./scripts/run-k6.sh 50 60s` and observe `kubectl get hpa -n trading-system` scaling `server` pods and Traefik `trading_session` sticky cookie routing.
  4. Trigger `DELETE /api/data` (or UI "Clear DB" button) and verify DB is empty, Worker EMA resets, and live UI charts clear.
