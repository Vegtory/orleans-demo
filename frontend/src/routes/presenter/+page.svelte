<script lang="ts">
  import { onDestroy } from 'svelte';

  interface ActionSummary { id: string; title: string; optionCount: number; }
  interface PresenterView { name: string; actions: ActionSummary[]; activeActionId: string | null; }
  interface ResultsView { actionId: string; title: string; options: string[]; counts: number[]; total: number; }

  let name = $state('');
  let password = $state('presenter-secret');
  let key = $state<string | null>(null);
  let error = $state<string | null>(null);
  let busy = $state(false);

  let view = $state<PresenterView | null>(null);

  // New-question form.
  let title = $state('');
  let options = $state<string[]>(['', '']);

  // Selected action's live results.
  let selectedActionId = $state<string | null>(null);
  let results = $state<ResultsView | null>(null);

  let poll: ReturnType<typeof setInterval> | null = null;

  function authHeaders(json = false): HeadersInit {
    const h: Record<string, string> = { 'X-Presenter-Password': password };
    if (json) h['Content-Type'] = 'application/json';
    return h;
  }

  async function create() {
    error = null;
    busy = true;
    try {
      const res = await fetch('/api/presenter', {
        method: 'POST',
        headers: authHeaders(true),
        body: JSON.stringify({ name })
      });
      if (res.status === 401) throw new Error('Wrong presenter password');
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      key = (await res.json()).key;
      startPolling();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      busy = false;
    }
  }

  async function refresh() {
    if (!key) return;
    try {
      const res = await fetch(`/api/presenter/${encodeURIComponent(key)}`, { headers: authHeaders() });
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      view = await res.json();
      if (selectedActionId) await loadResults(selectedActionId);
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  function startPolling() {
    refresh();
    poll = setInterval(refresh, 2000);
  }

  function addOption() { options = [...options, '']; }
  function removeOption(i: number) { options = options.filter((_, idx) => idx !== i); }

  async function createQuestion() {
    if (!key) return;
    error = null;
    busy = true;
    try {
      const res = await fetch(`/api/presenter/${encodeURIComponent(key)}/actions`, {
        method: 'POST',
        headers: authHeaders(true),
        body: JSON.stringify({ title, options })
      });
      if (!res.ok) throw new Error((await res.json().catch(() => null))?.error ?? `Request failed (${res.status})`);
      title = '';
      options = ['', ''];
      await refresh();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      busy = false;
    }
  }

  async function activate(actionId: string) {
    if (!key) return;
    await fetch(`/api/presenter/${encodeURIComponent(key)}/actions/${actionId}/activate`, {
      method: 'POST',
      headers: authHeaders()
    });
    await refresh();
  }

  async function loadResults(actionId: string) {
    if (!key) return;
    selectedActionId = actionId;
    const res = await fetch(`/api/presenter/${encodeURIComponent(key)}/actions/${actionId}/results`, {
      headers: authHeaders()
    });
    if (res.ok) results = await res.json();
  }

  onDestroy(() => { if (poll) clearInterval(poll); });
</script>

<main>
  <p><a href="/">&larr; home</a></p>
  <h1>Presenter</h1>

  {#if !key}
    <div class="row">
      <label for="p-name">Your name</label>
      <input id="p-name" bind:value={name} placeholder="bob" />
    </div>
    <div class="row">
      <label for="p-pwd">Password</label>
      <input id="p-pwd" type="password" bind:value={password} />
    </div>
    <button onclick={create} disabled={busy || !name.trim()}>Start presenting</button>
  {:else}
    <p>Your presenter key: <code>{key}</code> {#if view}({view.name}){/if}</p>

    <section>
      <h2>Create a multiple-choice question</h2>
      <div class="row">
        <label for="q-title">Question</label>
        <input id="q-title" bind:value={title} placeholder="What's for lunch?" />
      </div>
      {#each options as _opt, i}
        <div class="row">
          <input bind:value={options[i]} placeholder={`Option ${i + 1}`} />
          {#if options.length > 2}
            <button onclick={() => removeOption(i)}>✕</button>
          {/if}
        </div>
      {/each}
      <div class="row">
        <button onclick={addOption}>Add option</button>
        <button onclick={createQuestion} disabled={busy || !title.trim()}>Create</button>
      </div>
    </section>

    <section>
      <h2>Your questions</h2>
      {#if view && view.actions.length > 0}
        <ul class="actions">
          {#each view.actions as a}
            <li class:active={a.id === view.activeActionId}>
              <span class="a-title">{a.title}</span>
              <span class="a-meta">{a.optionCount} options</span>
              {#if a.id === view.activeActionId}
                <span class="badge">LIVE</span>
              {:else}
                <button onclick={() => activate(a.id)}>Set live</button>
              {/if}
              <button onclick={() => loadResults(a.id)}>Results</button>
            </li>
          {/each}
        </ul>
      {:else}
        <p>No questions yet.</p>
      {/if}
    </section>

    {#if results}
      <section>
        <h2>Results: {results.title}</h2>
        <ul class="results">
          {#each results.options as opt, i}
            <li>
              <span>{opt}</span>
              <strong>{results.counts[i]}</strong>
            </li>
          {/each}
        </ul>
        <p>{results.total} response{results.total === 1 ? '' : 's'}</p>
      </section>
    {/if}
  {/if}

  {#if error}<p class="error">Error: {error}</p>{/if}
</main>

<style>
  main { max-width: 40rem; margin: 3rem auto; padding: 0 1rem; font-family: system-ui, sans-serif; line-height: 1.5; }
  h1 { font-size: 1.6rem; }
  h2 { font-size: 1.15rem; margin-top: 1.5rem; }
  section { border-top: 1px solid #eee; padding-top: 0.5rem; }
  .row { display: flex; gap: 0.5rem; align-items: center; margin: 0.6rem 0; }
  label { min-width: 5rem; }
  input { padding: 0.4rem 0.6rem; font-size: 1rem; flex: 1; }
  button { padding: 0.4rem 0.9rem; font-size: 1rem; cursor: pointer; }
  button:disabled { cursor: default; opacity: 0.6; }
  .actions, .results { list-style: none; padding: 0; }
  .actions li { display: flex; gap: 0.6rem; align-items: center; padding: 0.4rem 0; border-bottom: 1px solid #f0f0f0; }
  .actions li.active { background: #f3fbf3; }
  .a-title { flex: 1; }
  .a-meta { color: #777; font-size: 0.85rem; }
  .badge { background: #137333; color: #fff; border-radius: 0.3rem; padding: 0.05rem 0.4rem; font-size: 0.75rem; }
  .results li { display: flex; justify-content: space-between; padding: 0.3rem 0; border-bottom: 1px solid #f0f0f0; }
  .error { color: #b00020; }
  code { background: #f0f0f0; padding: 0.1rem 0.3rem; border-radius: 0.2rem; }
</style>
