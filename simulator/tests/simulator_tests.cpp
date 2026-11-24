#include <cmath>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <vector>

#include "burst_tracker.hpp"
#include "constants.hpp"
#include "csv_json.hpp"
#include "csv_seeker.hpp"
#include "day_discovery.hpp"
#include "market_shard.hpp"
#include "timestamp_pacer.hpp"

namespace fs = std::filesystem;

#define CHECK(cond)                                                              \
    do {                                                                         \
        if (!(cond)) {                                                           \
            std::cerr << "CHECK failed (" << __FILE__ << ":" << __LINE__ << "): "\
                      << #cond << "\n";                                          \
            std::exit(1);                                                        \
        }                                                                        \
    } while (false)

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
    CHECK(all_days.size() == 3);
    CHECK(fs::path(all_days[0]).filename() == "debs2022-gc-trading-day-08-11-21.csv");
    CHECK(fs::path(all_days[1]).filename() == "debs2022-gc-trading-day-30-11-21.csv");
    CHECK(fs::path(all_days[2]).filename() == "debs2022-gc-trading-day-01-12-21.csv");

    auto single = sim::discover_trading_days(dir.string(), "30-11-21", "");
    CHECK(single.size() == 1);
    CHECK(fs::path(single[0]).filename() == "debs2022-gc-trading-day-30-11-21.csv");

    auto missing_single = sim::discover_trading_days(dir.string(), "99-99-99", "");
    CHECK(missing_single.empty());

    auto from_start = sim::discover_trading_days(dir.string(), "", "30-11-21");
    CHECK(from_start.size() == 2);
    CHECK(fs::path(from_start[0]).filename() == "debs2022-gc-trading-day-30-11-21.csv");
    CHECK(fs::path(from_start[1]).filename() == "debs2022-gc-trading-day-01-12-21.csv");

    auto invalid_start = sim::discover_trading_days(dir.string(), "", "99-99-99");
    CHECK(invalid_start.empty());

    auto full_year_start = sim::discover_trading_days(dir.string(), "", "08-11-2021");
    CHECK(full_year_start.empty());

    fs::remove_all(dir);
}

void test_csv_row_to_json_preserves_raw_values_and_escapes_quotes() {
    CHECK(sim::strip_cr("abc\r") == "abc");
    CHECK(sim::strip_cr("abc") == "abc");
    CHECK(sim::is_comment_or_empty(""));
    CHECK(sim::is_comment_or_empty("   \t"));
    CHECK(sim::is_comment_or_empty("# license header\r"));
    CHECK(sim::is_comment_or_empty("#'Unique ID','Security Type'"));
    CHECK(!sim::is_comment_or_empty("ID,SecType,Date,Time"));

    std::string header_line = "ID,SecType,Date,Time,Last,Trading time,Trading date, Note";
    auto headers = sim::parse_csv_line(sim::strip_cr(header_line));
    CHECK(headers.size() == 8);
    CHECK(headers[7] == " Note");

    std::string data_line =
        "A0HN5C.ETR,E,08-11-2021,08:00:01.123,123.45,08:00:01.120,08-11-2021,\"quoted, \"\"hi\"\" \\ path\"\r";
    auto row = sim::csv_row_to_json(headers, sim::strip_cr(data_line));
    CHECK(row.symbol_id == "A0HN5C.ETR");
    CHECK(row.date == "08-11-2021");
    CHECK(row.time == "08:00:01.123");
    CHECK(row.trading_time == "08:00:01.120");
    CHECK(row.trading_date == "08-11-2021");
    CHECK(row.json_payload.find("\"ID\":\"A0HN5C.ETR\"") != std::string::npos);
    CHECK(row.json_payload.find("\"Last\":\"123.45\"") != std::string::npos);
    CHECK(row.json_payload.find("\" Note\":\"quoted, \\\"hi\\\" \\\\ path\"") != std::string::npos);

    std::string short_line = "SAP.ETR,E";
    auto short_row = sim::csv_row_to_json(headers, short_line);
    CHECK(short_row.symbol_id == "SAP.ETR");
    CHECK(short_row.json_payload.find("\"Last\":\"\"") != std::string::npos);
    CHECK(short_row.json_payload.find("\" Note\":\"\"") != std::string::npos);

    std::string extra_line = "BMW.ETR,E,08-11-2021,09:00:00.000,90.1,,08-11-2021,ok,extra1,extra2";
    auto extra_row = sim::csv_row_to_json(headers, extra_line);
    CHECK(extra_row.symbol_id == "BMW.ETR");
    CHECK(extra_row.json_payload.find("\"_extra_0\":\"extra1\"") != std::string::npos);
    CHECK(extra_row.json_payload.find("\"_extra_1\":\"extra2\"") != std::string::npos);

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
    CHECK(parsed_headers.size() == 9);
    CHECK(parsed_headers[7] == " Day low bid time");
    CHECK(parsed_rows.size() == 1);
    CHECK(parsed_rows[0].symbol_id == "A0HN5C.ETR");
    CHECK(parsed_rows[0].trading_time == "::");
    CHECK(parsed_rows[0].json_payload.find("\" Day low bid time\":\"\\t\\u0001\"") != std::string::npos);
}

