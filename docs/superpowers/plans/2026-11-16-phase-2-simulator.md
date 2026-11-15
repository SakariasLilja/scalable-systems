# Phase 2: CSV Traffic Simulator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a high-throughput, controllable CSV playback engine in C# ASP.NET Core that parses DEBS 2022 tick datasets, filters trade events, regulates stream speed via delta calculations, and exposes simulator control endpoints.

**Architecture:** A streaming CSV parser (`ReadCsvService`) filters out comment lines and non-trade ticks (null `Last` price). A background `SimulationEngine` manages playback timing against a dynamic `speedFactor` and emits parsed records into a high-performance `Channel<MinimalTradingEntry>`. A `SimulatorController` exposes start, pause, resume, speed adjustment, and status endpoints.

**Tech Stack:** C# .NET 10, ASP.NET Core Web API, `System.Threading.Channels`, xUnit.

**Spec:** [specs/phase-2-simulator.md](file:///home/zak/aalto/scalable-systems/specs/phase-2-simulator.md)

## Global Constraints
- C# Naming Conventions (per AGENTS.md): Classes in `PascalCase`, Methods in `camelCase`.
- No JSDoc or XML documentation comments.
- Test coverage focused on logic-heavy components: CSV filtering, timestamp parsing, and delta timing calculations.
- File paths and defaults configured via `Constants.cs` / `.env`.

## Review Focus
1. Rows with `#` comments or empty `Last` prices are discarded without allocating trade entries.
2. `speedFactor = 0.0` operates in unbounded mode (zero synthetic delay) without dividing by zero.
3. Timestamp delta rollover across midnight or disordered ticks handles edge cases gracefully.
4. Pausing simulation suspends the playback loop without dropping the current stream position.
5. Cancellation tokens cleanly terminate file streams and reader loops on shutdown.

---

### Task 1: Test Project Setup & Trade Record Parser

**Files:**
- Create: `InitialAttempt.Tests/InitialAttempt.Tests.csproj`
- Create: `InitialAttempt.Tests/CsvParserTests.cs`
- Modify: `InitialAttempt/Models/MinimalTradeEntry.cs`
- Modify: `InitialAttempt/Services/ReadCsvService.cs`

**Interfaces:**
- Produces: `MinimalTradingEntry` record struct with guaranteed non-null `decimal Last`.
- Produces: `IReadCsvService.streamTradeEvents(string filePath, CancellationToken ct) -> IEnumerable<MinimalTradingEntry>`.

- [ ] **Step 1: Create test project `InitialAttempt.Tests`**
```bash
dotnet new xunit -o InitialAttempt.Tests
dotnet add InitialAttempt.Tests/InitialAttempt.Tests.csproj reference InitialAttempt/InitialAttempt.csproj
dotnet sln InitialAttempt.slnx add InitialAttempt.Tests/InitialAttempt.Tests.csproj
```

- [ ] **Step 2: Write failing unit test for trade filtering**
In `InitialAttempt.Tests/CsvParserTests.cs`:
```csharp
using InitialAttempt.Services;
using InitialAttempt.Models;
using Xunit;

namespace InitialAttempt.Tests;

public class CsvParserTests
{
    [Fact]
    public void streamTradeEvents_FiltersOutNullLastAndComments()
    {
        var csvContent = """
        # Comment line
        ID,SecType,Date,Time,Ask,Ask volume,Bid,Bid volume,Ask time,Day's high ask,Close,Currency,Day's high ask time,Day's high,ISIN,Auction price,Day's low ask,Day's low,Day's low ask time,Open,Nominal value,Last,Last volume,Trading time,Total volume,Mid price,Trading date,Profit,Current price,Related indices,Day high bid time, Day low bid time,Open Time, Last trade time, Close Time, Day high Time, Day low Time,Bid time,Auction Time
        A0HN5C.ETR,E,13-11-2021,01:01:23.126,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,
        RDSA.NL,E,13-11-2021,02:00:00.000,,,,,,,,,,,,,,,,,,,,100.50,10,02:00:00.000,,,13-11-2021,,,,,,,00:00:00.000,00:00:00.000,,,,,
        """;
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, csvContent);

        try
        {
            var service = new ReadCsvService();
            var results = service.streamTradeEvents(tempFile, CancellationToken.None).ToList();

            Assert.Single(results);
            Assert.Equal("RDSA.NL", results[0].Id);
            Assert.Equal(100.50m, results[0].Last);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
```

- [ ] **Step 3: Run test to verify it fails**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj`
Expected: FAIL (method `streamTradeEvents` does not exist yet).

- [ ] **Step 4: Implement `streamTradeEvents` in `ReadCsvService.cs`**
Implement `streamTradeEvents` to yield only records where `Last.HasValue` and skip comment headers. Follow camelCase for method names (`streamTradeEvents`).

- [ ] **Step 5: Run test to verify it passes**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**
```bash
git add InitialAttempt/ InitialAttempt.Tests/ InitialAttempt.slnx
git commit -m "feat(simulator): add trade record streaming parser with null filter and tests"
```

---

### Task 2: Simulation Playback Engine

**Files:**
- Create: `InitialAttempt/Services/SimulationEngine.cs`
- Create: `InitialAttempt/Services/ISimulationEngine.cs`
- Create: `InitialAttempt.Tests/SimulationEngineTests.cs`

**Interfaces:**
- Produces: `ISimulationEngine`
  - `startSimulation(string filePath, double speedFactor) -> Task`
  - `pauseSimulation() -> void`
  - `resumeSimulation() -> void`
  - `setSpeedFactor(double speedFactor) -> void`
  - `getStatus() -> SimulationStatus`
  - `getEventChannel() -> ChannelReader<MinimalTradingEntry>`

- [ ] **Step 1: Write failing unit test for delay calculation**
In `InitialAttempt.Tests/SimulationEngineTests.cs`:
```csharp
using InitialAttempt.Services;
using Xunit;

namespace InitialAttempt.Tests;

public class SimulationEngineTests
{
    [Theory]
    [InlineData(1000, 1.0, 1000)]
    [InlineData(1000, 2.0, 500)]
    [InlineData(1000, 10.0, 100)]
    [InlineData(1000, 0.0, 0)]
    public void calculateDelay_ComputesCorrectScaledDelay(long deltaMs, double speedFactor, long expectedDelayMs)
    {
        var delay = SimulationEngine.calculateDelay(deltaMs, speedFactor);
        Assert.Equal(expectedDelayMs, delay);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj --filter "calculateDelay"`
Expected: FAIL (`SimulationEngine` not found).

- [ ] **Step 3: Implement `SimulationEngine`**
Create `ISimulationEngine` and `SimulationEngine` with:
- Static helper `calculateDelay(long deltaMs, double speedFactor) -> long`.
- Background playback loop reading `streamTradeEvents` and pushing into `Channel<MinimalTradingEntry>.CreateBounded<MinimalTradingEntry>(100000)`.
- Thread-safe state properties: `Status`, `SpeedFactor`, `ProcessedCount`, `CurrentSimulationTime`.

- [ ] **Step 4: Run test to verify it passes**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj --filter "calculateDelay"`
Expected: PASS.

- [ ] **Step 5: Commit**
```bash
git add InitialAttempt/Services/SimulationEngine.cs InitialAttempt/Services/ISimulationEngine.cs InitialAttempt.Tests/SimulationEngineTests.cs
git commit -m "feat(simulator): implement simulation playback engine with dynamic speed scaling"
```

---

### Task 3: Simulator API Controller & Service Registration

**Files:**
- Create: `InitialAttempt/Controllers/SimulatorController.cs`
- Modify: `InitialAttempt/Program.cs`
- Create: `InitialAttempt.Tests/SimulatorControllerTests.cs`

**Interfaces:**
- Consumes: `ISimulationEngine`
- Produces: REST endpoints:
  - `POST /api/simulator/start` (body: `{ "filePath": string, "speedFactor": double }`)
  - `POST /api/simulator/pause`
  - `POST /api/simulator/resume`
  - `POST /api/simulator/speed` (body: `{ "speedFactor": double }`)
  - `GET /api/simulator/status`

- [ ] **Step 1: Write controller unit test**
In `InitialAttempt.Tests/SimulatorControllerTests.cs`:
Test that `SimulatorController.getStatus` returns current engine status and `setSpeed` propagates speed changes.

- [ ] **Step 2: Run test to verify it fails**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj --filter "SimulatorController"`
Expected: FAIL (`SimulatorController` does not exist).

- [ ] **Step 3: Implement `SimulatorController` & register services in `Program.cs`**
- Register `builder.Services.AddSingleton<ISimulationEngine, SimulationEngine>();`
- Implement controller methods using camelCase per `AGENTS.md`.

- [ ] **Step 4: Run test to verify it passes**
Run: `XDG_RUNTIME_DIR=/tmp dotnet test InitialAttempt.Tests/InitialAttempt.Tests.csproj`
Expected: ALL PASS.

- [ ] **Step 5: Commit**
```bash
git add InitialAttempt/Controllers/SimulatorController.cs InitialAttempt/Program.cs InitialAttempt.Tests/SimulatorControllerTests.cs
git commit -m "feat(simulator): add simulator REST API endpoints and DI registration"
```
