# Offline CSV Speedup Probe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an offline CSV-reader speedup probe (`--probe --offline`) to `tick_simulator` that measures reader throughput and peak simulated-time burst density across all DEBS 2022 CSV files without needing Kubernetes.

**Architecture:** Introduce an O(1)-memory `BurstTracker` header in `simulator/src/burst_tracker.hpp` that tracks peak tick counts per monotonic simulated-time bucket (10s primary, 1s secondary). Wire `--offline` and `--bucket-sec` into `simulator/src/main.cpp` to run the exact `parse_csv_line` + `csv_row_to_json` + `TimestampPacer::resolve_row_epoch_ms` loop without `ShardedPool` or HTTP sockets, and update `scripts/probe-speed.sh` to forward `--offline` cleanly.

**Tech Stack:** C++20, CMake, CTest, Bash.

**Spec:** `docs/superpowers/specs/2026-11-21-offline-csv-speed-probe-design.md`

## Global Constraints
- C++20, zero external dependencies, O(1) memory streaming.
- No over-commenting (per `AGENTS.md`); constants in `simulator/src/constants.hpp`.
- Existing online `--probe` behavior must remain unchanged when `--offline` is not set.
- Release CTest assertions must use the non-strippable `CHECK(...)` macro in `simulator/tests/simulator_tests.cpp`.

## Review Focus
- Interleaved `00:00:00.000` placeholder rows (non-monotonic timestamps) must not reset or corrupt active bucket counts.
- Rows with missing/unparseable timestamps (`std::nullopt`) must be ignored by `BurstTracker` without crashing or inflating bucket peaks.
- Invalid `--bucket-sec` values (`<= 0`, non-integer) must be rejected cleanly with non-zero exit code.
- Explicit `--probe-ticks N` must still cap rows per day in `--offline` mode, while omitting `--probe-ticks` in `--offline` mode defaults to `0` (full file scan).
- `./scripts/probe-speed.sh --offline` must work without requiring positional `CONCURRENCY` or `PROBE_TICKS` arguments, and without validating/connecting to `SIM_URL`.

---

### Task 1: `BurstTracker` & Constants

**Files:**
- Modify: `simulator/src/constants.hpp`
- Create: `simulator/src/burst_tracker.hpp`
- Modify: `simulator/tests/simulator_tests.cpp`

- [ ] **Step 1: Add burst constants to `simulator/src/constants.hpp`**
  - Add `DEFAULT_BURST_BUCKET_SEC = 10` and `STRICT_BURST_BUCKET_SEC = 1` inside `namespace sim`.

- [ ] **Step 2: Write failing unit tests in `simulator/tests/simulator_tests.cpp`**
  - Add `test_burst_tracker()` covering:
    1. Multi-bucket peak tracking (10s bucket: 3 ticks in bucket 1, 7 ticks in bucket 2, 2 ticks in bucket 3 -> `peak_count() == 7`, `peak_rows_per_sim_sec() == 0.7`).
    2. Non-monotonic timestamp (`epoch_ms < current_bucket_start_ms`, e.g. `00:00:00.000` midnight row) does not reset `current_bucket_start_ms` or corrupt peak.
    3. `std::nullopt` timestamps are ignored safely.
    4. Maintainable speedup helper: `compute_maintainable_speedup(reader_rps, peak_sim_rps)` returns `reader_rps / peak_sim_rps` (and `0.0` when `peak_sim_rps <= 0.0`).
  - Call `test_burst_tracker()` from `main()`.

- [ ] **Step 3: Run CTest to verify test fails (missing header)**
  - Run `cmake --build simulator/build -j` and confirm compile failure on missing `burst_tracker.hpp`.

- [ ] **Step 4: Implement `simulator/src/burst_tracker.hpp`**
  - `class BurstTracker`:
    - Constructor `explicit BurstTracker(int64_t bucket_ms)`
    - `void record(std::optional<int64_t> epoch_ms)`:
      - Ignore `!epoch_ms.has_value()`.
      - Compute `bucket_start = (*epoch_ms / bucket_ms_) * bucket_ms_`.
      - If `!has_bucket_`: set `current_bucket_start_ms_ = bucket_start`, `current_count_ = 1`, `has_bucket_ = true`.
      - Else if `bucket_start < current_bucket_start_ms_`: ignore (non-monotonic).
      - Else if `bucket_start == current_bucket_start_ms_`: `++current_count_`.
      - Else (`bucket_start > current_bucket_start_ms_`): advance `current_bucket_start_ms_ = bucket_start`, `current_count_ = 1`.
      - Update `peak_count_` and `peak_bucket_start_ms_` whenever `current_count_ > peak_count_`.
    - Getters: `uint64_t peak_count() const`, `int64_t peak_bucket_start_ms() const`, `int64_t bucket_ms() const`, `double peak_rows_per_sim_sec() const`.
  - Free function `inline double compute_maintainable_speedup(double reader_rps, double peak_sim_rps)`.

