#pragma once

#include <chrono>
#include <cmath>
#include <cstdint>
#include <optional>
#include <string_view>

#include "constants.hpp"
#include "csv_json.hpp"

namespace sim {

class TimestampPacer {
public:
    static std::optional<int64_t> parse_epoch_ms(std::string_view date_str,
                                                 std::string_view time_str) {
        date_str = detail::trim_ascii(date_str);
        time_str = detail::trim_ascii(time_str);
        if (date_str.empty() || time_str.empty()) {
            return std::nullopt;
        }

        int year = 0;
        int month = 0;
        int day = 0;
        if (date_str.size() != 10) {
            return std::nullopt;
        }
        if (date_str[2] == '-' && date_str[5] == '-') {
            if (!parse_uint(date_str.substr(0, 2), day) ||
                !parse_uint(date_str.substr(3, 2), month) ||
                !parse_uint(date_str.substr(6, 4), year)) {
                return std::nullopt;
            }
        } else if (date_str[4] == '-' && date_str[7] == '-') {
            if (!parse_uint(date_str.substr(0, 4), year) ||
                !parse_uint(date_str.substr(5, 2), month) ||
                !parse_uint(date_str.substr(8, 2), day)) {
                return std::nullopt;
            }
        } else {
            return std::nullopt;
        }

        std::chrono::year_month_day ymd{
            std::chrono::year{year},
            std::chrono::month{static_cast<unsigned>(month)},
            std::chrono::day{static_cast<unsigned>(day)},
        };
        if (!ymd.ok()) {
            return std::nullopt;
        }

        if (time_str.size() < 8 || time_str[2] != ':' || time_str[5] != ':') {
            return std::nullopt;
        }
        int hour = 0;
        int minute = 0;
        int second = 0;
        if (!parse_uint(time_str.substr(0, 2), hour) ||
            !parse_uint(time_str.substr(3, 2), minute) ||
            !parse_uint(time_str.substr(6, 2), second)) {
            return std::nullopt;
        }
        if (hour >= HOURS_PER_DAY || minute >= MINUTES_PER_HOUR || second >= SECONDS_PER_MINUTE) {
            return std::nullopt;
        }

        int millis = 0;
        if (time_str.size() > 8) {
            if (time_str[8] != '.' || time_str.size() == 9) {
                return std::nullopt;
            }
            std::string_view frac = time_str.substr(9);
            int scale = 100;
            for (char ch : frac) {
                if (ch < '0' || ch > '9') {
                    return std::nullopt;
                }
                if (scale > 0) {
                    millis += (ch - '0') * scale;
                    scale /= 10;
                }
            }
        }

        auto day_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                          std::chrono::sys_days{ymd}.time_since_epoch())
                          .count();
        int64_t tod_ms = ((static_cast<int64_t>(hour) * MINUTES_PER_HOUR + minute) *
                              SECONDS_PER_MINUTE +
                          second) *
                             MS_PER_SECOND +
                         millis;
        return day_ms + tod_ms;
    }

    static std::optional<int64_t> resolve_row_epoch_ms(const ParsedRow& row) {
        if (auto trading_ts = parse_epoch_ms(row.trading_date, row.trading_time)) {
            return trading_ts;
        }
        return parse_epoch_ms(row.date, row.time);
    }

    int64_t compute_delay_ms(std::optional<int64_t> event_epoch_ms, double speed_factor) {
        if (!event_epoch_ms.has_value()) {
            return 0;
        }
        int64_t current = *event_epoch_ms;
        if (!prev_epoch_ms_.has_value()) {
            prev_epoch_ms_ = current;
            return 0;
        }

        int64_t delta = current - *prev_epoch_ms_;
        prev_epoch_ms_ = current;

        if (delta <= 0 || speed_factor <= 0.0 || !std::isfinite(speed_factor)) {
            return 0;
        }

        double scaled = static_cast<double>(delta) / speed_factor;
        if (scaled >= static_cast<double>(MAX_SIMULATION_DELAY_MS)) {
            return MAX_SIMULATION_DELAY_MS;
        }
        return static_cast<int64_t>(scaled);
    }

    void reset() {
        prev_epoch_ms_.reset();
    }

private:
    static bool parse_uint(std::string_view sv, int& out) {
        if (sv.empty()) {
            return false;
        }
        int val = 0;
        for (char ch : sv) {
            if (ch < '0' || ch > '9') {
                return false;
            }
            val = val * 10 + (ch - '0');
        }
        out = val;
        return true;
    }

    std::optional<int64_t> prev_epoch_ms_;
};

}  // namespace sim