void test_timestamp_pacer_handles_colons_and_caps_day_transition_gap() {
    CHECK(!sim::TimestampPacer::parse_epoch_ms("", "").has_value());
    CHECK(!sim::TimestampPacer::parse_epoch_ms("08-11-2021", "::").has_value());
    CHECK(!sim::TimestampPacer::parse_epoch_ms("bad-date", "12:00:00.000").has_value());
    CHECK(!sim::TimestampPacer::parse_epoch_ms("08-11-2021", "99:99:99.000").has_value());

    auto t1 = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "08:00:00.000");
    auto t2 = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "08:00:01.500");
    auto t3 = sim::TimestampPacer::parse_epoch_ms("2021-11-08", "08:00:01");
    auto t_next_day = sim::TimestampPacer::parse_epoch_ms("09-11-2021", "08:00:00.000");
    CHECK(t1.has_value() && t2.has_value() && t3.has_value() && t_next_day.has_value());
    CHECK(*t2 - *t1 == 1500);
    CHECK(*t3 - *t1 == 1000);

    sim::ParsedRow row_pref{};
    row_pref.date = "08-11-2021";
    row_pref.time = "08:00:05.000";
    row_pref.trading_date = "08-11-2021";
    row_pref.trading_time = "08:00:01.500";
    CHECK(sim::TimestampPacer::resolve_row_epoch_ms(row_pref) == t2);

    sim::ParsedRow row_fallback{};
    row_fallback.date = "08-11-2021";
    row_fallback.time = "08:00:00.000";
    row_fallback.trading_date = "";
    row_fallback.trading_time = "::";
    CHECK(sim::TimestampPacer::resolve_row_epoch_ms(row_fallback) == t1);

    sim::TimestampPacer pacer;
    CHECK(pacer.compute_delay_ms(std::nullopt, 1.0) == 0);
    CHECK(pacer.compute_delay_ms(t1, 1.0) == 0);
    CHECK(pacer.compute_delay_ms(t2, 1.0) == 1500);

    pacer.reset();
    CHECK(pacer.compute_delay_ms(t1, 100.0) == 0);
    CHECK(pacer.compute_delay_ms(t2, 100.0) == 15);

    CHECK(pacer.compute_delay_ms(t_next_day, 1.0) == sim::MAX_SIMULATION_DELAY_MS);
    CHECK(pacer.compute_delay_ms(t1, 1.0) == 0);

    pacer.reset();
    CHECK(pacer.compute_delay_ms(t1, 0.0) == 0);
    CHECK(pacer.compute_delay_ms(t2, 0.0) == 0);

    auto t_interleave_hi = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "05:10:04.000");
    auto t_interleave_zero = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "00:00:00.000");
    auto t_interleave_next = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "05:10:04.500");
    CHECK(t_interleave_hi.has_value() && t_interleave_zero.has_value() && t_interleave_next.has_value());

    pacer.reset();
    CHECK(pacer.compute_delay_ms(t_interleave_hi, 1.0) == 0);
    CHECK(pacer.compute_delay_ms(t_interleave_zero, 1.0) == 0);
    CHECK(pacer.compute_delay_ms(t_interleave_next, 1.0) == 500);
}

static void test_burst_tracker() {
    sim::BurstTracker tracker(10000);
    CHECK(tracker.peak_count() == 0);
    CHECK(tracker.peak_rows_per_sim_sec() == 0.0);

    tracker.record(std::nullopt);
    CHECK(tracker.peak_count() == 0);

    tracker.record(10000);
    tracker.record(12000);
    tracker.record(19999);
    CHECK(tracker.peak_count() == 3);
    CHECK(tracker.peak_bucket_start_ms() == 10000);

    for (int i = 0; i < 7; ++i) {
        tracker.record(20000 + i * 1000);
    }
    CHECK(tracker.peak_count() == 7);
    CHECK(tracker.peak_bucket_start_ms() == 20000);

    tracker.record(0);
    tracker.record(5000);
    CHECK(tracker.peak_count() == 7);
    CHECK(tracker.peak_bucket_start_ms() == 20000);

    tracker.record(27000);
    CHECK(tracker.peak_count() == 8);
    CHECK(tracker.peak_bucket_start_ms() == 20000);

    tracker.record(30000);
    tracker.record(35000);
    CHECK(tracker.peak_count() == 8);
    CHECK(std::abs(tracker.peak_rows_per_sim_sec() - 0.8) < 1e-9);

    CHECK(std::abs(sim::compute_maintainable_speedup(70000.0, 700.0) - 100.0) < 1e-9);
    CHECK(sim::compute_maintainable_speedup(70000.0, 0.0) == 0.0);
    CHECK(sim::compute_maintainable_speedup(0.0, 700.0) == 0.0);
}

