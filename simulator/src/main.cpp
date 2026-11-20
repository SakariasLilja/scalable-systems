#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <csignal>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iomanip>
#include <iostream>
#include <limits>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#include "constants.hpp"
#include "csv_json.hpp"
#include "day_discovery.hpp"
#include "http_sender.hpp"
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
    std::string day;
    std::string file;
    std::string start_day;
    bool probe = false;
    int64_t probe_ticks = sim::DEFAULT_PROBE_TICKS;
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
              << "  --day <dd-mm-yy>      Run a single trading day\n"
              << "  --file <path>         Run an explicit single CSV file\n"
              << "  --start-day <dd-mm-yy> Start from trading day through end of week\n"
              << "  --probe               Measure max-speed ingestion across discovered days\n"
              << "  --probe-ticks <N>     Ticks per day in probe mode; 0 = full file (default: "
              << sim::DEFAULT_PROBE_TICKS << ")\n"
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
        } else if (key == "--day") {
            if (!next_value(i, arg, val)) return false;
            opts.day = std::move(val);
        } else if (key == "--file") {
            if (!next_value(i, arg, val)) return false;
            opts.file = std::move(val);
        } else if (key == "--start-day") {
            if (!next_value(i, arg, val)) return false;
            opts.start_day = std::move(val);
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
            } catch (...) {
                err_msg = "Invalid --probe-ticks value: " + val;
                return false;
            }
        } else {
            err_msg = "Unknown option: " + std::string(arg);
            return false;
        }
    }
    return true;
}

struct ShardQueue {
    std::mutex mu;
    std::condition_variable cv_not_empty;
    std::condition_variable cv_not_full;
    std::deque<std::string> items;
    bool closed = false;
};

class ShardedWorkerPool {
public:
    ShardedWorkerPool(int concurrency, const sim::HttpUrl& target_url)
        : target_url_(target_url) {
        shards_.reserve(static_cast<std::size_t>(concurrency));
        for (int i = 0; i < concurrency; ++i) {
            shards_.push_back(std::make_unique<ShardQueue>());
        }
        workers_.reserve(static_cast<std::size_t>(concurrency));
        for (int i = 0; i < concurrency; ++i) {
            workers_.emplace_back(&ShardedWorkerPool::worker_loop, this, i);
        }
    }

    ~ShardedWorkerPool() {
        shutdown_and_join();
    }

    ShardedWorkerPool(const ShardedWorkerPool&) = delete;
    ShardedWorkerPool& operator=(const ShardedWorkerPool&) = delete;

    bool push(const std::string& symbol_id, std::string&& json_payload) {
        std::size_t idx = std::hash<std::string>{}(symbol_id) % shards_.size();
        ShardQueue& shard = *shards_[idx];
        std::unique_lock<std::mutex> lock(shard.mu);
        while (shard.items.size() >= sim::MAX_SHARD_QUEUE_SIZE && !shard.closed &&
               !g_stop_requested.load(std::memory_order_relaxed)) {
            shard.cv_not_full.wait_for(
                lock, std::chrono::milliseconds(sim::QUEUE_POLL_TIMEOUT_MS));
        }
        if (shard.closed || g_stop_requested.load(std::memory_order_relaxed)) {
            return false;
        }
        shard.items.push_back(std::move(json_payload));
        lock.unlock();
        shard.cv_not_empty.notify_one();
        return true;
    }

    void shutdown_and_join() {
        if (joined_) {
            return;
        }
        for (auto& shard : shards_) {
            std::lock_guard<std::mutex> lock(shard->mu);
            shard->closed = true;
            shard->cv_not_empty.notify_all();
            shard->cv_not_full.notify_all();
        }
        for (auto& t : workers_) {
            if (t.joinable()) {
                t.join();
            }
        }
        joined_ = true;
    }

    uint64_t ticks_sent() const {
        return ticks_sent_.load(std::memory_order_relaxed);
    }

