#include <arpa/inet.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <unistd.h>

#include <atomic>
#include <cctype>
#include <chrono>
#include <cerrno>
#include <csignal>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>
#include <thread>
#include <vector>

#include "constants.hpp"
#include "http_sender.hpp"
#include "sharded_pool.hpp"
#include "timestamp_pacer.hpp"

#define CHECK(cond)                                                                      \
    do {                                                                                 \
        if (!(cond)) {                                                                   \
            std::cerr << "CHECK failed at " << __FILE__ << ":" << __LINE__ << ": " #cond \
                      << "\n";                                                           \
            std::abort();                                                                \
        }                                                                                \
    } while (0)

namespace {

bool read_exact_http_request(int fd, std::string& carry, std::string& body_out, std::string& headers_out) {
    while (true) {
        auto pos = carry.find("\r\n\r\n");
        if (pos != std::string::npos) {
            headers_out = carry.substr(0, pos);
            std::size_t content_len = 0;
            std::string lower_headers = headers_out;
            for (char& ch : lower_headers) {
                ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
            }
            auto cl_pos = lower_headers.find("content-length:");
            if (cl_pos != std::string::npos) {
                auto val_start = cl_pos + 15;
                while (val_start < lower_headers.size() && lower_headers[val_start] == ' ') {
                    ++val_start;
                }
                content_len = static_cast<std::size_t>(std::stoul(lower_headers.substr(val_start)));
            }
            std::size_t total_needed = pos + 4 + content_len;
            while (carry.size() < total_needed) {
                char buf[4096];
                ssize_t n = ::recv(fd, buf, sizeof(buf), 0);
                if (n <= 0) {
                    return false;
                }
                carry.append(buf, static_cast<std::size_t>(n));
            }
            body_out = carry.substr(pos + 4, content_len);
            carry.erase(0, total_needed);
            return true;
        }
        char buf[4096];
        ssize_t n = ::recv(fd, buf, sizeof(buf), 0);
        if (n <= 0) {
            return false;
        }
        carry.append(buf, static_cast<std::size_t>(n));
    }
}

bool send_all_raw(int fd, const std::string& data) {
    std::size_t sent = 0;
    while (sent < data.size()) {
        ssize_t n = ::send(fd, data.data() + sent, data.size() - sent, MSG_NOSIGNAL);
        if (n <= 0) {
            return false;
        }
        sent += static_cast<std::size_t>(n);
    }
    return true;
}

}  // namespace

void test_parse_http_url_handles_default_and_explicit_ports() {
    auto u1 = sim::parse_http_url("http://localhost/api/ingest");
    CHECK(u1.has_value());
    CHECK(u1->host == "localhost");
    CHECK(u1->port == "80");
    CHECK(u1->path == "/api/ingest");

    auto u2 = sim::parse_http_url("http://127.0.0.1:8080/api/ingest");
    CHECK(u2.has_value());
    CHECK(u2->host == "127.0.0.1");
    CHECK(u2->port == "8080");
    CHECK(u2->path == "/api/ingest");

    auto u3 = sim::parse_http_url("http://worker.local");
    CHECK(u3.has_value());
    CHECK(u3->host == "worker.local");
    CHECK(u3->port == "80");
    CHECK(u3->path == "/");

    CHECK(!sim::parse_http_url("https://localhost:8080/api/ingest").has_value());
    CHECK(!sim::parse_http_url("localhost:8080/api/ingest").has_value());
    CHECK(!sim::parse_http_url("http:///api/ingest").has_value());
    CHECK(!sim::parse_http_url("http://:8080/api/ingest").has_value());
    CHECK(!sim::parse_http_url("http://localhost:/api/ingest").has_value());
    CHECK(!sim::parse_http_url("http://localhost:abc/api/ingest").has_value());
}

