<script>
  import { createEventDispatcher } from 'svelte';
  import '../styles/status_bar.css';
  import { clear_database } from '../lib/sse_service.js';
  import { format_eu_datetime } from '../lib/date_format.js';

  export let is_connected = false;
  export let latest_sim_time = null;
  export let known_symbol_count = 0;

  const dispatch = createEventDispatcher();
  let confirm_pending = false;
  let is_clearing = false;
  let error_message = null;

  function request_clear() {
    error_message = null;
    confirm_pending = true;
  }

  function cancel_clear() {
    confirm_pending = false;
  }

  async function confirm_clear() {
    is_clearing = true;
    error_message = null;

    const result = await clear_database();
    is_clearing = false;
    confirm_pending = false;

    if (!result.ok) {
      error_message = result.error || `Clear DB failed (HTTP ${result.status})`;
      return;
    }

    dispatch('db_cleared');
  }
</script>

{#if error_message}
  <div class="error-banner">
    <span class="error-text">{error_message}</span>
    <button class="dismiss-btn" on:click={() => (error_message = null)} aria-label="Dismiss error">&times;</button>
  </div>
{/if}

<div class="status-card">
  <div class="status-info">
    <span class="connection-badge">
      <span class="status-dot" class:connected={is_connected}></span>
      SSE: <strong>{is_connected ? 'Connected' : 'Disconnected'}</strong>
    </span>
    <span>Known Symbols: <strong>{known_symbol_count}</strong></span>
    <span>Latest Sim Time: <strong>{latest_sim_time ? format_eu_datetime(latest_sim_time) : 'Waiting for stream...'}</strong></span>
  </div>

  <div class="admin-actions">
    {#if confirm_pending}
      <span class="confirm-prompt">Clear all DB &amp; EMA data?</span>
      <button class="btn btn-danger" on:click={confirm_clear} disabled={is_clearing}>
        {is_clearing ? 'Clearing...' : 'Confirm Clear'}
      </button>
      <button class="btn btn-secondary" on:click={cancel_clear} disabled={is_clearing}>Cancel</button>
    {:else}
      <button class="btn btn-danger" on:click={request_clear} disabled={is_clearing}>Clear DB</button>
    {/if}
  </div>
</div>
