# Phase 5: Constants & Configuration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Centralize all magic numbers, intervals, algorithmic factors, environment variables, and endpoints across the C# backend and Svelte frontend into dedicated configuration files.

**Architecture:** Expand `Constants.cs` in `InitialAttempt/Utils/` to hold all windowing, EMA, batching, and simulator parameters. Refactor backend services (`WindowProcessor`, `DbBatchWriter`, `SimulationEngine`, `SseService`) to reference these constants. Expand `frontend/src/constants.js` to include polling/streaming intervals. Harmonize `.env` and `.env.example` across Docker and local configurations.

**Tech Stack:** C# .NET 10, ASP.NET Core, xUnit, Svelte 4, Vite, Docker Compose

**Spec:** `specs/phase-5-constants-config.md`

## Global Constraints

- C# Naming Conventions: Classes in `PascalCase`, Methods in `PascalCase`, Variables/Parameters in `camelCase`.
- JS/Svelte Naming Conventions: Methods and Variables/Parameters in `snake_case`.
- No Over-Commenting: Do NOT add comments or documentation into the code (no JSDoc, no XML docs). Only minimal inline comments for complex mathematical logic.
- Configuration Management: Secrets and environment variables MUST go into `.env`. Reusable constants MUST go into `Constants.cs` (backend) and `constants.js` (frontend).
- Zero Magic Numbers: No raw numerical literals for window sizes, periods, alphas, buffer capacities, or intervals in business logic.

## Review Focus

1. Mathematical precision: `EmaShortAlpha` and `EmaLongAlpha` must equal $2.0 / (1.0 + N)$ precisely (`double`).
2. Behavioral equivalence: Window processor minute bucketing `(time.Minute / 5) * 5` and window end `AddMinutes(5)` must derive from `Constants.WindowDurationMinutes` or `Constants.WindowSizeMs`.
3. Configuration fallback: Missing environment variables must fall back to deterministic defaults without crashing.
4. Channel/Buffer bounds: `DbBatchWriter` and `SimulationEngine` must use `Constants` for queue limits and flush intervals.
5. Frontend consistency: Streaming and poll intervals in `constants.js` must align with backend emission rates (2000ms active, 10000ms tracked).

---

### Task 1: Environment Variables Standardization

**Files:**
- Modify: `.env.example`
- Modify: `.env`

- [ ] **Step 1:** Update `.env.example` to declare all variables defined in Section 2 of spec:
  - `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD`
  - `BACKEND_PORT`, `ASPNETCORE_ENVIRONMENT`
  - `DATA_DIRECTORY`, `CSV_PREFIX`, `CSV_SUFFIX`
  - `FRONTEND_PORT`, `VITE_API_BASE_URL`
- [ ] **Step 2:** Update `.env` to match `.env.example` schema with working local development defaults (`trading_db`, `postgres_password`, `./InitialAttempt/Data`).
- [ ] **Step 3:** Commit environment file updates.

---

### Task 2: Backend `Constants.cs` Expansion & Math Unit Tests

**Files:**
- Modify: `InitialAttempt/Utils/Constants.cs`
- Create: `InitialAttempt.Tests/ConstantsTests.cs`

- [ ] **Step 1: Write the failing tests in `ConstantsTests.cs`:**
  - Verify `Constants.WindowSizeMs == 300000` (5 minutes).
  - Verify `Constants.WindowDurationMinutes == 5`.
  - Verify `Constants.WindowStartHour == 0`.
  - Verify `Constants.EmaShortPeriod == 38` and `Constants.EmaLongPeriod == 100`.
  - Verify `Constants.EmaShortAlpha == 2.0 / (1.0 + 38.0)`.
  - Verify `Constants.EmaLongAlpha == 2.0 / (1.0 + 100.0)`.
  - Verify `Constants.TrackedPushIntervalMs == 10000` and `Constants.ActivePushIntervalMs == 2000`.
  - Verify `Constants.DbBatchFlushIntervalMs == 5000` and `Constants.DbBatchMaxCapacity == 1000`.
- [ ] **Step 2: Run test to confirm failure:**
  - `dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj`
- [ ] **Step 3: Update `InitialAttempt/Utils/Constants.cs`:**
  - Add public static constants or properties matching the specification and test assertions.
  - Retain `IConstants` interface and environment-backed properties (`CSV_PREFIX`, `CSV_SUFFIX`, `CSV_DIRECTORY`).
- [ ] **Step 4: Run test to confirm pass:**
  - `dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj`
- [ ] **Step 5: Commit `Constants.cs` and `ConstantsTests.cs`.**

---

### Task 3: Refactor Backend Services to Use `Constants`

**Files:**
- Modify: `InitialAttempt/Services/WindowProcessor.cs`
- Modify: `InitialAttempt/Services/DbBatchWriter.cs`
- Modify: `InitialAttempt/Services/SimulationEngine.cs`
- Modify: `InitialAttempt/Services/EmaCalculator.cs`
- Modify: `InitialAttempt/Services/SseService.cs`

- [ ] **Step 1:** In `WindowProcessor.cs`, replace literal `5` with `Constants.WindowDurationMinutes`, and replace `38` and `100` with `Constants.EmaShortPeriod` and `Constants.EmaLongPeriod`.
- [ ] **Step 2:** In `DbBatchWriter.cs`, replace literal `1000` with `Constants.DbBatchMaxCapacity`, `5000` with `Constants.DbBatchMaxBufferSize`, and `TimeSpan.FromSeconds(5)` with `TimeSpan.FromMilliseconds(Constants.DbBatchFlushIntervalMs)`.
- [ ] **Step 3:** In `SimulationEngine.cs`, replace literal `1.0` with `Constants.DefaultSimulationSpeed` and `100000` with `Constants.SimulationChannelCapacity`.
- [ ] **Step 4:** In `EmaCalculator.cs`, provide overload or default period bindings utilizing `Constants.EmaShortPeriod` and `Constants.EmaLongPeriod`.
- [ ] **Step 5:** Run full backend test suite:
  - `dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj` (all 20+ tests pass).
- [ ] **Step 6: Commit backend service refactoring.**

---

### Task 4: Frontend `constants.js` Expansion & Integration

**Files:**
- Modify: `frontend/src/constants.js`
- Modify: `frontend/src/components/ActiveChartModal.svelte`
- Modify: `frontend/src/components/TrackedList.svelte`
- Modify: `frontend/src/App.svelte`

- [ ] **Step 1:** Expand `frontend/src/constants.js`:
  - Add `tracked_poll_interval_ms: 10000`
  - Add `active_chart_update_interval_ms: 2000`
  - Add `default_simulation_speed: 1.0`
- [ ] **Step 2:** Ensure components reference these constants for any timer or interval intervals.
- [ ] **Step 3:** Verify frontend build:
  - `npm --prefix frontend run build` (clean, 0 warnings/errors).
- [ ] **Step 4: Commit frontend constants updates.**

---

### Task 5: End-to-End Build & Test Verification

**Files:**
- Verify: Full repository build and tests

- [ ] **Step 1:** Run full backend unit test suite:
  - `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp dotnet test --no-restore InitialAttempt.Tests/InitialAttempt.Tests.csproj`
- [ ] **Step 2:** Run frontend production build:
  - `npm --prefix frontend run build`
- [ ] **Step 3:** Verify no hardcoded secrets or arbitrary literals remain in modified code.
- [ ] **Step 4: Ready for final review.**
