<script>
  import { onMount, onDestroy, createEventDispatcher } from 'svelte';
  import { Chart, registerables } from 'chart.js';
  import { fetch_history } from '../lib/sse_service.js';
  import { format_eu_time } from '../lib/date_format.js';
  import { chart_colors, card_history_limit } from '../constants.js';

  Chart.register(...registerables);

  export let symbol = '';
  export let data = {};
  export let latest_tick = null;
  export let mode = 'tracked';
  export let is_tracked = false;
  export let advisory_type = null;

  const dispatch = createEventDispatcher();
  let canvas_element;
  let chart_instance = null;
  let last_appended_key = '';

  $: effective_advisory = advisory_type || data.recent_alert || null;

  async function init_mini_chart() {
    if (!symbol || !canvas_element) return;
    const history = await fetch_history(symbol, card_history_limit);
    const recent = history.slice(-card_history_limit);

    const labels = recent.map((item) => format_eu_time(item.windowStart));
    const ema38_data = recent.map((item) => item.ema38);
    const ema100_data = recent.map((item) => item.ema100);
    const price_data = recent.map((item) => item.lastPrice);

    if (recent.length > 0) {
      const tail = recent[recent.length - 1];
      last_appended_key = `${tail.windowEnd}|${tail.lastPrice}`;
    }

    if (!canvas_element) return;
    if (chart_instance) {
      chart_instance.destroy();
    }

    chart_instance = new Chart(canvas_element, {
      type: 'line',
      data: {
        labels,
        datasets: [
          {
            label: 'EMA 38',
            data: ema38_data,
            borderColor: chart_colors.ema_38,
            borderWidth: 1.5,
            tension: 0.2,
            pointRadius: 1.5
          },
          {
            label: 'EMA 100',
            data: ema100_data,
            borderColor: chart_colors.ema_100,
            borderWidth: 1.5,
            tension: 0.2,
            pointRadius: 1.5
          },
          {
            label: 'Price',
            data: price_data,
            borderColor: chart_colors.price,
            borderWidth: 1.25,
            borderDash: [3, 3],
            tension: 0.1,
            pointRadius: 1.5
          }
        ]
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        animation: false,
        scales: {
          x: {
            grid: { color: '#1e293b' },
            ticks: { color: '#94a3b8', maxTicksLimit: 5, font: { size: 10 } }
          },
          y: {
            grid: { color: '#334155' },
            ticks: { color: '#94a3b8', maxTicksLimit: 4, font: { size: 10 } }
          }
        },
        plugins: {
          legend: {
            display: true,
            labels: { color: '#cbd5e1', boxWidth: 10, font: { size: 10 }, padding: 6 }
          }
        }
      }
    });
  }

  $: if (latest_tick && latest_tick.symbol === symbol && chart_instance) {
    append_point(latest_tick);
  }

  function append_point(tick) {
    const close_price = tick.closePrice ?? tick.price;
    if (close_price === undefined || close_price === null) return;
    const key = `${tick.time || ''}|${close_price}`;
    if (key === last_appended_key) return;
    last_appended_key = key;

    chart_instance.data.labels.push(format_eu_time(tick.time || new Date()));
    chart_instance.data.datasets[0].data.push(tick.ema38 ?? close_price);
    chart_instance.data.datasets[1].data.push(tick.ema100 ?? close_price);
    chart_instance.data.datasets[2].data.push(close_price);

    while (chart_instance.data.labels.length > card_history_limit) {
      chart_instance.data.labels.shift();
      chart_instance.data.datasets.forEach((ds) => ds.data.shift());
    }

    chart_instance.update('none');
  }

  onMount(() => {
    init_mini_chart();
  });

  onDestroy(() => {
    if (chart_instance) {
      chart_instance.destroy();
      chart_instance = null;
    }
  });
</script>

