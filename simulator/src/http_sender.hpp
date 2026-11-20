#pragma once

#include <arpa/inet.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <sys/time.h>
#include <unistd.h>

#include <algorithm>
#include <cctype>
#include <cerrno>
#include <cstring>
#include <limits>
#include <optional>
#include <string>
#include <string_view>

#include "constants.hpp"
#include "csv_json.hpp"

namespace sim {

struct HttpUrl {
    std::string host;
    std::string port;
    std::string path;
};

struct HttpPostResult {
    bool ok{false};
    int status_code{0};
};

namespace detail {

inline bool parse_port_number(std::string_view port_sv) {
    if (port_sv.empty()) {
        return false;
    }
    int val = 0;
    for (char ch : port_sv) {
        if (ch < '0' || ch > '9') {
            return false;
        }
        val = val * 10 + (ch - '0');
        if (val > MAX_VALID_PORT) {
            return false;
        }
    }
    return val >= MIN_VALID_PORT;
}

inline std::string to_lower_ascii(std::string_view sv) {
    std::string out(sv);
    for (char& ch : out) {
        ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
    }
    return out;
}

inline bool contains_ci(std::string_view haystack, std::string_view needle) {
    std::string h = to_lower_ascii(haystack);
    std::string n = to_lower_ascii(needle);
    return h.find(n) != std::string::npos;
}

}  // namespace detail

inline std::optional<HttpUrl> parse_http_url(const std::string& url) {
    constexpr std::string_view SCHEME = "http://";
    std::string_view sv(url);
    if (sv.size() <= SCHEME.size() || sv.substr(0, SCHEME.size()) != SCHEME) {
        return std::nullopt;
    }
    sv.remove_prefix(SCHEME.size());

    auto slash_pos = sv.find('/');
    std::string_view authority = (slash_pos == std::string_view::npos) ? sv : sv.substr(0, slash_pos);
    std::string path =
        (slash_pos == std::string_view::npos) ? std::string(DEFAULT_HTTP_PATH) : std::string(sv.substr(slash_pos));

    if (authority.empty()) {
        return std::nullopt;
    }

    std::string host;
    std::string port = DEFAULT_HTTP_PORT;

    if (authority.front() == '[') {
        auto close_bracket = authority.find(']');
        if (close_bracket == std::string_view::npos || close_bracket <= 1) {
            return std::nullopt;
        }
        host = std::string(authority.substr(1, close_bracket - 1));
        std::string_view tail = authority.substr(close_bracket + 1);
        if (!tail.empty()) {
            if (tail.front() != ':' || !detail::parse_port_number(tail.substr(1))) {
                return std::nullopt;
            }
            port = std::string(tail.substr(1));
        }
    } else {
        auto colon_pos = authority.find(':');
        if (colon_pos != std::string_view::npos) {
            std::string_view host_sv = authority.substr(0, colon_pos);
            std::string_view port_sv = authority.substr(colon_pos + 1);
            if (host_sv.empty() || !detail::parse_port_number(port_sv)) {
                return std::nullopt;
            }
            host = std::string(host_sv);
            port = std::string(port_sv);
        } else {
            host = std::string(authority);
        }
    }

    if (host.empty()) {
        return std::nullopt;
    }
    for (char ch : host) {
        if (std::isspace(static_cast<unsigned char>(ch))) {
            return std::nullopt;
        }
    }

    return HttpUrl{std::move(host), std::move(port), std::move(path)};
}

class HttpKeepAliveSender {
public:
    explicit HttpKeepAliveSender(HttpUrl target) : target_(std::move(target)) {}

    ~HttpKeepAliveSender() {
        close_socket();
    }

    HttpKeepAliveSender(const HttpKeepAliveSender&) = delete;
    HttpKeepAliveSender& operator=(const HttpKeepAliveSender&) = delete;

    HttpKeepAliveSender(HttpKeepAliveSender&& other) noexcept
        : target_(std::move(other.target_)), fd_(other.fd_) {
        other.fd_ = -1;
    }

