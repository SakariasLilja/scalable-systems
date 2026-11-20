import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate } from 'k6/metrics';
import {
  default_base_url,
  default_vus,
  default_duration,
  default_sse_timeout,
  sticky_cookie_name,
  symbol_search_limit,
  tracked_symbol_count,
  history_limit,
  think_time_seconds,
  stage_ramp_ratio,
  min_stage_seconds,
  default_duration_seconds,
  seconds_per_minute,
  seconds_per_hour,
  rest_error_rate_threshold,
  rest_p95_latency_ms,
  sse_ok_rate_threshold,
  k6_timeout_error_code,
  subscription_states,
  fallback_symbols
} from './constants.js';

export const sticky_cookie_present = new Rate('sticky_cookie_present');
export const sse_stream_ok = new Rate('sse_stream_ok');

const base_url = __ENV.BASE_URL || default_base_url;
const target_vus = Math.max(1, parseInt(__ENV.VUS || String(default_vus), 10) || default_vus);
const total_duration = __ENV.DURATION || default_duration;
const sse_timeout = __ENV.SSE_TIMEOUT || default_sse_timeout;

function parse_duration_seconds(raw_duration) {
  const match = String(raw_duration || '').trim().match(/^(\d+)\s*(s|m|h)?$/i);
  if (!match) {
    return default_duration_seconds;
  }
  const value = parseInt(match[1], 10);
  const unit = (match[2] || 's').toLowerCase();
  if (unit === 'h') {
    return value * seconds_per_hour;
  }
  if (unit === 'm') {
    return value * seconds_per_minute;
  }
  return value;
}

function build_stages(vus, total_sec) {
  const safe_total = Math.max(min_stage_seconds * 3, total_sec);
  const ramp_sec = Math.max(min_stage_seconds, Math.round(safe_total * stage_ramp_ratio));
  const hold_sec = Math.max(min_stage_seconds, safe_total - 2 * ramp_sec);
  return [
    { duration: `${ramp_sec}s`, target: vus },
    { duration: `${hold_sec}s`, target: vus },
    { duration: `${ramp_sec}s`, target: 0 }
  ];
}

export const options = {
  stages: build_stages(target_vus, parse_duration_seconds(total_duration)),
  thresholds: {
    'http_req_failed{type:rest}': [`rate<${rest_error_rate_threshold}`],
    'http_req_duration{type:rest}': [`p(95)<${rest_p95_latency_ms}`],
    sse_stream_ok: [`rate>${sse_ok_rate_threshold}`]
  }
};

function select_symbols(available) {
  const pool = Array.isArray(available) && available.length > 0 ? available : fallback_symbols;
  const start_idx = (__VU - 1) % pool.length;
  const active_symbol = pool[start_idx];
  const tracked = [];
  const count = Math.min(tracked_symbol_count, Math.max(0, pool.length - 1));
  for (let i = 1; i <= count; i += 1) {
    tracked.push(pool[(start_idx + i) % pool.length]);
  }
  return { active_symbol, tracked };
}

function is_expected_sse_result(res) {
  if (res.status === 200) {
    return true;
  }
  if (res.status === 0) {
    const err_text = String(res.error || '').toLowerCase();
    return res.error_code === k6_timeout_error_code || err_text.includes('timeout') || err_text.includes('deadline');
  }
  return false;
}

export default function () {
  const jar = http.cookieJar();
  const client_id = `k6_vu_${__VU}`;

  const search_res = http.get(
    `${base_url}/api/symbols/search?q=&limit=${symbol_search_limit}`,
    { tags: { type: 'rest', endpoint: 'symbols_search' } }
  );

  let available = fallback_symbols;
  const search_ok = check(search_res, {
    'symbols search 200': (r) => r.status === 200
  });
  if (search_ok) {
    try {
      const parsed = JSON.parse(search_res.body);
      if (Array.isArray(parsed) && parsed.length > 0) {
        available = parsed;
      }
    } catch {
      available = fallback_symbols;
    }
  }

  const { active_symbol, tracked } = select_symbols(available);

  const set_active_res = http.post(
    `${base_url}/api/subscription/set-state`,
    JSON.stringify({
      clientId: client_id,
      symbol: active_symbol,
      state: subscription_states.active
    }),
    {
      headers: { 'Content-Type': 'application/json' },
      tags: { type: 'rest', endpoint: 'set_state' }
    }
  );
  check(set_active_res, {
    'set active state 200': (r) => r.status === 200
  });

  for (const sym of tracked) {
    const set_tracked_res = http.post(
      `${base_url}/api/subscription/set-state`,
      JSON.stringify({
        clientId: client_id,
        symbol: sym,
        state: subscription_states.tracked
      }),
      {
        headers: { 'Content-Type': 'application/json' },
        tags: { type: 'rest', endpoint: 'set_state' }
      }
    );
    check(set_tracked_res, {
      'set tracked state 200': (r) => r.status === 200
    });
  }

  const history_res = http.get(
    `${base_url}/api/history/${encodeURIComponent(active_symbol)}?limit=${history_limit}`,
    { tags: { type: 'rest', endpoint: 'history' } }
  );
  check(history_res, {
    'history query 200': (r) => r.status === 200
  });

  const cookies = jar.cookiesForURL(base_url);
  const sticky_vals = cookies[sticky_cookie_name];
  const has_sticky = Array.isArray(sticky_vals) && sticky_vals.length > 0;
  sticky_cookie_present.add(has_sticky);

  const sse_res = http.get(
    `${base_url}/api/sse/stream?clientId=${encodeURIComponent(client_id)}`,
    {
      timeout: sse_timeout,
      tags: { type: 'sse', endpoint: 'sse_stream' }
    }
  );
  sse_stream_ok.add(is_expected_sse_result(sse_res));

  sleep(think_time_seconds);
}
