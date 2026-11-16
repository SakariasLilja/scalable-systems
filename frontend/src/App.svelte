<script>
  import { onMount, onDestroy } from 'svelte';
  import SearchBar from './components/SearchBar.svelte';
  import SimulatorControls from './components/SimulatorControls.svelte';
  import TrackedList from './components/TrackedList.svelte';
  import ActiveChartModal from './components/ActiveChartModal.svelte';
  import {
    get_or_create_client_id,
    connect_sse,
    set_symbol_state
  } from './lib/sse_service.js';
  import {
    default_tracked_symbols,
    subscription_states,
    alert_dismiss_timeout_ms,
    tracked_symbols_storage_key
  } from './constants.js';

  function load_initial_tracked_symbols() {
    try {
      const cached = localStorage.getItem(tracked_symbols_storage_key);
      if (cached) {
        const parsed = JSON.parse(cached);
        if (Array.isArray(parsed)) return parsed;
      }
    } catch (e) {
      // fallback on invalid cache
    }
    return [...default_tracked_symbols];
  }

  function save_tracked_symbols(symbols) {
    try {
      localStorage.setItem(tracked_symbols_storage_key, JSON.stringify(symbols));
    } catch (e) {
      // ignore storage errors
    }
  }

  let client_id = '';
  let event_source = null;
  let tracked_symbols = load_initial_tracked_symbols();
  let symbol_data = {};
  let active_modal_symbol = null;
  let latest_active_tick = null;
  let alert_timers = {};

  async function re_register_subscriptions() {
    if (!client_id) return;
    for (const sym of tracked_symbols) {
      if (sym === active_modal_symbol) {
        await set_symbol_state(client_id, sym, subscription_states.active);
      } else {
        await set_symbol_state(client_id, sym, subscription_states.tracked);
      }
    }
  }

  function handle_sse_event(event_type, payload) {
    if (event_type === 'connected') {
      re_register_subscriptions();
      return;
    }

    if (event_type === 'advisory_alert') {
      const { symbol, type, closePrice, ema38, ema100 } = payload;
      symbol_data[symbol] = {
        ...(symbol_data[symbol] || {}),
        price: closePrice,
        ema38: ema38,
        ema100: ema100,
        recent_alert: type
      };
      symbol_data = symbol_data;

      if (symbol === active_modal_symbol) {
        latest_active_tick = payload;
      }

      if (alert_timers[symbol]) {
        clearTimeout(alert_timers[symbol]);
      }

      alert_timers[symbol] = setTimeout(() => {
        if (symbol_data[symbol] && symbol_data[symbol].recent_alert === type) {
          symbol_data[symbol].recent_alert = null;
          symbol_data = symbol_data;
        }
        delete alert_timers[symbol];
      }, alert_dismiss_timeout_ms);
    } else if (event_type === 'active_update') {
      const { symbol, closePrice, ema38, ema100 } = payload;
      symbol_data[symbol] = {
        ...(symbol_data[symbol] || {}),
        price: closePrice,
        ema38: ema38,
        ema100: ema100
      };
      symbol_data = symbol_data;

      if (symbol === active_modal_symbol) {
        latest_active_tick = payload;
      }
    } else if (event_type === 'tracked_update') {
      const { symbol, price } = payload;
      symbol_data[symbol] = {
        ...(symbol_data[symbol] || {}),
        price: price
      };
      symbol_data = symbol_data;
    }
  }

  async function handle_add_symbol(event) {
    const symbol = event.detail.symbol;
    if (!tracked_symbols.includes(symbol)) {
      tracked_symbols = [...tracked_symbols, symbol];
      save_tracked_symbols(tracked_symbols);
      await set_symbol_state(client_id, symbol, subscription_states.tracked);
    }
  }

  async function handle_untrack_symbol(event) {
    const symbol = event.detail.symbol;
    tracked_symbols = tracked_symbols.filter(s => s !== symbol);
    save_tracked_symbols(tracked_symbols);
    await set_symbol_state(client_id, symbol, subscription_states.hidden);
    delete symbol_data[symbol];
    symbol_data = symbol_data;
  }

  async function handle_open_active(event) {
    const symbol = event.detail.symbol;
    active_modal_symbol = symbol;
    await set_symbol_state(client_id, symbol, subscription_states.active);
  }

  async function handle_close_modal() {
    if (active_modal_symbol) {
      const prev_symbol = active_modal_symbol;
      active_modal_symbol = null;
      latest_active_tick = null;
      if (tracked_symbols.includes(prev_symbol)) {
        await set_symbol_state(client_id, prev_symbol, subscription_states.tracked);
      } else {
        await set_symbol_state(client_id, prev_symbol, subscription_states.hidden);
      }
    }
  }

  onMount(() => {
    client_id = get_or_create_client_id();
    event_source = connect_sse(client_id, handle_sse_event);
  });

  onDestroy(() => {
    if (event_source) {
      event_source.close();
    }
    Object.values(alert_timers).forEach(clearTimeout);
  });
</script>

<main class="app-container">
  <header class="app-header">
    <div class="header-content">
      <h1>Financial Tick Data Breakout Detector</h1>
      <span class="header-subtitle">CS-E4780 Scalable Systems Project &bull; Tumbling Windows &bull; EMA (38/100)</span>
    </div>
  </header>

  <div class="content-wrapper">
    <SimulatorControls />
    <SearchBar on:add_symbol={handle_add_symbol} />
    <TrackedList
      {tracked_symbols}
      {symbol_data}
      on:select_active={handle_open_active}
      on:untrack_symbol={handle_untrack_symbol}
    />
  </div>

  {#if active_modal_symbol}
    <ActiveChartModal
      active_symbol={active_modal_symbol}
      latest_tick={latest_active_tick}
      on:close={handle_close_modal}
    />
  {/if}
</main>

<style>
  :global(body) {
    margin: 0;
    padding: 0;
    background-color: #0b0f19;
    color: #f8fafc;
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif;
  }
  .app-container {
    min-height: 100vh;
    display: flex;
    flex-direction: column;
  }
  .app-header {
    background-color: #0f172a;
    border-bottom: 1px solid #1e293b;
    padding: 1.25rem 2rem;
  }
  .header-content h1 {
    margin: 0 0 0.25rem 0;
    font-size: 1.5rem;
    font-weight: 800;
    color: #f8fafc;
  }
  .header-subtitle {
    font-size: 0.85rem;
    color: #94a3b8;
  }
  .content-wrapper {
    max-width: 1200px;
    width: 100%;
    margin: 0 auto;
    padding: 2rem 1.5rem;
    box-sizing: border-box;
  }
</style>
