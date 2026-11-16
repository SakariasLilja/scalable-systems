# Resilient CSV Parsing and Simulator Error Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent CSV parsing crashes on DEBS placeholder time strings (`::`), safely skip malformed records with structured logging, and propagate simulation loop exceptions to `SimulationStatus` and the frontend UI error banner.

**Architecture:** `ReadCsvService` configures `CsvReaderOptions.NullValues = ["", "::"]` and an `OnRecordError` callback with injected `ILogger<ReadCsvService>` to skip corrupt records without halting the stream. `SimulationEngine` injects `ILogger<SimulationEngine>`, catches unhandled exceptions in `RunPlaybackLoop`, records `lastError`, and exposes it via `SimulationStatus(..., string? LastError = null)`. `SimulatorControls.svelte` displays a visible error banner whenever `lastError` is present.

**Tech Stack:** C# 12 / .NET 8 (ASP.NET Core Web API), CsvHelper, xUnit, Svelte 4.

**Spec:**
1. Simulation must process through DEBS placeholder rows (`::`) and malformed lines in `debs2022-gc-trading-day-08-11-21.csv` past tick #5170 without crashing.
2. Any malformed CSV rows must log structured warnings with row numbers and raw content.
3. If the playback loop fails unexpectedly, the error must be logged with stack trace and exposed to `GET /api/simulator/status`.
4. The frontend UI must present a visible error banner when `lastError` is reported.

## Global Constraints
- C# naming: Classes/records `PascalCase`, methods `PascalCase`, variables/parameters `camelCase`.
- JS/Svelte naming: Functions/methods `snake_case`, variables/parameters `snake_case`.
- Zero magic numbers: Centralize any new constants in `Constants.cs` / `constants.js`.
- No over-commenting: Do NOT add XML or JSDoc comments.
- TDD required: Write failing unit test, verify red, write minimal code, verify green, commit.

## Review Focus
1. `ReadCsvService` parsing rows with `::` in `Time`: parses as null, stream continues without exception.
2. `ReadCsvService` with malformed column values: `OnRecordError` logs warning and returns `true`, skipping corrupted row.
3. `SimulationEngine` when reader throws unhandled exception: logs error with `ILogger`, stores error message in `lastError`, sets state to `Stopped`.
4. `SimulationEngine.StartSimulation`: resets `lastError` to null on new run.
5. `SimulatorControls.svelte`: displays dismissible red error banner when `sim_status.lastError` is present.

---

### Task 1: Resilient CSV Parsing in `ReadCsvService`

**Files:**
- Modify: `InitialAttempt/Services/ReadCsvService.cs:1-43`
- Modify: `InitialAttempt.Tests/CsvParserTests.cs`

**Interfaces:**
- Consumes: `CsvReaderOptions.NullValues`, `CsvReaderOptions.OnRecordError`, `ILogger<ReadCsvService>`
- Produces: `IReadCsvService.StreamTradeEvents` resilient to placeholder timestamps and corrupt rows

- [ ] **Step 1: Write failing test in `CsvParserTests.cs`**

```csharp
[Fact]
public void StreamTradeEvents_SkipsCorruptedRecordsAndParsesColonsAsNull()
{
    var csvContent = """
    # Comment
    ID,SecType,Date,Time,Ask,Ask volume,Bid,Bid volume,Ask time,Day's high ask,Close,Currency,Day's high ask time,Day's high,ISIN,Auction price,Day's low ask,Day's low,Day's low ask time,Open,Nominal value,Last,Last volume,Trading time,Total volume,Mid price,Trading date,Profit,Current price,Related indices,Day high bid time, Day low bid time,Open Time, Last trade time, Close Time, Day high Time, Day low Time,Bid time,Auction Time
    T1.SYM,E,08-11-2021,::,,,,,,,,,,,,,,,,,10.50,10,00:00:00.000,0,,08-11-2021,,,,,,,00:00:00.000,00:00:00.000,,,,,
    T2.CORRUPT,INVALID_SECTYPE,08-11-2021,05:10:04.000,,,,,,,,,,,,,,,,,NOT_A_DECIMAL,,,,,,,,,,,,,,,,,,,
    T3.SYM,E,08-11-2021,05:10:05.000,,,,,,,,,,,,,,,,,20.50,10,00:00:00.000,0,,08-11-2021,,,,,,,00:00:00.000,00:00:00.000,,,,,
    """;
    var tempFile = Path.GetTempFileName();
    File.WriteAllText(tempFile, csvContent);

    try
    {
        var service = new ReadCsvService(NullLogger<ReadCsvService>.Instance);
        var records = service.StreamTradeEvents(tempFile, CancellationToken.None).ToList();

        Assert.Equal(2, records.Count);
        Assert.Equal("T1.SYM", records[0].Id);
        Assert.Null(records[0].Time);
        Assert.Equal("T3.SYM", records[1].Id);
        Assert.Equal(20.50m, records[1].Last);
    }
    finally
    {
        File.Delete(tempFile);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~StreamTradeEvents_SkipsCorruptedRecordsAndParsesColonsAsNull"`
