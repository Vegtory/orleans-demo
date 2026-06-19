<script lang="ts">
  import { onDestroy } from 'svelte';

  interface QuestionView { actionId: string; title: string; options: string[]; }
  interface AttendeeView { name: string; focus: QuestionView | null; yourAnswer: number | null; }

  let name = $state('');
  let key = $state<string | null>(null);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let view = $state<AttendeeView | null>(null);

  let poll: ReturnType<typeof setInterval> | null = null;

  async function join() {
    error = null;
    busy = true;
    try {
      const res = await fetch('/api/attendee', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name })
      });
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      key = (await res.json()).key;
      refresh();
      poll = setInterval(refresh, 2000);
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      busy = false;
    }
  }

  async function refresh() {
    if (!key) return;
    try {
      const res = await fetch(`/api/attendee/${encodeURIComponent(key)}`);
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      view = await res.json();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  async function answer(optionIndex: number) {
    if (!key) return;
    error = null;
    try {
      const res = await fetch(`/api/attendee/${encodeURIComponent(key)}/answer`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ optionIndex })
      });
      if (res.status === 409) throw new Error('That question is no longer live');
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      await refresh();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  onDestroy(() => { if (poll) clearInterval(poll); });
</script>

<main>
  <p><a href="/">&larr; home</a></p>
  <h1>Attendee</h1>

  {#if !key}
    <div class="row">
      <label for="a-name">Your name</label>
      <input id="a-name" bind:value={name} placeholder="alice" />
    </div>
    <button onclick={join} disabled={busy || !name.trim()}>Join</button>
  {:else}
    <p>You joined as <code>{key}</code> {#if view}({view.name}){/if}</p>

    {#if view?.focus}
      <section>
        <h2>{view.focus.title}</h2>
        <div class="options">
          {#each view.focus.options as opt, i}
            <button class:chosen={view.yourAnswer === i} onclick={() => answer(i)}>{opt}</button>
          {/each}
        </div>
        {#if view.yourAnswer !== null}
          <p class="hint">You answered: <strong>{view.focus.options[view.yourAnswer]}</strong>. You can change it while this question is live.</p>
        {/if}
      </section>
    {:else}
      <p class="waiting">Waiting for the presenter to start a question…</p>
    {/if}
  {/if}

  {#if error}<p class="error">Error: {error}</p>{/if}
</main>

<style>
  main { max-width: 36rem; margin: 3rem auto; padding: 0 1rem; font-family: system-ui, sans-serif; line-height: 1.5; }
  h1 { font-size: 1.6rem; }
  h2 { font-size: 1.25rem; }
  .row { display: flex; gap: 0.5rem; align-items: center; margin: 0.6rem 0; }
  label { min-width: 5rem; }
  input { padding: 0.4rem 0.6rem; font-size: 1rem; flex: 1; }
  button { padding: 0.5rem 1rem; font-size: 1rem; cursor: pointer; }
  button:disabled { cursor: default; opacity: 0.6; }
  .options { display: flex; flex-direction: column; gap: 0.5rem; margin: 1rem 0; }
  .options button { text-align: left; }
  .options button.chosen { border: 2px solid #137333; background: #f3fbf3; font-weight: 600; }
  .waiting { color: #777; font-style: italic; margin-top: 2rem; }
  .hint { color: #555; font-size: 0.9rem; }
  .error { color: #b00020; }
  code { background: #f0f0f0; padding: 0.1rem 0.3rem; border-radius: 0.2rem; }
</style>
