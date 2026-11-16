# Fix Simulation Stall and Max Speed Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the simulation playback freeze at tick #3 caused by missing `Trading date` fallback jumping to `DateTime.UtcNow`, cap maximum inter-tick delay against overnight gaps, and enable 0.0 (Max speed) in the simulator controller.

**Architecture:** `MinimalTradingEntry` is extended to map DEBS 2022's system `Date` and `Time` columns (always populated) and provide `EffectiveDate` and `EffectiveTime`. `SimulationEngine` uses effective timestamps, falls back to `previousEventTime` instead of `DateTime.UtcNow` if dates are missing to prevent artificial 5-year delays, and caps `CalculateDelay` at `Constants.MaxSimulationDelayMs` (2,000 ms). `SimulatorController` is updated to allow `SpeedFactor >= 0` so `0.0` activates Max speed mode without throwing HTTP 400 Bad Request.

**Tech Stack:** C# 12 / .NET 8 (ASP.NET Core Web API), CsvHelper, xUnit.

**Spec:**
1. Simulation must continuously process ticks from DEBS dataset without stalling at tick #3.
2. Selecting "Max" speed in UI (factor 0.0) must succeed on backend and run playback with zero inter-tick delay.
3. Overnight, weekend, or missing timestamp gaps must never block the simulation loop.

## Global Constraints
- C# naming: Classes/records `PascalCase`, methods `PascalCase`, variables/parameters `camelCase`.
- JS/Svelte naming: Functions/methods `snake_case`, variables/parameters `snake_case`.
- Zero magic numbers: Centralize new constants (`MaxSimulationDelayMs`) in `Constants.cs`.
- No over-commenting: Do NOT add XML or JSDoc comments.
- TDD required: Write failing unit test, verify red, write minimal code, verify green, commit.

## Review Focus
1. `MinimalTradingEntry` with missing `Trading date` and present `Date`: `EffectiveDate` must return `Date`.
2. `MinimalTradingEntry` with both dates missing: `EffectiveDate` returns null without throwing.
3. `CalculateDelay` with huge `deltaMs` (e.g. 5-year gap or 12-hour overnight gap): must cap delay to `Constants.MaxSimulationDelayMs` (2000 ms).
4. `CalculateDelay` with `speedFactor = 0.0`: must return 0 delay.
5. `POST /api/simulator/speed` with `speedFactor = 0.0`: must return 200 OK and set speed to 0.0; negative speed (< 0) returns 400 Bad Request.

---

### Task 1: Map System Date & Time in `MinimalTradingEntry` & Update `WindowProcessor`

**Files:**
- Modify: `InitialAttempt/Models/MinimalTradingEntry.cs:1-26`
- Modify: `InitialAttempt/Services/WindowProcessor.cs:25-45`
- Modify: `InitialAttempt.Tests/CsvParserTests.cs`

**Interfaces:**
- Consumes: DEBS 2022 CSV columns `Date` (column 2) and `Time` (column 3)
- Produces: `MinimalTradingEntry.Date`, `MinimalTradingEntry.Time`, `MinimalTradingEntry.EffectiveDate`, `MinimalTradingEntry.EffectiveTime`

- [ ] **Step 1: Write failing test in `CsvParserTests.cs`**

```csharp
[Fact]
public void MinimalTradingEntry_ResolvesEffectiveDateAndTime_WhenTradingDateIsEmpty()
{
    var csvContent = "ID,SecType,Date,Time,Last,Trading time,Trading date\n" +
                     "TEST.SYM,E,08-11-2021,05:10:04.000,100.50,00:00:00.000,\n";

    var tempFile = Path.GetTempFileName();
    try
    {
        File.WriteAllText(tempFile, csvContent);
        var service = new ReadCsvService();
        var records = service.StreamTradeEvents(tempFile, CancellationToken.None).ToList();

        Assert.Single(records);
        var record = records[0];
        Assert.Equal(new DateOnly(2021, 11, 8), record.EffectiveDate);
        Assert.Equal(new TimeOnly(0, 0, 0), record.EffectiveTime);
    }
    finally
    {
        File.Delete(tempFile);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~MinimalTradingEntry_ResolvesEffectiveDateAndTime"`
Expected: FAIL (compilation failure: `EffectiveDate` does not exist).