    HttpKeepAliveSender& operator=(HttpKeepAliveSender&& other) noexcept {
        if (this != &other) {
            close_socket();
            target_ = std::move(other.target_);
            fd_ = other.fd_;
            other.fd_ = -1;
        }
        return *this;
    }

    bool is_connected() const {
        return fd_ >= 0;
    }

    void close_socket() {
        if (fd_ >= 0) {
            ::close(fd_);
            fd_ = -1;
        }
    }

    HttpPostResult post_json(std::string_view json_body) {
        std::string request;
        request.reserve(160 + target_.path.size() + target_.host.size() + target_.port.size() +
                        json_body.size());
        request.append("POST ");
        request.append(target_.path);
        request.append(" HTTP/1.1\r\nHost: ");
        request.append(target_.host);
        request.push_back(':');
        request.append(target_.port);
        request.append("\r\nContent-Type: application/json\r\nContent-Length: ");
        request.append(std::to_string(json_body.size()));
        request.append("\r\nConnection: keep-alive\r\n\r\n");
        request.append(json_body);

        bool was_reused = is_connected();
        bool transport_error = false;
        HttpPostResult res = send_and_receive(request, transport_error);
        if (transport_error && was_reused) {
            close_socket();
            transport_error = false;
            res = send_and_receive(request, transport_error);
        }
        return res;
    }

private:
    bool ensure_connected() {
        if (fd_ >= 0) {
            return true;
        }
        addrinfo hints{};
        hints.ai_family = AF_UNSPEC;
        hints.ai_socktype = SOCK_STREAM;

        addrinfo* result = nullptr;
        int rc = ::getaddrinfo(target_.host.c_str(), target_.port.c_str(), &hints, &result);
        if (rc != 0 || result == nullptr) {
            return false;
        }

        for (addrinfo* rp = result; rp != nullptr; rp = rp->ai_next) {
            int fd = ::socket(rp->ai_family, rp->ai_socktype, rp->ai_protocol);
            if (fd < 0) {
                continue;
            }
            int nodelay = 1;
            ::setsockopt(fd, IPPROTO_TCP, TCP_NODELAY, &nodelay, sizeof(nodelay));

            timeval tv{};
            tv.tv_sec = HTTP_IO_TIMEOUT_SEC;
            tv.tv_usec = 0;
            ::setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
            ::setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, sizeof(tv));

            int conn_rc = -1;
            do {
                conn_rc = ::connect(fd, rp->ai_addr, rp->ai_addrlen);
            } while (conn_rc < 0 && errno == EINTR);

            if (conn_rc == 0) {
                fd_ = fd;
                break;
            }
            ::close(fd);
        }

        ::freeaddrinfo(result);
        return fd_ >= 0;
    }

    bool send_all(const std::string& data) {
        std::size_t sent = 0;
        while (sent < data.size()) {
            ssize_t n = ::send(fd_, data.data() + sent, data.size() - sent, MSG_NOSIGNAL);
            if (n < 0) {
                if (errno == EINTR) {
                    continue;
                }
                return false;
            }
            if (n == 0) {
                return false;
            }
            sent += static_cast<std::size_t>(n);
        }
        return true;
    }

    bool recv_append(std::string& buf) {
        char tmp[HTTP_RECV_BUFFER_SIZE];
        while (true) {
            ssize_t n = ::recv(fd_, tmp, sizeof(tmp), 0);
            if (n < 0) {
                if (errno == EINTR) {
                    continue;
                }
                return false;
            }
            if (n == 0) {
                return false;
            }
            buf.append(tmp, static_cast<std::size_t>(n));
            return true;
        }
    }

    bool read_line_crlf(std::string& buf, std::size_t& cursor, std::string& line_out) {
        while (true) {
            auto crlf = buf.find("\r\n", cursor);
            if (crlf != std::string::npos) {
                line_out.assign(buf, cursor, crlf - cursor);
                cursor = crlf + 2;
                return true;
            }
            if (buf.size() - cursor > MAX_HTTP_HEADER_BYTES) {
                return false;
            }
            if (!recv_append(buf)) {
                return false;
            }
        }
    }