Expected: FAIL (compilation failure: `ReadCsvService` constructor does not take `ILogger`).

- [ ] **Step 3: Implement `ILogger` injection, `NullValues`, and `OnRecordError` in `ReadCsvService.cs`**

In `InitialAttempt/Services/ReadCsvService.cs`:
```csharp
using CsvHelper;
using InitialAttempt.Models;
using Microsoft.Extensions.Logging;

namespace InitialAttempt.Services;

public interface IReadCsvService
{
    IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken);
    IEnumerable<MinimalTradingEntry> StreamTradeEvents(string filePath, CancellationToken cancellationToken);
}

public class ReadCsvService(ILogger<ReadCsvService> logger) : IReadCsvService
{
    public IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken)
    {
        return StreamTradeEvents(filePath, cancellationToken);
    }

    public IEnumerable<MinimalTradingEntry> StreamTradeEvents(string filePath, CancellationToken cancellationToken)
    {
        var config = new CsvReaderOptions
        {
            HasHeader = true,
            Separator = ',',
            CommentPrefix = '#',
            NullValues = ["", "::"],
            OnRecordError = error =>
            {
                logger.LogWarning(error.Exception, "Malformed CSV record at row {RowNumber}. Skipping: {RawRecord}", error.RowNumber, error.RawRecord);
                return true;
            }
        };

        using var reader = CsvReader.FromFile(filePath, config);

        foreach (MinimalTradingEntry record in reader.GetRecords<MinimalTradingEntry>())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (record.Last.HasValue)
            {
                yield return record;
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~CsvParserTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add InitialAttempt/Services/ReadCsvService.cs InitialAttempt.Tests/CsvParserTests.cs
git commit -m "fix: make csv reader resilient to placeholder timestamps and malformed records"
```

---

### Task 2: Engine Error Handling, Structured Logging, and `LastError` in `SimulationStatus`

**Files:**
- Modify: `InitialAttempt/Services/ISimulationEngine.cs:13-19`
- Modify: `InitialAttempt/Services/SimulationEngine.cs:8-120, 170-190`
- Modify: `InitialAttempt.Tests/SimulationEngineTests.cs`
- Modify: `InitialAttempt.Tests/SimulatorControllerTests.cs`

**Interfaces:**
- Consumes: `SimulationStatus.LastError`, `ILogger<SimulationEngine>`
- Produces: `SimulationStatus` reporting `string? LastError`, exception logging in `RunPlaybackLoop`

- [ ] **Step 1: Write failing tests in `SimulationEngineTests.cs`**

```csharp
[Fact]
public async Task RunPlaybackLoop_WhenExceptionOccurs_LogsAndSetsLastError()
{
    var throwingService = new ThrowingCsvService();
    var engine = new SimulationEngine(throwingService, NullLogger<SimulationEngine>.Instance);

    await engine.StartSimulation("fake.csv", 1.0);

    // Wait until simulation stops
    for (int i = 0; i < 20 && engine.GetStatus().State == SimulationState.Running; i++)
    {
        await Task.Delay(20);
    }

    var status = engine.GetStatus();
    Assert.Equal(SimulationState.Stopped, status.State);
    Assert.NotNull(status.LastError);
    Assert.Contains("Test CSV failure", status.LastError);
}

private class ThrowingCsvService : IReadCsvService
{
    public IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken) => StreamTradeEvents(filePath, cancellationToken);
    public IEnumerable<MinimalTradingEntry> StreamTradeEvents(string filePath, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Test CSV failure");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~RunPlaybackLoop_WhenExceptionOccurs_LogsAndSetsLastError"`
Expected: FAIL (compilation failure: `SimulationEngine` constructor and `SimulationStatus.LastError`).

- [ ] **Step 3: Implement `LastError` in `SimulationStatus` and `SimulationEngine`**