- [ ] **Step 5: Build and run CTest to verify all tests pass**
  - Run `cmake -S simulator -B simulator/build -DCMAKE_BUILD_TYPE=Release && cmake --build simulator/build -j && ctest --test-dir simulator/build --output-on-failure`.

- [ ] **Step 6: Commit Task 1**
  - `git add simulator/src/constants.hpp simulator/src/burst_tracker.hpp simulator/tests/simulator_tests.cpp`
  - `git commit -m "feat(sim): add O(1) BurstTracker for offline speedup probe"`

---

### Task 2: `--offline` & `--bucket-sec` CLI Mode + `scripts/probe-speed.sh`

**Files:**
- Modify: `simulator/src/main.cpp`
- Modify: `scripts/probe-speed.sh`

- [ ] **Step 1: Add CLI flags to `simulator/src/main.cpp`**
  - In `CliOptions`: add `bool offline = false;`, `int64_t bucket_sec = sim::DEFAULT_BURST_BUCKET_SEC;`, and `bool probe_ticks_explicit = false;`.
  - In `print_usage`: document `--offline` and `--bucket-sec <int>`.
  - In `parse_args`:
    - Handle `--offline`.
    - Handle `--bucket-sec` (require integer `> 0`, else error).
    - Track `opts.probe_ticks_explicit = true` when `--probe-ticks` is passed.
  - In `main()`:
    - If `opts.probe && opts.offline`: skip `parse_http_url(opts.url)` and call `run_offline_probe_mode(opts, files)`.

- [ ] **Step 2: Implement `run_offline_probe_mode(const CliOptions& opts, const std::vector<std::string>& files)` in `simulator/src/main.cpp`**
  - Determine `effective_probe_ticks = opts.probe_ticks_explicit ? opts.probe_ticks : 0`.
  - For each file:
    - Stream with `FILE_STREAM_BUFFER_BYTES` buffer, skip `#` lines, parse headers once, run `sim::csv_row_to_json(headers, clean)` and `sim::TimestampPacer::resolve_row_epoch_ms(row)`.
    - Feed `epoch_ms` into `primary_tracker(opts.bucket_sec * 1000)` and `strict_tracker(sim::STRICT_BURST_BUCKET_SEC * 1000)`.
    - Prevent dead-code elimination of `row.json_payload` (e.g., accumulate `total_json_bytes += row.json_payload.size()`).
    - Print per-day line: `[offline-probe] file=... rows=... wall=...s reader_rows/s=... peak_10s=.../s peak_1s=.../s maintainable_10s=...x maintainable_1s=...x`.
  - Track week-wide totals (`total_rows`, `total_wall_sec`, worst-case `peak_primary_rps` + filename, worst-case `peak_strict_rps` + filename).
  - Print `[offline-probe-summary]` with overall `reader_rows/s`, worst-case peak burst file & rate, and `maintainable_speedup` for both primary (`--bucket-sec`) and `1s`.

- [ ] **Step 3: Update `scripts/probe-speed.sh`**
  - If `--offline` is among arguments, invoke `"$SIM_BIN" --probe --data-dir "$REPO_ROOT/data" "$@"` directly (no positional `CONCURRENCY` / `PROBE_TICKS` assumptions, no `--url`).
  - Otherwise preserve existing online `--probe` behavior verbatim.

- [ ] **Step 4: Build, run CTest, and smoke-test offline probe on real `data/`**
  - Run `cmake --build simulator/build -j && ctest --test-dir simulator/build --output-on-failure`.
  - Smoke test with bounded ticks: `./scripts/probe-speed.sh --offline --probe-ticks 50000` (verify exit code `0`, zero network calls, valid `maintainable_10s` and `maintainable_1s` output).

- [ ] **Step 5: Commit Task 2**
  - `git add simulator/src/main.cpp scripts/probe-speed.sh`
  - `git commit -m "feat(sim): add offline CSV-reader speedup probe mode"`
