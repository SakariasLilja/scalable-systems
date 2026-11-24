<script>
  import { createEventDispatcher } from 'svelte';
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

<style>
  .advisory-panel {
    background-color: #0f172a;
    border: 1px solid #1e293b;
    border-radius: 10px;
    padding: 1rem;
    display: flex;
    flex-direction: column;
    gap: 0.9rem;
    align-self: start;
  }
  .panel-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
    border-bottom: 1px solid #1e293b;
    padding-bottom: 0.7rem;
  }
  .panel-title-group {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }
  .panel-header h2 {
    margin: 0;
    font-size: 1.05rem;
    font-weight: 700;
    color: #f8fafc;
  }
  .count-pill {
    background-color: #1e293b;
    color: #94a3b8;
    border-radius: 999px;
    padding: 0.1rem 0.55rem;
    font-size: 0.78rem;
    font-weight: 700;
  }
  .header-actions {
    display: flex;
    align-items: center;
    gap: 0.4rem;
  }
  .history-btn,
  .dismiss-all-btn {
    border: none;
    border-radius: 5px;
    padding: 0.35rem 0.65rem;
    font-size: 0.78rem;
    font-weight: 600;
    cursor: pointer;
  }
  .history-btn {
    background-color: #334155;
    color: #e2e8f0;
  }
  .history-btn:hover {
    background-color: #475569;
  }
  .dismiss-all-btn {
    background-color: #3f1d2e;
    color: #fda4af;
  }
  .dismiss-all-btn:hover {
    background-color: #7f1d1d;
    color: #fecaca;
  }
  .follow-bar {
    display: flex;
    flex-direction: column;
    gap: 0.4rem;
    padding-bottom: 0.6rem;
    border-bottom: 1px solid #1e293b;
  }
  .follow-label {
    font-size: 0.74rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: #94a3b8;
  }
  .follow-pills {
    display: flex;
    flex-wrap: wrap;
    gap: 0.35rem;
  }
  .follow-pill {
    padding: 0.22rem 0.6rem;
    border-radius: 999px;
    border: 1px solid #334155;
    background-color: #1e293b;
    color: #cbd5e1;
    font-size: 0.76rem;
    font-weight: 600;
    cursor: pointer;
  }
  .follow-pill.followed {
    background-color: #0f766e;
    border-color: #14b8a6;
    color: #f0fdfa;
  }
  .follow-hint {
    font-size: 0.74rem;
    color: #64748b;
  }
  .panel-empty {
    text-align: center;
    padding: 2rem 1rem;
    color: #64748b;
    border: 1px dashed #334155;
    border-radius: 8px;
    font-size: 0.86rem;
  }
  .panel-cards {
    display: flex;
    flex-direction: column;
    gap: 0.9rem;
  }
</style>