<div class="stock-card" class:flash-buy={effective_advisory === 'BUY'} class:flash-sell={effective_advisory === 'SELL'}>
  <div class="card-header">
    <span class="symbol-title">{symbol}</span>
    {#if effective_advisory}
      <button
        type="button"
        class="advisory-btn"
        class:btn-buy={effective_advisory === 'BUY'}
        class:btn-sell={effective_advisory === 'SELL'}
        on:click={() => dispatch('advisory_action', { symbol, type: effective_advisory })}
      >
        {effective_advisory}
      </button>
    {/if}
  </div>

  <div class="card-body">
    <div class="metric">
      <span class="metric-label">Last Price</span>
      <span class="metric-value">{data.price !== undefined && data.price !== null ? Number(data.price).toFixed(2) : '--'}</span>
    </div>
    <div class="metric">
      <span class="metric-label">EMA (38)</span>
      <span class="metric-value">{data.ema38 !== undefined && data.ema38 !== null ? Number(data.ema38).toFixed(4) : '--'}</span>
    </div>
    <div class="metric">
      <span class="metric-label">EMA (100)</span>
      <span class="metric-value">{data.ema100 !== undefined && data.ema100 !== null ? Number(data.ema100).toFixed(4) : '--'}</span>
    </div>
  </div>

  <div class="mini-chart-wrap">
    <canvas bind:this={canvas_element}></canvas>
  </div>

  <div class="card-actions">
    <button type="button" class="action-btn view-btn" on:click={() => dispatch('select_active', { symbol })}>Open Chart</button>
    {#if mode === 'tracked'}
      <button type="button" class="action-btn untrack-btn" on:click={() => dispatch('untrack_symbol', { symbol })}>Untrack</button>
    {:else}
      {#if is_tracked}
        <button type="button" class="action-btn tracked-btn" on:click={() => dispatch('untrack_symbol', { symbol })}>Tracked</button>
      {:else}
        <button type="button" class="action-btn track-btn" on:click={() => dispatch('track_symbol', { symbol })}>Track</button>
      {/if}
      <button type="button" class="action-btn dismiss-btn" on:click={() => dispatch('dismiss_advisory', { symbol })}>Dismiss</button>
    {/if}
  </div>
</div>

<style>
  .stock-card {
    background-color: #1e293b;
    border: 1px solid #334155;
    border-radius: 8px;
    padding: 1.1rem;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    transition: transform 0.15s, border-color 0.3s, box-shadow 0.3s;
  }
  .stock-card:hover {
    transform: translateY(-2px);
    border-color: #475569;
  }
  .flash-buy {
    border-color: #22c55e !important;
    box-shadow: 0 0 14px rgba(34, 197, 94, 0.35);
  }
  .flash-sell {
    border-color: #3b82f6 !important;
    box-shadow: 0 0 14px rgba(59, 130, 246, 0.35);
  }
  .card-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 0.5rem;
  }
  .symbol-title {
    font-size: 1.1rem;
    font-weight: 700;
    color: #f8fafc;
  }
  .advisory-btn {
    padding: 0.25rem 0.7rem;
    border-radius: 4px;
    border: none;
    font-size: 0.78rem;
    font-weight: 800;
    letter-spacing: 0.05em;
    cursor: pointer;
    transition: opacity 0.15s, transform 0.1s;
  }
  .advisory-btn:hover {
    opacity: 0.9;
    transform: scale(1.03);
  }
  .btn-buy {
    background-color: #22c55e;
    color: #052e16;
  }
  .btn-sell {
    background-color: #3b82f6;
    color: #082f49;
  }
  .card-body {
    display: grid;
    grid-template-columns: repeat(3, 1fr);
    gap: 0.5rem;
    padding: 0.45rem 0;
    border-top: 1px solid #334155;
    border-bottom: 1px solid #334155;
  }
  .metric {
    display: flex;
    flex-direction: column;
  }
  .metric-label {
    font-size: 0.72rem;
    color: #94a3b8;
  }
  .metric-value {
    font-size: 0.92rem;
    font-weight: 600;
    color: #f1f5f9;
  }
  .mini-chart-wrap {
    position: relative;
    width: 100%;
    height: 145px;
    background-color: #0f172a;
    border-radius: 6px;
    padding: 0.35rem;
    box-sizing: border-box;
  }
  .card-actions {
    display: flex;
    gap: 0.5rem;
  }
  .action-btn {
    flex: 1;
    padding: 0.45rem 0.5rem;
    border-radius: 4px;
    border: none;
    font-size: 0.82rem;
    font-weight: 600;
    cursor: pointer;
  }
  .view-btn {
    background-color: #2563eb;
    color: white;
  }
  .track-btn {
    background-color: #0f766e;
    color: #f0fdfa;
  }
  .tracked-btn {
    background-color: #1e3a5f;
    color: #93c5fd;
  }
  .untrack-btn,
  .dismiss-btn {
    background-color: #334155;
    color: #94a3b8;
  }
  .untrack-btn:hover,
  .dismiss-btn:hover {
    color: #ef4444;
  }
</style>
