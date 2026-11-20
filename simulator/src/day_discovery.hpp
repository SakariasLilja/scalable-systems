#pragma once

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <optional>
#include <string>
#include <string_view>
#include <tuple>
#include <vector>

#include "constants.hpp"

namespace sim {

namespace detail {

struct DayKey {
    int year{};
    int month{};
    int day{};

    auto operator<=>(const DayKey&) const = default;
};

inline bool parse_two_digits(std::string_view s, int& out) {
    if (s.size() != 2 || !std::isdigit(static_cast<unsigned char>(s[0])) ||
        !std::isdigit(static_cast<unsigned char>(s[1]))) {
        return false;
    }
    out = (s[0] - '0') * 10 + (s[1] - '0');
    return true;
}

inline std::optional<DayKey> parse_day_key(std::string_view token) {
    if (token.size() != 8 || token[2] != '-' || token[5] != '-') {
        return std::nullopt;
    }
    int dd = 0;
    int mm = 0;
    int yy = 0;
    if (!parse_two_digits(token.substr(0, 2), dd) ||
        !parse_two_digits(token.substr(3, 2), mm) ||
        !parse_two_digits(token.substr(6, 2), yy)) {
        return std::nullopt;
    }
    if (dd < 1 || dd > 31 || mm < 1 || mm > 12) {
        return std::nullopt;
    }
    return DayKey{CENTURY_BASE_YEAR + yy, mm, dd};
}

inline std::optional<DayKey> extract_day_key_from_filename(std::string_view filename) {
    if (filename.size() < 4 || filename.compare(filename.size() - 4, 4, ".csv") != 0) {
        return std::nullopt;
    }
    std::string_view stem = filename.substr(0, filename.size() - 4);
    if (stem.size() < 8) {
        return std::nullopt;
    }
    return parse_day_key(stem.substr(stem.size() - 8));
}

inline bool ends_with_csv(std::string_view filename) {
    return filename.size() >= 4 && filename.compare(filename.size() - 4, 4, ".csv") == 0;
}

}  // namespace detail

inline std::vector<std::string> discover_trading_days(const std::string& data_dir,
                                                      const std::string& single_day = "",
                                                      const std::string& start_day = "") {
    namespace fs = std::filesystem;
    std::vector<std::string> result;
    std::error_code ec;
    if (!fs::exists(data_dir, ec) || !fs::is_directory(data_dir, ec)) {
        return result;
    }

    struct Entry {
        std::string path;
        std::string filename;
        std::optional<detail::DayKey> key;
    };

    std::vector<Entry> entries;
    for (const auto& dir_entry : fs::directory_iterator(data_dir, ec)) {
        if (ec) {
            break;
        }
        if (!dir_entry.is_regular_file(ec)) {
            continue;
        }
        std::string filename = dir_entry.path().filename().string();
        if (!detail::ends_with_csv(filename)) {
            continue;
        }
        entries.push_back(Entry{
            dir_entry.path().string(),
            filename,
            detail::extract_day_key_from_filename(filename),
        });
    }

    std::sort(entries.begin(), entries.end(), [](const Entry& a, const Entry& b) {
        if (a.key.has_value() != b.key.has_value()) {
            return a.key.has_value();
        }
        if (a.key.has_value() && *a.key != *b.key) {
            return *a.key < *b.key;
        }
        return a.path < b.path;
    });

    if (!single_day.empty()) {
        auto target_key = detail::parse_day_key(single_day);
        for (const auto& entry : entries) {
            bool matches = false;
            if (target_key.has_value() && entry.key.has_value()) {
                matches = (*entry.key == *target_key);
            } else {
                matches = (entry.filename.find(single_day) != std::string::npos);
            }
            if (matches) {
                result.push_back(entry.path);
            }
        }
        return result;
    }

    if (!start_day.empty()) {
        auto start_key = detail::parse_day_key(start_day);
        if (!start_key.has_value()) {
            return result;
        }
        for (const auto& entry : entries) {
            if (entry.key.has_value() && *entry.key >= *start_key) {
                result.push_back(entry.path);
            }
        }
        return result;
    }

    result.reserve(entries.size());
    for (const auto& entry : entries) {
        result.push_back(entry.path);
    }
    return result;
}

}  // namespace sim
