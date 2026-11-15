export const api_base_url = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5000';
export const sse_stream_url = `${api_base_url}/api/sse/stream`;

export const subscription_states = {
  hidden: 'hidden',
  tracked: 'tracked',
  active: 'active'
};

export const chart_colors = {
  ema_38: '#38bdf8',
  ema_100: '#f97316',
  price: '#a3e635',
  buy_alert: '#22c55e',
  sell_alert: '#3b82f6'
};

export const default_tracked_symbols = [
  'RDSA.NL',
  'A0HN5C.ETR',
  'CBOT.FR',
  'SGFI.FR'
];