    bool drain_chunked_body(std::string& buf, std::size_t cursor) {
        while (true) {
            std::string size_line;
            if (!read_line_crlf(buf, cursor, size_line)) {
                return false;
            }
            auto semi = size_line.find(';');
            if (semi != std::string::npos) {
                size_line.resize(semi);
            }
            std::string_view size_sv = detail::trim_ascii(size_line);
            if (size_sv.empty()) {
                return false;
            }
            std::size_t chunk_size = 0;
            for (char ch : size_sv) {
                unsigned int digit = 0;
                if (ch >= '0' && ch <= '9') {
                    digit = static_cast<unsigned int>(ch - '0');
                } else if (ch >= 'a' && ch <= 'f') {
                    digit = static_cast<unsigned int>(10 + (ch - 'a'));
                } else if (ch >= 'A' && ch <= 'F') {
                    digit = static_cast<unsigned int>(10 + (ch - 'A'));
                } else {
                    return false;
                }
                if (chunk_size > (std::numeric_limits<std::size_t>::max() - digit) / 16) {
                    return false;
                }
                chunk_size = chunk_size * 16 + digit;
            }
            if (chunk_size == 0) {
                while (true) {
                    std::string trailer;
                    if (!read_line_crlf(buf, cursor, trailer)) {
                        return false;
                    }
                    if (trailer.empty()) {
                        return true;
                    }
                }
            }
            std::size_t needed = cursor + chunk_size + 2;
            while (buf.size() < needed) {
                if (cursor > HTTP_RECV_BUFFER_SIZE) {
                    buf.erase(0, cursor);
                    needed -= cursor;
                    cursor = 0;
                }
                if (!recv_append(buf)) {
                    return false;
                }
            }
            if (buf[cursor + chunk_size] != '\r' || buf[cursor + chunk_size + 1] != '\n') {
                return false;
            }
            cursor += chunk_size + 2;
        }
    }

    bool drain_content_length(std::size_t content_length, std::size_t already_read) {
        if (already_read >= content_length) {
            return true;
        }
        std::size_t remaining = content_length - already_read;
        char tmp[HTTP_RECV_BUFFER_SIZE];
        while (remaining > 0) {
            std::size_t to_read = std::min(remaining, sizeof(tmp));
            ssize_t n = ::recv(fd_, tmp, to_read, 0);
            if (n < 0) {
                if (errno == EINTR) {
                    continue;
                }
                return false;
            }
            if (n == 0) {
                return false;
            }
            remaining -= static_cast<std::size_t>(n);
        }
        return true;
    }

    void drain_until_eof() {
        char tmp[HTTP_RECV_BUFFER_SIZE];
        while (true) {
            ssize_t n = ::recv(fd_, tmp, sizeof(tmp), 0);
            if (n < 0 && errno == EINTR) {
                continue;
            }
            if (n <= 0) {
                break;
            }
        }
    }

