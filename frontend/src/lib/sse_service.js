import {
  api_base_url,
  sse_stream_url,
  default_symbol_search_limit,
  default_market_symbols_limit,
  search_timeout_ms
} from '../constants.js';

export function get_or_create_client_id() {
  const storage_key = 'tick_detector_client_id';
  let client_id = localStorage.getItem(storage_key);
  if (!client_id) {
    client_id = 'client_' + Math.random().toString(36).substring(2, 11);
    localStorage.setItem(storage_key, client_id);
  }
  return client_id;
}

export function connect_sse(client_id, on_event) {
  const url = `${sse_stream_url}?clientId=${encodeURIComponent(client_id)}`;
  const event_source = new EventSource(url);

  event_source.addEventListener('connected', (e) => {
    const data = JSON.parse(e.data);
    on_event('connected', data);
  });

  event_source.addEventListener('batch_update', (e) => {
    const data = JSON.parse(e.data);
    on_event('batch_update', data);
  });

  event_source.addEventListener('advisory_alert', (e) => {
    const data = JSON.parse(e.data);
    on_event('advisory_alert', data);
  });

  event_source.addEventListener('active_update', (e) => {
    const data = JSON.parse(e.data);
    on_event('active_update', data);
  });

  event_source.addEventListener('tracked_update', (e) => {
    const data = JSON.parse(e.data);
    on_event('tracked_update', data);
  });

  event_source.addEventListener('reset', (e) => {
    const data = JSON.parse(e.data);
    on_event('reset', data);
  });

  event_source.onopen = (e) => {
    on_event('open', e);
  };

  event_source.onerror = (err) => {
    on_event('error', err);
  };

  return event_source;
}

export async function set_symbol_state(client_id, symbol, state) {
  try {
    const response = await fetch(`${api_base_url}/api/subscription/set-state`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        clientId: client_id,
        symbol: symbol,
        state: state
      })
    });
    if (!response.ok) return { success: false, status: response.status };
    return await response.json();
  } catch (err) {
    console.error('Failed to set symbol state:', err);
    return { success: false, error: err.message };
  }
}

export async function fetch_history(symbol, limit = 50) {
  const response = await fetch(`${api_base_url}/api/history/${encodeURIComponent(symbol)}?limit=${limit}`);
  if (!response.ok) return [];
  return response.json();
}

export async function search_symbols(query = '', limit = default_symbol_search_limit, external_signal = null) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), search_timeout_ms);
  const on_external_abort = () => controller.abort();
  if (external_signal) {
    if (external_signal.aborted) {
      clearTimeout(timer);
      return null;
    }
    external_signal.addEventListener('abort', on_external_abort, { once: true });
  }

  try {
    const response = await fetch(
      `${api_base_url}/api/symbols/search?q=${encodeURIComponent(query)}&limit=${limit}`,
      { signal: controller.signal }
    );
    if (!response.ok) return [];
    return await response.json();
  } catch (err) {
    if (err && err.name === 'AbortError') {
      return null;
    }
    return [];
  } finally {
    clearTimeout(timer);
    if (external_signal) {
      external_signal.removeEventListener('abort', on_external_abort);
    }
  }
}

export async function get_markets() {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), search_timeout_ms);
  try {
    const response = await fetch(`${api_base_url}/api/symbols/markets`, { signal: controller.signal });
    if (!response.ok) return [];
    return await response.json();
  } catch {
    return [];
  } finally {
    clearTimeout(timer);
  }
}

export async function get_symbols_by_market(market, query = '', limit = default_market_symbols_limit) {
  if (!market) return [];
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), search_timeout_ms);
  try {
    const response = await fetch(
      `${api_base_url}/api/symbols/by-market/${encodeURIComponent(market)}?q=${encodeURIComponent(query)}&limit=${limit}`,
      { signal: controller.signal }
    );
    if (!response.ok) return [];
    return await response.json();
  } catch {
    return [];
  } finally {
    clearTimeout(timer);
  }
}

export async function clear_database() {
  try {
    const response = await fetch(`${api_base_url}/api/data`, { method: 'DELETE' });
    return { ok: response.ok, status: response.status };
  } catch (err) {
    console.error('Failed to clear database:', err);
    return { ok: false, error: err.message };
  }
}

export async function get_system_status() {
  try {
    const response = await fetch(`${api_base_url}/api/status`);
    if (!response.ok) return null;
    return await response.json();
  } catch {
    return null;
  }
}
