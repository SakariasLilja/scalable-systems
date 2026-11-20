#include <cassert>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

#include "constants.hpp"
#include "csv_json.hpp"
#include "day_discovery.hpp"
#include "timestamp_pacer.hpp"

namespace fs = std::filesystem;

static fs::path make_temp_dir(const std::string& name) {
    fs::path dir = fs::temp_directory_path() / ("sim_test_" + name);
    fs::remove_all(dir);
    fs::create_directories(dir);
    return dir;
}

static void touch_file(const fs::path& p) {
    std::ofstream out(p);
    out << "stub\n";
}

void test_discover_trading_days_chronological_and_filters_zone_identifier() {
    fs::path dir = make_temp_dir("days");
    touch_file(dir / "debs2022-gc-trading-day-01-12-21.csv");
    touch_file(dir / "debs2022-gc-trading-day-30-11-21.csv");
    touch_file(dir / "debs2022-gc-trading-day-08-11-21.csv");
    touch_file(dir / "debs2022-gc-trading-day-08-11-21.csv:Zone.Identifier");
    touch_file(dir / "debs2022-gc-trading-day-30-11-21.csv:Zone.Identifier");
    touch_file(dir / "notes.txt");
    fs::create_directories(dir / "nested.csv");

    auto all_days = sim::discover_trading_days(dir.string());
    assert(all_days.size() == 3);
    assert(fs::path(all_days[0]).filename() == "debs2022-gc-trading-day-08-11-21.csv");
    assert(fs::path(all_days[1]).filename() == "debs2022-gc-trading-day-30-11-21.csv");
    assert(fs::path(all_days[2]).filename() == "debs2022-gc-trading-day-01-12-21.csv");

    auto single = sim::discover_trading_days(dir.string(), "30-11-21", "");
    assert(single.size() == 1);
    assert(fs::path(single[0]).filename() == "debs2022-gc-trading-day-30-11-21.csv");

    auto missing_single = sim::discover_trading_days(dir.string(), "99-99-99", "");
    assert(missing_single.empty());

    auto from_start = sim::discover_trading_days(dir.string(), "", "30-11-21");
    assert(from_start.size() == 2);
    assert(fs::path(from_start[0]).filename() == "debs2022-gc-trading-day-30-11-21.csv");
    assert(fs::path(from_start[1]).filename() == "debs2022-gc-trading-day-01-12-21.csv");

    fs::remove_all(dir);
}

void test_csv_row_to_json_preserves_raw_values_and_escapes_quotes() {
    assert(sim::strip_cr("abc\r") == "abc");
    assert(sim::strip_cr("abc") == "abc");
    assert(sim::is_comment_or_empty(""));
    assert(sim::is_comment_or_empty("   \t"));
    assert(sim::is_comment_or_empty("# license header\r"));
    assert(sim::is_comment_or_empty("#'Unique ID','Security Type'"));
    assert(!sim::is_comment_or_empty("ID,SecType,Date,Time"));

    std::string header_line = "ID,SecType,Date,Time,Last,Trading time,Trading date, Note";
    auto headers = sim::parse_csv_line(sim::strip_cr(header_line));
    assert(headers.size() == 8);
    assert(headers[7] == " Note");

    std::string data_line =
        "A0HN5C.ETR,E,08-11-2021,08:00:01.123,123.45,08:00:01.120,08-11-2021,\"quoted, \"\"hi\"\" \\ path\"\r";
    auto row = sim::csv_row_to_json(headers, sim::strip_cr(data_line));
    assert(row.symbol_id == "A0HN5C.ETR");
    assert(row.date == "08-11-2021");
    assert(row.time == "08:00:01.123");
    assert(row.trading_time == "08:00:01.120");
    assert(row.trading_date == "08-11-2021");
    assert(row.json_payload.find("\"ID\":\"A0HN5C.ETR\"") != std::string::npos);
    assert(row.json_payload.find("\"Last\":\"123.45\"") != std::string::npos);
    assert(row.json_payload.find("\" Note\":\"quoted, \\\"hi\\\" \\\\ path\"") != std::string::npos);

    std::string short_line = "SAP.ETR,E";
    auto short_row = sim::csv_row_to_json(headers, short_line);
    assert(short_row.symbol_id == "SAP.ETR");
    assert(short_row.json_payload.find("\"Last\":\"\"") != std::string::npos);
    assert(short_row.json_payload.find("\" Note\":\"\"") != std::string::npos);

    std::string extra_line = "BMW.ETR,E,08-11-2021,09:00:00.000,90.1,,08-11-2021,ok,extra1,extra2";
    auto extra_row = sim::csv_row_to_json(headers, extra_line);
    assert(extra_row.symbol_id == "BMW.ETR");
    assert(extra_row.json_payload.find("\"_extra_0\":\"extra1\"") != std::string::npos);
    assert(extra_row.json_payload.find("\"_extra_1\":\"extra2\"") != std::string::npos);

    std::vector<std::string> sample_lines = {
        "# DEBS 2022 Grand Challenge\r",
        "#\r",
        "ID,SecType,Date,Time,Last,Trading time,Trading date, Day low bid time, Bid time\r",
        "#'Unique ID','Security Type'\r",
        "A0HN5C.ETR,E,13-11-2021,01:01:23.126,,::,,\t\x01,\r",
    };
    std::vector<std::string> parsed_headers;
    std::vector<sim::ParsedRow> parsed_rows;
    for (const auto& raw : sample_lines) {
        auto clean = sim::strip_cr(raw);
        if (sim::is_comment_or_empty(clean)) {
            continue;
        }
        if (parsed_headers.empty()) {
            parsed_headers = sim::parse_csv_line(clean);
        } else {
            parsed_rows.push_back(sim::csv_row_to_json(parsed_headers, clean));
        }
    }
    assert(parsed_headers.size() == 9);
    assert(parsed_headers[7] == " Day low bid time");
    assert(parsed_rows.size() == 1);
    assert(parsed_rows[0].symbol_id == "A0HN5C.ETR");
    assert(parsed_rows[0].trading_time == "::");
    assert(parsed_rows[0].json_payload.find("\" Day low bid time\":\"\\t\\u0001\"") != std::string::npos);
}

