<script>
  import { createEventDispatcher } from 'svelte';
  import { clear_database } from '../lib/sse_service.js';

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
    <span>Latest Sim Time: <strong>{latest_sim_time ? new Date(latest_sim_time).toLocaleString() : 'Waiting for stream...'}</strong></span>
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

<style>
  .error-banner {
    background-color: #7f1d1d;
    border: 1px solid #ef4444;
    color: #fecaca;
    padding: 0.75rem 1rem;
    border-radius: 6px;
    margin-bottom: 1rem;
    display: flex;
    justify-content: space-between;
    align-items: center;
    font-size: 0.875rem;
  }
  .error-text {
    font-weight: 500;
  }
  .dismiss-btn {
    background: transparent;
    border: none;
    color: #fecaca;
    font-size: 1.1rem;
    cursor: pointer;
    padding: 0 0.25rem;
    line-height: 1;
  }
  .status-card {
    background-color: #1e293b;
    border: 1px solid #334155;
    border-radius: 8px;
    padding: 1rem 1.25rem;
    margin-bottom: 1.5rem;
    display: flex;
    flex-wrap: wrap;
    justify-content: space-between;
    align-items: center;
    gap: 1rem;
  }
  .status-info {
    display: flex;
    flex-wrap: wrap;
    gap: 1.5rem;
    font-size: 0.9rem;
    color: #94a3b8;
    align-items: center;
  }
  .status-info strong {
    color: #f8fafc;
  }
  .connection-badge {
    display: inline-flex;
    align-items: center;
    gap: 0.45rem;
  }
  .status-dot {
    width: 9px;
    height: 9px;
    border-radius: 50%;
    background-color: #ef4444;
    display: inline-block;
  }
  .status-dot.connected {
    background-color: #10b981;
  }
  .admin-actions {
    display: flex;
    align-items: center;
    gap: 0.5rem;
  }
  .confirm-prompt {
    font-size: 0.85rem;
    color: #fca5a5;
    font-weight: 500;
  }
  .btn {
    padding: 0.45rem 0.9rem;
    border-radius: 4px;
    border: none;
    font-size: 0.85rem;
    font-weight: 600;
    cursor: pointer;
    transition: opacity 0.2s;
  }
  .btn:disabled {
    opacity: 0.6;
    cursor: not-allowed;
  }
  .btn:hover:not(:disabled) {
    opacity: 0.9;
  }
  .btn-danger {
    background-color: #dc2626;
    color: white;
  }
  .btn-secondary {
    background-color: #334155;
    color: #f8fafc;
  }
</style>
