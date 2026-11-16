# Search Autocomplete and Trading Day Selection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement tick symbol autocomplete search in frontend UI with empty-input discovery, clean startup with localStorage preferences, and dynamic trading day discovery/selection across backend and frontend.

**Architecture:** A new `SymbolCatalogService` in ASP.NET Core indexes unique tick symbol IDs from CSV data files on startup/first access and provides fast substring filtering via `GET /api/symbols/search`. `SimulatorController` exposes `GET /api/simulator/days` scanning `DATA_DIRECTORY` for available trading days and allows specifying `TradingDay` in `StartSimulationRequest`. The Svelte frontend initializes tracked symbols from `localStorage` (defaulting clean to empty `[]`), introduces an interactive dropdown in `SearchBar.svelte` for instant random discovery or typed filtering, and integrates a day picker in `SimulatorControls.svelte`.

**Tech Stack:** C# 12 / .NET 8 (ASP.NET Core Web API), xUnit, Svelte 4, Vite, HTML5 LocalStorage API.

**Spec:** User requirements from conversation:
1. Search functionality in UI has autocomplete based on unique ID of tick (e.g., `RDSA.NL`, `A0HN5C.ETR`).
2. Focusing/clicking search before typing (`q=""`) lists available IDs for random browsing without knowing names.
3. Clean startup: zero pre-existing items in tracking section, preferences saved to browser cache (`localStorage`).
4. Trading day selector in UI populated with available files (e.g., `08-11-21`, `13-11-21`).

## Global Constraints
- C# naming: Classes and records `PascalCase`, methods `PascalCase`, variables/parameters `camelCase`.
- JS/Svelte naming: Functions and methods `snake_case`, variables/parameters `snake_case`.
- Zero magic numbers: Centralize all constants in `Constants.cs` and `constants.js`.
- No over-commenting: Do NOT add XML doc comments or JSDoc comments. Inline comments only for rare, non-obvious algorithms.
- TDD required: Write failing unit test, run to confirm red, write minimal code, confirm green, commit.
- `docs/SYSTEM_GUIDE.md` must remain ignored by git.

## Review Focus
1. `GET /api/simulator/days` with missing directory or non-matching files: must return empty list without throwing 500.
2. `POST /api/simulator/start` with non-existent `TradingDay`: must return 400 Bad Request if file does not exist.
3. `SymbolCatalogService` with missing directory or malformed CSV: must initialize safely with empty list without crashing process.
4. `GET /api/symbols/search?q={query}` with empty/whitespace query or special characters: returns top N sample symbols safely without throwing.
5. Frontend `localStorage` containing invalid JSON or empty: must safely fall back to empty list `[]` without uncaught exception.

---

### Task 1: Backend Trading Day Discovery API

**Files:**
- Modify: `InitialAttempt/Utils/Constants.cs:30-35`
- Modify: `InitialAttempt/Controllers/SimulatorController.cs:7-35`
- Modify: `InitialAttempt.Tests/SimulatorControllerTests.cs`

**Interfaces:**
- Consumes: `IConstants.CsvDirectory`, `IConstants.CsvPrefix`, `IConstants.CsvSuffix`
- Produces: `GET /api/simulator/days` returning `TradingDayInfo[]`, `StartSimulationRequest.TradingDay` parameter

- [ ] **Step 1: Write failing tests in `SimulatorControllerTests.cs`**