- [ ] **Step 3: Implement `Date`, `Time`, `EffectiveDate`, `EffectiveTime` in `MinimalTradingEntry.cs` and use in `WindowProcessor.cs`**

1. In `MinimalTradingEntry.cs`:
   ```csharp
   [CsvColumn("Date")]
   [CsvFormat("dd-MM-yyyy")]
   public DateOnly? Date { get; set; }

   [CsvColumn("Time")]
   [CsvFormat("HH:mm:ss.fff")]
   public TimeOnly? Time { get; set; }

   public DateOnly? EffectiveDate => TradingDate ?? Date;
   public TimeOnly? EffectiveTime => TradingTime ?? Time;
   ```
2. In `WindowProcessor.cs`:
   - Replace `tick.TradingDate.HasValue` and `tick.TradingTime.HasValue` with `tick.EffectiveDate.HasValue` and `tick.EffectiveTime.HasValue`.
   - Use `tick.EffectiveDate.Value` and `tick.EffectiveTime.Value` for window time calculation.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~CsvParserTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add InitialAttempt/Models/MinimalTradingEntry.cs InitialAttempt/Services/WindowProcessor.cs InitialAttempt.Tests/CsvParserTests.cs
git commit -m "feat: map system date and time and use effective timestamps in window processor"
```

---

### Task 2: Timestamp Fallback and Delay Capping in `SimulationEngine`

**Files:**
- Modify: `InitialAttempt/Utils/Constants.cs`
- Modify: `InitialAttempt/Services/SimulationEngine.cs:35-49, 140-165`
- Modify: `InitialAttempt.Tests/SimulationEngineTests.cs`

**Interfaces:**
- Consumes: `Constants.MaxSimulationDelayMs`, `MinimalTradingEntry.EffectiveDate`, `MinimalTradingEntry.EffectiveTime`
- Produces: `SimulationEngine.CalculateDelay` capped at `MaxSimulationDelayMs`, playback loop using `EffectiveDate` and safe `previousEventTime` fallback

- [ ] **Step 1: Write failing tests in `SimulationEngineTests.cs`**

```csharp
[Fact]
public void CalculateDelay_WithHugeGap_CapsAtMaxSimulationDelay()
{
    long fiveYearsMs = 5L * 365 * 24 * 3600 * 1000;
    var delay = SimulationEngine.CalculateDelay(fiveYearsMs, 1.0);
    Assert.Equal(Constants.MaxSimulationDelayMs, delay);
}

[Fact]
public void CalculateDelay_WithZeroOrNegativeSpeed_ReturnsZero()
{
    var delay = SimulationEngine.CalculateDelay(5000, 0.0);
    Assert.Equal(0, delay);

    var delayNegative = SimulationEngine.CalculateDelay(5000, -1.0);
    Assert.Equal(0, delayNegative);
}

