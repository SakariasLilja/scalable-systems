#pragma once

#include <cstdint>
#include <optional>

#include "constants.hpp"

namespace sim {

class BurstTracker {
public:
    explicit BurstTracker(int64_t bucket_ms)
        : bucket_ms_(bucket_ms > 0 ? bucket_ms : MS_PER_SECOND) {}

    void record(std::optional<int64_t> epoch_ms) {
        if (!epoch_ms.has_value()) {
            return;
        }
        int64_t bucket_start = (*epoch_ms / bucket_ms_) * bucket_ms_;
        if (!has_bucket_) {
            has_bucket_ = true;
            current_bucket_start_ms_ = bucket_start;
            current_count_ = 1;
        } else if (bucket_start < current_bucket_start_ms_) {
            return;
        } else if (bucket_start == current_bucket_start_ms_) {
            ++current_count_;
        } else {
            current_bucket_start_ms_ = bucket_start;
            current_count_ = 1;
        }

        if (current_count_ > peak_count_) {
            peak_count_ = current_count_;
            peak_bucket_start_ms_ = current_bucket_start_ms_;
        }
    }

    uint64_t peak_count() const { return peak_count_; }
    int64_t peak_bucket_start_ms() const { return peak_bucket_start_ms_; }
    int64_t bucket_ms() const { return bucket_ms_; }

    double peak_rows_per_sim_sec() const {
        if (peak_count_ == 0 || bucket_ms_ <= 0) {
            return 0.0;
        }
        double bucket_sec = static_cast<double>(bucket_ms_) / static_cast<double>(MS_PER_SECOND);
        return static_cast<double>(peak_count_) / bucket_sec;
    }

private:
    int64_t bucket_ms_;
    bool has_bucket_{false};
    int64_t current_bucket_start_ms_{0};
    uint64_t current_count_{0};
    uint64_t peak_count_{0};
    int64_t peak_bucket_start_ms_{0};
};

inline double compute_maintainable_speedup(double reader_rps, double peak_sim_rps) {
    if (reader_rps <= 0.0 || peak_sim_rps <= 0.0) {
        return 0.0;
    }
    return reader_rps / peak_sim_rps;
}

}  // namespace sim
