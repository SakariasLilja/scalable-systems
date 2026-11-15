# Review Fixes & Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Address code review findings across Phases 1-5, fixing simulation blocking, channel lifecycle, script robustness, naming conventions, and frontend reconnection, and update documentation.

**Architecture:** Decouple simulator playback loop execution from `StartSimulation` task returned to HTTP controller, validate speed and subscription parameters, rename mismatched model file, update `IConstants` to PascalCase, ensure safe SSE reconnect behavior in frontend, and synchronize `docs/SYSTEM_GUIDE.md`.

**Tech Stack:** C# .NET 10, ASP.NET Core, xUnit, Svelte 4, Vite, Docker Compose, Bash

**Spec:** `code_review_comments.md` and design discussed in brainstorming.

## Global Constraints

- C# Naming Conventions: Classes in `PascalCase`, Methods in `PascalCase`, Variables/Parameters in `camelCase`.
- JS/Svelte Naming Conventions: Methods and Variables/Parameters in `snake_case`.
- No Over-Commenting: Do NOT add comments or documentation into code (no JSDoc, no XML docs).
- Zero Magic Numbers: Extract newly introduced defaults to `Constants.cs` / `constants.js`.
- All tests must pass cleanly (`dotnet test` and `npm run build`).

## Review Focus

1. Non-blocking simulation start: `POST /api/simulator/start` must return HTTP 202 immediately while playback continues in background.
2. Channel lifecycle: `SimulationEngine` must complete or reset channel on playback stop/restart without hanging consumers.
3. Subscription validation: `SetSubscriptionState` must reject or ignore invalid state strings outside `hidden`, `tracked`, `active`.
4. File naming alignment: `MinimalTradeEntry.cs` must match `MinimalTradingEntry` class name.
5. Reconnect resilience: EventSource reconnect must not lose subscribed symbols.

---

### Task 1: Simulation Engine & Controller Asynchronous Decoupling

**Files:**
- Modify: `InitialAttempt/Services/SimulationEngine.cs`
- Modify: `InitialAttempt/Controllers/SimulatorController.cs`
- Modify: `InitialAttempt.Tests/SimulationEngineTests.cs`

- [ ] **Step 1:** In `SimulationEngine.cs`, decouple playback execution:
  - In `StartSimulation`: validate and set state to `Running`, spawn background task without returning the un-awaited playback loop task. Return `Task.CompletedTask`.
  - In `RunPlaybackLoop`: on completion or cancellation, call `channel.Writer.TryComplete()`.
  - When starting a new simulation after stopped, ensure channel is reset/recreated if completed.
- [ ] **Step 2:** In `SimulatorController.cs`:
  - In `Start`: use `constants.DefaultSimulationSpeed` fallback instead of literal `1.0`. Use `Constants.DefaultTradingDate`.
  - In `SetSpeed`: add validation `if (request.SpeedFactor <= 0) return BadRequest("Speed factor must be positive.");`
- [ ] **Step 3:** In `SimulationEngine.cs`: clamp `(int)delayMs` with `Math.Min(delayMs, int.MaxValue)`.
- [ ] **Step 4:** Run tests to confirm pass:
  - `dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj`
- [ ] **Step 5:** Commit Task 1 changes.

---

### Task 2: Infrastructure & Script Resilience

**Files:**
- Modify: `docker-compose.yml`
- Modify: `scripts/verify-infra.sh`
- Modify: `initdb/01-init.sql`

- [ ] **Step 1:** In `docker-compose.yml`:
  - Replace idle `tail -f /dev/null` frontend command with live development server command `["sh", "-c", "npm install && npm run dev -- --host 0.0.0.0 --port 3000"]`.
  - Under `backend.environment`: change `POSTGRES_PORT: 5432` to `${POSTGRES_PORT:-5432}`.
- [ ] **Step 2:** In `scripts/verify-infra.sh`:
  - Replace fragile `export $(grep ... | xargs)` with `set -a; [ -f .env ] && . .env; set +a`.
  - Add cleanup query: `DELETE FROM trading_windows WHERE symbol_id = 'TEST.SYM';` after verification.