    uint64_t http_errors() const {
        return http_errors_.load(std::memory_order_relaxed);
    }

private:
    void worker_loop(int shard_index) {
        ShardQueue& shard = *shards_[static_cast<std::size_t>(shard_index)];
        sim::HttpKeepAliveSender sender(target_url_);

        while (true) {
            std::string payload;
            {
                std::unique_lock<std::mutex> lock(shard.mu);
                shard.cv_not_empty.wait(lock, [&]() {
                    return !shard.items.empty() || shard.closed;
                });
                if (shard.items.empty() && shard.closed) {
                    break;
                }
                payload = std::move(shard.items.front());
                shard.items.pop_front();
                lock.unlock();
                shard.cv_not_full.notify_one();
            }

            sim::HttpPostResult res = sender.post_json(payload);
            if (res.ok) {
                ticks_sent_.fetch_add(1, std::memory_order_relaxed);
            } else {
                http_errors_.fetch_add(1, std::memory_order_relaxed);
                if (g_stop_requested.load(std::memory_order_relaxed)) {
                    break;
                }
            }
        }
    }

    sim::HttpUrl target_url_;
    std::vector<std::unique_ptr<ShardQueue>> shards_;
    std::vector<std::thread> workers_;
    std::atomic<uint64_t> ticks_sent_{0};
    std::atomic<uint64_t> http_errors_{0};
    bool joined_{false};
};

struct DayTimestampTracker {
    std::optional<int64_t> first_epoch_ms;
    std::optional<int64_t> last_epoch_ms;
    std::optional<int64_t> max_epoch_ms;

    void record(std::optional<int64_t> epoch_ms) {
        if (!epoch_ms.has_value()) {
            return;
        }
        if (!first_epoch_ms.has_value()) {
            first_epoch_ms = epoch_ms;
        }
        last_epoch_ms = epoch_ms;
        if (!max_epoch_ms.has_value() || *epoch_ms > *max_epoch_ms) {
            max_epoch_ms = epoch_ms;
        }
    }

    int64_t elapsed_ms() const {
        if (!first_epoch_ms.has_value()) {
            return 0;
        }
        if (last_epoch_ms.has_value() && *last_epoch_ms > *first_epoch_ms) {
            return *last_epoch_ms - *first_epoch_ms;
        }
        if (max_epoch_ms.has_value() && *max_epoch_ms > *first_epoch_ms) {
            return *max_epoch_ms - *first_epoch_ms;
        }
        return 0;
    }