void test_csv_seeker_and_date_label() {
    CHECK(sim::extract_date_label("/tmp/debs2022-gc-trading-day-08-11-21.csv") == "08-11-21");
    CHECK(sim::extract_date_label("/tmp/custom_file.csv") == "custom_file.csv");

    auto t1 = sim::parse_time_of_day_ms("09:00");
    CHECK(t1.has_value() && *t1 == 9LL * 3600 * 1000);
    auto t2 = sim::parse_time_of_day_ms("09:15:30.250");
    CHECK(t2.has_value() && *t2 == (9LL * 3600 + 15 * 60 + 30) * 1000 + 250);
    CHECK(!sim::parse_time_of_day_ms("25:00:00").has_value());
    CHECK(!sim::parse_time_of_day_ms("bad").has_value());

    fs::path tmp = fs::temp_directory_path() / "sim_test_seeker.csv";
    {
        std::ofstream out(tmp);
        out << "# comment\n";
        out << "ID,SecType,Last,Trading time,LastTime,Time\n";
        out << "A.ETR,E,10.0,08:00:00,08:00:00,08:00:00\n";
        out << "B.ETR,E,10.1,,,\n";
        out << "C.ETR,E,10.2,,,\n";
        out << "D.ETR,E,10.3,09:00:05,09:00:05,09:00:05\n";
        out << "E.ETR,E,10.4,09:30:00,09:30:00,09:30:00\n";
    }

    auto open_and_read_headers = [&](std::ifstream& in, std::vector<std::string>& headers) {
        in.open(tmp);
        CHECK(in.is_open());
        std::string line;
        while (std::getline(in, line)) {
            std::string_view clean = sim::strip_cr(line);
            if (!sim::is_comment_or_empty(clean)) {
                headers = sim::parse_csv_line(clean);
                break;
            }
        }
    };

    {
        std::ifstream in;
        std::vector<std::string> headers;
        open_and_read_headers(in, headers);
        sim::seek_csv_to_time(in, *sim::parse_time_of_day_ms("09:00:00"), headers);
        std::string next_line;
        CHECK(static_cast<bool>(std::getline(in, next_line)));
        CHECK(next_line.rfind("D.ETR,", 0) == 0);
    }

    {
        std::ifstream in;
        std::vector<std::string> headers;
        open_and_read_headers(in, headers);
        sim::seek_csv_to_time(in, *sim::parse_time_of_day_ms("07:00:00"), headers);
        std::string next_line;
        CHECK(static_cast<bool>(std::getline(in, next_line)));
        CHECK(next_line.rfind("A.ETR,", 0) == 0);
    }

    {
        std::ifstream in;
        std::vector<std::string> headers;
        open_and_read_headers(in, headers);
        sim::seek_csv_to_time(in, *sim::parse_time_of_day_ms("12:00:00"), headers);
        std::string next_line;
        CHECK(!static_cast<bool>(std::getline(in, next_line)));
    }

    fs::remove(tmp);
}

void test_market_shard_hashing() {
    CHECK(sim::extract_market_suffix("A0HN5C.ETR") == "ETR");
    CHECK(sim::extract_market_suffix("RDSA.NL") == "NL");
    CHECK(sim::extract_market_suffix("NEW_STOCK.UNKNOWN_MKT") == "UNKNOWN_MKT");
    CHECK(sim::extract_market_suffix("AAPL") == "DEFAULT");
    CHECK(sim::extract_market_suffix("ABC.") == "DEFAULT");

    int s1 = sim::compute_market_shard("A0HN5C.ETR", 3);
    int s2 = sim::compute_market_shard("OTHER.ETR", 3);
    CHECK(s1 == s2);
    CHECK(s1 >= 0 && s1 < 3);
    CHECK(sim::compute_market_shard("AAPL", 3) >= 0 && sim::compute_market_shard("AAPL", 3) < 3);
    CHECK(sim::compute_market_shard("A0HN5C.ETR", 1) == 0);
}

int main() {
    test_discover_trading_days_chronological_and_filters_zone_identifier();
    test_csv_row_to_json_preserves_raw_values_and_escapes_quotes();
    test_timestamp_pacer_handles_colons_and_caps_day_transition_gap();
    test_burst_tracker();
    test_csv_seeker_and_date_label();
    test_market_shard_hashing();
    std::cout << "All simulator tests passed.\n";
    return 0;
}
