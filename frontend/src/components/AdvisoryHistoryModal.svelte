<script>
  import { onMount, createEventDispatcher } from 'svelte';
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
  <div class="modal-content">
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
                <td>
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

<style>
  .modal-backdrop {
    position: fixed;
    inset: 0;
    background-color: rgba(15, 23, 42, 0.85);
    display: flex;
    justify-content: center;
    align-items: center;
    z-index: 1000;
  }
  .modal-content {
    background-color: #0f172a;
    border: 1px solid #334155;
    border-radius: 12px;
    width: 90%;
    max-width: 760px;
    max-height: 80vh;
    display: flex;
    flex-direction: column;
    padding: 1.25rem;
    box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5);
  }
  .modal-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    border-bottom: 1px solid #1e293b;
    padding-bottom: 0.75rem;
    margin-bottom: 0.75rem;
  }
  .modal-header h2 {
    margin: 0;
    font-size: 1.2rem;
    color: #f8fafc;
  }
  .close-btn {
    background: none;
    border: none;
    font-size: 1.7rem;
    color: #94a3b8;
    cursor: pointer;
  }
  .log-body {
    flex: 1;
    overflow-y: auto;
  }
  .empty-msg {
    text-align: center;
    padding: 2rem;
    color: #64748b;
  }
  .log-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.88rem;
  }
  .log-table th,
  .log-table td {
    text-align: left;
    padding: 0.55rem 0.65rem;
    border-bottom: 1px solid #1e293b;
  }
  .log-table th {
    color: #94a3b8;
    font-weight: 600;
    font-size: 0.78rem;
    text-transform: uppercase;
  }
  .sym-cell {
    font-weight: 700;
    color: #f8fafc;
  }
  .time-cell {
    color: #cbd5e1;
    font-variant-numeric: tabular-nums;
  }
  .type-badge {
    padding: 0.15rem 0.5rem;
    border-radius: 4px;
    font-size: 0.75rem;
    font-weight: 800;
  }
  .badge-buy {
    background-color: #22c55e;
    color: #052e16;
  }
  .badge-sell {
    background-color: #3b82f6;
    color: #082f49;
  }
  .track-btn {
    padding: 0.3rem 0.65rem;
    border-radius: 4px;
    border: none;
    background-color: #0f766e;
    color: #f0fdfa;
    font-size: 0.8rem;
    font-weight: 600;
    cursor: pointer;
  }
  .track-btn.is-tracked {
    background-color: #1e3a5f;
    color: #93c5fd;
  }
  .modal-footer {
    display: flex;
    justify-content: space-between;
    align-items: center;
    border-top: 1px solid #1e293b;
    padding-top: 0.75rem;
    margin-top: 0.75rem;
  }
  .footer-hint {
    font-size: 0.82rem;
    color: #64748b;
  }
  .action-btn {
    padding: 0.4rem 1.1rem;
    background-color: #334155;
    color: #f8fafc;
    border: none;
    border-radius: 4px;
    font-weight: 600;
    cursor: pointer;
  }
</style>
