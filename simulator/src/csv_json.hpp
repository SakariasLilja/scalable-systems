#pragma once

#include <cctype>
#include <string>
#include <string_view>
#include <vector>

namespace sim {

struct ParsedRow {
    std::string json_payload;
    std::string symbol_id;
    std::string date;
    std::string time;
    std::string trading_date;
    std::string trading_time;
};

inline std::string_view strip_cr(std::string_view line) {
    if (!line.empty() && line.back() == '\r') {
        line.remove_suffix(1);
    }
    return line;
}

inline bool is_comment_or_empty(std::string_view line) {
    line = strip_cr(line);
    for (char ch : line) {
        if (ch == '#') {
            return true;
        }
        if (ch != ' ' && ch != '\t' && ch != '\n') {
            return false;
        }
    }
    return true;
}

inline std::vector<std::string> parse_csv_line(std::string_view line) {
    line = strip_cr(line);
    std::vector<std::string> fields;
    if (line.empty()) {
        return fields;
    }

    std::string current;
    bool in_quotes = false;
    for (std::size_t i = 0; i < line.size(); ++i) {
        char ch = line[i];
        if (in_quotes) {
            if (ch == '"') {
                if (i + 1 < line.size() && line[i + 1] == '"') {
                    current.push_back('"');
                    ++i;
                } else {
                    in_quotes = false;
                }
            } else {
                current.push_back(ch);
            }
        } else {
            if (ch == '"' && current.empty()) {
                in_quotes = true;
            } else if (ch == ',') {
                fields.push_back(std::move(current));
                current.clear();
            } else {
                current.push_back(ch);
            }
        }
    }
    fields.push_back(std::move(current));
    return fields;
}

namespace detail {

inline std::string_view trim_ascii(std::string_view sv) {
    while (!sv.empty() && (sv.front() == ' ' || sv.front() == '\t')) {
        sv.remove_prefix(1);
    }
    while (!sv.empty() && (sv.back() == ' ' || sv.back() == '\t')) {
        sv.remove_suffix(1);
    }
    return sv;
}

inline bool iequals_trimmed(std::string_view a, std::string_view b) {
    a = trim_ascii(a);
    b = trim_ascii(b);
    if (a.size() != b.size()) {
        return false;
    }
    for (std::size_t i = 0; i < a.size(); ++i) {
        if (std::tolower(static_cast<unsigned char>(a[i])) !=
            std::tolower(static_cast<unsigned char>(b[i]))) {
            return false;
        }
    }
    return true;
}

inline void append_json_escaped(std::string& out, std::string_view sv) {
    static constexpr char HEX_DIGITS[] = "0123456789abcdef";
    out.push_back('"');
    for (unsigned char uc : sv) {
        switch (uc) {
            case '"':
                out.append("\\\"");
                break;
            case '\\':
                out.append("\\\\");
                break;
            case '\b':
                out.append("\\b");
                break;
            case '\f':
                out.append("\\f");
                break;
            case '\n':
                out.append("\\n");
                break;
            case '\r':
                out.append("\\r");
                break;
            case '\t':
                out.append("\\t");
                break;
            default:
                if (uc < 0x20) {
                    out.append("\\u00");
                    out.push_back(HEX_DIGITS[(uc >> 4) & 0x0F]);
                    out.push_back(HEX_DIGITS[uc & 0x0F]);
                } else {
                    out.push_back(static_cast<char>(uc));
                }
                break;
        }
    }
    out.push_back('"');
}

}  // namespace detail

inline ParsedRow csv_row_to_json(const std::vector<std::string>& headers, std::string_view line) {
    auto fields = parse_csv_line(line);
    ParsedRow row;
    row.json_payload.reserve(line.size() + headers.size() * 8 + 16);
    row.json_payload.push_back('{');

    for (std::size_t i = 0; i < headers.size(); ++i) {
        if (i > 0) {
            row.json_payload.push_back(',');
        }
        std::string_view value =
            (i < fields.size()) ? std::string_view(fields[i]) : std::string_view{};
        detail::append_json_escaped(row.json_payload, headers[i]);
        row.json_payload.push_back(':');
        detail::append_json_escaped(row.json_payload, value);

        if (detail::iequals_trimmed(headers[i], "ID")) {
            row.symbol_id = std::string(value);
        } else if (detail::iequals_trimmed(headers[i], "Date")) {
            row.date = std::string(value);
        } else if (detail::iequals_trimmed(headers[i], "Time")) {
            row.time = std::string(value);
        } else if (detail::iequals_trimmed(headers[i], "Trading date")) {
            row.trading_date = std::string(value);
        } else if (detail::iequals_trimmed(headers[i], "Trading time")) {
            row.trading_time = std::string(value);
        }
    }

    for (std::size_t i = headers.size(); i < fields.size(); ++i) {
        if (i > 0) {
            row.json_payload.push_back(',');
        }
        std::string extra_key = "_extra_" + std::to_string(i - headers.size());
        detail::append_json_escaped(row.json_payload, extra_key);
        row.json_payload.push_back(':');
        detail::append_json_escaped(row.json_payload, fields[i]);
    }

    row.json_payload.push_back('}');
    return row;
}

}  // namespace sim
