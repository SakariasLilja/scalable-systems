#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <csignal>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <limits>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "burst_tracker.hpp"
#include "constants.hpp"
#include "csv_json.hpp"
#include "csv_seeker.hpp"
#include "day_discovery.hpp"
#include "http_sender.hpp"
#include "sharded_pool.hpp"
#include "timestamp_pacer.hpp"

namespace {

std::atomic<bool> g_stop_requested{false};

void handle_stop_signal(int) {
    g_stop_requested.store(true, std::memory_order_relaxed);
}

struct CliOptions {
    std::string data_dir = sim::DEFAULT_DATA_DIR;
    std::string url = sim::DEFAULT_INGEST_URL;
    double speed = sim::DEFAULT_SPEED;
    int concurrency = sim::DEFAULT_CONCURRENCY;
    int worker_shards = sim::DEFAULT_WORKER_SHARDS;
    int batch_size = sim::DEFAULT_BATCH_SIZE;
    std::string day;
    std::string file;
    std::string start_day;
    std::optional<int64_t> start_time_ms;
    bool probe = false;
    bool offline = false;
    int64_t bucket_sec = sim::DEFAULT_BURST_BUCKET_SEC;
    int64_t probe_ticks = sim::DEFAULT_PROBE_TICKS;
    bool probe_ticks_explicit = false;
    bool help = false;
};

void print_usage(const char* prog) {
    std::cout << "Usage: " << prog << " [options]\n"
              << "Options:\n"
              << "  --data-dir <dir>      Directory with trading CSV files (default: "
              << sim::DEFAULT_DATA_DIR << ")\n"
              << "  --url <url>           Target HTTP ingest URL (default: "
              << sim::DEFAULT_INGEST_URL << ")\n"
              << "  --speed <double>      Playback speed multiplier; 0 = max speed (default: "
              << sim::DEFAULT_SPEED << ")\n"
              << "  --concurrency <int>   Sharded keep-alive worker threads (default: "
              << sim::DEFAULT_CONCURRENCY << ")\n"
              << "  --worker-shards <int> Number of market-sharded Worker pods (default: "
              << sim::DEFAULT_WORKER_SHARDS << ")\n"
              << "  --batch-size <int>    Optional JSON-array batch size per HTTP POST (default: "
              << sim::DEFAULT_BATCH_SIZE << ")\n"
              << "  --day <dd-mm-yy>      Run a single trading day\n"
              << "  --file <path>         Run an explicit single CSV file\n"
              << "  --start-day <dd-mm-yy> Start from trading day through end of week\n"
              << "  --start-time <HH:MM[:SS]> Seek to time of day (first day in stream, every day in probe)\n"
              << "  --probe               Measure max-speed ingestion across discovered days\n"
              << "  --offline             With --probe: offline CSV reader & burst speedup probe (no HTTP)\n"
              << "  --bucket-sec <int>    Burst window size in seconds for --offline probe (default: "
              << sim::DEFAULT_BURST_BUCKET_SEC << ")\n"
              << "  --probe-ticks <N>     Ticks per day in probe mode; 0 = full file (default: "
              << sim::DEFAULT_PROBE_TICKS << ", 0 in --offline)\n"
              << "  --help                Show this help message\n";
}

bool parse_args(int argc, char* argv[], CliOptions& opts, std::string& err_msg) {
    auto next_value = [&](int& i, std::string_view flag, std::string& out) -> bool {
        auto eq = flag.find('=');
        if (eq != std::string_view::npos) {
            out = std::string(flag.substr(eq + 1));
            return true;
        }
        if (i + 1 >= argc) {
            err_msg = "Missing value for " + std::string(flag);
            return false;
        }
        out = argv[++i];
        return true;
    };

    for (int i = 1; i < argc; ++i) {
        std::string_view arg(argv[i]);
        std::string_view key = arg;
        auto eq = arg.find('=');
        if (eq != std::string_view::npos) {
            key = arg.substr(0, eq);
        }

        if (key == "--help" || key == "-h") {
            opts.help = true;
            return true;
        }
        if (key == "--probe") {
            opts.probe = true;
            continue;
        }
        if (key == "--offline") {
            opts.offline = true;
            continue;
        }

        std::string val;
        if (key == "--data-dir") {
            if (!next_value(i, arg, val)) return false;
            opts.data_dir = std::move(val);
        } else if (key == "--url") {
            if (!next_value(i, arg, val)) return false;
            opts.url = std::move(val);
        } else if (key == "--speed") {
            if (!next_value(i, arg, val)) return false;
            try {
                std::size_t pos = 0;
                opts.speed = std::stod(val, &pos);
                if (pos != val.size() || !std::isfinite(opts.speed) || opts.speed < 0.0) {
                    err_msg = "Invalid --speed value: " + val;
                    return false;
                }
            } catch (...) {
                err_msg = "Invalid --speed value: " + val;
                return false;
            }
        } else if (key == "--concurrency") {
            if (!next_value(i, arg, val)) return false;
            try {
                std::size_t pos = 0;
                int c = std::stoi(val, &pos);
                if (pos != val.size() || c <= 0) {
                    err_msg = "Invalid --concurrency value: " + val;
                    return false;
                }
                opts.concurrency = c;
            } catch (...) {
                err_msg = "Invalid --concurrency value: " + val;
                return false;
            }
        } else if (key == "--worker-shards") {
            if (!next_value(i, arg, val)) return false;
            try {
                std::size_t pos = 0;
                int ws = std::stoi(val, &pos);
                if (pos != val.size() || ws <= 0) {
                    err_msg = "Invalid --worker-shards value: " + val;
                    return false;
                }
                opts.worker_shards = ws;
            } catch (...) {
                err_msg = "Invalid --worker-shards value: " + val;
                return false;
            }
        } else if (key == "--day") {
            if (!next_value(i, arg, val)) return false;
            opts.day = std::move(val);
        } else if (key == "--file") {
            if (!next_value(i, arg, val)) return false;
            opts.file = std::move(val);
        } else if (key == "--start-day") {
            if (!next_value(i, arg, val)) return false;
            opts.start_day = std::move(val);
        } else if (key == "--start-time") {
            if (!next_value(i, arg, val)) return false;
            auto parsed_ms = sim::parse_time_of_day_ms(val);
            if (!parsed_ms.has_value()) {
                err_msg = "Invalid --start-time value (expected HH:MM or HH:MM:SS): " + val;
                return false;
            }
            opts.start_time_ms = *parsed_ms;
        } else if (key == "--batch-size") {
            if (!next_value(i, arg, val)) return false;
            try {
                std::size_t pos = 0;
                int b = std::stoi(val, &pos);
                if (pos != val.size() || b <= 0) {
                    err_msg = "Invalid --batch-size value: " + val;
                    return false;
                }
                opts.batch_size = b;
            } catch (...) {
                err_msg = "Invalid --batch-size value: " + val;
                return false;
            }
        } else if (key == "--bucket-sec") {
            if (!next_value(i, arg, val)) return false;
            try {
                std::size_t pos = 0;
                int64_t s = std::stoll(val, &pos);
                if (pos != val.size() || s <= 0 ||
                    s > std::numeric_limits<int64_t>::max() / sim::MS_PER_SECOND) {
                    err_msg = "Invalid --bucket-sec value: " + val;
                    return false;
                }
                opts.bucket_sec = s;
            } catch (...) {
                err_msg = "Invalid --bucket-sec value: " + val;
                return false;
            }
        } else if (key == "--probe-ticks") {
            if (!next_value(i, arg, val)) return false;
            try {
                std::size_t pos = 0;
                int64_t n = std::stoll(val, &pos);
                if (pos != val.size() || n < 0) {
                    err_msg = "Invalid --probe-ticks value: " + val;
                    return false;
                }
                opts.probe_ticks = n;
                opts.probe_ticks_explicit = true;
            } catch (...) {
                err_msg = "Invalid --probe-ticks value: " + val;
                return false;
            }
        } else {
            err_msg = "Unknown option: " + std::string(arg);
            return false;
        }
    }
    if (opts.offline && !opts.probe) {
        err_msg = "--offline requires --probe";
        return false;
    }
    return true;
}

struct LiveStatsState {
    std::mutex file_mu;
    std::string current_filename;
    std::atomic<uint64_t> ticks_read{0};
    std::atomic<int64_t> sim_elapsed_ms{0};
};

void sleep_interruptible_ms(int64_t delay_ms) {
    int64_t remaining = delay_ms;
    while (remaining > 0 && !g_stop_requested.load(std::memory_order_relaxed)) {
        int64_t slice = std::min(remaining, sim::SLEEP_SLICE_MS);
        std::this_thread::sleep_for(std::chrono::milliseconds(slice));
        remaining -= slice;
    }
}

void print_stats_line(LiveStatsState& state,
                      const sim::ShardedPool& pool,
                      std::chrono::steady_clock::time_point start_tp,
                      std::chrono::steady_clock::time_point& prev_tp,
                      uint64_t& prev_completed,
                      double* prev_sim_sec = nullptr,
                      double* min_speedup = nullptr) {
    auto now = std::chrono::steady_clock::now();
    double interval_sec = std::chrono::duration<double>(now - prev_tp).count();
    double total_sec = std::chrono::duration<double>(now - start_tp).count();

    uint64_t read_count = state.ticks_read.load(std::memory_order_relaxed);
    uint64_t sent_count = pool.ticks_sent();
    uint64_t error_count = pool.http_errors();
    uint64_t completed = sent_count + error_count;

    double req_per_sec =
        (interval_sec > 0.0) ? static_cast<double>(completed - prev_completed) / interval_sec : 0.0;
    double sim_sec = static_cast<double>(state.sim_elapsed_ms.load(std::memory_order_relaxed)) /
                     static_cast<double>(sim::MS_PER_SECOND);
    double speedup = (total_sec > 0.0 && sim_sec > 0.0) ? (sim_sec / total_sec) : 0.0;
    if (prev_sim_sec && min_speedup) {
        double interval_sim_sec = sim_sec - *prev_sim_sec;
        if (interval_sec > 0.0 && (interval_sim_sec > 0.0 || read_count > 0)) {
            double interval_speedup = (interval_sim_sec > 0.0) ? (interval_sim_sec / interval_sec) : 0.0;
            if (interval_speedup < *min_speedup) {
                *min_speedup = interval_speedup;
            }
        }
        *prev_sim_sec = sim_sec;
    }

    std::string filename;
    {
        std::lock_guard<std::mutex> lock(state.file_mu);
        filename = state.current_filename;
    }

    std::cout << std::fixed << std::setprecision(2)
              << "[stats] file=" << filename
              << " read=" << read_count
              << " sent=" << sent_count
              << " req/s=" << std::setprecision(1) << req_per_sec
              << " errors=" << error_count
              << " speedup=" << std::setprecision(2) << speedup << "x\n";

    prev_tp = now;
    prev_completed = completed;
}

int run_stream_mode(const CliOptions& opts,
                    const sim::HttpUrl& target_url,
                    const std::vector<std::string>& files) {
    LiveStatsState stats;
    sim::ShardedPool pool(opts.concurrency, target_url, &g_stop_requested, opts.batch_size, opts.worker_shards);
    auto start_tp = std::chrono::steady_clock::now();

    std::mutex stats_mu;
    std::condition_variable stats_cv;
    bool stats_done = false;
    double min_speedup = std::numeric_limits<double>::infinity();

    std::thread stats_thread([&]() {
        auto prev_tp = start_tp;
        uint64_t prev_completed = 0;
        double prev_sim_sec = 0.0;
        std::unique_lock<std::mutex> lock(stats_mu);
        while (!stats_done && !g_stop_requested.load(std::memory_order_relaxed)) {
            stats_cv.wait_for(lock, std::chrono::seconds(sim::STATS_INTERVAL_SECONDS), [&]() {
                return stats_done || g_stop_requested.load(std::memory_order_relaxed);
            });
            if (stats_done) {
                break;
            }
            lock.unlock();
            print_stats_line(stats, pool, start_tp, prev_tp, prev_completed, &prev_sim_sec, &min_speedup);
            lock.lock();
        }
    });

    std::vector<char> io_buf(sim::FILE_STREAM_BUFFER_BYTES);
    sim::TimestampPacer pacer;
    int64_t completed_days_sim_ms = 0;
    bool read_error = false;

    for (std::size_t file_idx = 0; file_idx < files.size(); ++file_idx) {
        const std::string& path = files[file_idx];
        if (g_stop_requested.load(std::memory_order_relaxed)) {
            break;
        }

        std::ifstream in;
        in.rdbuf()->pubsetbuf(io_buf.data(), static_cast<std::streamsize>(io_buf.size()));
        in.open(path, std::ios::binary);
        if (!in.is_open()) {
            std::cerr << "Failed to open CSV file: " << path << "\n";
            read_error = true;
            break;
        }

        {
            std::lock_guard<std::mutex> lock(stats.file_mu);
            stats.current_filename = sim::extract_date_label(path);
        }

        pacer.reset();
        sim::SimTimeTracker day_ts;
        std::vector<std::string> headers;
        std::string line;

        while (!g_stop_requested.load(std::memory_order_relaxed) && std::getline(in, line)) {
            std::string_view clean = sim::strip_cr(line);
            if (sim::is_comment_or_empty(clean)) {
                continue;
            }
            if (headers.empty()) {
                headers = sim::parse_csv_line(clean);
                if (file_idx == 0 && opts.start_time_ms.has_value()) {
                    sim::seek_csv_to_time(in, *opts.start_time_ms, headers);
                }
                continue;
            }

            sim::ParsedRow row = sim::csv_row_to_json(headers, clean);
            auto epoch_ms = sim::TimestampPacer::resolve_row_epoch_ms(row);
            day_ts.record(epoch_ms);
            stats.sim_elapsed_ms.store(completed_days_sim_ms + day_ts.elapsed_sim_ms(),
                                       std::memory_order_relaxed);

            if (opts.speed > 0.0) {
                int64_t delay_ms = pacer.compute_delay_ms(epoch_ms, opts.speed);
                if (delay_ms > 0) {
                    sleep_interruptible_ms(delay_ms);
                }
            }

            if (g_stop_requested.load(std::memory_order_relaxed)) {
                break;
            }

            stats.ticks_read.fetch_add(1, std::memory_order_relaxed);
            if (!pool.push(row.symbol_id, std::move(row.json_payload))) {
                break;
            }
        }

        completed_days_sim_ms += day_ts.elapsed_sim_ms();
        stats.sim_elapsed_ms.store(completed_days_sim_ms, std::memory_order_relaxed);
    }

    bool flush_error = false;
    if (g_stop_requested.load(std::memory_order_relaxed)) {
        pool.stop_and_join();
    } else {
        pool.wait_idle();
        pool.shutdown_and_join();

        sim::HttpUrl flush_url = target_url;
        if (!flush_url.path.empty() && flush_url.path.back() == '/') {
            flush_url.path += "flush";
        } else {
            flush_url.path += "/flush";
        }
        sim::HttpKeepAliveSender flush_sender(flush_url);
        int shards = std::max(1, opts.worker_shards);
        for (int s = 0; s < shards; ++s) {
            std::optional<int> shard_hdr = (shards > 1) ? std::optional<int>(s) : std::nullopt;
            sim::HttpPostResult res = flush_sender.post_json("{}", shard_hdr);
            if (!res.ok) {
                std::cerr << "Failed to flush worker shard " << s << " (status=" << res.status_code << ")\n";
                flush_error = true;
            }
        }
    }

    {
        std::lock_guard<std::mutex> lock(stats_mu);
        stats_done = true;
    }
    stats_cv.notify_all();
    if (stats_thread.joinable()) {
        stats_thread.join();
    }

    auto end_tp = std::chrono::steady_clock::now();
    double total_sec = std::chrono::duration<double>(end_tp - start_tp).count();
    uint64_t sent = pool.ticks_sent();
    uint64_t errors = pool.http_errors();
    uint64_t read = stats.ticks_read.load(std::memory_order_relaxed);
    double rps = (total_sec > 0.0) ? static_cast<double>(sent + errors) / total_sec : 0.0;
    double sim_sec = static_cast<double>(completed_days_sim_ms) /
                     static_cast<double>(sim::MS_PER_SECOND);
    double speedup = (total_sec > 0.0 && sim_sec > 0.0) ? (sim_sec / total_sec) : 0.0;
    double reported_min_speedup = std::isfinite(min_speedup) ? std::min(min_speedup, speedup) : speedup;

    std::cout << std::fixed << std::setprecision(2)
              << "[summary] files=" << files.size()
              << " read=" << read
              << " sent=" << sent
              << " errors=" << errors
              << " wall=" << total_sec << "s"
              << " sim=" << sim_sec << "s"
              << " avg_req/s=" << std::setprecision(1) << rps
              << " speedup=" << std::setprecision(2) << speedup << "x"
              << " min_speedup=" << std::setprecision(2) << reported_min_speedup << "x\n";

    return (read_error || errors > 0 || flush_error) ? 1 : 0;
}

int run_probe_mode(const CliOptions& opts,
                   const sim::HttpUrl& target_url,
                   const std::vector<std::string>& files) {
    std::vector<char> io_buf(sim::FILE_STREAM_BUFFER_BYTES);
    sim::ShardedPool pool(opts.concurrency, target_url, &g_stop_requested, opts.batch_size, opts.worker_shards);
    double min_speedup = std::numeric_limits<double>::infinity();
    std::size_t probed_days = 0;
    uint64_t total_errors = 0;

    for (const std::string& path : files) {
        if (g_stop_requested.load(std::memory_order_relaxed)) {
            break;
        }

        std::ifstream in;
        in.rdbuf()->pubsetbuf(io_buf.data(), static_cast<std::streamsize>(io_buf.size()));
        in.open(path, std::ios::binary);
        if (!in.is_open()) {
            std::cerr << "Failed to open CSV file: " << path << "\n";
            if (g_stop_requested.load(std::memory_order_relaxed)) {
                pool.stop_and_join();
            } else {
                pool.wait_idle();
                pool.shutdown_and_join();
            }
            return 1;
        }

        std::string filename = sim::extract_date_label(path);
        LiveStatsState stats;
        {
            std::lock_guard<std::mutex> lock(stats.file_mu);
            stats.current_filename = filename;
        }

        uint64_t sent_before = pool.ticks_sent();
        uint64_t errors_before = pool.http_errors();
        auto day_start_tp = std::chrono::steady_clock::now();

        std::mutex stats_mu;
        std::condition_variable stats_cv;
        bool stats_done = false;
        std::thread stats_thread([&]() {
            auto prev_tp = day_start_tp;
            uint64_t prev_completed = sent_before + errors_before;
            std::unique_lock<std::mutex> lock(stats_mu);
            while (!stats_done && !g_stop_requested.load(std::memory_order_relaxed)) {
                stats_cv.wait_for(lock, std::chrono::seconds(sim::STATS_INTERVAL_SECONDS), [&]() {
                    return stats_done || g_stop_requested.load(std::memory_order_relaxed);
                });
                if (stats_done) {
                    break;
                }
                lock.unlock();
                print_stats_line(stats, pool, day_start_tp, prev_tp, prev_completed);
                lock.lock();
            }
        });

        sim::SimTimeTracker day_ts;
        std::vector<std::string> headers;
        std::string line;
        int64_t day_read = 0;

        while (!g_stop_requested.load(std::memory_order_relaxed) && std::getline(in, line)) {
            std::string_view clean = sim::strip_cr(line);
            if (sim::is_comment_or_empty(clean)) {
                continue;
            }
            if (headers.empty()) {
                headers = sim::parse_csv_line(clean);
                if (opts.start_time_ms.has_value()) {
                    sim::seek_csv_to_time(in, *opts.start_time_ms, headers);
                }
                continue;
            }

            sim::ParsedRow row = sim::csv_row_to_json(headers, clean);
            auto epoch_ms = sim::TimestampPacer::resolve_row_epoch_ms(row);
            day_ts.record(epoch_ms);
            stats.sim_elapsed_ms.store(day_ts.elapsed_sim_ms(), std::memory_order_relaxed);

            ++day_read;
            stats.ticks_read.fetch_add(1, std::memory_order_relaxed);
            if (!pool.push(row.symbol_id, std::move(row.json_payload))) {
                break;
            }
            if (opts.probe_ticks > 0 && day_read >= opts.probe_ticks) {
                break;
            }
        }

        if (!g_stop_requested.load(std::memory_order_relaxed)) {
            pool.wait_idle();
        }
        auto day_end_tp = std::chrono::steady_clock::now();

        {
            std::lock_guard<std::mutex> lock(stats_mu);
            stats_done = true;
        }
        stats_cv.notify_all();
        if (stats_thread.joinable()) {
            stats_thread.join();
        }

        double wall_sec = std::chrono::duration<double>(day_end_tp - day_start_tp).count();
        int64_t sim_ms = day_ts.elapsed_sim_ms();
        double sim_sec = static_cast<double>(sim_ms) / static_cast<double>(sim::MS_PER_SECOND);
        uint64_t sent = pool.ticks_sent() - sent_before;
        uint64_t errors = pool.http_errors() - errors_before;
        total_errors += errors;
        double rps = (wall_sec > 0.0) ? static_cast<double>(sent + errors) / wall_sec : 0.0;
        double speedup = (wall_sec > 0.0 && sim_sec > 0.0) ? (sim_sec / wall_sec) : 0.0;

        if (day_read > 0 && sim_sec > 0.0) {
            if (speedup < min_speedup) {
                min_speedup = speedup;
            }
            ++probed_days;
        }

        std::cout << std::fixed << std::setprecision(2)
                  << "[probe] file=" << filename
                  << " ticks=" << day_read
                  << " sent=" << sent
                  << " errors=" << errors
                  << " wall=" << wall_sec << "s"
                  << " sim=" << sim_sec << "s"
                  << " req/s=" << std::setprecision(1) << rps
                  << " speedup=" << std::setprecision(2) << speedup << "x"
                  << (day_read == 0 ? " (skipped: no rows at/after --start-time)" : "")
                  << "\n";
    }

    if (g_stop_requested.load(std::memory_order_relaxed)) {
        pool.stop_and_join();
    } else {
        pool.shutdown_and_join();
    }

    if (probed_days == 0) {
        std::cerr << "No trading days probed.\n";
        return 1;
    }
    if (!std::isfinite(min_speedup)) {
        min_speedup = 0.0;
    }

    std::cout << std::fixed << std::setprecision(2)
              << "[probe-summary] days=" << probed_days
              << " total_errors=" << total_errors
              << " min_sustained_speedup=" << min_speedup << "x\n";

    return total_errors > 0 ? 1 : 0;
}

int run_offline_probe_mode(const CliOptions& opts, const std::vector<std::string>& files) {
    int64_t effective_probe_ticks = opts.probe_ticks_explicit ? opts.probe_ticks : 0;
    int64_t primary_bucket_ms = opts.bucket_sec * sim::MS_PER_SECOND;
    int64_t strict_bucket_ms = sim::STRICT_BURST_BUCKET_SEC * sim::MS_PER_SECOND;

    uint64_t total_rows = 0;
    uint64_t total_json_bytes = 0;
    double total_wall_sec = 0.0;
    double worst_primary_sim_rps = 0.0;
    double worst_strict_sim_rps = 0.0;
    std::string worst_primary_file;
    std::string worst_strict_file;
    std::size_t probed_days = 0;
    std::size_t open_failures = 0;

    std::vector<char> io_buf(sim::FILE_STREAM_BUFFER_BYTES);

    for (const auto& path : files) {
        if (g_stop_requested.load(std::memory_order_relaxed)) {
            break;
        }
        std::ifstream in;
        in.rdbuf()->pubsetbuf(io_buf.data(), static_cast<std::streamsize>(io_buf.size()));
        in.open(path, std::ios::in | std::ios::binary);
        if (!in.is_open()) {
            std::cerr << "Failed to open CSV file: " << path << "\n";
            ++open_failures;
            continue;
        }

        std::string filename = sim::extract_date_label(path);
        sim::BurstTracker primary_tracker(primary_bucket_ms);
        sim::BurstTracker strict_tracker(strict_bucket_ms);
        int64_t day_rows = 0;
        std::vector<std::string> headers;
        std::string line;

        auto day_start_tp = std::chrono::steady_clock::now();

        while (!g_stop_requested.load(std::memory_order_relaxed) && std::getline(in, line)) {
            std::string_view clean = sim::strip_cr(line);
            if (sim::is_comment_or_empty(clean)) {
                continue;
            }
            if (headers.empty()) {
                headers = sim::parse_csv_line(clean);
                if (opts.start_time_ms.has_value()) {
                    sim::seek_csv_to_time(in, *opts.start_time_ms, headers);
                }
                continue;
            }

            sim::ParsedRow row = sim::csv_row_to_json(headers, clean);
            total_json_bytes += row.json_payload.size();
            auto epoch_ms = sim::TimestampPacer::resolve_row_epoch_ms(row);
            primary_tracker.record(epoch_ms);
            strict_tracker.record(epoch_ms);

            ++day_rows;
            if (effective_probe_ticks > 0 && day_rows >= effective_probe_ticks) {
                break;
            }
        }

        auto day_end_tp = std::chrono::steady_clock::now();
        double wall_sec = std::chrono::duration<double>(day_end_tp - day_start_tp).count();
        double reader_rps = (wall_sec > 0.0) ? static_cast<double>(day_rows) / wall_sec : 0.0;
        double peak_primary_rps = primary_tracker.peak_rows_per_sim_sec();
        double peak_strict_rps = strict_tracker.peak_rows_per_sim_sec();
        double maint_primary = sim::compute_maintainable_speedup(reader_rps, peak_primary_rps);
        double maint_strict = sim::compute_maintainable_speedup(reader_rps, peak_strict_rps);

        total_rows += static_cast<uint64_t>(day_rows);
        total_wall_sec += wall_sec;
        if (peak_primary_rps > worst_primary_sim_rps) {
            worst_primary_sim_rps = peak_primary_rps;
            worst_primary_file = filename;
        }
        if (peak_strict_rps > worst_strict_sim_rps) {
            worst_strict_sim_rps = peak_strict_rps;
            worst_strict_file = filename;
        }
        ++probed_days;

        std::cout << std::fixed << std::setprecision(2)
                  << "[offline-probe] file=" << filename
                  << " rows=" << day_rows
                  << " wall=" << wall_sec << "s"
                  << " reader_rows/s=" << std::setprecision(1) << reader_rps
                  << " peak_" << opts.bucket_sec << "s=" << peak_primary_rps << "/s"
                  << " peak_1s=" << peak_strict_rps << "/s"
                  << " maintainable_" << opts.bucket_sec << "s=" << std::setprecision(2) << maint_primary << "x"
                  << " maintainable_1s=" << maint_strict << "x\n";
    }

    if (probed_days == 0) {
        std::cerr << "No trading days probed.\n";
        return 1;
    }

    double overall_reader_rps = (total_wall_sec > 0.0) ? static_cast<double>(total_rows) / total_wall_sec : 0.0;
    double week_maint_primary = sim::compute_maintainable_speedup(overall_reader_rps, worst_primary_sim_rps);
    double week_maint_strict = sim::compute_maintainable_speedup(overall_reader_rps, worst_strict_sim_rps);

    std::cout << std::fixed << std::setprecision(2)
              << "[offline-probe-summary] days=" << probed_days
              << " total_rows=" << total_rows
              << " json_mb=" << (static_cast<double>(total_json_bytes) / sim::BYTES_PER_MIB)
              << " wall=" << total_wall_sec << "s"
              << " reader_rows/s=" << std::setprecision(1) << overall_reader_rps
              << " worst_" << opts.bucket_sec << "s=" << worst_primary_sim_rps << "/s(" << worst_primary_file << ")"
              << " worst_1s=" << worst_strict_sim_rps << "/s(" << worst_strict_file << ")"
              << " maintainable_" << opts.bucket_sec << "s=" << std::setprecision(2) << week_maint_primary << "x"
              << " maintainable_1s=" << week_maint_strict << "x\n";

    return open_failures > 0 ? 1 : 0;
}

}  // namespace

