export const default_base_url = 'http://localhost:8080';
export const default_vus = 50;
export const default_duration = '60s';
export const default_sse_timeout = '10s';
export const sticky_cookie_name = 'trading_session';
export const symbol_search_limit = 20;
export const tracked_symbol_count = 3;
export const history_limit = 50;
export const think_time_seconds = 1;
export const stage_ramp_ratio = 0.2;
export const min_stage_seconds = 1;
export const default_duration_seconds = 60;
export const seconds_per_minute = 60;
export const seconds_per_hour = 3600;
export const rest_error_rate_threshold = 0.05;
export const rest_p95_latency_ms = 1000;
export const sse_ok_rate_threshold = 0.95;
export const k6_timeout_error_code = 1050;
export const subscription_states = {
  active: 'active',
  tracked: 'tracked',
  hidden: 'hidden'
};
export const fallback_symbols = ['SAP.ETR', 'BMW.ETR', 'SIE.ETR', 'ADS.ETR'];
