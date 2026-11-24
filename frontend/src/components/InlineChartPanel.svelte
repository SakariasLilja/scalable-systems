<script>
  import { onDestroy, createEventDispatcher, tick } from 'svelte';
  import { Chart, registerables } from 'chart.js';
  import { fetch_history } from '../lib/sse_service.js';
  import { chart_colors } from '../constants.js';

  Chart.register(...registerables);

  export let tracked_symbols = [];
  export let selected_symbol = '';
  export let latest_tick = null;

  const dispatch = createEventDispatcher();
  let canvas_element;
  let chart_instance = null;
  let is_loading = false;
  let loaded_symbol = '';

  async function render_chart(symbol) {
    if (!symbol) {
      if (chart_instance) {
        chart_instance.destroy();
        chart_instance = null;
      }
      loaded_symbol = '';
      return;
    }

    is_loading = true;
    loaded_symbol = symbol;
    await tick();
    const history = await fetch_history(symbol, 50);
    if (loaded_symbol !== symbol) return;

    const labels = history.map((item) =>
      new Date(item.windowStart).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    );
    const ema38_data = history.map((item) => item.ema38);
    const ema100_data = history.map((item) => item.ema100);
    const price_data = history.map((item) => item.lastPrice);

    if (canvas_element) {
      if (chart_instance) {
        chart_instance.destroy();
      }
      chart_instance = new Chart(canvas_element, {
        type: 'line',
        data: {
          labels,
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
            x: { grid: { color: '#334155' }, ticks: { color: '#94a3b8' } },
            y: { grid: { color: '#334155' }, ticks: { color: '#94a3b8' } }
          },
          plugins: {
            legend: { labels: { color: '#f8fafc' } }
          }
        }
      });
    }

    is_loading = false;
  }

  $: if (selected_symbol !== loaded_symbol) {
    render_chart(selected_symbol);
  }

  $: if (latest_tick && latest_tick.symbol === selected_symbol && chart_instance) {
    const tick_date = latest_tick.time ? new Date(latest_tick.time) : new Date();
    const label = tick_date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    chart_instance.data.labels.push(label);
    chart_instance.data.datasets[0].data.push(latest_tick.ema38 || latest_tick.closePrice);
    chart_instance.data.datasets[1].data.push(latest_tick.ema100 || latest_tick.closePrice);
    chart_instance.data.datasets[2].data.push(latest_tick.closePrice);

    if (chart_instance.data.labels.length > 50) {
      chart_instance.data.labels.shift();
      chart_instance.data.datasets.forEach((ds) => ds.data.shift());
    }
    chart_instance.update('none');
  }

  function handle_change(event) {
    dispatch('select_inline', { symbol: event.target.value });
  }

  onDestroy(() => {
    if (chart_instance) {
      chart_instance.destroy();
      chart_instance = null;
    }
  });
</script>

{#if tracked_symbols.length > 0}
  <section class="inline-chart-section">
    <div class="inline-toolbar">
      <label class="inline-label" for="inline-chart-select">Quick Chart (Tracked Stocks):</label>
      <select
        id="inline-chart-select"
        class="inline-select"
        value={selected_symbol}
        on:change={handle_change}
      >
        <option value="">-- Hide Inline Graph --</option>
        {#each tracked_symbols as symbol (symbol)}
          <option value={symbol}>{symbol}</option>
        {/each}
      </select>
    </div>

    {#if selected_symbol}
      <div class="inline-chart-box">
        {#if is_loading}
          <div class="inline-loading">Loading {selected_symbol} indicators...</div>
        {/if}
        <canvas bind:this={canvas_element}></canvas>
      </div>
    {/if}
  </section>
{/if}

<style>
  .inline-chart-section {
    background-color: #0f172a;
    border: 1px solid #1e293b;
    border-radius: 8px;
    padding: 0.9rem 1.1rem;
    margin-bottom: 1.5rem;
  }
  .inline-toolbar {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 0.75rem;
  }
  .inline-label {
    font-size: 0.85rem;
    font-weight: 600;
    color: #cbd5e1;
  }
  .inline-select {
    padding: 0.45rem 0.75rem;
    border-radius: 6px;
    border: 1px solid #334155;
    background-color: #1e293b;
    color: #f8fafc;
    font-size: 0.9rem;
    min-width: 220px;
  }
  .inline-chart-box {
    position: relative;
    height: 300px;
    margin-top: 1rem;
  }
  .inline-loading {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    color: #94a3b8;
    font-size: 0.9rem;
  }
</style>