1. In `ISimulationEngine.cs`:
   ```csharp
   public record SimulationStatus(
       SimulationState State,
       double SpeedFactor,
       long ProcessedCount,
       DateTime? CurrentSimulationTime,
       TimeSpan ElapsedWallTime,
       string? LastError = null
   );
   ```
2. In `SimulationEngine.cs`:
   - Inject `ILogger<SimulationEngine> logger`.
   - Add `private string? lastError = null;`.
   - In `StartSimulation`: set `this.lastError = null;`.
   - In `GetStatus`: pass `lastError` to `SimulationStatus`.
   - In `RunPlaybackLoop`:
     ```csharp
     catch (OperationCanceledException)
     {
     }
     catch (Exception ex)
     {
         logger.LogError(ex, "Simulation playback loop crashed unexpectedly after {ProcessedCount} ticks at simulation time {SimulationTime}", processedCount, currentSimulationTime);
         lock (stateLock)
         {
             lastError = ex.Message;
         }
     }
     finally
     {
         lock (stateLock)
         {
             state = SimulationState.Stopped;
             wallStopwatch.Stop();
         }
     }
     ```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~SimulationEngineTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add InitialAttempt/Services/ISimulationEngine.cs InitialAttempt/Services/SimulationEngine.cs InitialAttempt.Tests/SimulationEngineTests.cs InitialAttempt.Tests/SimulatorControllerTests.cs
git commit -m "feat: propagate simulation playback errors to status and structured logging"
```

---

### Task 3: Frontend UI Error Banner in `SimulatorControls.svelte`

**Files:**
- Modify: `frontend/src/components/SimulatorControls.svelte:10-35, 75-115`

**Interfaces:**
- Consumes: `sim_status.lastError` from `get_simulation_status()`
- Produces: Dismissible red error banner in UI

- [ ] **Step 1: Add error banner markup and state in `SimulatorControls.svelte`**

1. In `sim_status` state initialization:
   ```javascript
   let sim_status = {
     state: 0,
     speedFactor: default_simulation_speed,
     processedCount: 0,
     lastError: null
   };
   ```
2. In template above or inside `.controls-card`:
   ```svelte
   {#if sim_status.lastError}
     <div class="error-banner">
       <span class="error-message">Simulation Error: {sim_status.lastError}</span>
       <button class="error-dismiss-btn" on:click={() => (sim_status.lastError = null)}>Dismiss</button>
     </div>
   {/if}
   ```
3. In `<style>`:
   ```css
   .error-banner {
     background-color: rgba(239, 68, 68, 0.15);
     border: 1px solid #ef4444;
     color: #fca5a5;
     padding: 0.6rem 1rem;
     border-radius: 6px;
     margin-bottom: 1rem;
     display: flex;
     justify-content: space-between;
     align-items: center;
     font-size: 0.85rem;
   }
   .error-message {
     font-weight: 500;
   }
   .error-dismiss-btn {
     background: transparent;
     border: 1px solid #ef4444;
     color: #fca5a5;
     padding: 0.2rem 0.5rem;
     border-radius: 4px;
     cursor: pointer;
     font-size: 0.75rem;
   }
   .error-dismiss-btn:hover {
     background-color: #ef4444;
     color: white;
   }
   ```

- [ ] **Step 2: Verify build**

Run: `cd frontend && npm run build`
Expected: Build passes with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add frontend/src/components/SimulatorControls.svelte
git commit -m "feat: add simulation error banner to controls component"
```

---

### Task 4: Full System Verification on Real Dataset & Operations Guide Update

**Files:**
- Modify: `InitialAttempt.Tests/CsvParserTests.cs` (real data test past 6,000 ticks)
- Modify: `docs/SYSTEM_GUIDE.md` (untracked/ignored)

- [ ] **Step 1: Add and verify real dataset regression test past 6000 ticks**

Add test in `CsvParserTests.cs` reading 6,000 trade events from `debs2022-gc-trading-day-08-11-21.csv`.
Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~RealFile"`
Expected: PASS (processes past tick 5170 without error).

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test InitialAttempt.Tests --no-restore`
Expected: All unit tests pass.

- [ ] **Step 3: Run frontend production build**

Run: `cd frontend && npm run build`
Expected: Clean build.

- [ ] **Step 4: Update `docs/SYSTEM_GUIDE.md`**

Document CSV parser resilience (`NullValues = ["", "::"]`, `OnRecordError`), simulator error propagation, and UI error notification.
