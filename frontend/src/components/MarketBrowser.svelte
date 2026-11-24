<script>
  import { createEventDispatcher } from 'svelte';
  import { get_markets, get_symbols_by_market } from '../lib/sse_service.js';
  import { default_market_symbols_limit } from '../constants.js';

  export let tracked_symbols = [];
  export let known_symbol_count = 0;
  export let reset_counter = 0;

  const dispatch = createEventDispatcher();

  let markets = [];
  let selected_market = '';
  let market_symbols = [];
  let filter_text = '';
  let is_loading = false;
  let last_known_count = -1;

  async function load_markets() {
    const result = await get_markets();
    markets = Array.isArray(result) ? result : [];
    dispatch('markets_loaded', { markets });
    if (selected_market && !markets.includes(selected_market)) {
      selected_market = '';
      market_symbols = [];
    }
  }

  async function select_market(market) {
    if (selected_market === market) {
      selected_market = '';
      market_symbols = [];
      filter_text = '';
      return;
    }
    selected_market = market;
    filter_text = '';
    is_loading = true;
    const symbols = await get_symbols_by_market(market, '', default_market_symbols_limit);
    if (selected_market === market) {
      market_symbols = Array.isArray(symbols) ? symbols : [];
      is_loading = false;
    }
  }

  function toggle_symbol(symbol) {
    if (tracked_symbols.includes(symbol)) {
      dispatch('untrack_symbol', { symbol });
    } else {
      dispatch('add_symbol', { symbol });
    }
  }

  $: if (reset_counter > 0) {
    markets = [];
    selected_market = '';
    market_symbols = [];
    filter_text = '';
    last_known_count = -1;
    dispatch('markets_loaded', { markets: [] });
  }

  $: if (known_symbol_count !== last_known_count) {
    last_known_count = known_symbol_count;
    load_markets();
    if (selected_market) {
      const req_market = selected_market;
      get_symbols_by_market(req_market, '', default_market_symbols_limit).then((symbols) => {
        if (selected_market === req_market && Array.isArray(symbols)) {
          market_symbols = symbols;
        }
      });
    }
  }

  $: visible_symbols = filter_text.trim()
    ? market_symbols.filter((s) => s.toLowerCase().includes(filter_text.trim().toLowerCase()))
    : market_symbols;
</script>

<section class="market-browser">
  <div class="market-header">
    <span class="market-label">Browse by Market</span>
    <div class="market-pills">
      {#if markets.length === 0}
        <span class="empty-hint">No markets discovered yet</span>
      {:else}
        {#each markets as market (market)}
          <button
            type="button"
            class="market-pill {selected_market === market ? 'active' : ''}"
            on:click={() => select_market(market)}
          >
            {market}
          </button>
        {/each}
      {/if}
    </div>
  </div>

  {#if selected_market}
    <div class="market-panel">
      <div class="market-panel-bar">
        <span class="panel-title">{selected_market} Stocks ({visible_symbols.length})</span>
        <input
          type="text"
          class="filter-input"
          placeholder="Filter {selected_market} symbols..."
          bind:value={filter_text}
        />
      </div>

      {#if is_loading}
        <div class="panel-status">Loading {selected_market} symbols...</div>
      {:else if visible_symbols.length === 0}
        <div class="panel-status">No symbols found in {selected_market}.</div>
      {:else}
        <div class="symbol-chips">
          {#each visible_symbols as symbol (symbol)}
            {@const is_tracked = tracked_symbols.includes(symbol)}
            <button
              type="button"
              class="symbol-chip {is_tracked ? 'tracked' : ''}"
              on:click={() => toggle_symbol(symbol)}
            >
              <span>{symbol}</span>
              <span class="chip-tag">{is_tracked ? 'Tracked' : '+ Track'}</span>
            </button>
          {/each}
        </div>
      {/if}
    </div>
  {/if}
</section>

<style>
  .market-browser {
    background-color: #0f172a;
    border: 1px solid #1e293b;
    border-radius: 8px;
    padding: 0.9rem 1.1rem;
    margin-bottom: 1.5rem;
  }
  .market-header {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.75rem;
  }
  .market-label {
    font-size: 0.8rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: #94a3b8;
  }
  .market-pills {
    display: flex;
    flex-wrap: wrap;
    gap: 0.45rem;
    align-items: center;
  }
  .empty-hint {
    font-size: 0.85rem;
    color: #64748b;
  }
  .market-pill {
    padding: 0.35rem 0.75rem;
    border-radius: 999px;
    border: 1px solid #334155;
    background-color: #1e293b;
    color: #e2e8f0;
    font-size: 0.82rem;
    font-weight: 600;
    cursor: pointer;
  }
  .market-pill.active {
    background-color: #2563eb;
    border-color: #3b82f6;
    color: #ffffff;
  }
  .market-panel {
    margin-top: 0.9rem;
    padding-top: 0.9rem;
    border-top: 1px solid #1e293b;
  }
  .market-panel-bar {
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    margin-bottom: 0.75rem;
  }
  .panel-title {
    font-size: 0.9rem;
    font-weight: 600;
    color: #f1f5f9;
  }
  .filter-input {
    padding: 0.35rem 0.7rem;
    border-radius: 6px;
    border: 1px solid #334155;
    background-color: #1e293b;
    color: #f8fafc;
    font-size: 0.85rem;
    min-width: 200px;
  }
  .panel-status {
    font-size: 0.85rem;
    color: #94a3b8;
    padding: 0.5rem 0;
  }
  .symbol-chips {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(175px, 1fr));
    gap: 0.5rem;
    max-height: 220px;
    overflow-y: auto;
  }
  .symbol-chip {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.45rem 0.65rem;
    border-radius: 6px;
    border: 1px solid #334155;
    background-color: #1e293b;
    color: #f8fafc;
    font-size: 0.82rem;
    cursor: pointer;
    text-align: left;
  }
  .symbol-chip.tracked {
    border-color: #22c55e;
    background-color: #052e16;
  }
  .chip-tag {
    font-size: 0.7rem;
    color: #93c5fd;
    font-weight: 600;
  }
  .symbol-chip.tracked .chip-tag {
    color: #86efac;
  }
</style>