```csharp
[Fact]
public void GetDays_ReturnsDiscoveredTradingDays()
{
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    try
    {
        File.WriteAllText(Path.Combine(tempDir, "debs2022-gc-trading-day-08-11-21.csv"), "Id,SecType\n");
        File.WriteAllText(Path.Combine(tempDir, "debs2022-gc-trading-day-13-11-21.csv"), "Id,SecType\n");
        File.WriteAllText(Path.Combine(tempDir, "other-file.txt"), "text\n");

        mockConstants.Setup(c => c.CsvDirectory).Returns(tempDir);
        mockConstants.Setup(c => c.CsvPrefix).Returns("debs2022-gc-trading-day-");
        mockConstants.Setup(c => c.CsvSuffix).Returns(".csv");

        var result = controller.GetDays();
        var okResult = Assert.IsType<OkObjectResult>(result);
        var days = Assert.IsAssignableFrom<IEnumerable<TradingDayInfo>>(okResult.Value).ToList();

        Assert.Equal(2, days.Count);
        Assert.Contains(days, d => d.TradingDay == "08-11-21");
        Assert.Contains(days, d => d.TradingDay == "13-11-21");
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}

[Fact]
public async Task Start_WithSpecificTradingDay_UsesTradingDayPath()
{
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    try
    {
        var targetFile = Path.Combine(tempDir, "debs2022-gc-trading-day-08-11-21.csv");
        File.WriteAllText(targetFile, "Id,SecType\n");

        mockConstants.Setup(c => c.CsvDirectory).Returns(tempDir);
        mockConstants.Setup(c => c.CsvPrefix).Returns("debs2022-gc-trading-day-");
        mockConstants.Setup(c => c.CsvSuffix).Returns(".csv");

        var request = new StartSimulationRequest(null, 1.0, "08-11-21");
        var result = await controller.Start(request);

        Assert.IsType<AcceptedResult>(result);
        mockEngine.Verify(e => e.StartSimulation(targetFile, 1.0), Times.Once);
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}

[Fact]
public async Task Start_WithInvalidTradingDay_ReturnsBadRequest()
{
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    try
    {
        mockConstants.Setup(c => c.CsvDirectory).Returns(tempDir);
        mockConstants.Setup(c => c.CsvPrefix).Returns("debs2022-gc-trading-day-");
        mockConstants.Setup(c => c.CsvSuffix).Returns(".csv");

        var request = new StartSimulationRequest(null, 1.0, "99-99-99");
        var result = await controller.Start(request);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("not found", badRequest.Value?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~SimulatorControllerTests"`
Expected: FAIL due to missing `TradingDayInfo` and missing `GetDays` endpoint.

- [ ] **Step 3: Implement `TradingDayInfo` and `GetDays` endpoint in `SimulatorController.cs` and update `Constants.DefaultTradingDate`**

1. In `Constants.cs`, set `public const string DefaultTradingDate = "08-11-21";`.
2. In `SimulatorController.cs`:
   - Define `public record TradingDayInfo(string TradingDay, string FileName, long FileSizeBytes);`.
   - Update `public record StartSimulationRequest(string? FilePath = null, double SpeedFactor = 1.0, string? TradingDay = null);`.
   - Implement `[HttpGet("days")] public IActionResult GetDays()`:
     - Check if `constants.CsvDirectory` exists. If not, return `Ok(Array.Empty<TradingDayInfo>())`.
     - Scan files matching `${constants.CsvPrefix}*${constants.CsvSuffix}`.
     - Extract `tradingDay` by trimming prefix and suffix.
     - Return sorted list of `TradingDayInfo`.
   - In `[HttpPost("start")]`:
     - If `request?.TradingDay` provided, build target file path. If file does not exist, return `BadRequest($"Trading day file not found: {filePath}")`.
     - Otherwise if `request?.FilePath` provided, use it.
     - Otherwise fall back to `DefaultTradingDate`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~SimulatorControllerTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add InitialAttempt/Utils/Constants.cs InitialAttempt/Controllers/SimulatorController.cs InitialAttempt.Tests/SimulatorControllerTests.cs
