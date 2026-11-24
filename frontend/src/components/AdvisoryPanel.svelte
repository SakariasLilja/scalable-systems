<script>
  import { createEventDispatcher } from 'svelte';
  import '../styles/advisory_panel.css';
  import StockCard from './StockCard.svelte';

  export let title = 'Live Advisories';
  export let empty_text = 'No active advisories. Signals appear here for 120s.';
  export let advisory_items = [];
  export let symbol_data = {};
  export let latest_ticks = {};
  export let tracked_symbols = [];
  export let auto_action_label = '';
  export let auto_action_enabled = false;

  const dispatch = createEventDispatcher();
</script>

<aside class="advisory-panel">
  <div class="panel-header">
    <div class="panel-title-group">
      <h2>{title}</h2>
      <span class="count-pill">{advisory_items.length}</span>
    </div>
    {#if advisory_items.length > 0}
      <button type="button" class="panel-dismiss-btn" on:click={() => dispatch('dismiss_all')}>Dismiss All</button>
    {/if}
  </div>

  {#if auto_action_label}
    <div class="panel-toggle-row">
      <button
        type="button"
        class="auto-toggle-btn"
        class:enabled={auto_action_enabled}
        on:click={() => dispatch('toggle_auto_action')}
      >
        {auto_action_label}: {auto_action_enabled ? 'ON' : 'OFF'}
      </button>
    </div>
  {/if}

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
