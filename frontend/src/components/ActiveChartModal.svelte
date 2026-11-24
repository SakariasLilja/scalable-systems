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
  let is_fetching_older = false;
  let has_more_history = true;
  let all_points = [];
  let view_end = 0;
  let is_following_live = true;
  let last_appended_key = '';

  function normalize_history_item(item) {
    return {
      key: `${item.windowEnd}|${item.lastPrice}`,
      window_start: item.windowStart,
      label: format_eu_time(item.windowStart),
      ema38: item.ema38,
      ema100: item.ema100,
      price: item.lastPrice
    };
  }

  function render_viewport() {
    if (!chart_instance) return;
    const end = Math.max(0, Math.min(view_end, all_points.length));
    const start = Math.max(0, end - modal_history_limit);
    const slice = all_points.slice(start, end);

    chart_instance.data.labels = slice.map((p) => p.label);
    chart_instance.data.datasets[0].data = slice.map((p) => p.ema38);
    chart_instance.data.datasets[1].data = slice.map((p) => p.ema100);
    chart_instance.data.datasets[2].data = slice.map((p) => p.price);
    chart_instance.update('none');
  }

  async function load_older_history() {
    if (is_fetching_older || !has_more_history || all_points.length === 0) return;
    const oldest = all_points[0];
    if (!oldest || !oldest.window_start) return;

    is_fetching_older = true;
    const older = await fetch_history(active_symbol, modal_history_limit, oldest.window_start);
    is_fetching_older = false;

    if (!Array.isArray(older) || older.length === 0) {
      has_more_history = false;
      return;
    }

    if (older.length < modal_history_limit) {
      has_more_history = false;
    }

    const existing_keys = new Set(all_points.map((p) => p.key));
    const prepended = older
      .map(normalize_history_item)
      .filter((p) => !existing_keys.has(p.key));

    if (prepended.length > 0) {
      all_points = [...prepended, ...all_points];
      view_end += prepended.length;
      render_viewport();
    } else {
      has_more_history = false;
    }
  }

  async function pan_viewport(delta) {
    if (all_points.length === 0) return;
    const min_end = Math.min(all_points.length, modal_history_limit);
    const max_end = all_points.length;
    const next_end = Math.max(min_end, Math.min(max_end, view_end + delta));
    view_end = next_end;
    is_following_live = view_end >= all_points.length;
    render_viewport();

    if (delta < 0 && view_end - modal_history_limit <= 5 && has_more_history) {
      await load_older_history();
    }
  }

  function handle_wheel(event) {
    event.preventDefault();
    const step = Math.max(1, Math.round(modal_history_limit / 10));
    if (event.deltaY > 0) {
      pan_viewport(-step);
    } else if (event.deltaY < 0) {
      pan_viewport(step);
    }
  }

  async function initialize_chart() {
    is_loading = true;
    const history = await fetch_history(active_symbol, modal_history_limit);
    all_points = Array.isArray(history) ? history.map(normalize_history_item) : [];
    has_more_history = all_points.length >= modal_history_limit;
    view_end = all_points.length;
    is_following_live = true;

    if (all_points.length > 0) {
      last_appended_key = all_points[all_points.length - 1].key;
    }

    if (canvas_element) {
      if (chart_instance) {
        chart_instance.destroy();
      }

      chart_instance = new Chart(canvas_element, {
        type: 'line',
        data: {
          labels: [],
          datasets: [
            {
              label: 'EMA (38)',
              data: [],
              borderColor: chart_colors.ema_38,
              backgroundColor: chart_colors.ema_38,
              borderWidth: 2,
              tension: 0.2,
              pointRadius: 2
            },
            {
              label: 'EMA (100)',
              data: [],
              borderColor: chart_colors.ema_100,
              backgroundColor: chart_colors.ema_100,
              borderWidth: 2,
              tension: 0.2,
              pointRadius: 2
            },
            {
              label: 'Close Price',
              data: [],
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

      render_viewport();
    }

    is_loading = false;
  }

  $: if (latest_tick && latest_tick.symbol === active_symbol && chart_instance) {
    append_live_point(latest_tick);
  }

  $: can_scroll_left = view_end > Math.min(all_points.length, modal_history_limit) || has_more_history;
  $: can_scroll_right = view_end < all_points.length;

  function append_live_point(tick) {
    if (!chart_instance) return;
    const close_price = tick.closePrice ?? tick.price;
    if (close_price === undefined || close_price === null) return;
    const key = `${tick.time || ''}|${close_price}`;
    if (key === last_appended_key) return;
    last_appended_key = key;

    const tick_date = tick.time ? new Date(tick.time) : new Date();
    all_points = [
      ...all_points,
      {
        key,
        window_start: tick.time || tick_date.toISOString(),
        label: format_eu_time(tick_date, true),
        ema38: tick.ema38 ?? close_price,
        ema100: tick.ema100 ?? close_price,
        price: close_price
      }
    ];

    if (is_following_live) {
      view_end = all_points.length;
      render_viewport();
    }
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

    <div class="chart-container" on:wheel={handle_wheel}>
      {#if is_loading}
        <div class="loading-spinner">Loading historical indicators...</div>
      {/if}
      <button
        type="button"
        class="chart-nav-btn nav-left"
        disabled={!can_scroll_left || is_fetching_older}
        on:click={() => pan_viewport(-Math.max(1, Math.round(modal_history_limit / 5)))}
        aria-label="Scroll back in time"
      >
        &lt;
      </button>
      <canvas bind:this={canvas_element}></canvas>
      <button
        type="button"
        class="chart-nav-btn nav-right"
        disabled={!can_scroll_right}
        on:click={() => pan_viewport(Math.max(1, Math.round(modal_history_limit / 5)))}
        aria-label="Scroll forward in time"
      >
        &gt;
      </button>
    </div>

    <div class="modal-footer">
      <span class="legend-hint">
        {is_following_live ? 'Live • Following latest 5-minute tumbling windows.' : 'Paused on history • Scroll right or wheel up to resume live.'}
      </span>
      <button class="action-btn" on:click={handle_close}>Close</button>
    </div>
  </div>
</div>
