<script>
  import { onMount, onDestroy } from 'svelte';
  import SearchBar from './components/SearchBar.svelte';
  import MarketBrowser from './components/MarketBrowser.svelte';
  import SystemStatusBar from './components/SystemStatusBar.svelte';
  import TrackedList from './components/TrackedList.svelte';
  import AdvisoryPanel from './components/AdvisoryPanel.svelte';
  import ActiveChartModal from './components/ActiveChartModal.svelte';
  import AdvisoryHistoryModal from './components/AdvisoryHistoryModal.svelte';
  import {
    get_or_create_client_id,
    connect_sse,
    set_symbol_state,
    get_system_status
  } from './lib/sse_service.js';
  import {
    default_tracked_symbols,
    subscription_states,
    advisory_expiry_ms,
    advisory_panel_max_items,
    advisory_history_limit,
    audio_throttle_ms,
    advisory_audio_url,
    tracked_symbols_storage_key,
    status_poll_interval_ms
  } from './constants.js';

  function load_initial_tracked_symbols() {
    try {
      const cached = localStorage.getItem(tracked_symbols_storage_key);
      if (cached) {
        const parsed = JSON.parse(cached);
        if (Array.isArray(parsed)) return parsed;
      }
    } catch {
    }
    return [...default_tracked_symbols];
  }

  function save_tracked_symbols(symbols) {
    try {
      localStorage.setItem(tracked_symbols_storage_key, JSON.stringify(symbols));
    } catch {
    }
  }

  let client_id = '';
  let event_source = null;
  let tracked_symbols = load_initial_tracked_symbols();
  let symbol_data = {};
  let latest_ticks = {};
  let advisory_items = [];
  let advisory_timers = {};
  let session_advisories = [];
  let active_modal_symbol = null;
  let is_history_modal_open = false;
  let last_audio_played_at = 0;
  let is_sse_connected = false;
  let latest_sim_time = null;
  let known_symbol_count = 0;
  let reset_counter = 0;
  let status_timer = null;

  function is_in_advisory_panel(symbol) {
    return advisory_items.some((item) => item.symbol === symbol);
  }

  async function sync_symbol_subscription(symbol) {
    if (!client_id || !symbol) return;
    if (symbol === active_modal_symbol) {
      await set_symbol_state(client_id, symbol, subscription_states.active);
    } else if (tracked_symbols.includes(symbol) || is_in_advisory_panel(symbol)) {
      await set_symbol_state(client_id, symbol, subscription_states.tracked);
    } else {
      await set_symbol_state(client_id, symbol, subscription_states.hidden);
    }
  }

  function play_advisory_sound() {
    const now = Date.now();
    if (now - last_audio_played_at < audio_throttle_ms) return;
    last_audio_played_at = now;
    try {
      const audio = new Audio(advisory_audio_url);
      audio.play().catch(() => {});
    } catch {
    }
  }

  async function refresh_system_status() {
    const status = await get_system_status();
    if (status) {
      known_symbol_count = status.knownSymbols || 0;
      if (status.latestSimulationTime) {
        latest_sim_time = status.latestSimulationTime;
      }
    }
  }

  function clear_advisory_timer(symbol) {
    if (advisory_timers[symbol]) {
      clearTimeout(advisory_timers[symbol]);
      delete advisory_timers[symbol];
    }
  }

  async function remove_advisory(symbol) {
    clear_advisory_timer(symbol);
    advisory_items = advisory_items.filter((item) => item.symbol !== symbol);
    if (symbol_data[symbol]) {
      symbol_data[symbol] = { ...symbol_data[symbol], recent_alert: null };
      symbol_data = symbol_data;
    }
    await sync_symbol_subscription(symbol);
  }

  function schedule_advisory_expiry(symbol) {
    clear_advisory_timer(symbol);
    advisory_timers[symbol] = setTimeout(() => {
      remove_advisory(symbol);
    }, advisory_expiry_ms);
  }

  function reset_client_state() {
    Object.values(advisory_timers).forEach(clearTimeout);
    advisory_timers = {};
    advisory_items = [];
    session_advisories = [];
    symbol_data = {};
    latest_ticks = {};
    tracked_symbols = [];
    save_tracked_symbols([]);
    active_modal_symbol = null;
    is_history_modal_open = false;
    latest_sim_time = null;
    known_symbol_count = 0;
    reset_counter += 1;
  }

  async function re_register_subscriptions() {
    if (!client_id) return;
    const all_needed = new Set([
      ...tracked_symbols,
      ...advisory_items.map((item) => item.symbol),
      ...(active_modal_symbol ? [active_modal_symbol] : [])
    ]);
    for (const sym of all_needed) {
      await sync_symbol_subscription(sym);
    }
  }

  function apply_single_data_event(event_type, payload) {
    if (event_type === 'advisory_alert') {
      const { symbol, type, closePrice, ema38, ema100, time } = payload;
      if (time) latest_sim_time = time;
      symbol_data[symbol] = {
        ...(symbol_data[symbol] || {}),
        price: closePrice,
        ema38,
        ema100,
        recent_alert: type
      };
      latest_ticks[symbol] = payload;

      const filtered = advisory_items.filter((item) => item.symbol !== symbol);
      const next_items = [{ symbol, type, time }, ...filtered];
      if (next_items.length > advisory_panel_max_items) {
        const evicted = next_items.splice(advisory_panel_max_items);
        for (const item of evicted) {
          clear_advisory_timer(item.symbol);
          sync_symbol_subscription(item.symbol);
        }
      }
      advisory_items = next_items;
      schedule_advisory_expiry(symbol);

      session_advisories = [{ symbol, type, closePrice, ema38, ema100, time }, ...session_advisories].slice(0, advisory_history_limit);
      sync_symbol_subscription(symbol);
      play_advisory_sound();
      return true;
    }

    if (event_type === 'window_update' || event_type === 'active_update') {
      const { symbol, closePrice, ema38, ema100, time } = payload;
      if (time) latest_sim_time = time;
      symbol_data[symbol] = {
        ...(symbol_data[symbol] || {}),
        price: closePrice,
        ema38,
        ema100
      };
      latest_ticks[symbol] = payload;
      return true;
    }

    if (event_type === 'tracked_update') {
      const { symbol, price, time } = payload;
      if (time) latest_sim_time = time;
      symbol_data[symbol] = {
        ...(symbol_data[symbol] || {}),
        price
      };
      latest_ticks[symbol] = payload;
      return true;
    }

    return false;
  }

  function handle_sse_event(event_type, payload) {
    if (event_type === 'open') {
      is_sse_connected = true;
      return;
    }

    if (event_type === 'error') {
      is_sse_connected = false;
      return;
    }

    if (event_type === 'connected') {
      is_sse_connected = true;
      re_register_subscriptions();
      return;
    }

    if (event_type === 'reset') {
      reset_client_state();
      return;
    }

    if (event_type === 'batch_update' && Array.isArray(payload)) {
      let dirty = false;
      for (const item of payload) {
        if (item && apply_single_data_event(item.eventType, item.payload)) {
          dirty = true;
        }
      }
      if (dirty) {
        symbol_data = symbol_data;
        latest_ticks = latest_ticks;
      }
      return;
    }

    if (apply_single_data_event(event_type, payload)) {
      symbol_data = symbol_data;
      latest_ticks = latest_ticks;
    }
  }

  async function handle_add_symbol(event) {
    const symbol = event.detail.symbol;
    if (!tracked_symbols.includes(symbol)) {
      tracked_symbols = [...tracked_symbols, symbol];
      save_tracked_symbols(tracked_symbols);
      await sync_symbol_subscription(symbol);
    }
  }

  async function handle_untrack_symbol(event) {
    const symbol = event.detail.symbol;
    tracked_symbols = tracked_symbols.filter((s) => s !== symbol);
    save_tracked_symbols(tracked_symbols);
    await sync_symbol_subscription(symbol);
    if (!is_in_advisory_panel(symbol)) {
      delete symbol_data[symbol];
      delete latest_ticks[symbol];
      symbol_data = symbol_data;
      latest_ticks = latest_ticks;
    }
  }

  async function handle_open_active(event) {
    const symbol = event.detail.symbol;
    const prev_symbol = active_modal_symbol;
    active_modal_symbol = symbol;
    if (prev_symbol && prev_symbol !== symbol) {
      await sync_symbol_subscription(prev_symbol);
    }
    await sync_symbol_subscription(symbol);
  }

  async function handle_close_modal() {
    if (active_modal_symbol) {
      const prev_symbol = active_modal_symbol;
      active_modal_symbol = null;
      await sync_symbol_subscription(prev_symbol);
    }
  }

  async function handle_dismiss_advisory(event) {
    await remove_advisory(event.detail.symbol);
  }

  async function handle_advisory_action(event) {
    await remove_advisory(event.detail.symbol);
  }

  onMount(() => {
    client_id = get_or_create_client_id();
    event_source = connect_sse(client_id, handle_sse_event);
    refresh_system_status();
    status_timer = setInterval(refresh_system_status, status_poll_interval_ms);
  });

  onDestroy(() => {
    if (event_source) {
      event_source.close();
    }
    if (status_timer) {
      clearInterval(status_timer);
    }
    Object.values(advisory_timers).forEach(clearTimeout);
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
    <SystemStatusBar
      is_connected={is_sse_connected}
      {latest_sim_time}
      {known_symbol_count}
      on:db_cleared={reset_client_state}
    />
    <SearchBar {reset_counter} on:add_symbol={handle_add_symbol} />
    <MarketBrowser
      {tracked_symbols}
      {known_symbol_count}
      {reset_counter}
      on:add_symbol={handle_add_symbol}
      on:untrack_symbol={handle_untrack_symbol}
    />

    <div class="workspace-grid">
      <section class="tracked-column">
        <TrackedList
          {tracked_symbols}
          {symbol_data}
          {latest_ticks}
          on:select_active={handle_open_active}
          on:untrack_symbol={handle_untrack_symbol}
          on:advisory_action={handle_advisory_action}
        />
      </section>

      <AdvisoryPanel
        {advisory_items}
        {symbol_data}
        {latest_ticks}
        {tracked_symbols}
        on:select_active={handle_open_active}
        on:track_symbol={handle_add_symbol}
        on:untrack_symbol={handle_untrack_symbol}
        on:dismiss_advisory={handle_dismiss_advisory}
        on:advisory_action={handle_advisory_action}
        on:open_history={() => (is_history_modal_open = true)}
      />
    </div>
  </div>

  {#if active_modal_symbol}
    <ActiveChartModal
      active_symbol={active_modal_symbol}
      latest_tick={latest_ticks[active_modal_symbol] || null}
      advisory_type={symbol_data[active_modal_symbol]?.recent_alert || null}
      on:advisory_action={handle_advisory_action}
      on:close={handle_close_modal}
    />
  {/if}

  {#if is_history_modal_open}
    <AdvisoryHistoryModal
      {session_advisories}
      {tracked_symbols}
      on:add_symbol={handle_add_symbol}
      on:untrack_symbol={handle_untrack_symbol}
      on:close={() => (is_history_modal_open = false)}
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
    max-width: 1600px;
    width: 100%;
    margin: 0 auto;
    padding: 1.5rem;
    box-sizing: border-box;
  }
  .workspace-grid {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 400px;
    gap: 1.5rem;
    align-items: start;
  }
  .tracked-column {
    min-width: 0;
  }
  @media (max-width: 1100px) {
    .workspace-grid {
      grid-template-columns: 1fr;
    }
  }
</style>
