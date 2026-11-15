# Phase 4: Frontend & Real-Time Delivery (Svelte & SSE) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the 3-tier Server-Sent Events (SSE) backend streaming service and the interactive Svelte frontend dashboard with real-time stock tracking, EMA visualization, breakout alerts, and simulation controls.

**Architecture:** The C# ASP.NET Core backend manages client session subscriptions (`hidden`, `tracked`, `active`). A background SSE dispatcher pushes average prices every 10s for `tracked`, every 2s for `active`, and emits instant buy/sell advisories upon window finalization. The Svelte frontend connects via native `EventSource`, manages subscription states, and renders live cards and Chart.js line charts.

**Tech Stack:** C# ASP.NET Core, Svelte (Vite), Chart.js, Server-Sent Events (SSE), xUnit.

**Spec:** [specs/phase-4-frontend.md](file:///home/zak/aalto/scalable-systems/specs/phase-4-frontend.md)

## Global Constraints
- C# Conventions: Classes in `PascalCase`, Methods in `PascalCase`, Variables/Parameters in `camelCase`.
- JavaScript/Svelte Conventions: Methods in `snake_case`, Variables/Parameters in `snake_case`.
- No JSDoc or XML documentation comments.
- 3 Subscription Tiers:
  - `hidden`: No SSE data sent.
  - `tracked`: Price updates every 10s, immediate breakout alerts.
  - `active`: Price updates every 2s, streaming EMA points, immediate breakout alerts.
- Global constants isolated in `src/constants.js`.

## Review Focus
1. Disconnected SSE clients cleanly unsubscribe and release memory without hanging background tasks.
2. Symbol state transitions (`hidden` -> `tracked` -> `active` -> `tracked`) update ticker frequencies immediately.
3. Closing active modal notifies backend to revert symbol state to prevent wasteful 2s high-frequency streaming.
4. Chart.js destroys previous chart instances before re-instantiating to prevent canvas memory leaks.
5. Flashing buy (Green) and sell (Blue) indicators reset cleanly after animation timeout.

---

### Task 1: Backend SSE Service & Subscription Session Manager

**Files:**
- Create: `InitialAttempt/Services/ISseService.cs`
- Create: `InitialAttempt/Services/SseService.cs`
- Create: `InitialAttempt/Controllers/SseController.cs`
- Create: `InitialAttempt/Controllers/HistoryController.cs`
- Modify: `InitialAttempt/Program.cs`
- Create: `InitialAttempt.Tests/SseServiceTests.cs`

**Interfaces:**
- Produces: `ISseService`
  - `RegisterClient(string clientId, HttpResponse response, CancellationToken ct) -> Task`
  - `SetSubscriptionState(string clientId, string symbol, string state) -> void`
  - `GetClientSubscription(string clientId, string symbol) -> string`
  - `BroadcastAdvisory(FinalizedWindowRecord record) -> Task`
- Endpoints:
  - `GET /api/sse/stream?clientId={clientId}`
  - `POST /api/subscription/set-state` (body: `{ "clientId": string, "symbol": string, "state": string }`)
  - `GET /api/history/{symbol}?limit=100`

- [ ] **Step 1: Write failing unit test for SSE subscription state manager**
In `InitialAttempt.Tests/SseServiceTests.cs`:
Test adding a client, setting subscription states (`tracked`, `active`, `hidden`), and verifying state transitions.

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj --filter "SseService"`
Expected: FAIL (`SseService` does not exist).

- [ ] **Step 3: Implement `SseService.cs`**
- Manages `ConcurrentDictionary<string, ClientSession>` where `ClientSession` holds symbol states (`hidden`, `tracked`, `active`) and response stream writer.
- Background ticker loop: pushes 2s ticks for `active` symbols and 10s ticks for `tracked` symbols.
- Subscribes to `IWindowStreamConsumer.OnWindowFinalized` to push immediate `advisory_alert` and `active_update` events.

- [ ] **Step 4: Implement `SseController.cs` and `HistoryController.cs`**
- `SseController`: `GET /api/sse/stream` sets `Content-Type: text/event-stream`, keeps connection open with `TaskCompletionSource` or long-running loop.
- `HistoryController`: `GET /api/history/{symbol}` queries TimescaleDB `trading_windows` ordered by `window_start ASC`.

- [ ] **Step 5: Run tests to verify they pass**
Run: `dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj`
Expected: ALL PASS.

- [ ] **Step 6: Commit**
```bash
git add InitialAttempt/ InitialAttempt.Tests/
git commit -m "feat(sse): implement 3-tier SSE subscription service, controllers, and tests"
```

---

### Task 2: Svelte Application Scaffolding & SSE Client Service

**Files:**
- Create: `frontend/package.json`
- Create: `frontend/vite.config.js`
- Create: `frontend/index.html`
- Create: `frontend/src/constants.js`
- Create: `frontend/src/lib/sse_service.js`
- Create: `frontend/src/App.svelte`

**Interfaces:**
- Produces: `sse_service.js`:
  - `connect_sse(on_message_callback) -> EventSource`
  - `set_symbol_state(client_id, symbol, state) -> Promise<void>`
  - `fetch_history(symbol, limit) -> Promise<Array>`
  - `set_simulation_speed(speed_factor) -> Promise<void>`
- Produces: `src/constants.js`: API endpoints, colors, poll intervals.

- [ ] **Step 1: Scaffold Svelte project in `frontend/`**
Install Svelte with Vite, Chart.js, and configure port 3000.

- [ ] **Step 2: Create `frontend/src/constants.js`**
Define `API_BASE_URL`, `SSE_STREAM_URL`, `SUBSCRIPTION_STATES`, `CHART_COLORS`, `REFRESH_INTERVALS`.

- [ ] **Step 3: Implement `frontend/src/lib/sse_service.js`**
Write functions using `snake_case` naming conventions for SSE connectivity, state updates, and simulation controls.

- [ ] **Step 4: Verify frontend builds without errors**
Run: `cd frontend && npm run build` (or `npx vite build`)
Expected: Build succeeded.

- [ ] **Step 5: Commit**
```bash
git add frontend/
git commit -m "feat(frontend): scaffold Svelte application with constants and SSE service"
```

---

### Task 3: Interactive Dashboard UI & Real-Time Charting

**Files:**
- Create: `frontend/src/components/SearchBar.svelte`
- Create: `frontend/src/components/SimulatorControls.svelte`
- Create: `frontend/src/components/TrackedList.svelte`
- Create: `frontend/src/components/ActiveChartModal.svelte`
- Modify: `frontend/src/App.svelte`

**Interfaces:**
- Produces: Complete reactive UI with search, tracked cards, flashing breakout badges, Chart.js line charts, and playback speed controls.

- [ ] **Step 1: Implement `SearchBar.svelte` and `SimulatorControls.svelte`**
- `SearchBar.svelte`: Autocomplete / input for symbol ID with button to track.
- `SimulatorControls.svelte`: Buttons for Play, Pause, Resume, and Speed buttons (1x, 5x, 10x, Max).

- [ ] **Step 2: Implement `TrackedList.svelte`**
- Displays cards for all tracked symbols.
- Updates live price on SSE ticks.
- Flashing **Green** badge on Buy advisory, flashing **Blue** badge on Sell advisory.
- Button to open `active` chart modal or remove from tracking.

- [ ] **Step 3: Implement `ActiveChartModal.svelte`**
- On open: sets symbol state to `active`, fetches historical records from `GET /api/history/{symbol}`, renders Chart.js canvas with 3 series ($EMA^{38}$, $EMA^{100}$, Price).
- Appends live incoming SSE points every 2 seconds.
- On close: sets symbol state back to `tracked`, destroys Chart.js instance.

- [ ] **Step 4: Assemble in `App.svelte` and verify build**
Run: `cd frontend && npm run build`
Expected: Production build passes with 0 errors.

- [ ] **Step 5: Commit**
```bash
git add frontend/
git commit -m "feat(frontend): implement dashboard UI, tracked cards, active chart modal, and simulator controls"
```