void test_http_sender_keep_alive_reuse_and_reconnect() {
    int listen_fd = ::socket(AF_INET, SOCK_STREAM, 0);
    CHECK(listen_fd >= 0);

    int opt = 1;
    CHECK(::setsockopt(listen_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt)) == 0);

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    CHECK(::bind(listen_fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == 0);
    CHECK(::listen(listen_fd, 8) == 0);

    sockaddr_in bound{};
    socklen_t bound_len = sizeof(bound);
    CHECK(::getsockname(listen_fd, reinterpret_cast<sockaddr*>(&bound), &bound_len) == 0);
    uint16_t port = ntohs(bound.sin_port);

    std::atomic<int> accept_count{0};
    std::vector<std::string> received_bodies;

    std::thread server([&]() {
        int c1 = ::accept(listen_fd, nullptr, nullptr);
        if (c1 < 0) {
            return;
        }
        accept_count.fetch_add(1);
        std::string carry;
        for (int i = 0; i < 20; ++i) {
            std::string body;
            std::string headers;
            if (!read_exact_http_request(c1, carry, body, headers)) {
                ::close(c1);
                return;
            }
            received_bodies.push_back(body);
            std::string resp =
                "HTTP/1.1 202 Accepted\r\n"
                "Content-Length: 0\r\n"
                "Connection: keep-alive\r\n\r\n";
            if (!send_all_raw(c1, resp)) {
                ::close(c1);
                return;
            }
        }
        ::close(c1);

        int c2 = ::accept(listen_fd, nullptr, nullptr);
        if (c2 < 0) {
            return;
        }
        accept_count.fetch_add(1);
        carry.clear();
        std::string body;
        std::string headers;
        if (read_exact_http_request(c2, carry, body, headers)) {
            received_bodies.push_back(body);
            std::string resp =
                "HTTP/1.1 202 Accepted\r\n"
                "Content-Length: 0\r\n"
                "Connection: close\r\n\r\n";
            send_all_raw(c2, resp);
        }
        ::close(c2);
    });

    auto url = sim::parse_http_url("http://127.0.0.1:" + std::to_string(port) + "/api/ingest");
    CHECK(url.has_value());

    sim::HttpKeepAliveSender sender(*url);
    for (int i = 0; i < 20; ++i) {
        std::string payload = "{\"seq\":" + std::to_string(i) + "}";
        auto res = sender.post_json(payload);
        CHECK(res.ok);
        CHECK(res.status_code == 202);
    }
    CHECK(accept_count.load() == 1);

    auto res21 = sender.post_json("{\"seq\":20}");
    CHECK(res21.ok);
    CHECK(res21.status_code == 202);
    CHECK(!sender.is_connected());

    server.join();
    ::close(listen_fd);

    CHECK(accept_count.load() == 2);
    CHECK(received_bodies.size() == 21);
    for (int i = 0; i < 21; ++i) {
        CHECK(received_bodies[static_cast<std::size_t>(i)] ==
              ("{\"seq\":" + std::to_string(i) + "}"));
    }
}

