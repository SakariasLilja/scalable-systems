<script>
  import '../styles/stock_card.css';
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
