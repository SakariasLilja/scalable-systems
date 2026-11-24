<script>
  import { onMount, onDestroy, createEventDispatcher } from 'svelte';
  import '../styles/modals.css';
  import { Chart, registerables } from 'chart.js';
  import { fetch_history } from '../lib/sse_service.js';
  import { format_eu_time } from '../lib/date_format.js';
  import { chart_colors, modal_history_limit } from '../constants.js';

  Chart.register(...registerables);

  export let active_symbol = '';
  export let latest_tick = null;
  export let advisory_type = null;

  const dispatch = createEventDispatcher();
  let canvas_element;
  let chart_instance = null;
  let is_loading = true;
  let last_appended_key = '';

  async function initialize_chart() {
    is_loading = true;
    const history = await fetch_history(active_symbol, modal_history_limit);

    const labels = history.map(item => format_eu_time(item.windowStart));
    const ema38_data = history.map(item => item.ema38);
    const ema100_data = history.map(item => item.ema100);
    const price_data = history.map(item => item.lastPrice);

    if (history.length > 0) {
      const tail = history[history.length - 1];
      last_appended_key = `${tail.windowEnd}|${tail.lastPrice}`;
    }

    if (canvas_element) {
      if (chart_instance) {
        chart_instance.destroy();
      }

      chart_instance = new Chart(canvas_element, {
        type: 'line',
        data: {
          labels: labels,
          datasets: [
            {
              label: 'EMA (38)',
              data: ema38_data,
              borderColor: chart_colors.ema_38,
              backgroundColor: chart_colors.ema_38,
              borderWidth: 2,
              tension: 0.2,
              pointRadius: 2
            },
            {
              label: 'EMA (100)',
              data: ema100_data,
              borderColor: chart_colors.ema_100,
              backgroundColor: chart_colors.ema_100,
              borderWidth: 2,
              tension: 0.2,
              pointRadius: 2
            },
            {
              label: 'Close Price',
              data: price_data,
              borderColor: chart_colors.price,
              backgroundColor: chart_colors.price,
              borderWidth: 1.5,
              borderDash: [4, 4],
              tension: 0.1,
              pointRadius: 2
            }
          ]
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          animation: false,
          scales: {
            x: {
              grid: { color: '#334155' },
              ticks: { color: '#94a3b8' }
            },
            y: {
              grid: { color: '#334155' },
              ticks: { color: '#94a3b8' }
            }
          },
          plugins: {
            legend: {
              labels: { color: '#f8fafc' }
            }
          }
        }
      });
    }

    is_loading = false;
  }

  $: if (latest_tick && latest_tick.symbol === active_symbol && chart_instance) {
    append_live_point(latest_tick);
  }

  function append_live_point(tick) {
    if (!chart_instance) return;
    const close_price = tick.closePrice ?? tick.price;
    if (close_price === undefined || close_price === null) return;
    const key = `${tick.time || ''}|${close_price}`;
    if (key === last_appended_key) return;
    last_appended_key = key;

    const tick_date = tick.time ? new Date(tick.time) : new Date();
    const time_label = format_eu_time(tick_date, true);
    chart_instance.data.labels.push(time_label);

    chart_instance.data.datasets[0].data.push(tick.ema38 ?? close_price);
    chart_instance.data.datasets[1].data.push(tick.ema100 ?? close_price);
    chart_instance.data.datasets[2].data.push(close_price);

    if (chart_instance.data.labels.length > modal_history_limit) {
      chart_instance.data.labels.shift();
      chart_instance.data.datasets.forEach(ds => ds.data.shift());
    }

    chart_instance.update('none');
  }

  function handle_close() {
    dispatch('close');
  }

  onMount(() => {
    initialize_chart();
  });

  onDestroy(() => {
    if (chart_instance) {
      chart_instance.destroy();
      chart_instance = null;
    }
  });
</script>

<svelte:window on:keydown={(e) => e.key === 'Escape' && handle_close()} />

<div class="modal-backdrop" role="presentation" on:click|self={handle_close}>
  <div class="modal-content chart-modal">
    <div class="modal-header">
      <div class="modal-title-row">
        <h2>Real-Time Technical Indicators: {active_symbol}</h2>
        {#if advisory_type}
          <button
            type="button"
            class="advisory-btn"
            class:btn-buy={advisory_type === 'BUY'}
            class:btn-sell={advisory_type === 'SELL'}
            on:click={() => dispatch('advisory_action', { symbol: active_symbol, type: advisory_type })}
          >
            {advisory_type}
          </button>
        {/if}
      </div>
      <button class="close-btn" on:click={handle_close}>&times;</button>
    </div>

    <div class="chart-container">
      {#if is_loading}
        <div class="loading-spinner">Loading historical indicators...</div>
      {/if}
      <canvas bind:this={canvas_element}></canvas>
    </div>

    <div class="modal-footer">
      <span class="legend-hint">5-minute tumbling window updates via SSE.</span>
      <button class="action-btn" on:click={handle_close}>Close</button>
    </div>
  </div>
</div>