void test_http_sender_handles_chunked_response() {
    int listen_fd = ::socket(AF_INET, SOCK_STREAM, 0);
    CHECK(listen_fd >= 0);

    int opt = 1;
    CHECK(::setsockopt(listen_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt)) == 0);

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    CHECK(::bind(listen_fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == 0);
    CHECK(::listen(listen_fd, 4) == 0);

    sockaddr_in bound{};
    socklen_t bound_len = sizeof(bound);
    CHECK(::getsockname(listen_fd, reinterpret_cast<sockaddr*>(&bound), &bound_len) == 0);
    uint16_t port = ntohs(bound.sin_port);

    std::atomic<int> accept_count{0};
    std::thread server([&]() {
        int c = ::accept(listen_fd, nullptr, nullptr);
        if (c < 0) {
            return;
        }
        accept_count.fetch_add(1);
        std::string carry;
        for (int i = 0; i < 2; ++i) {
            std::string body;
            std::string headers;
            if (!read_exact_http_request(c, carry, body, headers)) {
                break;
            }
            std::string resp =
                "HTTP/1.1 200 OK\r\n"
                "Transfer-Encoding: chunked\r\n\r\n"
                "2\r\nok\r\n"
                "0\r\n\r\n";
            if (!send_all_raw(c, resp)) {
                break;
            }
        }
        ::close(c);
    });

    auto url = sim::parse_http_url("http://127.0.0.1:" + std::to_string(port) + "/api/ingest");
    CHECK(url.has_value());
    sim::HttpKeepAliveSender sender(*url);
    auto r1 = sender.post_json("{\"a\":1}");
    CHECK(r1.ok);
    CHECK(r1.status_code == 200);
    auto r2 = sender.post_json("{\"a\":2}");
    CHECK(r2.ok);
    CHECK(r2.status_code == 200);

    server.join();
    ::close(listen_fd);
    CHECK(accept_count.load() == 1);
}

void test_sharded_pool_wait_idle_waits_for_http_delivery() {
    int listen_fd = ::socket(AF_INET, SOCK_STREAM, 0);
    CHECK(listen_fd >= 0);

    int opt = 1;
    CHECK(::setsockopt(listen_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt)) == 0);

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    CHECK(::bind(listen_fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == 0);
    CHECK(::listen(listen_fd, 8) == 0);

    sockaddr_in bound{};
    socklen_t bound_len = sizeof(bound);
    CHECK(::getsockname(listen_fd, reinterpret_cast<sockaddr*>(&bound), &bound_len) == 0);
    uint16_t port = ntohs(bound.sin_port);

    std::atomic<bool> stop_server{false};
    std::atomic<int> received_count{0};
    std::vector<std::thread> conn_threads;

    std::thread accept_thread([&]() {
        while (!stop_server.load(std::memory_order_relaxed)) {
            int c = ::accept(listen_fd, nullptr, nullptr);
            if (c < 0) {
                break;
            }
            conn_threads.emplace_back([c, &received_count]() {
                std::string carry;
                while (true) {
                    std::string body;
                    std::string headers;
                    if (!read_exact_http_request(c, carry, body, headers)) {
                        break;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(25));
                    received_count.fetch_add(1, std::memory_order_relaxed);
                    std::string resp =
                        "HTTP/1.1 202 Accepted\r\n"
                        "Content-Length: 0\r\n"
                        "Connection: keep-alive\r\n\r\n";
                    if (!send_all_raw(c, resp)) {
                        break;
                    }
                }
                ::close(c);
            });
        }
    });

    auto url = sim::parse_http_url("http://127.0.0.1:" + std::to_string(port) + "/api/ingest");
    CHECK(url.has_value());

    {
        sim::ShardedPool pool(2, *url);
        auto t0 = std::chrono::steady_clock::now();
        for (int i = 0; i < 8; ++i) {
            std::string sym = "SYM_" + std::to_string(i);
            std::string payload = "{\"i\":" + std::to_string(i) + "}";
            CHECK(pool.push(sym, std::move(payload)));
        }
        pool.wait_idle();
        auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                              std::chrono::steady_clock::now() - t0)
                              .count();
        CHECK(pool.ticks_sent() == 8);
        CHECK(pool.http_errors() == 0);
        CHECK(received_count.load(std::memory_order_relaxed) == 8);
        CHECK(elapsed_ms >= 90);
        pool.shutdown_and_join();
    }

    stop_server.store(true, std::memory_order_relaxed);
    ::shutdown(listen_fd, SHUT_RDWR);
    ::close(listen_fd);
    accept_thread.join();
    for (auto& t : conn_threads) {
        if (t.joinable()) {
            t.join();
        }
    }
}