    void reset() {
        first_epoch_ms.reset();
        last_epoch_ms.reset();
        max_epoch_ms.reset();
    }
};

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
                      const ShardedWorkerPool& pool,
                      std::chrono::steady_clock::time_point start_tp,
                      std::chrono::steady_clock::time_point& prev_tp,
                      uint64_t& prev_completed) {
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
    ShardedWorkerPool pool(opts.concurrency, target_url);
    auto start_tp = std::chrono::steady_clock::now();

    std::mutex stats_mu;
    std::condition_variable stats_cv;
    bool stats_done = false;

    std::thread stats_thread([&]() {
        auto prev_tp = start_tp;
        uint64_t prev_completed = 0;
        std::unique_lock<std::mutex> lock(stats_mu);
        while (!stats_done && !g_stop_requested.load(std::memory_order_relaxed)) {
            stats_cv.wait_for(lock, std::chrono::seconds(sim::STATS_INTERVAL_SECONDS), [&]() {
                return stats_done || g_stop_requested.load(std::memory_order_relaxed);
            });
            if (stats_done) {
                break;
            }
            lock.unlock();
            print_stats_line(stats, pool, start_tp, prev_tp, prev_completed);
            lock.lock();
        }
    });

    std::vector<char> io_buf(sim::FILE_STREAM_BUFFER_BYTES);
    sim::TimestampPacer pacer;
    int64_t completed_days_sim_ms = 0;
    bool read_error = false;

    for (const std::string& path : files) {
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
            stats.current_filename = std::filesystem::path(path).filename().string();
        }

        pacer.reset();
        DayTimestampTracker day_ts;
        std::vector<std::string> headers;
        std::string line;

        while (!g_stop_requested.load(std::memory_order_relaxed) && std::getline(in, line)) {
            std::string_view clean = sim::strip_cr(line);
            if (sim::is_comment_or_empty(clean)) {
                continue;
            }
            if (headers.empty()) {
                headers = sim::parse_csv_line(clean);
                continue;
            }

            sim::ParsedRow row = sim::csv_row_to_json(headers, clean);
            auto epoch_ms = sim::TimestampPacer::resolve_row_epoch_ms(row);
            day_ts.record(epoch_ms);
            stats.sim_elapsed_ms.store(completed_days_sim_ms + day_ts.elapsed_ms(),
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

        completed_days_sim_ms += day_ts.elapsed_ms();
        stats.sim_elapsed_ms.store(completed_days_sim_ms, std::memory_order_relaxed);
    }

    pool.shutdown_and_join();

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

    std::cout << std::fixed << std::setprecision(2)
              << "[summary] files=" << files.size()
              << " read=" << read
              << " sent=" << sent
              << " errors=" << errors
              << " wall=" << total_sec << "s"
              << " sim=" << sim_sec << "s"
              << " avg_req/s=" << std::setprecision(1) << rps
              << " speedup=" << std::setprecision(2) << speedup << "x\n";

    return (read_error || errors > 0) ? 1 : 0;
}

int run_probe_mode(const CliOptions& opts,
                   const sim::HttpUrl& target_url,
                   const std::vector<std::string>& files) {
    std::vector<char> io_buf(sim::FILE_STREAM_BUFFER_BYTES);
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
            return 1;
        }

        std::string filename = std::filesystem::path(path).filename().string();
        LiveStatsState stats;
        {
            std::lock_guard<std::mutex> lock(stats.file_mu);
            stats.current_filename = filename;
        }

        ShardedWorkerPool pool(opts.concurrency, target_url);
        auto day_start_tp = std::chrono::steady_clock::now();

        std::mutex stats_mu;
        std::condition_variable stats_cv;
        bool stats_done = false;
        std::thread stats_thread([&]() {
            auto prev_tp = day_start_tp;
            uint64_t prev_completed = 0;
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

        DayTimestampTracker day_ts;
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
                continue;
            }

            sim::ParsedRow row = sim::csv_row_to_json(headers, clean);
            auto epoch_ms = sim::TimestampPacer::resolve_row_epoch_ms(row);
            day_ts.record(epoch_ms);
            stats.sim_elapsed_ms.store(day_ts.elapsed_ms(), std::memory_order_relaxed);

            ++day_read;
            stats.ticks_read.fetch_add(1, std::memory_order_relaxed);
            if (!pool.push(row.symbol_id, std::move(row.json_payload))) {
                break;
            }
            if (opts.probe_ticks > 0 && day_read >= opts.probe_ticks) {
                break;
            }
        }

        pool.shutdown_and_join();
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
        int64_t sim_ms = day_ts.elapsed_ms();
        double sim_sec = static_cast<double>(sim_ms) / static_cast<double>(sim::MS_PER_SECOND);
        uint64_t sent = pool.ticks_sent();
        uint64_t errors = pool.http_errors();
        total_errors += errors;
        double rps = (wall_sec > 0.0) ? static_cast<double>(sent + errors) / wall_sec : 0.0;
        double speedup = (wall_sec > 0.0 && sim_sec > 0.0) ? (sim_sec / wall_sec) : 0.0;

        if (speedup < min_speedup) {
            min_speedup = speedup;
        }
        ++probed_days;

        std::cout << std::fixed << std::setprecision(2)
                  << "[probe] file=" << filename
                  << " ticks=" << day_read
                  << " sent=" << sent
                  << " errors=" << errors
                  << " wall=" << wall_sec << "s"
                  << " sim=" << sim_sec << "s"
                  << " req/s=" << std::setprecision(1) << rps
                  << " speedup=" << std::setprecision(2) << speedup << "x\n";
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

    auto target_url = sim::parse_http_url(opts.url);
    if (!target_url.has_value()) {
        std::cerr << "Error: Invalid HTTP URL: " << opts.url << "\n";
        return 1;
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

    if (opts.probe) {
        return run_probe_mode(opts, *target_url, files);
    }
    return run_stream_mode(opts, *target_url, files);
}
