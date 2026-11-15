import { api_base_url, sse_stream_url } from '../constants.js';

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

  event_source.addEventListener('advisory_alert', (e) => {
    const data = JSON.parse(e.data);
    on_event('advisory_alert', data);
  });

  event_source.onerror = (err) => {
    on_event('error', err);
  };

  return event_source;
}

export async function set_symbol_state(client_id, symbol, state) {
  const response = await fetch(`${api_base_url}/api/subscription/set-state`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      clientId: client_id,
      symbol: symbol,
      state: state
    })
  });
  return response.json();
}

export async function fetch_history(symbol, limit = 50) {
  const response = await fetch(`${api_base_url}/api/history/${encodeURIComponent(symbol)}?limit=${limit}`);
  if (!response.ok) return [];
  return response.json();
}

export async function start_simulation(file_path = null, speed_factor = 1.0) {
  const response = await fetch(`${api_base_url}/api/simulator/start`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ filePath: file_path, speedFactor: speed_factor })
  });
  return response.json();
}

export async function pause_simulation() {
  const response = await fetch(`${api_base_url}/api/simulator/pause`, { method: 'POST' });
  return response.json();
}

export async function resume_simulation() {
  const response = await fetch(`${api_base_url}/api/simulator/resume`, { method: 'POST' });
  return response.json();
}

export async function set_simulation_speed(speed_factor) {
  const response = await fetch(`${api_base_url}/api/simulator/speed`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ speedFactor: speed_factor })
  });
  return response.ok;
}

export async function get_simulation_status() {
  const response = await fetch(`${api_base_url}/api/simulator/status`);
  if (!response.ok) return null;
  return response.json();
}
