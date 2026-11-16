<script>
  import { createEventDispatcher, onMount, onDestroy } from 'svelte';
  import { search_symbols } from '../lib/sse_service.js';
  import { search_debounce_ms, default_symbol_search_limit } from '../constants.js';

  const dispatch = createEventDispatcher();

  let search_input = '';
  let suggestions = [];
  let is_open = false;
  let highlighted_index = -1;
  let debounce_timer = null;
  let container_ref = null;
  let search_seq = 0;

  async function fetch_suggestions(query) {
    const seq = ++search_seq;
    const results = await search_symbols(query, default_symbol_search_limit);
    if (seq === search_seq && is_open) {
      suggestions = results || [];
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

<style>
  .search-container {
    position: relative;
    margin-bottom: 1.5rem;
  }
  .search-form {
    display: flex;
    gap: 0.5rem;
    position: relative;
  }
  .input-wrapper {
    flex: 1;
    position: relative;
  }
  .search-input {
    width: 100%;
    padding: 0.6rem 1rem;
    border-radius: 6px;
    border: 1px solid #334155;
    background-color: #1e293b;
    color: #f8fafc;
    font-size: 0.95rem;
    box-sizing: border-box;
  }
  .search-input:focus {
    outline: none;
    border-color: #3b82f6;
  }
  .autocomplete-dropdown {
    position: absolute;
    top: calc(100% + 4px);
    left: 0;
    right: 0;
    background-color: #1e293b;
    border: 1px solid #334155;
    border-radius: 6px;
    max-height: 260px;
    overflow-y: auto;
    z-index: 50;
    box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.5);
  }
  .dropdown-header {
    padding: 0.5rem 0.75rem;
    font-size: 0.75rem;
    text-transform: uppercase;
    color: #94a3b8;
    background-color: #0f172a;
    border-bottom: 1px solid #334155;
    letter-spacing: 0.05em;
  }
  .suggestions-list {
    list-style: none;
    margin: 0;
    padding: 0;
  }
  .suggestion-item {
    width: 100%;
    background: transparent;
    border: none;
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 0.55rem 0.85rem;
    cursor: pointer;
    border-bottom: 1px solid #1e293b;
    transition: background 0.15s;
    text-align: left;
    box-sizing: border-box;
  }
  .suggestion-item:last-child {
    border-bottom: none;
  }
  .suggestion-item:hover,
  .suggestion-item.active {
    background-color: #2563eb;
  }
  .symbol-id {
    font-weight: 600;
    color: #f8fafc;
    font-size: 0.9rem;
  }
  .action-hint {
    font-size: 0.75rem;
    color: #93c5fd;
    text-transform: uppercase;
    font-weight: 500;
  }
  .search-button {
    padding: 0.6rem 1.2rem;
    border-radius: 6px;
    border: none;
    background-color: #3b82f6;
    color: white;
    font-weight: 600;
    cursor: pointer;
    transition: background 0.2s;
    white-space: nowrap;
  }
  .search-button:hover {
    background-color: #2563eb;
  }
</style>
