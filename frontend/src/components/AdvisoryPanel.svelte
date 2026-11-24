<script>
  import { createEventDispatcher } from 'svelte';
  import '../styles/advisory_panel.css';
  import StockCard from './StockCard.svelte';

  export let advisory_items = [];
  export let symbol_data = {};
  export let latest_ticks = {};
  export let tracked_symbols = [];
  export let available_markets = [];
  export let followed_markets = [];

  const dispatch = createEventDispatcher();
</script>

<aside class="advisory-panel">
  <div class="panel-header">
    <div class="panel-title-group">
      <h2>Live Advisories</h2>
      <span class="count-pill">{advisory_items.length}</span>
    </div>
    <div class="header-actions">
      {#if advisory_items.length > 0}
        <button type="button" class="dismiss-all-btn" on:click={() => dispatch('dismiss_all_advisories')}>Dismiss All</button>
      {/if}
      <button type="button" class="history-btn" on:click={() => dispatch('open_history')}>History Log</button>
    </div>
  </div>

  <div class="follow-bar">
    <span class="follow-label">Follow Markets</span>
    <div class="follow-pills">
      {#if available_markets.length === 0}
        <span class="follow-hint">Tracked stocks always alert</span>
      {:else}
        {#each available_markets as market (market)}
          <button
            type="button"
            class="follow-pill"
            class:followed={followed_markets.includes(market)}
            on:click={() => dispatch('toggle_follow_market', { market })}
          >
            {market}
          </button>
        {/each}
      {/if}
    </div>
    {#if available_markets.length > 0 && followed_markets.length === 0}
      <span class="follow-hint">Tracked stocks always alert</span>
    {/if}
  </div>

  {#if advisory_items.length === 0}
    <div class="panel-empty">No active Buy/Sell advisories. Signals appear here for 120s.</div>
  {:else}
    <div class="panel-cards">
      {#each advisory_items as item (item.symbol)}
        <StockCard
          symbol={item.symbol}
          data={symbol_data[item.symbol] || {}}
          latest_tick={latest_ticks[item.symbol] || null}
          mode="advisory"
          is_tracked={tracked_symbols.includes(item.symbol)}
          advisory_type={item.type}
          on:select_active
          on:track_symbol
          on:untrack_symbol
          on:dismiss_advisory
          on:advisory_action
        />
      {/each}
    </div>
  {/if}
</aside>
