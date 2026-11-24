<script>
  import { createEventDispatcher, onMount, onDestroy } from 'svelte';
  import '../styles/search_bar.css';
  import { search_symbols } from '../lib/sse_service.js';
  import { search_debounce_ms, default_symbol_search_limit } from '../constants.js';

  export let reset_counter = 0;

  const dispatch = createEventDispatcher();

  let search_input = '';
  let suggestions = [];
  let is_open = false;
  let highlighted_index = -1;
  let debounce_timer = null;
  let container_ref = null;
  let search_seq = 0;
  let active_controller = null;

  function abort_pending_search() {
    if (active_controller) {
      active_controller.abort();
      active_controller = null;
    }
  }

  $: if (reset_counter > 0) {
    if (debounce_timer) {
      clearTimeout(debounce_timer);
      debounce_timer = null;
    }
    abort_pending_search();
    search_seq += 1;
    search_input = '';
    suggestions = [];
    is_open = false;
    highlighted_index = -1;
  }

  async function fetch_suggestions(query) {
    abort_pending_search();
    const controller = new AbortController();
    active_controller = controller;
    const seq = ++search_seq;
    const results = await search_symbols(query, default_symbol_search_limit, controller.signal);
    if (active_controller === controller) {
      active_controller = null;
    }
    if (results !== null && seq === search_seq && is_open) {
      suggestions = results;
    }
  }

  function handle_focus() {
    fetch_suggestions(search_input);
    is_open = true;
  }

  function handle_input() {
    if (debounce_timer) {
      clearTimeout(debounce_timer);
    }
    debounce_timer = setTimeout(() => {
      fetch_suggestions(search_input);
      is_open = true;
      highlighted_index = -1;
    }, search_debounce_ms);
  }

  function select_symbol(symbol) {
    if (!symbol) return;
    if (debounce_timer) {
      clearTimeout(debounce_timer);
      debounce_timer = null;
    }
    dispatch('add_symbol', { symbol: symbol.toUpperCase() });
    search_input = '';
    is_open = false;
    suggestions = [];
    highlighted_index = -1;
  }

  function handle_submit(event) {
    event.preventDefault();
    if (highlighted_index >= 0 && suggestions[highlighted_index]) {
      select_symbol(suggestions[highlighted_index]);
      return;
    }
    const clean_symbol = search_input.trim().toUpperCase();
    if (clean_symbol) {
      select_symbol(clean_symbol);
    }
  }

  function handle_keydown(event) {
    if (!is_open && (event.key === 'ArrowDown' || event.key === 'ArrowUp')) {
      is_open = true;
      fetch_suggestions(search_input);
      return;
    }

    if (event.key === 'ArrowDown') {
      event.preventDefault();
      highlighted_index = Math.min(highlighted_index + 1, suggestions.length - 1);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      highlighted_index = Math.max(highlighted_index - 1, 0);
    } else if (event.key === 'Escape') {
      is_open = false;
      highlighted_index = -1;
    }
  }

  function handle_document_click(event) {
    if (container_ref && !container_ref.contains(event.target)) {
      is_open = false;
      highlighted_index = -1;
    }
  }

  onMount(() => {
    document.addEventListener('click', handle_document_click);
  });

  onDestroy(() => {
    document.removeEventListener('click', handle_document_click);
    if (debounce_timer) clearTimeout(debounce_timer);
    abort_pending_search();
  });
</script>

<div class="search-container" bind:this={container_ref}>
  <form class="search-form" on:submit={handle_submit}>
    <div class="input-wrapper">
      <input
        type="text"
        placeholder="Search or pick symbol (e.g. RDSA.NL, A0HN5C.ETR)..."
        bind:value={search_input}
        on:focus={handle_focus}
        on:click={() => { if (!is_open) handle_focus(); }}
        on:input={handle_input}
        on:keydown={handle_keydown}
        class="search-input"
        autocomplete="off"
      />
      {#if is_open && suggestions.length > 0}
        <div class="autocomplete-dropdown">
          <div class="dropdown-header">
            {search_input.trim() ? 'Matching symbols:' : 'Available symbols (click to track):'}
          </div>
          <ul class="suggestions-list" role="listbox">
            {#each suggestions as symbol, i}
              <li role="presentation">
                <button
                  type="button"
                  class="suggestion-item {i === highlighted_index ? 'active' : ''}"
                  role="option"
                  aria-selected={i === highlighted_index}
                  on:mousedown|preventDefault={() => select_symbol(symbol)}
                  on:mouseenter={() => (highlighted_index = i)}
                >
                  <span class="symbol-id">{symbol}</span>
                  <span class="action-hint">Track</span>
                </button>
              </li>
            {/each}
          </ul>
        </div>
      {/if}
    </div>
    <button type="submit" class="search-button">Track Symbol</button>
  </form>
</div>