[Fact]
public async Task RunPlaybackLoop_WithMissingDates_DoesNotStall()
{
    var records = new List<MinimalTradingEntry>
    {
        new() { Id = "S1", Last = 10m, Date = new DateOnly(2021, 11, 8), Time = new TimeOnly(1, 0, 0) },
        new() { Id = "S1", Last = 11m, Date = new DateOnly(2021, 11, 8), Time = new TimeOnly(1, 0, 1) },
        new() { Id = "S1", Last = 12m, TradingDate = new DateOnly(2021, 11, 8), TradingTime = new TimeOnly(1, 0, 2) },
        new() { Id = "S1", Last = 13m, Date = null, Time = null, TradingDate = null, TradingTime = null }
    };

    var fakeReader = new FakeReadCsvService(records);
    var engine = new SimulationEngine(fakeReader);

    var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await engine.StartSimulation("fake.csv", 1000.0, cts.Token);

    await Task.Delay(200);
    var status = engine.GetStatus();

    Assert.Equal(4, status.ProcessedCount);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~CalculateDelay_WithHugeGap_CapsAtMaxSimulationDelay"`
Expected: FAIL (`MaxSimulationDelayMs` not defined, delay exceeds cap).

- [ ] **Step 3: Implement `MaxSimulationDelayMs`, delay capping, and timestamp fallback**

1. In `Constants.cs`:
   - Add `public const int MaxSimulationDelayMs = 2000;`.
2. In `SimulationEngine.cs`:
   - In `CalculateDelay`:
     ```csharp
     public static long CalculateDelay(long deltaMs, double speedFactor)
     {
         if (speedFactor <= 0.0 || deltaMs <= 0)
         {
             return 0;
         }

         double scaled = deltaMs / speedFactor;
         if (scaled > Constants.MaxSimulationDelayMs)
         {
             return Constants.MaxSimulationDelayMs;
         }

         return (long)scaled;
     }
     ```
   - In `RunPlaybackLoop`:
     ```csharp
     DateTime currentEventTime;
     if (record.EffectiveDate.HasValue && record.EffectiveTime.HasValue)
     {
         currentEventTime = record.EffectiveDate.Value.ToDateTime(record.EffectiveTime.Value);
     }
     else
     {
         currentEventTime = previousEventTime ?? DateTime.UtcNow;
     }
     ```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~SimulationEngineTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add InitialAttempt/Utils/Constants.cs InitialAttempt/Services/SimulationEngine.cs InitialAttempt.Tests/SimulationEngineTests.cs
git commit -m "fix: cap inter-tick delay to prevent simulation stalls on date gaps"
```

---

### Task 3: Support Max Speed (SpeedFactor = 0.0) in `SimulatorController`

**Files:**
- Modify: `InitialAttempt/Controllers/SimulatorController.cs:50-60`
- Modify: `InitialAttempt.Tests/SimulatorControllerTests.cs`

**Interfaces:**
- Consumes: `SetSpeedRequest.SpeedFactor`
- Produces: `POST /api/simulator/speed` allowing `SpeedFactor >= 0`

- [ ] **Step 1: Write failing test in `SimulatorControllerTests.cs`**

```csharp
[Fact]
public void SetSpeed_WithZeroSpeed_AllowsMaxSpeedAndReturnsOk()
{
    var fakeEngine = new FakeSimulationEngine();
    var controller = new SimulatorController(NullLogger<SimulatorController>.Instance, fakeEngine, new FakeConstants());

    var result = controller.SetSpeed(new SetSpeedRequest(0.0));

    Assert.Equal(0.0, fakeEngine.LastSetSpeed);
    Assert.IsType<OkResult>(result);
}

[Fact]
public void SetSpeed_WithNegativeSpeed_ReturnsBadRequest()
{
    var fakeEngine = new FakeSimulationEngine();
    var controller = new SimulatorController(NullLogger<SimulatorController>.Instance, fakeEngine, new FakeConstants());

    var result = controller.SetSpeed(new SetSpeedRequest(-1.0));

    var badRequest = Assert.IsType<BadRequestObjectResult>(result);
    Assert.Contains("negative", badRequest.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~SetSpeed_WithZeroSpeed_AllowsMaxSpeedAndReturnsOk"`
Expected: FAIL (returns BadRequest instead of Ok).

- [ ] **Step 3: Update `SetSpeed` validation in `SimulatorController.cs`**

In `SimulatorController.cs`:
```csharp
[HttpPost("speed")]
public IActionResult SetSpeed([FromBody] SetSpeedRequest request)
{
    if (request.SpeedFactor < 0)
    {
        return BadRequest("Speed factor cannot be negative.");
    }

    simulationEngine.SetSpeedFactor(request.SpeedFactor);
    return Ok();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~SimulatorControllerTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add InitialAttempt/Controllers/SimulatorController.cs InitialAttempt.Tests/SimulatorControllerTests.cs
git commit -m "fix: allow zero speed factor for max playback rate in simulator controller"
```

---

### Task 4: Full System Verification & Operations Guide Update

**Files:**
- Modify: `docs/SYSTEM_GUIDE.md` (untracked/ignored)

- [ ] **Step 1: Run all unit tests**

Run: `dotnet test InitialAttempt.Tests --no-restore`
Expected: All 39+ unit tests pass with 0 failures.

- [ ] **Step 2: Run frontend production build**

Run: `cd frontend && npm run build`
Expected: Build passes with 0 errors.

- [ ] **Step 3: Update `docs/SYSTEM_GUIDE.md`**

Document `Constants.MaxSimulationDelayMs`, effective timestamp fallback behavior, and `speedFactor = 0.0` Max speed operation.
