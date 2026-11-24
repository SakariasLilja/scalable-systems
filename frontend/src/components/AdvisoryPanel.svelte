<script>
  import '../styles/advisory_panel.css';
  import StockCard from './StockCard.svelte';

  export let title = 'Live Advisories';
  export let empty_text = 'No active advisories. Signals appear here for 120s.';
  export let advisory_items = [];
  export let symbol_data = {};
  export let latest_ticks = {};
  export let tracked_symbols = [];
</script>

<aside class="advisory-panel">
  <div class="panel-header">
    <div class="panel-title-group">
      <h2>{title}</h2>
      <span class="count-pill">{advisory_items.length}</span>
    </div>
  </div>

  {#if advisory_items.length === 0}
    <div class="panel-empty">{empty_text}</div>
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
