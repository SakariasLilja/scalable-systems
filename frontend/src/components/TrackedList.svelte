<script>
  import StockCard from './StockCard.svelte';

  export let tracked_symbols = [];
  export let symbol_data = {};
  export let latest_ticks = {};
</script>

<div class="cards-grid">
  {#if tracked_symbols.length === 0}
    <div class="empty-state">No symbols currently tracked. Use the search bar or market browser above to track symbols.</div>
  {:else}
    {#each tracked_symbols as symbol (symbol)}
      <StockCard
        {symbol}
        data={symbol_data[symbol] || {}}
        latest_tick={latest_ticks[symbol] || null}
        mode="tracked"
        is_tracked={true}
        on:select_active
        on:untrack_symbol
        on:advisory_action
      />
    {/each}
  {/if}
</div>

<style>
  .cards-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
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
</style>
