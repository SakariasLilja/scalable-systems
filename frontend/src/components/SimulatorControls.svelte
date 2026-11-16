<script>
  import { onMount } from 'svelte';
  import {
    default_simulation_speed,
    active_chart_update_interval_ms,
    default_trading_day,
    bytes_per_mb
  } from '../constants.js';
  import {
    start_simulation,
    pause_simulation,
    resume_simulation,
    set_simulation_speed,
    get_simulation_status,
    get_trading_days
  } from '../lib/sse_service.js';

  let sim_status = {
    state: 0,
    speedFactor: default_simulation_speed,
    processedCount: 0,
    lastError: null
  };
  let trading_days = [];
  let selected_day = default_trading_day;
  let poll_timer = null;
  let dismissed_error = null;

  function dismiss_error() {
    dismissed_error = sim_status.lastError;
  }

  async function refresh_status() {
    const data = await get_simulation_status();
    if (data) {
      sim_status = data;
    }
  }

  async function handle_start() {
    dismissed_error = null;
    await start_simulation(null, sim_status.speedFactor || default_simulation_speed, selected_day);
    await refresh_status();
  }

  async function handle_pause() {
    await pause_simulation();
    await refresh_status();
  }

  async function handle_resume() {
    await resume_simulation();
    await refresh_status();
  }

  async function handle_speed(factor) {
    await set_simulation_speed(factor);
    sim_status.speedFactor = factor;
    await refresh_status();
  }

  onMount(() => {
    refresh_status();
    poll_timer = setInterval(refresh_status, active_chart_update_interval_ms);
    get_trading_days().then((days) => {
      if (days && days.length > 0) {
        trading_days = days;
        if (!trading_days.some((d) => d.tradingDay === selected_day)) {
          selected_day = trading_days[0].tradingDay;
        }
      }
    });

    return () => {
      if (poll_timer) clearInterval(poll_timer);
    };
  });
</script>

{#if sim_status.lastError && sim_status.lastError !== dismissed_error}
  <div class="error-banner">
    <span class="error-message">Simulation Error: {sim_status.lastError}</span>
    <button class="error-dismiss-btn" on:click={dismiss_error} aria-label="Dismiss error">✕</button>
  </div>
{/if}

<div class="controls-card">
  <div class="controls-info">
    <span class="status-indicator">
      Status: <strong>{sim_status.state === 1 ? 'Running' : sim_status.state === 2 ? 'Paused' : 'Stopped'}</strong>
    </span>
    <span>Processed: <strong>{sim_status.processedCount || 0}</strong> ticks</span>
    <span>Speed: <strong>{sim_status.speedFactor === 0 ? 'Max (No delay)' : sim_status.speedFactor + 'x'}</strong></span>
  </div>

  <div class="button-group">
    {#if trading_days.length > 0}
      <label class="day-picker-label">
        <span class="day-picker-text">Day:</span>
        <select bind:value={selected_day} class="day-select">
          {#each trading_days as d}
            <option value={d.tradingDay}>
              {d.tradingDay} ({Math.round(d.fileSizeBytes / bytes_per_mb)} MB)
            </option>
          {/each}
        </select>
      </label>
      <span class="divider">|</span>
    {/if}
    <button class="btn btn-primary" on:click={handle_start}>Start</button>
    <button class="btn btn-warning" on:click={handle_pause}>Pause</button>
    <button class="btn btn-success" on:click={handle_resume}>Resume</button>
    <span class="divider">|</span>
    <button class="btn btn-secondary" on:click={() => handle_speed(1.0)}>1x</button>
    <button class="btn btn-secondary" on:click={() => handle_speed(5.0)}>5x</button>
    <button class="btn btn-secondary" on:click={() => handle_speed(10.0)}>10x</button>
    <button class="btn btn-secondary" on:click={() => handle_speed(0.0)}>Max</button>
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
  .error-message {
    font-weight: 500;
  }
  .error-dismiss-btn {
    background: transparent;
    border: none;
    color: #fecaca;
    font-size: 1rem;
    cursor: pointer;
    padding: 0 0.25rem;
    line-height: 1;
  }
  .error-dismiss-btn:hover {
    color: #ffffff;
  }
  .controls-card {
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
  .controls-info {
    display: flex;
    gap: 1.5rem;
    font-size: 0.9rem;
    color: #94a3b8;
  }
  .controls-info strong {
    color: #f8fafc;
  }
  .button-group {
    display: flex;
    gap: 0.4rem;
    align-items: center;
    flex-wrap: wrap;
  }
  .day-picker-label {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    font-size: 0.85rem;
    color: #94a3b8;
  }
  .day-picker-text {
    font-weight: 500;
  }
  .day-select {
    padding: 0.35rem 0.6rem;
    border-radius: 4px;
    border: 1px solid #334155;
    background-color: #0f172a;
    color: #f8fafc;
    font-size: 0.85rem;
    cursor: pointer;
  }
  .day-select:focus {
    outline: none;
    border-color: #3b82f6;
  }
  .divider {
    color: #475569;
    margin: 0 0.25rem;
  }
  .btn {
    padding: 0.4rem 0.8rem;
    border-radius: 4px;
    border: none;
    font-size: 0.85rem;
    font-weight: 600;
    cursor: pointer;
    transition: opacity 0.2s;
  }
  .btn:hover {
    opacity: 0.9;
  }
  .btn-primary { background-color: #3b82f6; color: white; }
  .btn-warning { background-color: #f59e0b; color: white; }
  .btn-success { background-color: #10b981; color: white; }
  .btn-secondary { background-color: #334155; color: #f8fafc; }
</style>
