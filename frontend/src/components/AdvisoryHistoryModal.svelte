<script>
  import { onMount, createEventDispatcher } from 'svelte';
  import '../styles/modals.css';
  import { fetch_advisories } from '../lib/sse_service.js';
  import { format_eu_datetime } from '../lib/date_format.js';
  import { advisory_history_limit } from '../constants.js';

  export let session_advisories = [];
  export let tracked_symbols = [];

  const dispatch = createEventDispatcher();
  let db_advisories = [];
  let is_loading = true;

  $: merged_advisories = merge_advisories(session_advisories, db_advisories);

  function merge_advisories(live_list, db_list) {
    const seen = new Set();
    const combined = [];
    for (const item of [...live_list, ...db_list]) {
      if (!item || !item.symbol) continue;
      const key = `${item.symbol}|${item.type}|${item.time || ''}`;
      if (seen.has(key)) continue;
      seen.add(key);
      combined.push(item);
    }
    combined.sort((a, b) => new Date(b.time || 0).getTime() - new Date(a.time || 0).getTime());
    return combined.slice(0, advisory_history_limit);
  }

  function handle_close() {
    dispatch('close');
  }

  function toggle_track(symbol) {
    if (tracked_symbols.includes(symbol)) {
      dispatch('untrack_symbol', { symbol });
    } else {
      dispatch('add_symbol', { symbol });
    }
  }

  onMount(async () => {
    is_loading = true;
    db_advisories = await fetch_advisories(advisory_history_limit);
    is_loading = false;
  });
</script>

<svelte:window on:keydown={(e) => e.key === 'Escape' && handle_close()} />

<div class="modal-backdrop" role="presentation" on:click|self={handle_close}>
  <div class="modal-content history-modal">
    <div class="modal-header">
      <h2>Buy / Sell Advisory History</h2>
      <button type="button" class="close-btn" on:click={handle_close}>&times;</button>
    </div>

    <div class="log-body">
      {#if is_loading && merged_advisories.length === 0}
        <div class="empty-msg">Loading advisory history...</div>
      {:else if merged_advisories.length === 0}
        <div class="empty-msg">No Buy/Sell advisories recorded yet.</div>
      {:else}
        <table class="log-table">
          <thead>
            <tr>
              <th>Symbol</th>
              <th>Event</th>
              <th>Price</th>
              <th>Time (EU)</th>
              <th>Action</th>
            </tr>
          </thead>
          <tbody>
            {#each merged_advisories as item (`${item.symbol}|${item.type}|${item.time}`)}
              {@const tracked = tracked_symbols.includes(item.symbol)}
              <tr>
                <td class="sym-cell">{item.symbol}</td>
                <td>
                  <span class="type-badge" class:badge-buy={item.type === 'BUY'} class:badge-sell={item.type === 'SELL'}>
                    {item.type}
                  </span>
                </td>
                <td>{item.closePrice !== undefined && item.closePrice !== null ? Number(item.closePrice).toFixed(2) : '--'}</td>
                <td class="time-cell">{format_eu_datetime(item.time)}</td>
                <td class="action-cell">
                  <button
                    type="button"
                    class="chart-btn"
                    on:click={() => dispatch('select_active', { symbol: item.symbol })}
                  >
                    Open Chart
                  </button>
                  <button
                    type="button"
                    class="track-btn"
                    class:is-tracked={tracked}
                    on:click={() => toggle_track(item.symbol)}
                  >
                    {tracked ? 'Tracked' : 'Track'}
                  </button>
                </td>
              </tr>
            {/each}
          </tbody>
        </table>
      {/if}
    </div>

    <div class="modal-footer">
      <span class="footer-hint">Showing up to {advisory_history_limit} most recent advisories.</span>
      <button type="button" class="action-btn" on:click={handle_close}>Close</button>
    </div>
  </div>
</div>