int main(int argc, char* argv[]) {
    std::signal(SIGPIPE, SIG_IGN);
    std::signal(SIGINT, handle_stop_signal);
    std::signal(SIGTERM, handle_stop_signal);

    CliOptions opts;
    std::string err_msg;
    if (!parse_args(argc, argv, opts, err_msg)) {
        std::cerr << "Error: " << err_msg << "\n";
        print_usage(argv[0]);
        return 1;
    }
    if (opts.help) {
        print_usage(argv[0]);
        return 0;
    }

    std::vector<std::string> files;
    if (!opts.file.empty()) {
        std::error_code ec;
        if (!std::filesystem::exists(opts.file, ec) ||
            !std::filesystem::is_regular_file(opts.file, ec)) {
            std::cerr << "Error: CSV file not found: " << opts.file << "\n";
            return 1;
        }
        files.push_back(opts.file);
    } else {
        files = sim::discover_trading_days(opts.data_dir, opts.day, opts.start_day);
        if (files.empty()) {
            std::cerr << "Error: No CSV trading day files found in: " << opts.data_dir << "\n";
            return 1;
        }
    }

    if (opts.probe && opts.offline) {
        return run_offline_probe_mode(opts, files);
    }

    auto target_url = sim::parse_http_url(opts.url);
    if (!target_url.has_value()) {
        std::cerr << "Error: Invalid HTTP URL: " << opts.url << "\n";
        return 1;
    }

    if (opts.probe) {
        return run_probe_mode(opts, *target_url, files);
    }
    return run_stream_mode(opts, *target_url, files);
}