git commit -m "feat: add trading days discovery and day selection to simulator controller"
```

---

### Task 2: Backend Symbol Catalog & Search API

**Files:**
- Create: `InitialAttempt/Services/ISymbolCatalogService.cs`
- Create: `InitialAttempt/Services/SymbolCatalogService.cs`
- Create: `InitialAttempt/Controllers/SymbolsController.cs`
- Modify: `InitialAttempt/Program.cs`
- Modify: `InitialAttempt/Utils/Constants.cs`
- Create: `InitialAttempt.Tests/SymbolCatalogServiceTests.cs`
- Create: `InitialAttempt.Tests/SymbolsControllerTests.cs`

**Interfaces:**
- Consumes: `IConstants.CsvDirectory`, `IConstants.CsvPrefix`, `IConstants.CsvSuffix`
- Produces: `ISymbolCatalogService.SearchSymbols(string? query, int limit)`, `GET /api/symbols/search?q={query}&limit={limit}`

- [ ] **Step 1: Write failing tests in `SymbolCatalogServiceTests.cs` and `SymbolsControllerTests.cs`**

```csharp
[Fact]
public void SearchSymbols_WithEmptyQuery_ReturnsTopSymbolsAlphabetically()
{
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    try
    {
        var csvPath = Path.Combine(tempDir, "debs2022-gc-trading-day-test.csv");
        File.WriteAllText(csvPath, "Id,SecType\nMSFT,1\nAAPL,1\nGOOG,1\nAMZN,1\nAAPL,1\n");

        var mockConstants = new Mock<IConstants>();
        mockConstants.Setup(c => c.CsvDirectory).Returns(tempDir);
        mockConstants.Setup(c => c.CsvPrefix).Returns("debs2022-gc-trading-day-");
        mockConstants.Setup(c => c.CsvSuffix).Returns(".csv");

        var service = new SymbolCatalogService(Mock.Of<ILogger<SymbolCatalogService>>(), mockConstants.Object);
        var results = service.SearchSymbols("", 3);

        Assert.Equal(3, results.Count);
        Assert.Equal(["AAPL", "AMZN", "GOOG"], results);
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}

[Fact]
public void SearchSymbols_WithSubstringQuery_FiltersCaseInsensitively()
{
    var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(tempDir);
    try
    {
        var csvPath = Path.Combine(tempDir, "debs2022-gc-trading-day-test.csv");
        File.WriteAllText(csvPath, "Id,SecType\nRDSA.NL,1\nA0HN5C.ETR,1\nRDSB.NL,1\n");

        var mockConstants = new Mock<IConstants>();
        mockConstants.Setup(c => c.CsvDirectory).Returns(tempDir);
        mockConstants.Setup(c => c.CsvPrefix).Returns("debs2022-gc-trading-day-");
        mockConstants.Setup(c => c.CsvSuffix).Returns(".csv");

        var service = new SymbolCatalogService(Mock.Of<ILogger<SymbolCatalogService>>(), mockConstants.Object);
        var results = service.SearchSymbols("rds", 10);

        Assert.Equal(2, results.Count);
        Assert.Contains("RDSA.NL", results);
        Assert.Contains("RDSB.NL", results);
    }
    finally
    {
        Directory.Delete(tempDir, true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~SymbolCatalogServiceTests"`
Expected: FAIL due to missing `ISymbolCatalogService` and `SymbolCatalogService`.

- [ ] **Step 3: Implement `ISymbolCatalogService`, `SymbolCatalogService`, and `SymbolsController`**

1. In `Constants.cs`:
   - Add `public const int DefaultSymbolSearchLimit = 20;`
   - Add `public const int MaxSymbolCatalogSampleLines = 500000;`
2. In `ISymbolCatalogService.cs`:
   ```csharp
   public interface ISymbolCatalogService
   {
       IReadOnlyList<string> SearchSymbols(string? query, int limit = Constants.DefaultSymbolSearchLimit);
   }
   ```
3. In `SymbolCatalogService.cs`:
   - Lazy index symbols using `Lazy<IReadOnlyList<string>>`.
   - Read lines from CSV files in `constants.CsvDirectory`. Extract first column before comma.
   - Guard against missing directory or missing files (return empty list).
   - `SearchSymbols(string? query, int limit)`:
     - If query is null/empty/whitespace: return `symbols.Take(limit).ToList()`.
     - Else: return `symbols.Where(s => s.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).Take(limit).ToList()`.
4. In `SymbolsController.cs`:
   - `[ApiController] [Route("api/symbols")] public class SymbolsController(ISymbolCatalogService catalogService)`
   - `[HttpGet("search")] public IActionResult Search([FromQuery] string? q, [FromQuery] int limit = Constants.DefaultSymbolSearchLimit)` returning `Ok(catalogService.SearchSymbols(q, limit))`.
5. Register in `Program.cs`: `builder.Services.AddSingleton<ISymbolCatalogService, SymbolCatalogService>();`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test InitialAttempt.Tests --no-restore --filter "FullyQualifiedName~SymbolCatalogServiceTests|FullyQualifiedName~SymbolsControllerTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add InitialAttempt/Services/ISymbolCatalogService.cs InitialAttempt/Services/SymbolCatalogService.cs InitialAttempt/Controllers/SymbolsController.cs InitialAttempt/Program.cs InitialAttempt/Utils/Constants.cs InitialAttempt.Tests/SymbolCatalogServiceTests.cs InitialAttempt.Tests/SymbolsControllerTests.cs
git commit -m "feat: implement symbol catalog service and search endpoint"
```

---

### Task 3: Frontend API & Constants Updates

**Files:**
- Modify: `frontend/src/constants.js:1-28`
- Modify: `frontend/src/lib/sse_service.js:1-60`

**Interfaces:**
- Consumes: Backend `/api/simulator/days`, `/api/simulator/start`, `/api/symbols/search`
- Produces: `get_trading_days()`, `search_symbols(query, limit)`, updated `start_simulation(filePath, speedFactor, tradingDay)`

- [ ] **Step 1: Update `constants.js`**

1. Set `default_tracked_symbols = [];` (clean startup).
2. Add `tracked_symbols_storage_key = 'tick_detector_tracked_symbols';`.
3. Add `default_symbol_search_limit = 20;`.
4. Add `search_debounce_ms = 150;`.
5. Add `default_trading_day = '08-11-21';`.

- [ ] **Step 2: Update `sse_service.js` with API helpers**

1. Add `get_trading_days()` calling `GET ${api_base_url}/api/simulator/days`.
2. Add `search_symbols(query = '', limit = default_symbol_search_limit)` calling `GET ${api_base_url}/api/symbols/search?q=${encodeURIComponent(query)}&limit=${limit}`.
3. Update `start_simulation(file_path = null, speed_factor = default_simulation_speed, trading_day = null)` to send `{ filePath: file_path, speedFactor: speed_factor, tradingDay: trading_day }` in POST body.

- [ ] **Step 3: Verify build**

Run: `cd frontend && npm run build`
Expected: Build succeeds with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add frontend/src/constants.js frontend/src/lib/sse_service.js
git commit -m "feat: add trading days and symbol search api helpers and clean startup defaults"
```

---

### Task 4: Frontend Clean Startup & LocalStorage Persistence

**Files:**
- Modify: `frontend/src/App.svelte:1-60, 110-140`

**Interfaces:**
- Consumes: `tracked_symbols_storage_key` from `constants.js`
- Produces: Tracked symbols restored from `localStorage` on boot; synced to `localStorage` on add/remove.

- [ ] **Step 1: Implement LocalStorage load and save in `App.svelte`**

1. Define `load_initial_tracked_symbols()`:
   ```javascript
   function load_initial_tracked_symbols() {
     try {
       const cached = localStorage.getItem(tracked_symbols_storage_key);
       if (cached) {
         const parsed = JSON.parse(cached);
         if (Array.isArray(parsed)) return parsed;
       }
     } catch (e) {
       // fallback on invalid cache
     }
     return [];
   }
   ```
2. Initialize `let tracked_symbols = load_initial_tracked_symbols();`.
3. Define `save_tracked_symbols(symbols)`:
   ```javascript
   function save_tracked_symbols(symbols) {
     try {
       localStorage.setItem(tracked_symbols_storage_key, JSON.stringify(symbols));
     } catch (e) {
       // ignore quota / storage errors
     }
   }
   ```
4. Update `handle_add_symbol`: when new symbol is added, call `save_tracked_symbols(tracked_symbols)`.
5. Update `handle_remove_symbol`: when symbol is removed, call `save_tracked_symbols(tracked_symbols)`.

- [ ] **Step 2: Verify build**

Run: `cd frontend && npm run build`
Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add frontend/src/App.svelte
git commit -m "feat: persist tracked symbols in localstorage with clean initial startup"
```

---

### Task 5: Frontend Autocomplete Search Component

**Files:**
- Modify: `frontend/src/components/SearchBar.svelte:1-55`

**Interfaces:**
- Consumes: `search_symbols` from `../lib/sse_service.js`, `search_debounce_ms`, `default_symbol_search_limit` from `../constants.js`
- Produces: Dispatches `add_symbol` event `{ symbol: string }`

- [ ] **Step 1: Implement autocomplete and instant discovery in `SearchBar.svelte`**

1. State variables:
   - `let search_input = '';`
   - `let suggestions = [];`
   - `let is_open = false;`
   - `let highlighted_index = -1;`
   - `let debounce_timer = null;`
   - `let container_ref;`
2. Logic:
   - `fetch_suggestions(query)` calls `search_symbols(query, default_symbol_search_limit)`.
   - `handle_focus()`: if input is focused or clicked, trigger `fetch_suggestions(search_input)` and set `is_open = true;`.
   - `handle_input()`: clear debounce timer, set new timer for `search_debounce_ms`, then call `fetch_suggestions(search_input)` and set `is_open = true;`.
   - `handle_keydown(event)`:
     - `ArrowDown`: `highlighted_index = Math.min(highlighted_index + 1, suggestions.length - 1);`
     - `ArrowUp`: `highlighted_index = Math.max(highlighted_index - 1, 0);`
     - `Enter`: if `highlighted_index >= 0`, select highlighted symbol; else submit raw `search_input`.
     - `Escape`: `is_open = false;`
   - `select_symbol(symbol)`:
     - `dispatch('add_symbol', { symbol });`
     - `search_input = '';`
     - `is_open = false;`
     - `suggestions = [];`
     - `highlighted_index = -1;`
   - Click outside handler on `window` to close dropdown when clicking outside `container_ref`.
3. Template:
   - Wrapper `.search-container` containing input, Track button, and absolute-positioned `.autocomplete-dropdown`.
   - List rendered with `#each suggestions as symbol, i`.
   - Highlighted state style with `.active` class.
   - Hint message when list is showing: "Available symbols (click to track)".

- [ ] **Step 2: Verify build**

Run: `cd frontend && npm run build`
Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add frontend/src/components/SearchBar.svelte
git commit -m "feat: implement autocomplete search dropdown with empty-focus symbol discovery"
```

---

### Task 6: Frontend Trading Day Selector

**Files:**
- Modify: `frontend/src/components/SimulatorControls.svelte:1-75`

**Interfaces:**
- Consumes: `get_trading_days`, `start_simulation` from `../lib/sse_service.js`, `default_trading_day` from `../constants.js`
- Produces: Trading day `<select>` dropdown sending chosen day on Start.

- [ ] **Step 1: Implement trading day selector in `SimulatorControls.svelte`**

1. State variables:
   - `let trading_days = [];`
   - `let selected_day = default_trading_day;`
2. Logic:
   - In `onMount`, call `get_trading_days()`.
   - Populate `trading_days`. If `selected_day` is not in returned list but list is non-empty, default to `trading_days[0].tradingDay`.
   - In `handle_start()`, pass `selected_day` to `start_simulation(null, sim_status.speedFactor || default_simulation_speed, selected_day)`.
3. Template:
   - In `.controls-card`, add Day selector `<label class="day-selector-label">Day: <select bind:value={selected_day} class="day-select">...</select></label>`.
   - Render options: `<option value={d.tradingDay}>{d.tradingDay} ({Math.round(d.fileSizeBytes / (1024 * 1024))} MB)</option>`.

- [ ] **Step 2: Verify build**

Run: `cd frontend && npm run build`
Expected: Build succeeds with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add frontend/src/components/SimulatorControls.svelte
git commit -m "feat: add trading day selector to simulator controls"
```

---

### Task 7: Full System Verification & Guide Update

**Files:**
- Modify: `docs/SYSTEM_GUIDE.md` (untracked/ignored)

- [ ] **Step 1: Run complete backend test suite**

Run: `dotnet test InitialAttempt.Tests --no-restore`
Expected: All tests pass (30+ tests, 0 failures).

- [ ] **Step 2: Run frontend production build**

Run: `cd frontend && npm run build`
Expected: Production build finishes without warnings or errors.

- [ ] **Step 3: Update `docs/SYSTEM_GUIDE.md`**

Update `docs/SYSTEM_GUIDE.md` documenting `/api/simulator/days`, `/api/symbols/search`, the autocomplete UI, localStorage preferences, and trading day selection.