    HttpPostResult send_and_receive(const std::string& request, bool& transport_error) {
        transport_error = false;
        if (!ensure_connected()) {
            transport_error = true;
            return {};
        }

        if (!send_all(request)) {
            close_socket();
            transport_error = true;
            return {};
        }

        std::string resp_buf;
        resp_buf.reserve(HTTP_RECV_BUFFER_SIZE);
        std::size_t hdr_end = std::string::npos;
        while (true) {
            hdr_end = resp_buf.find("\r\n\r\n");
            if (hdr_end != std::string::npos) {
                break;
            }
            if (resp_buf.size() > MAX_HTTP_HEADER_BYTES) {
                close_socket();
                transport_error = true;
                return {};
            }
            if (!recv_append(resp_buf)) {
                close_socket();
                transport_error = true;
                return {};
            }
        }

        std::string_view header_block(resp_buf.data(), hdr_end);
        auto first_crlf = header_block.find("\r\n");
        std::string_view status_line = (first_crlf == std::string_view::npos)
                                           ? header_block
                                           : header_block.substr(0, first_crlf);
        if (!status_line.starts_with("HTTP/")) {
            close_socket();
            transport_error = true;
            return {};
        }
        auto sp1 = status_line.find(' ');
        if (sp1 == std::string_view::npos) {
            close_socket();
            transport_error = true;
            return {};
        }
        std::string_view version_sv = status_line.substr(0, sp1);
        std::string_view rest = detail::trim_ascii(status_line.substr(sp1 + 1));
        auto sp2 = rest.find(' ');
        std::string_view code_sv = (sp2 == std::string_view::npos) ? rest : rest.substr(0, sp2);
        if (code_sv.size() != 3) {
            close_socket();
            transport_error = true;
            return {};
        }
        int status_code = 0;
        for (char ch : code_sv) {
            if (ch < '0' || ch > '9') {
                close_socket();
                transport_error = true;
                return {};
            }
            status_code = status_code * 10 + (ch - '0');
        }

        bool is_http_10 = (version_sv == "HTTP/1.0");
        bool connection_close = is_http_10;
        bool has_content_length = false;
        std::size_t content_length = 0;
        bool is_chunked = false;

        if (first_crlf != std::string_view::npos) {
            std::size_t pos = first_crlf + 2;
            while (pos < header_block.size()) {
                auto next_crlf = header_block.find("\r\n", pos);
                std::string_view line = (next_crlf == std::string_view::npos)
                                            ? header_block.substr(pos)
                                            : header_block.substr(pos, next_crlf - pos);
                auto colon = line.find(':');
                if (colon != std::string_view::npos) {
                    std::string_view name = detail::trim_ascii(line.substr(0, colon));
                    std::string_view val = detail::trim_ascii(line.substr(colon + 1));
                    if (detail::iequals_trimmed(name, "Content-Length")) {
                        std::size_t parsed_len = 0;
                        if (val.empty()) {
                            close_socket();
                            transport_error = true;
                            return {};
                        }
                        for (char ch : val) {
                            if (ch < '0' || ch > '9') {
                                close_socket();
                                transport_error = true;
                                return {};
                            }
                            std::size_t digit = static_cast<std::size_t>(ch - '0');
                            if (parsed_len > (std::numeric_limits<std::size_t>::max() - digit) / 10) {
                                close_socket();
                                transport_error = true;
                                return {};
                            }
                            parsed_len = parsed_len * 10 + digit;
                        }
                        has_content_length = true;
                        content_length = parsed_len;
                    } else if (detail::iequals_trimmed(name, "Transfer-Encoding")) {
                        if (detail::contains_ci(val, "chunked")) {
                            is_chunked = true;
                        }
                    } else if (detail::iequals_trimmed(name, "Connection")) {
                        if (detail::contains_ci(val, "close")) {
                            connection_close = true;
                        } else if (detail::contains_ci(val, "keep-alive")) {
                            connection_close = false;
                        }
                    }
                }
                if (next_crlf == std::string_view::npos) {
                    break;
                }
                pos = next_crlf + 2;
            }
        }

        std::size_t body_start = hdr_end + 4;
        if (is_chunked) {
            if (!drain_chunked_body(resp_buf, body_start)) {
                close_socket();
                transport_error = true;
                return {};
            }
        } else if (has_content_length) {
            if (!drain_content_length(content_length, resp_buf.size() - body_start)) {
                close_socket();
                transport_error = true;
                return {};
            }
        } else {
            if (connection_close &&
                status_code != HTTP_STATUS_NO_CONTENT &&
                status_code != HTTP_STATUS_NOT_MODIFIED &&
                !(status_code >= HTTP_STATUS_INFO_MIN && status_code <= HTTP_STATUS_INFO_MAX)) {
                drain_until_eof();
            }
            connection_close = true;
        }

        if (connection_close) {
            close_socket();
        }

        bool ok = (status_code >= HTTP_STATUS_OK_MIN && status_code <= HTTP_STATUS_OK_MAX);
        return HttpPostResult{ok, status_code};
    }

    HttpUrl target_;
    int fd_{-1};
};

}  // namespace sim