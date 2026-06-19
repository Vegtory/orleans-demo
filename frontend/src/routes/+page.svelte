<script lang="ts">
  // Counter id is editable; defaults to "demo". The frontend only ever calls
  // relative API URLs (/api/...), so it works behind any host/ingress.
  let id = $state('demo');
  let value = $state<number | null>(null);
  let loading = $state(false);
  let error = $state<string | null>(null);

  async function call(path: string, method: 'GET' | 'POST'): Promise<void> {
    loading = true;
    error = null;
    try {
      const res = await fetch(path, { method });
      if (!res.ok) {
        throw new Error(`Request failed (${res.status})`);
      }
      const data = await res.json();
      value = data.value;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
      value = null;
    } finally {
      loading = false;
    }
  }

  const encodedId = $derived(encodeURIComponent(id.trim() || 'demo'));

  const load = () => call(`/api/counter/${encodedId}`, 'GET');
  const increment = () => call(`/api/counter/${encodedId}/increment`, 'POST');
  const reset = () => call(`/api/counter/${encodedId}/reset`, 'POST');
</script>

<main>
  <h1>.NET Orleans + Svelte starter</h1>
  <p>
    A minimal single-container starter: an ASP.NET Core minimal API co-hosting an
    Orleans silo, with this static Svelte frontend served from <code>wwwroot</code>.
    Each counter id maps to one Orleans grain.
  </p>

  <div class="row">
    <label for="counter-id">Counter id</label>
    <input id="counter-id" bind:value={id} placeholder="demo" />
    <button onclick={load} disabled={loading}>Load</button>
  </div>

  <div class="value">
    {#if loading}
      <span>Loading…</span>
    {:else if value !== null}
      <span>Value: <strong>{value}</strong></span>
    {:else}
      <span>No value loaded yet.</span>
    {/if}
  </div>

  {#if error}
    <p class="error">Error: {error}</p>
  {/if}

  <div class="row">
    <button onclick={increment} disabled={loading}>Increment</button>
    <button onclick={reset} disabled={loading}>Reset</button>
  </div>
</main>

<style>
  main {
    max-width: 36rem;
    margin: 3rem auto;
    padding: 0 1rem;
    font-family: system-ui, sans-serif;
    line-height: 1.5;
  }
  h1 {
    font-size: 1.6rem;
  }
  .row {
    display: flex;
    gap: 0.5rem;
    align-items: center;
    margin: 1rem 0;
  }
  input {
    padding: 0.4rem 0.6rem;
    font-size: 1rem;
  }
  button {
    padding: 0.4rem 0.9rem;
    font-size: 1rem;
    cursor: pointer;
  }
  button:disabled {
    cursor: default;
    opacity: 0.6;
  }
  .value {
    font-size: 1.2rem;
    margin: 1rem 0;
  }
  .error {
    color: #b00020;
  }
  code {
    background: #f0f0f0;
    padding: 0.1rem 0.3rem;
    border-radius: 0.2rem;
  }
</style>
