#pragma once

#include <cstddef>
#include <cstdint>

namespace sim {

inline constexpr int64_t MAX_SIMULATION_DELAY_MS = 2000;
inline constexpr int64_t DEFAULT_PROBE_TICKS = 20000;
inline constexpr int64_t DEFAULT_BURST_BUCKET_SEC = 10;
inline constexpr int64_t STRICT_BURST_BUCKET_SEC = 1;
inline constexpr int64_t MS_PER_SECOND = 1000;
inline constexpr int64_t SECONDS_PER_MINUTE = 60;
inline constexpr int64_t MINUTES_PER_HOUR = 60;
inline constexpr int64_t HOURS_PER_DAY = 24;
inline constexpr int CENTURY_BASE_YEAR = 2000;

inline constexpr std::size_t MAX_SHARD_QUEUE_SIZE = 4096;
inline constexpr int DEFAULT_CONCURRENCY = 16;
inline constexpr int DEFAULT_BATCH_SIZE = 1;
inline constexpr int64_t BATCH_FLUSH_TIMEOUT_MS = 10;
inline constexpr int MAX_SEEK_ITERATIONS = 64;
inline constexpr int MAX_SEEK_SCAN_LINES = 256;
inline constexpr const char* DEFAULT_PROBE_START_TIME = "09:00:00";
inline constexpr double DEFAULT_SPEED = 1.0;
inline constexpr int64_t STATS_INTERVAL_SECONDS = 2;
inline constexpr int64_t QUEUE_POLL_TIMEOUT_MS = 50;
inline constexpr int64_t SLEEP_SLICE_MS = 100;
inline constexpr std::size_t FILE_STREAM_BUFFER_BYTES = 1024 * 1024;
inline constexpr double BYTES_PER_MIB = 1024.0 * 1024.0;

inline constexpr int64_t HTTP_IO_TIMEOUT_SEC = 10;
inline constexpr std::size_t MAX_HTTP_HEADER_BYTES = 16384;
inline constexpr std::size_t HTTP_RECV_BUFFER_SIZE = 8192;
inline constexpr int MIN_VALID_PORT = 1;
inline constexpr int MAX_VALID_PORT = 65535;
inline constexpr int HTTP_STATUS_OK_MIN = 200;
inline constexpr int HTTP_STATUS_OK_MAX = 299;
inline constexpr int HTTP_STATUS_INFO_MIN = 100;
inline constexpr int HTTP_STATUS_INFO_MAX = 199;
inline constexpr int HTTP_STATUS_NO_CONTENT = 204;
inline constexpr int HTTP_STATUS_NOT_MODIFIED = 304;

inline constexpr const char* DEFAULT_HTTP_PORT = "80";
inline constexpr const char* DEFAULT_HTTP_PATH = "/";
inline constexpr const char* DEFAULT_DATA_DIR = "./data";
inline constexpr const char* DEFAULT_INGEST_URL = "http://localhost:8080/api/ingest";

}  // namespace sim
