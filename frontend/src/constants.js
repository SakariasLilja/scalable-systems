export const api_base_url = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5000';
export const sse_stream_url = `${api_base_url}/api/sse/stream`;

export const subscription_states = {
  hidden: 'hidden',
  tracked: 'tracked',
  active: 'active'
};

export const tracked_poll_interval_ms = 10000;
export const active_chart_update_interval_ms = 2000;
export const default_simulation_speed = 1.0;
export const alert_dismiss_timeout_ms = 4000;
export const default_symbol_search_limit = 20;
export const search_debounce_ms = 150;
export const default_trading_day = '08-11-21';
export const tracked_symbols_storage_key = 'tick_detector_tracked_symbols';
export const bytes_per_mb = 1048576;

export const chart_colors = {
  ema_38: '#38bdf8',
  ema_100: '#f97316',
  price: '#a3e635',
  buy_alert: '#22c55e',
  sell_alert: '#3b82f6'
};

export const default_tracked_symbols = [];