void test_timestamp_pacer_handles_colons_and_caps_day_transition_gap() {
    assert(!sim::TimestampPacer::parse_epoch_ms("", "").has_value());
    assert(!sim::TimestampPacer::parse_epoch_ms("08-11-2021", "::").has_value());
    assert(!sim::TimestampPacer::parse_epoch_ms("bad-date", "12:00:00.000").has_value());
    assert(!sim::TimestampPacer::parse_epoch_ms("08-11-2021", "99:99:99.000").has_value());

    auto t1 = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "08:00:00.000");
    auto t2 = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "08:00:01.500");
    auto t3 = sim::TimestampPacer::parse_epoch_ms("2021-11-08", "08:00:01");
    auto t_next_day = sim::TimestampPacer::parse_epoch_ms("09-11-2021", "08:00:00.000");
    assert(t1.has_value() && t2.has_value() && t3.has_value() && t_next_day.has_value());
    assert(*t2 - *t1 == 1500);
    assert(*t3 - *t1 == 1000);

    sim::ParsedRow row_pref{};
    row_pref.date = "08-11-2021";
    row_pref.time = "08:00:05.000";
    row_pref.trading_date = "08-11-2021";
    row_pref.trading_time = "08:00:01.500";
    assert(sim::TimestampPacer::resolve_row_epoch_ms(row_pref) == t2);

    sim::ParsedRow row_fallback{};
    row_fallback.date = "08-11-2021";
    row_fallback.time = "08:00:00.000";
    row_fallback.trading_date = "";
    row_fallback.trading_time = "::";
    assert(sim::TimestampPacer::resolve_row_epoch_ms(row_fallback) == t1);

    sim::TimestampPacer pacer;
    assert(pacer.compute_delay_ms(std::nullopt, 1.0) == 0);
    assert(pacer.compute_delay_ms(t1, 1.0) == 0);
    assert(pacer.compute_delay_ms(t2, 1.0) == 1500);

    pacer.reset();
    assert(pacer.compute_delay_ms(t1, 100.0) == 0);
    assert(pacer.compute_delay_ms(t2, 100.0) == 15);

    assert(pacer.compute_delay_ms(t_next_day, 1.0) == sim::MAX_SIMULATION_DELAY_MS);
    assert(pacer.compute_delay_ms(t1, 1.0) == 0);

    pacer.reset();
    assert(pacer.compute_delay_ms(t1, 0.0) == 0);
    assert(pacer.compute_delay_ms(t2, 0.0) == 0);
}

int main() {
    test_discover_trading_days_chronological_and_filters_zone_identifier();
    test_csv_row_to_json_preserves_raw_values_and_escapes_quotes();
    test_timestamp_pacer_handles_colons_and_caps_day_transition_gap();
    std::cout << "All simulator tests passed.\n";
    return 0;
}
