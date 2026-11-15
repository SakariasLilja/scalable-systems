<script>
  import { onMount, onDestroy, createEventDispatcher } from 'svelte';
  import { Chart, registerables } from 'chart.js';
  import { fetch_history } from '../lib/sse_service.js';
  import { chart_colors } from '../constants.js';

  Chart.register(...registerables);

  export let active_symbol = '';
  export let latest_tick = null;

  const dispatch = createEventDispatcher();
  let canvas_element;
  let chart_instance = null;
  let is_loading = true;

  async function initialize_chart() {
    is_loading = true;
    const history = await fetch_history(active_symbol, 50);

    const labels = history.map(item => new Date(item.windowStart).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }));
    const ema38_data = history.map(item => item.ema38);
    const ema100_data = history.map(item => item.ema100);
    const price_data = history.map(item => item.lastPrice);

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

    const tick_date = tick.time ? new Date(tick.time) : new Date();
    const time_label = tick_date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    chart_instance.data.labels.push(time_label);

    chart_instance.data.datasets[0].data.push(tick.ema38 || tick.closePrice);
    chart_instance.data.datasets[1].data.push(tick.ema100 || tick.closePrice);
    chart_instance.data.datasets[2].data.push(tick.closePrice);

    if (chart_instance.data.labels.length > 50) {
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
  <div class="modal-content">
    <div class="modal-header">
      <h2>Real-Time Technical Indicators: {active_symbol}</h2>
      <button class="close-btn" on:click={handle_close}>&times;</button>
    </div>

    <div class="chart-container">
      {#if is_loading}
        <div class="loading-spinner">Loading historical indicators...</div>
      {/if}
      <canvas bind:this={canvas_element}></canvas>
    </div>

    <div class="modal-footer">
      <span class="legend-hint">Streaming real-time updates every 2 seconds via SSE.</span>
      <button class="action-btn" on:click={handle_close}>Close</button>
    </div>
  </div>
</div>

<style>
  .modal-backdrop {
    position: fixed;
    top: 0;
    left: 0;
    width: 100vw;
    height: 100vh;
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
    max-width: 950px;
    height: 600px;
    display: flex;
    flex-direction: column;
    box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5);
    padding: 1.5rem;
  }
  .modal-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    border-bottom: 1px solid #1e293b;
    padding-bottom: 0.75rem;
    margin-bottom: 1rem;
  }
  .modal-header h2 {
    margin: 0;
    color: #f8fafc;
    font-size: 1.3rem;
  }
  .close-btn {
    background: none;
    border: none;
    font-size: 1.75rem;
    color: #94a3b8;
    cursor: pointer;
  }
  .close-btn:hover {
    color: #f8fafc;
  }
  .chart-container {
    flex: 1;
    position: relative;
    width: 100%;
    height: 100%;
  }
  .loading-spinner {
    position: absolute;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%);
    color: #94a3b8;
  }
  .modal-footer {
    display: flex;
    justify-content: space-between;
    align-items: center;
    border-top: 1px solid #1e293b;
    padding-top: 0.75rem;
    margin-top: 1rem;
  }
  .legend-hint {
    font-size: 0.85rem;
    color: #64748b;
  }
  .action-btn {
    padding: 0.4rem 1.2rem;
    background-color: #334155;
    color: #f8fafc;
    border: none;
    border-radius: 4px;
    font-weight: 600;
    cursor: pointer;
  }
</style>
