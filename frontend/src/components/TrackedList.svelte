<script>
  import { createEventDispatcher } from 'svelte';

  export let tracked_symbols = [];
  export let symbol_data = {};

  const dispatch = createEventDispatcher();

  function handle_select(symbol) {
    dispatch('select_active', { symbol });
  }

  function handle_remove(symbol) {
    dispatch('untrack_symbol', { symbol });
  }
</script>

<div class="cards-grid">
  {#if tracked_symbols.length === 0}
    <div class="empty-state">No symbols currently tracked. Use the search bar above to track symbols.</div>
  {:else}
    {#each tracked_symbols as symbol (symbol)}
      {@const data = symbol_data[symbol] || {}}
      <div class="stock-card" class:flash-buy={data.recent_alert === 'BUY'} class:flash-sell={data.recent_alert === 'SELL'}>
        <div class="card-header">
          <span class="symbol-title">{symbol}</span>
          {#if data.recent_alert}
            <span class="alert-badge" class:badge-buy={data.recent_alert === 'BUY'} class:badge-sell={data.recent_alert === 'SELL'}>
              {data.recent_alert}
            </span>
          {/if}
        </div>

        <div class="card-body">
          <div class="metric">
            <span class="metric-label">Last Price</span>
            <span class="metric-value">{data.price ? data.price.toFixed(2) : '--'}</span>
          </div>
          <div class="metric">
            <span class="metric-label">EMA (38)</span>
            <span class="metric-value">{data.ema38 ? data.ema38.toFixed(4) : '--'}</span>
          </div>
          <div class="metric">
            <span class="metric-label">EMA (100)</span>
            <span class="metric-value">{data.ema100 ? data.ema100.toFixed(4) : '--'}</span>
          </div>
        </div>

        <div class="card-actions">
          <button class="action-btn view-btn" on:click={() => handle_select(symbol)}>Open Chart</button>
          <button class="action-btn untrack-btn" on:click={() => handle_remove(symbol)}>Untrack</button>
        </div>
      </div>
    {/each}
  {/if}
</div>

<style>
  .cards-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
    gap: 1.25rem;
  }
  .empty-state {
    grid-column: 1 / -1;
    text-align: center;
    padding: 3rem;
    color: #64748b;
    border: 1px dashed #334155;
    border-radius: 8px;
  }
  .stock-card {
    background-color: #1e293b;
    border: 1px solid #334155;
    border-radius: 8px;
    padding: 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 1rem;
    transition: transform 0.15s, border-color 0.3s, box-shadow 0.3s;
  }
  .stock-card:hover {
    transform: translateY(-2px);
    border-color: #475569;
  }
  .flash-buy {
    border-color: #22c55e !important;
    box-shadow: 0 0 15px rgba(34, 197, 94, 0.4);
  }
  .flash-sell {
    border-color: #3b82f6 !important;
    box-shadow: 0 0 15px rgba(59, 130, 246, 0.4);
  }
  .card-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
  }
  .symbol-title {
    font-size: 1.15rem;
    font-weight: 700;
    color: #f8fafc;
  }
  .alert-badge {
    padding: 0.2rem 0.5rem;
    border-radius: 4px;
    font-size: 0.75rem;
    font-weight: 700;
    letter-spacing: 0.05em;
  }
  .badge-buy {
    background-color: #22c55e;
    color: #052e16;
  }
  .badge-sell {
    background-color: #3b82f6;
    color: #082f49;
  }
  .card-body {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    padding: 0.5rem 0;
    border-top: 1px solid #334155;
    border-bottom: 1px solid #334155;
  }
  .metric {
    display: flex;
    flex-direction: column;
  }
  .metric-label {
    font-size: 0.75rem;
    color: #94a3b8;
  }
  .metric-value {
    font-size: 0.95rem;
    font-weight: 600;
    color: #f1f5f9;
  }
  .card-actions {
    display: flex;
    gap: 0.5rem;
  }
  .action-btn {
    flex: 1;
    padding: 0.45rem;
    border-radius: 4px;
    border: none;
    font-size: 0.85rem;
    font-weight: 600;
    cursor: pointer;
  }
  .view-btn {
    background-color: #2563eb;
    color: white;
  }
  .untrack-btn {
    background-color: #334155;
    color: #94a3b8;
  }
  .untrack-btn:hover {
    color: #ef4444;
  }
</style>