void test_sim_time_tracker_monotonic_high_water() {
    auto t_a = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "05:10:04.000");
    auto t_zero = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "00:00:00.000");
    auto t_b = sim::TimestampPacer::parse_epoch_ms("08-11-2021", "05:10:04.500");
    CHECK(t_a.has_value() && t_zero.has_value() && t_b.has_value());

    sim::SimTimeTracker tracker;
    tracker.record(t_a);
    tracker.record(t_zero);
    tracker.record(t_b);
    tracker.record(t_zero);
    CHECK(tracker.elapsed_sim_ms() == 500);
}

void test_sharded_pool_stops_promptly_when_stop_flag_set() {
    int listen_fd = ::socket(AF_INET, SOCK_STREAM, 0);
    CHECK(listen_fd >= 0);

    int opt = 1;
    CHECK(::setsockopt(listen_fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt)) == 0);

    sockaddr_in addr{};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    addr.sin_port = 0;
    CHECK(::bind(listen_fd, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) == 0);
    CHECK(::listen(listen_fd, 8) == 0);

    sockaddr_in bound{};
    socklen_t bound_len = sizeof(bound);
    CHECK(::getsockname(listen_fd, reinterpret_cast<sockaddr*>(&bound), &bound_len) == 0);
    uint16_t port = ntohs(bound.sin_port);

    std::atomic<bool> stop_server{false};
    std::vector<std::thread> conn_threads;

    std::thread accept_thread([&]() {
        while (!stop_server.load(std::memory_order_relaxed)) {
            int c = ::accept(listen_fd, nullptr, nullptr);
            if (c < 0) {
                break;
            }
            conn_threads.emplace_back([c, &stop_server]() {
                std::string carry;
                while (!stop_server.load(std::memory_order_relaxed)) {
                    std::string body;
                    std::string headers;
                    if (!read_exact_http_request(c, carry, body, headers)) {
                        break;
                    }
                    std::this_thread::sleep_for(std::chrono::milliseconds(50));
                    std::string resp =
                        "HTTP/1.1 202 Accepted\r\n"
                        "Content-Length: 0\r\n"
                        "Connection: keep-alive\r\n\r\n";
                    if (!send_all_raw(c, resp)) {
                        break;
                    }
                }
                ::close(c);
            });
        }
    });

    auto url = sim::parse_http_url("http://127.0.0.1:" + std::to_string(port) + "/api/ingest");
    CHECK(url.has_value());

    std::atomic<bool> stop_flag{false};
    {
        sim::ShardedPool pool(2, *url, &stop_flag);
        for (int i = 0; i < 100; ++i) {
            std::string sym = "SYM_" + std::to_string(i);
            std::string payload = "{\"i\":" + std::to_string(i) + "}";
            CHECK(pool.push(sym, std::move(payload)));
        }

        while (pool.ticks_sent() < 2) {
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }

        auto t0 = std::chrono::steady_clock::now();
        stop_flag.store(true, std::memory_order_relaxed);
        pool.stop_and_join();
        auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                              std::chrono::steady_clock::now() - t0)
                              .count();

        CHECK(elapsed_ms < 500);
        CHECK(pool.ticks_sent() < 20);
    }

    stop_server.store(true, std::memory_order_relaxed);
    ::shutdown(listen_fd, SHUT_RDWR);
    ::close(listen_fd);
    accept_thread.join();
    for (auto& t : conn_threads) {
        if (t.joinable()) {
            t.join();
        }
    }
}

int main() {
    std::signal(SIGPIPE, SIG_IGN);
    test_parse_http_url_handles_default_and_explicit_ports();
    test_http_sender_keep_alive_reuse_and_reconnect();
    test_http_sender_handles_chunked_response();
    test_sharded_pool_wait_idle_waits_for_http_delivery();
    test_sim_time_tracker_monotonic_high_water();
    test_sharded_pool_stops_promptly_when_stop_flag_set();
    std::cout << "All HTTP sender tests passed.\n";
    return 0;
}
