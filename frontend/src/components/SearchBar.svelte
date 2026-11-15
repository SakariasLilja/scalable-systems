<script>
  import { createEventDispatcher } from 'svelte';

  const dispatch = createEventDispatcher();
  let search_input = '';

  function handle_submit(event) {
    event.preventDefault();
    const clean_symbol = search_input.trim().toUpperCase();
    if (clean_symbol) {
      dispatch('add_symbol', { symbol: clean_symbol });
      search_input = '';
    }
  }
</script>

<form class="search-form" on:submit={handle_submit}>
  <input
    type="text"
    placeholder="Search Symbol (e.g. RDSA.NL, A0HN5C.ETR)..."
    bind:value={search_input}
    class="search-input"
  />
  <button type="submit" class="search-button">Track Symbol</button>
</form>

<style>
  .search-form {
    display: flex;
    gap: 0.5rem;
    margin-bottom: 1.5rem;
  }
  .search-input {
    flex: 1;
    padding: 0.6rem 1rem;
    border-radius: 6px;
    border: 1px solid #334155;
    background-color: #1e293b;
    color: #f8fafc;
    font-size: 0.95rem;
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
  }
  .search-button:hover {
    background-color: #2563eb;
  }
</style>
