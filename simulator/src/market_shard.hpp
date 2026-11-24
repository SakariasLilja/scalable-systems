#pragma once

#include <algorithm>
#include <cstdint>
#include <string_view>

namespace sim {

inline std::string_view extract_market_suffix(std::string_view symbol_id) {
    auto dot = symbol_id.rfind('.');
    if (dot == std::string_view::npos || dot + 1 >= symbol_id.size()) {
        return "DEFAULT";
    }
    return symbol_id.substr(dot + 1);
}

inline uint64_t fnv1a_64(std::string_view text) {
    uint64_t hash = 14695981039346656037ULL;
    for (unsigned char c : text) {
        hash ^= static_cast<uint64_t>(c);
        hash *= 1099511628211ULL;
    }
    return hash;
}

inline int compute_market_shard(std::string_view symbol_id, int worker_shards) {
    int shards = std::max(1, worker_shards);
    if (shards == 1) {
        return 0;
    }
    std::string_view key = symbol_id.empty() ? std::string_view("DEFAULT") : symbol_id;
    return static_cast<int>(fnv1a_64(key) % static_cast<uint64_t>(shards));
}

}  // namespace sim