- [ ] **Step 3:** In `initdb/01-init.sql`:
  - Set `chunk_time_interval => INTERVAL '1 day'` in `create_hypertable`.
- [ ] **Step 4:** Commit Task 2 changes.

---

### Task 3: Backend Model File Renaming, Constants Polish & Subscription Validation

**Files:**
- Rename: `InitialAttempt/Models/MinimalTradeEntry.cs` -> `InitialAttempt/Models/MinimalTradingEntry.cs`
- Modify: `InitialAttempt/Utils/Constants.cs`
- Modify: `InitialAttempt/Services/SseService.cs`
- Modify: `InitialAttempt.Tests/ConstantsTests.cs`
- Modify: `InitialAttempt/Program.cs`
- Modify: `InitialAttempt/Controllers/SimulatorController.cs`

- [ ] **Step 1:** Rename `InitialAttempt/Models/MinimalTradeEntry.cs` to `InitialAttempt/Models/MinimalTradingEntry.cs`.
- [ ] **Step 2:** In `Constants.cs`:
  - Add `public const string DefaultTradingDate = "13-11-21";`
  - In `IConstants`: provide PascalCase properties `CsvPrefix`, `CsvSuffix`, `CsvDirectory` (and keep uppercase as aliases for compatibility).
  - In `Constants`: trim quotes and whitespace from `DATA_DIRECTORY` / `CSV_DIRECTORY`.
- [ ] **Step 3:** In `SseService.cs`:
  - In `SetSubscriptionState`: validate input state against `Constants.SubscriptionHidden`, `Constants.SubscriptionTracked`, `Constants.SubscriptionActive`. If invalid, ignore or default to `Constants.SubscriptionHidden`.
- [ ] **Step 4:** In `ConstantsTests.cs`:
  - Update unit tests to assert `Constants.DefaultTradingDate` and `Constants.CsvPrefix`.
- [ ] **Step 5:** Run backend unit tests:
  - `dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj` (all pass).
- [ ] **Step 6:** Commit Task 3 changes.

---

### Task 4: Frontend Reconnect Resilience, Error Guarding & Alert Dismissal

**Files:**
- Modify: `frontend/src/lib/sse_service.js`
- Modify: `frontend/src/App.svelte`

- [ ] **Step 1:** In `frontend/src/lib/sse_service.js`:
  - In `set_symbol_state`: wrap fetch in `try/catch` block and return `{ success: false }` on network error.
  - In `connect_sse`: add `on_connected` or `onopen` callback support to allow re-registering tracked symbols when connection is re-established.
- [ ] **Step 2:** In `frontend/src/App.svelte`:
  - Maintain a dictionary of dismiss timeout IDs `alert_timers = {}` so rapid successive alerts on a symbol reset previous timers without clipping badge visibility prematurely.
- [ ] **Step 3:** Verify frontend production build:
  - `npm --prefix frontend run build`
- [ ] **Step 4:** Commit Task 4 changes.

---

### Task 5: Documentation Synchronization & Cleanup

**Files:**
- Modify: `docs/SYSTEM_GUIDE.md`
- Delete: `code_review_comments.md`

- [ ] **Step 1:** Update `docs/SYSTEM_GUIDE.md` with:
  - New constants (`DefaultTradingDate`, PascalCase property names).
  - Updated Docker Compose frontend run command.
  - Subscription validation rules.
- [ ] **Step 2:** Remove temporary review file `code_review_comments.md`.
- [ ] **Step 3:** Commit Task 5 changes.

---

### Task 6: Full Verification

**Files:**
- All project files

- [ ] **Step 1:** Run full test suite:
  - `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test --no-restore InitialAttempt.Tests/InitialAttempt.Tests.csproj`
- [ ] **Step 2:** Run frontend build:
  - `npm --prefix frontend run build`
- [ ] **Step 3:** Verify git status is clean.
