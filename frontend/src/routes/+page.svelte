<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { attendeeSession, sessionHeaders } from '$lib/session';
  import ChargerSimAttendee from '$lib/ChargerSimAttendee.svelte';

  interface QuestionView { actionId: string; title: string; options: string[]; }
  interface AttendeeView {
    name: string;
    focus: QuestionView | null;
    yourAnswer: number | null;
    chargerSimActionId: string | null;
  }

  let name = $state('');
  let key = $state<string | null>(null);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let view = $state<AttendeeView | null>(null);

  let poll: ReturnType<typeof setInterval> | null = null;

  // On load, re-attach to an existing attendee grain if we joined before. The
  // grain still lives in the Orleans cluster, so we just resume polling it.
  onMount(() => {
    const saved = attendeeSession.load();
    if (saved?.key) {
      key = saved.key;
      name = saved.name;
      startPolling();
    }
  });

  function startPolling() {
    refresh();
    poll = setInterval(refresh, 2000);
  }

  async function join() {
    error = null;
    busy = true;
    try {
      const res = await fetch('/api/attendee', {
        method: 'POST',
        headers: { ...sessionHeaders(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ name })
      });
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      key = (await res.json()).key;
      attendeeSession.save({ key: key!, name });
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
      const res = await fetch(`/api/attendee/${encodeURIComponent(key)}`, { headers: sessionHeaders() });
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      view = await res.json();
      error = null;
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
        headers: { ...sessionHeaders(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ optionIndex })
      });
      if (res.status === 409) throw new Error('That question is no longer live');
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      await refresh();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  function leave() {
    if (poll) clearInterval(poll);
    poll = null;
    attendeeSession.clear();
    key = null;
    view = null;
    name = '';
  }

  onDestroy(() => { if (poll) clearInterval(poll); });
</script>

<div class="mx-auto flex min-h-screen w-full max-w-xl flex-col px-4 py-8">
  <header class="mb-8 flex items-center justify-between">
    <div class="flex items-center gap-2">
      <span class="flex h-9 w-9 items-center justify-center rounded-lg bg-indigo-600 text-lg font-bold text-white">P</span>
      <span class="text-lg font-semibold tracking-tight">Live Poll</span>
    </div>
    <a
      href="/presenter"
      class="rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-200 hover:text-slate-900"
    >
      Presenter →
    </a>
  </header>

  {#if !key}
    <div class="flex flex-1 flex-col justify-center">
      <div class="rounded-2xl border border-slate-200 bg-white p-8 shadow-sm">
        <h1 class="text-2xl font-bold tracking-tight">Join the poll</h1>
        <p class="mt-1 text-sm text-slate-500">
          Enter your name to answer the live question.
        </p>

        <form class="mt-6 space-y-4" onsubmit={(e) => { e.preventDefault(); join(); }}>
          <div>
            <label for="a-name" class="mb-1 block text-sm font-medium text-slate-700">Your name</label>
            <input
              id="a-name"
              bind:value={name}
              placeholder="alice"
              autocomplete="name"
              class="w-full rounded-lg border border-slate-300 px-3 py-2 text-base shadow-sm outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200"
            />
          </div>
          <button
            type="submit"
            disabled={busy || !name.trim()}
            class="w-full rounded-lg bg-indigo-600 px-4 py-2.5 text-base font-semibold text-white shadow-sm transition hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-300 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {busy ? 'Joining…' : 'Join'}
          </button>
        </form>
      </div>
    </div>
  {:else}
    <div class="flex flex-1 flex-col">
      <div class="mb-6 flex items-center justify-between rounded-xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
        <div class="min-w-0">
          <p class="text-xs uppercase tracking-wide text-slate-400">Joined as</p>
          <p class="truncate font-semibold">{view?.name || name}</p>
        </div>
        <button
          onclick={leave}
          class="shrink-0 rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-100 hover:text-slate-900"
        >
          Leave
        </button>
      </div>

      {#if view?.chargerSimActionId && key}
        <ChargerSimAttendee actionId={view.chargerSimActionId} attendeeKey={key} name={view?.name || name} />
      {:else if view?.focus}
        <div class="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
          <span class="inline-flex items-center gap-1.5 rounded-full bg-green-100 px-2.5 py-1 text-xs font-semibold text-green-700">
            <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-green-500"></span>
            LIVE
          </span>
          <h2 class="mt-3 text-xl font-bold tracking-tight">{view.focus.title}</h2>

          <div class="mt-5 space-y-3">
            {#each view.focus.options as opt, i}
              <button
                onclick={() => answer(i)}
                class="flex w-full items-center gap-3 rounded-xl border px-4 py-3 text-left text-base font-medium transition
                  {view.yourAnswer === i
                    ? 'border-indigo-600 bg-indigo-50 text-indigo-900 ring-1 ring-indigo-600'
                    : 'border-slate-300 bg-white hover:border-slate-400 hover:bg-slate-50'}"
              >
                <span
                  class="flex h-6 w-6 shrink-0 items-center justify-center rounded-full border text-sm font-bold
                    {view.yourAnswer === i ? 'border-indigo-600 bg-indigo-600 text-white' : 'border-slate-300 text-slate-500'}"
                >
                  {String.fromCharCode(65 + i)}
                </span>
                <span class="flex-1">{opt}</span>
                {#if view.yourAnswer === i}
                  <svg class="h-5 w-5 text-indigo-600" viewBox="0 0 20 20" fill="currentColor"><path fill-rule="evenodd" d="M16.7 5.3a1 1 0 0 1 0 1.4l-7.5 7.5a1 1 0 0 1-1.4 0L3.3 9.7a1 1 0 1 1 1.4-1.4l3.1 3.1 6.8-6.8a1 1 0 0 1 1.4 0Z" clip-rule="evenodd"/></svg>
                {/if}
              </button>
            {/each}
          </div>

          {#if view.yourAnswer !== null}
            <p class="mt-4 text-sm text-slate-500">
              Answer recorded. You can change it while this question is live.
            </p>
          {/if}
        </div>
      {:else}
        <div class="flex flex-1 flex-col items-center justify-center rounded-2xl border border-dashed border-slate-300 bg-white/50 p-10 text-center">
          <div class="flex h-12 w-12 items-center justify-center rounded-full bg-slate-100">
            <span class="h-3 w-3 animate-ping rounded-full bg-indigo-500"></span>
          </div>
          <p class="mt-4 font-medium text-slate-600">Just enjoy the talk for now</p>
          <p class="mt-1 text-sm text-slate-400">The next question will appear here automatically.</p>
        </div>
      {/if}
    </div>
  {/if}

  {#if error}
    <p class="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
  {/if}
</div>
