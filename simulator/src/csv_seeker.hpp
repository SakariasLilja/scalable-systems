#pragma once

#include <cctype>
#include <cstdint>
#include <fstream>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "constants.hpp"
#include "csv_json.hpp"

namespace sim {

inline std::optional<int64_t> parse_time_of_day_ms(std::string_view text) {
    while (!text.empty() && std::isspace(static_cast<unsigned char>(text.front()))) {
        text.remove_prefix(1);
    }
    while (!text.empty() && std::isspace(static_cast<unsigned char>(text.back()))) {
        text.remove_suffix(1);
    }
    if (text.size() < 5) {
        return std::nullopt;
    }

    auto parse_two = [](std::string_view s, int& out) -> bool {
        if (s.size() != 2 || !std::isdigit(static_cast<unsigned char>(s[0])) ||
            !std::isdigit(static_cast<unsigned char>(s[1]))) {
            return false;
        }
        out = (s[0] - '0') * 10 + (s[1] - '0');
        return true;
    };

    if (text[2] != ':') {
        return std::nullopt;
    }
    int hh = 0;
    int mm = 0;
    int ss = 0;
    int ms = 0;
    if (!parse_two(text.substr(0, 2), hh) || !parse_two(text.substr(3, 2), mm)) {
        return std::nullopt;
    }
    std::size_t pos = 5;
    if (pos < text.size()) {
        if (text[pos] != ':' || text.size() < pos + 3) {
            return std::nullopt;
        }
        if (!parse_two(text.substr(pos + 1, 2), ss)) {
            return std::nullopt;
        }
        pos += 3;
        if (pos < text.size()) {
            if (text[pos] != '.') {
                return std::nullopt;
            }
            ++pos;
            int mult = 100;
            int digits = 0;
            while (pos < text.size() && std::isdigit(static_cast<unsigned char>(text[pos]))) {
                if (mult > 0) {
                    ms += (text[pos] - '0') * mult;
                    mult /= 10;
                }
                ++pos;
                ++digits;
            }
            if (digits == 0 || pos != text.size()) {
                return std::nullopt;
            }
        }
    }

    if (hh < 0 || hh >= HOURS_PER_DAY || mm < 0 || mm >= MINUTES_PER_HOUR ||
        ss < 0 || ss >= SECONDS_PER_MINUTE) {
        return std::nullopt;
    }
    int64_t total_sec = static_cast<int64_t>(hh) * MINUTES_PER_HOUR * SECONDS_PER_MINUTE +
                        static_cast<int64_t>(mm) * SECONDS_PER_MINUTE +
                        static_cast<int64_t>(ss);
    return total_sec * MS_PER_SECOND + ms;
}

inline int find_time_column_index(const std::vector<std::string>& headers, const char* name) {
    for (std::size_t i = 0; i < headers.size(); ++i) {
        if (headers[i] == name) {
            return static_cast<int>(i);
        }
    }
    return -1;
}

inline std::optional<int64_t> extract_row_time_ms(const std::string& raw_line,
                                                  int last_time_idx,
                                                  int time_idx) {
    std::string_view clean = strip_cr(raw_line);
    if (is_comment_or_empty(clean)) {
        return std::nullopt;
    }
    auto fields = parse_csv_line(clean);
    if (last_time_idx >= 0 && static_cast<std::size_t>(last_time_idx) < fields.size() &&
        !fields[static_cast<std::size_t>(last_time_idx)].empty()) {
        if (auto t = parse_time_of_day_ms(fields[static_cast<std::size_t>(last_time_idx)])) {
            return t;
        }
    }
    if (time_idx >= 0 && static_cast<std::size_t>(time_idx) < fields.size() &&
        !fields[static_cast<std::size_t>(time_idx)].empty()) {
        if (auto t = parse_time_of_day_ms(fields[static_cast<std::size_t>(time_idx)])) {
            return t;
        }
    }
    return std::nullopt;
}

inline std::streamoff seek_csv_to_time(std::ifstream& in,
                                       int64_t target_ms,
                                       const std::vector<std::string>& headers) {
    in.clear();
    std::streamoff data_start = in.tellg();
    if (data_start < 0) {
        return 0;
    }

    int last_time_idx = find_time_column_index(headers, "LastTime");
    int time_idx = find_time_column_index(headers, "Time");
    if (last_time_idx < 0 && time_idx < 0) {
        in.clear();
        in.seekg(data_start);
        return data_start;
    }

    in.seekg(0, std::ios::end);
    std::streamoff file_end = in.tellg();
    if (file_end <= data_start) {
        in.clear();
        in.seekg(data_start);
        return data_start;
    }

    std::streamoff lo = data_start;
    std::streamoff hi = file_end;
    std::streamoff best_offset = file_end;
    std::string line;

    for (int iter = 0; iter < MAX_SEEK_ITERATIONS && lo < hi; ++iter) {
        std::streamoff mid = lo + (hi - lo) / 2;
        in.clear();
        in.seekg(mid);
        if (mid > data_start) {
            if (!std::getline(in, line)) {
                hi = mid;
                continue;
            }
        }

        std::optional<int64_t> found_ms;
        std::streamoff found_line_start = -1;
        std::streamoff found_line_end = -1;

        for (int scan = 0; scan < MAX_SEEK_SCAN_LINES; ++scan) {
            std::streamoff line_start = in.tellg();
            if (line_start < 0 || line_start >= hi) {
                break;
            }
            if (!std::getline(in, line)) {
                break;
            }
            auto t = extract_row_time_ms(line, last_time_idx, time_idx);
            if (t.has_value()) {
                found_ms = t;
                found_line_start = line_start;
                std::streamoff next_pos = in.tellg();
                found_line_end = (next_pos >= 0) ? next_pos : file_end;
                break;
            }
        }

        if (!found_ms.has_value()) {
            hi = mid;
            continue;
        }

        if (*found_ms < target_ms) {
            lo = (found_line_end > mid) ? found_line_end : (mid + 1);
        } else {
            best_offset = found_line_start;
            hi = mid;
        }
    }

    if (best_offset > data_start && best_offset < file_end) {
        in.clear();
        in.seekg(best_offset);
    } else if (best_offset == data_start) {
        in.clear();
        in.seekg(data_start);
    } else {
        in.clear();
        in.seekg(file_end);
    }
    return best_offset;
}

}  // namespace sim
