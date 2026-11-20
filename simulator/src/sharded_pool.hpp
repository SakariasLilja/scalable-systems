#pragma once

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include "constants.hpp"
#include "http_sender.hpp"

namespace sim {

class SimTimeTracker {
public:
    void record(std::optional<int64_t> epoch_ms) {
        if (!epoch_ms.has_value()) {
            return;
        }
        if (!first_epoch_ms_.has_value()) {
            first_epoch_ms_ = *epoch_ms;
            last_epoch_ms_ = *epoch_ms;
        } else {
            last_epoch_ms_ = std::max(*last_epoch_ms_, *epoch_ms);
        }
    }

    int64_t elapsed_sim_ms() const {
        if (!first_epoch_ms_.has_value() || !last_epoch_ms_.has_value()) {
            return 0;
        }
        return *last_epoch_ms_ - *first_epoch_ms_;
    }

    void reset() {
        first_epoch_ms_.reset();
        last_epoch_ms_.reset();
    }

private:
    std::optional<int64_t> first_epoch_ms_;
    std::optional<int64_t> last_epoch_ms_;
};

class ShardedPool {
public:
    ShardedPool(int concurrency,
                const HttpUrl& target_url,
                const std::atomic<bool>* stop_flag = nullptr)
        : target_url_(target_url), stop_flag_(stop_flag) {
        shards_.reserve(static_cast<std::size_t>(concurrency));
        for (int i = 0; i < concurrency; ++i) {
            shards_.push_back(std::make_unique<ShardQueue>());
        }
        workers_.reserve(static_cast<std::size_t>(concurrency));
        for (int i = 0; i < concurrency; ++i) {
            workers_.emplace_back(&ShardedPool::worker_loop, this, i);
        }
    }

    ~ShardedPool() {
        shutdown_and_join();
    }

    ShardedPool(const ShardedPool&) = delete;
    ShardedPool& operator=(const ShardedPool&) = delete;

    bool push(const std::string& symbol_id, std::string&& json_payload) {
        std::size_t idx = std::hash<std::string>{}(symbol_id) % shards_.size();
        ShardQueue& shard = *shards_[idx];
        std::unique_lock<std::mutex> lock(shard.mu);
        while (shard.items.size() >= MAX_SHARD_QUEUE_SIZE && !shard.closed && !is_stopped()) {
            shard.cv_not_full.wait_for(lock, std::chrono::milliseconds(QUEUE_POLL_TIMEOUT_MS));
        }
        if (shard.closed || is_stopped()) {
            return false;
        }
        pending_count_.fetch_add(1, std::memory_order_acq_rel);
        shard.items.push_back(std::move(json_payload));
        lock.unlock();
        shard.cv_not_empty.notify_one();
        return true;
    }

    void wait_idle() {
        std::unique_lock<std::mutex> lock(idle_mu_);
        idle_cv_.wait(lock, [&]() {
            return pending_count_.load(std::memory_order_acquire) == 0;
        });
    }

    void shutdown_and_join() {
        if (joined_) {
            return;
        }
        for (auto& shard : shards_) {
            std::lock_guard<std::mutex> lock(shard->mu);
            shard->closed = true;
            shard->cv_not_empty.notify_all();
            shard->cv_not_full.notify_all();
        }
        for (auto& t : workers_) {
            if (t.joinable()) {
                t.join();
            }
        }
        joined_ = true;
    }

    uint64_t ticks_sent() const {
        return ticks_sent_.load(std::memory_order_relaxed);
    }

    uint64_t http_errors() const {
        return http_errors_.load(std::memory_order_relaxed);
    }

private:
    struct ShardQueue {
        std::mutex mu;
        std::condition_variable cv_not_empty;
        std::condition_variable cv_not_full;
        std::deque<std::string> items;
        bool closed = false;
    };

    bool is_stopped() const {
        return stop_flag_ != nullptr && stop_flag_->load(std::memory_order_relaxed);
    }

    void finish_one_item() {
        if (pending_count_.fetch_sub(1, std::memory_order_acq_rel) == 1) {
            std::lock_guard<std::mutex> lock(idle_mu_);
            idle_cv_.notify_all();
        }
    }

    void worker_loop(int shard_index) {
        ShardQueue& shard = *shards_[static_cast<std::size_t>(shard_index)];
        HttpKeepAliveSender sender(target_url_);

        while (true) {
            std::string payload;
            {
                std::unique_lock<std::mutex> lock(shard.mu);
                shard.cv_not_empty.wait(lock, [&]() {
                    return !shard.items.empty() || shard.closed;
                });
                if (shard.items.empty() && shard.closed) {
                    break;
                }
                payload = std::move(shard.items.front());
                shard.items.pop_front();
                lock.unlock();
                shard.cv_not_full.notify_one();
            }

            HttpPostResult res = sender.post_json(payload);
            if (res.ok) {
                ticks_sent_.fetch_add(1, std::memory_order_relaxed);
                finish_one_item();
            } else {
                http_errors_.fetch_add(1, std::memory_order_relaxed);
                finish_one_item();
                if (is_stopped()) {
                    std::size_t dropped = 0;
                    {
                        std::lock_guard<std::mutex> lock(shard.mu);
                        dropped = shard.items.size();
                        shard.items.clear();
                        shard.cv_not_full.notify_all();
                    }
                    if (dropped > 0) {
                        if (pending_count_.fetch_sub(dropped, std::memory_order_acq_rel) ==
                            dropped) {
                            std::lock_guard<std::mutex> lock(idle_mu_);
                            idle_cv_.notify_all();
                        }
                    }
                    break;
                }
            }
        }
    }

    HttpUrl target_url_;
    const std::atomic<bool>* stop_flag_{nullptr};
    std::vector<std::unique_ptr<ShardQueue>> shards_;
    std::vector<std::thread> workers_;
    std::atomic<uint64_t> ticks_sent_{0};
    std::atomic<uint64_t> http_errors_{0};
    std::atomic<uint64_t> pending_count_{0};
    std::mutex idle_mu_;
    std::condition_variable idle_cv_;
    bool joined_{false};
};

}  // namespace sim
