# Phase 3: Backend Server & Stream Processing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement 5-minute tumbling window management, EMA mathematical indicators ($j \in \{38, 100\}$), breakout pattern detection (bullish/bearish crossovers), stream consumption from the simulation engine, and batched persistence to TimescaleDB.

**Architecture:** A streaming consumer reads ticks from `ISimulationEngine.GetEventChannel()`. A `WindowProcessor` partitions events per symbol ID, buckets timestamps into 5-minute tumbling windows from `00:00:00 CEST`, and finalizes windows when $w_{i+1}$ arrives. An `EmaCalculator` evaluates EMAs and tests for golden/death crossovers. Finalized window entries are queued into a `Channel<FinalizedWindowRecord>` and batch written to TimescaleDB via `DbBatchWriter`.

**Tech Stack:** C# .NET 10, ASP.NET Core, `System.Threading.Channels`, Npgsql, xUnit.

**Spec:** [specs/phase-3-backend-processing.md](file:///home/zak/aalto/scalable-systems/specs/phase-3-backend-processing.md)

## Global Constraints
- C# Naming Conventions (per AGENTS.md): Classes in `PascalCase`, Methods in `PascalCase`, Variables/Parameters in `camelCase`.
- No JSDoc or XML documentation comments.
- Test coverage focused on logic-heavy components: 5-minute windowing, EMA math precision, and crossover edge cases.
- Database writes: Only finalized 5-minute window records persisted (raw ticks never written to DB).

## Review Focus
1. Ticks arriving out-of-order within the same window update the last observed price without prematurely finalizing the window.
2. The very first window evaluation ($EMA_{w_0} = 0$) handles initial crossover check properly without false positive crossovers.
3. Multi-interval jumps (e.g. gap of multiple 5-minute windows with no ticks) handle transition cleanly.
4. Database batch flush handles connection retries without losing queued window records.
5. In-memory symbol state is thread-safe under concurrent stream processing.

---

### Task 1: Core Mathematical Engine & Tumbling Window Logic (with Tests)

**Files:**
- Create: `InitialAttempt/Services/EmaCalculator.cs`
- Create: `InitialAttempt/Services/WindowProcessor.cs`
- Create: `InitialAttempt/Models/FinalizedWindowRecord.cs`
- Create: `InitialAttempt.Tests/EmaCalculatorTests.cs`
- Create: `InitialAttempt.Tests/WindowProcessorTests.cs`

**Interfaces:**
- Produces: `FinalizedWindowRecord(string SymbolId, DateTime WindowStart, DateTime WindowEnd, double Ema38, double Ema100, double LastPrice, bool BuySignal, bool SellSignal)`
- Produces: `EmaCalculator.CalculateEma(double closePrice, double previousEma, int period) -> double`
- Produces: `WindowProcessor.ProcessTick(MinimalTradingEntry tick) -> FinalizedWindowRecord?`

- [ ] **Step 1: Write failing unit tests for EMA math & crossover detection**
In `InitialAttempt.Tests/EmaCalculatorTests.cs`:
Test exact analytical values for $j=38$ ($\alpha \approx 2/39$) and $j=100$ ($\alpha \approx 2/101$), and bullish/bearish crossover evaluation.

- [ ] **Step 2: Write failing unit tests for Tumbling Window finalization**
In `InitialAttempt.Tests/WindowProcessorTests.cs`:
Test that events in window 0 do not emit a record until an event in window 1 arrives; test closing price accuracy.

- [ ] **Step 3: Run tests to verify they fail**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj --no-restore`
Expected: FAIL (`EmaCalculator` and `WindowProcessor` not found).

- [ ] **Step 4: Implement `EmaCalculator.cs` and `WindowProcessor.cs`**
- Strict 5-minute boundary calculation: `WindowStart = date.ToDateTime(time).AddMinutes(-(time.Minute % 5)).AddSeconds(-time.Second).AddMilliseconds(-time.Millisecond)`.
- Finalization occurs only when event belonging to next window arrives.
- Recurrence: $EMA_{w_i}^j = [Close \times \frac{2}{1+j}] + [EMA_{w_{i-1}}^j \times (1 - \frac{2}{1+j})]$.
- Initial $EMA_{w_0} = 0$.

- [ ] **Step 5: Run tests to verify they pass**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj --no-restore`
Expected: ALL PASS.

- [ ] **Step 6: Commit**
```bash
git add InitialAttempt/ InitialAttempt.Tests/
git commit -m "feat(processing): implement tumbling window manager and EMA crossover engine with unit tests"
```

---

### Task 2: Stream Consumer Background Service

**Files:**
- Create: `InitialAttempt/Services/WindowStreamConsumer.cs`
- Create: `InitialAttempt/Services/IWindowStreamConsumer.cs`
- Modify: `InitialAttempt/Program.cs`
- Create: `InitialAttempt.Tests/WindowStreamConsumerTests.cs`

**Interfaces:**
- Consumes: `ISimulationEngine.GetEventChannel()`
- Produces: `IWindowStreamConsumer.GetFinalizedChannel() -> ChannelReader<FinalizedWindowRecord>`
- Produces: `event Action<FinalizedWindowRecord>? OnWindowFinalized`

- [ ] **Step 1: Write unit test for stream consumer**
In `InitialAttempt.Tests/WindowStreamConsumerTests.cs`:
Feed sample tick entries through channel and verify finalized records are emitted.

- [ ] **Step 2: Run test to verify it fails**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj --filter "WindowStreamConsumer"`
Expected: FAIL (`WindowStreamConsumer` not found).

- [ ] **Step 3: Implement `WindowStreamConsumer`**
- Hosted `BackgroundService` consuming ticks from `ISimulationEngine`.
- Passes ticks to `WindowProcessor`.
- Pushes finalized window records into `Channel<FinalizedWindowRecord>`.

- [ ] **Step 4: Run tests to verify they pass**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj --no-restore`
Expected: ALL PASS.

- [ ] **Step 5: Commit**
```bash
git add InitialAttempt/ InitialAttempt.Tests/
git commit -m "feat(processing): implement background stream consumer for simulation channel"
```

---

### Task 3: Database Batch Writer to TimescaleDB

**Files:**
- Create: `InitialAttempt/Services/DbBatchWriter.cs`
- Create: `InitialAttempt/Services/IDbBatchWriter.cs`
- Modify: `InitialAttempt/Program.cs`

**Interfaces:**
- Consumes: `IWindowStreamConsumer.GetFinalizedChannel()`
- Produces: Persistent writes to TimescaleDB table `trading_windows`.

- [ ] **Step 1: Add Npgsql dependency**
```bash
dotnet add InitialAttempt/InitialAttempt.csproj package Npgsql
```

- [ ] **Step 2: Implement `DbBatchWriter`**
- Background worker buffering finalized window records up to `DbBatchMaxCapacity` (1000) or timeout (5 seconds).
- Batch inserts using parameterized `INSERT INTO trading_windows (...) VALUES (...) ON CONFLICT (symbol_id, window_start) DO NOTHING;` or PostgreSQL `COPY`.
- Gracefully flushes remaining buffer on application shutdown.

- [ ] **Step 3: Register in `Program.cs` & verify build and tests**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj`
Expected: ALL PASS.

- [ ] **Step 4: Commit**
```bash
git add InitialAttempt/ InitialAttempt.Tests/
git commit -m "feat(database): implement TimescaleDB batch writer service with connection pooling"
```
