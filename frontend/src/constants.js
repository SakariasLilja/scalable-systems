export const api_base_url = import.meta.env.VITE_API_BASE_URL ?? '';
export const sse_stream_url = `${api_base_url}/api/sse/stream`;

export const subscription_states = {
  hidden: 'hidden',
  tracked: 'tracked',
  active: 'active'
};

export const status_poll_interval_ms = 5000;
export const alert_dismiss_timeout_ms = 4000;
export const card_history_limit = 10;
export const modal_history_limit = 50;
export const advisory_expiry_ms = 120000;
export const advisory_panel_max_items = 20;
export const advisory_history_limit = 100;
export const audio_throttle_ms = 1500;
export const advisory_audio_url = '/advisory_notification.wav';
export const default_symbol_search_limit = 20;
export const default_market_symbols_limit = 5000;
export const search_debounce_ms = 150;
export const search_timeout_ms = 3000;
export const tracked_symbols_storage_key = 'tick_detector_tracked_symbols';

export const chart_colors = {
  ema_38: '#38bdf8',
  ema_100: '#f97316',
  price: '#a3e635',
  buy_alert: '#22c55e',
  sell_alert: '#3b82f6'
};

export const default_tracked_symbols = [];
