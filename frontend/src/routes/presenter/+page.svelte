<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { presenterSession, sessionHeaders } from '$lib/session';
  import SiloGrainCanvas from '$lib/SiloGrainCanvas.svelte';

  interface ActionSummary { id: string; title: string; optionCount: number; }
  interface PresenterView { name: string; actions: ActionSummary[]; activeActionId: string | null; }
  interface ResultsView { actionId: string; title: string; options: string[]; counts: number[]; total: number; }

  let name = $state('');
  // The password is never pre-filled or persisted — the presenter types it each
  // session. The default lives only in backend config.
  let password = $state('');
  let key = $state<string | null>(null);
  // True once we've authenticated against the grain and started polling. While
  // false but `key` is set, we're resuming a saved session and just need the
  // password again.
  let connected = $state(false);
  let error = $state<string | null>(null);
  let busy = $state(false);

  let view = $state<PresenterView | null>(null);

  // The action currently in focus for attendees, resolved from the polled view.
  let liveAction = $derived.by(() => {
    if (!view?.activeActionId) return null;
    return view.actions.find((a) => a.id === view!.activeActionId) ?? null;
  });

  // New-question form.
  let title = $state('');
  let options = $state<string[]>(['', '']);

  // Selected action's live results.
  let selectedActionId = $state<string | null>(null);
  let results = $state<ResultsView | null>(null);

  let poll: ReturnType<typeof setInterval> | null = null;

  // On load, re-attach to an existing presenter grain if we created one before.
  // The grain still lives in the Orleans cluster. If we saved the password
  // (entered in a previous session), reconnect automatically; otherwise restore
  // the key + name and prompt for the password before reconnecting.
  onMount(() => {
    const saved = presenterSession.load();
    if (saved?.key) {
      key = saved.key;
      name = saved.name;
      if (saved.password) {
        password = saved.password;
        reconnect();
      }
    }
  });

  function authHeaders(json = false): HeadersInit {
    const h: Record<string, string> = { ...sessionHeaders(), 'X-Presenter-Password': password };
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
      await connect();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      busy = false;
    }
  }

  // Reconnect to a session restored from localStorage using the just-entered
  // password.
  async function reconnect() {
    error = null;
    busy = true;
    try {
      await connect();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      busy = false;
    }
  }

  // Verify the password against the grain, then start polling. Shared by the
  // create and reconnect flows.
  async function connect() {
    if (!key) return;
    const res = await fetch(`/api/presenter/${encodeURIComponent(key)}`, { headers: authHeaders() });
    if (res.status === 401) throw new Error('Wrong presenter password');
    if (!res.ok) throw new Error(`Request failed (${res.status})`);
    view = await res.json();
    presenterSession.save({ key, name, password });
    connected = true;
    poll = setInterval(refresh, 2000);
  }

  async function refresh() {
    if (!key) return;
    try {
      const res = await fetch(`/api/presenter/${encodeURIComponent(key)}`, { headers: authHeaders() });
      if (res.status === 401) {
        // Password changed/invalid mid-session — drop back to the prompt.
        if (poll) clearInterval(poll);
        poll = null;
        connected = false;
        throw new Error('Wrong presenter password');
      }
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      view = await res.json();
      if (selectedActionId) await loadResults(selectedActionId);
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
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

  async function deactivate() {
    if (!key) return;
    await fetch(`/api/presenter/${encodeURIComponent(key)}/deactivate`, {
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

  function signOut() {
    if (poll) clearInterval(poll);
    poll = null;
    presenterSession.clear();
    key = null;
    connected = false;
    password = '';
    name = '';
    view = null;
    results = null;
    selectedActionId = null;
  }

  // Abandon a restored session without reconnecting (e.g. "not me").
  function startFresh() {
    presenterSession.clear();
    key = null;
    password = '';
    name = '';
    error = null;
  }

  function pct(count: number, total: number) {
    return total > 0 ? Math.round((count / total) * 100) : 0;
  }

  onDestroy(() => { if (poll) clearInterval(poll); });
</script>

<div class="mx-auto flex min-h-screen w-full max-w-2xl flex-col px-4 py-8">
  <header class="mb-8 flex items-center justify-between">
    <div class="flex items-center gap-2">
      <span class="flex h-9 w-9 items-center justify-center rounded-lg bg-indigo-600 text-lg font-bold text-white">P</span>
      <span class="text-lg font-semibold tracking-tight">Live Poll <span class="text-slate-400">· Presenter</span></span>
    </div>
    <a
      href="/"
      class="rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-200 hover:text-slate-900"
    >
      ← Attendee view
    </a>
  </header>

  {#if !connected && key}
    <!-- Restored a saved session: re-enter the password to reconnect. -->
    <div class="flex flex-1 flex-col justify-center">
      <div class="rounded-2xl border border-slate-200 bg-white p-8 shadow-sm">
        <h1 class="text-2xl font-bold tracking-tight">Welcome back{name ? `, ${name}` : ''}</h1>
        <p class="mt-1 text-sm text-slate-500">Enter the presenter password to reconnect to your session.</p>

        <form class="mt-6 space-y-4" onsubmit={(e) => { e.preventDefault(); reconnect(); }}>
          <div>
            <label for="p-pwd-resume" class="mb-1 block text-sm font-medium text-slate-700">Presenter password</label>
            <input
              id="p-pwd-resume"
              type="password"
              bind:value={password}
              autocomplete="current-password"
              class="w-full rounded-lg border border-slate-300 px-3 py-2 text-base shadow-sm outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200"
            />
          </div>
          <button
            type="submit"
            disabled={busy || !password}
            class="w-full rounded-lg bg-indigo-600 px-4 py-2.5 text-base font-semibold text-white shadow-sm transition hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-300 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {busy ? 'Reconnecting…' : 'Reconnect'}
          </button>
          <button
            type="button"
            onclick={startFresh}
            class="w-full rounded-lg px-4 py-2 text-sm font-medium text-slate-500 transition hover:bg-slate-100 hover:text-slate-900"
          >
            Not you? Start fresh
          </button>
        </form>
      </div>
    </div>
  {:else if !connected}
    <div class="flex flex-1 flex-col justify-center">
      <div class="rounded-2xl border border-slate-200 bg-white p-8 shadow-sm">
        <h1 class="text-2xl font-bold tracking-tight">Start presenting</h1>
        <p class="mt-1 text-sm text-slate-500">Create questions and control which one is live.</p>

        <form class="mt-6 space-y-4" onsubmit={(e) => { e.preventDefault(); create(); }}>
          <div>
            <label for="p-name" class="mb-1 block text-sm font-medium text-slate-700">Your name</label>
            <input
              id="p-name"
              bind:value={name}
              placeholder="bob"
              class="w-full rounded-lg border border-slate-300 px-3 py-2 text-base shadow-sm outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200"
            />
          </div>
          <div>
            <label for="p-pwd" class="mb-1 block text-sm font-medium text-slate-700">Presenter password</label>
            <input
              id="p-pwd"
              type="password"
              bind:value={password}
              autocomplete="current-password"
              class="w-full rounded-lg border border-slate-300 px-3 py-2 text-base shadow-sm outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200"
            />
          </div>
          <button
            type="submit"
            disabled={busy || !name.trim() || !password}
            class="w-full rounded-lg bg-indigo-600 px-4 py-2.5 text-base font-semibold text-white shadow-sm transition hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-300 disabled:cursor-not-allowed disabled:opacity-50"
          >
            {busy ? 'Starting…' : 'Start presenting'}
          </button>
        </form>
      </div>
    </div>
  {:else}
    <div class="mb-6 flex items-center justify-between rounded-xl border border-slate-200 bg-white px-4 py-3 shadow-sm">
      <div class="min-w-0">
        <p class="text-xs uppercase tracking-wide text-slate-400">Presenting as</p>
        <p class="truncate font-semibold">{view?.name || name}</p>
      </div>
      <button
        onclick={signOut}
        class="shrink-0 rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-100 hover:text-slate-900"
      >
        Sign out
      </button>
    </div>

    <!-- Now live: always shows what attendees currently see, with an unfocus control. -->
    <section class="mb-6 rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
      <div class="flex items-center justify-between">
        <h2 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Now live</h2>
        {#if liveAction}
          <span class="inline-flex items-center gap-1.5 rounded-full bg-green-100 px-2.5 py-1 text-xs font-semibold text-green-700">
            <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-green-500"></span> LIVE
          </span>
        {/if}
      </div>
      {#if liveAction}
        <div class="mt-3 flex items-center gap-3">
          <p class="min-w-0 flex-1 truncate text-lg font-semibold tracking-tight">{liveAction.title}</p>
          <button
            onclick={deactivate}
            class="shrink-0 rounded-lg border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:border-red-300 hover:bg-red-50 hover:text-red-700"
          >
            Unfocus
          </button>
        </div>
      {:else}
        <p class="mt-3 text-sm text-slate-400">
          Nothing is live. Attendees see “Just enjoy the talk for now.” Set a question live to focus their attention.
        </p>
      {/if}
    </section>

    <!-- New question -->
    <section class="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
      <h2 class="text-lg font-semibold tracking-tight">Create a question</h2>
      <form class="mt-4 space-y-3" onsubmit={(e) => { e.preventDefault(); createQuestion(); }}>
        <input
          bind:value={title}
          placeholder="What's for lunch?"
          class="w-full rounded-lg border border-slate-300 px-3 py-2 text-base shadow-sm outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200"
        />
        <div class="space-y-2">
          {#each options as _opt, i}
            <div class="flex items-center gap-2">
              <span class="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-slate-100 text-xs font-bold text-slate-500">
                {String.fromCharCode(65 + i)}
              </span>
              <input
                bind:value={options[i]}
                placeholder={`Option ${i + 1}`}
                class="flex-1 rounded-lg border border-slate-300 px-3 py-2 text-base shadow-sm outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200"
              />
              {#if options.length > 2}
                <button
                  type="button"
                  onclick={() => removeOption(i)}
                  aria-label="Remove option"
                  class="flex h-9 w-9 shrink-0 items-center justify-center rounded-lg text-slate-400 transition hover:bg-red-50 hover:text-red-600"
                >✕</button>
              {/if}
            </div>
          {/each}
        </div>
        <div class="flex items-center justify-between pt-1">
          <button
            type="button"
            onclick={addOption}
            class="rounded-lg border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-600 transition hover:bg-slate-50"
          >
            + Add option
          </button>
          <button
            type="submit"
            disabled={busy || !title.trim()}
            class="rounded-lg bg-indigo-600 px-4 py-2 text-sm font-semibold text-white shadow-sm transition hover:bg-indigo-700 disabled:cursor-not-allowed disabled:opacity-50"
          >
            Create
          </button>
        </div>
      </form>
    </section>

    <!-- Questions list -->
    <section class="mt-6 rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
      <h2 class="text-lg font-semibold tracking-tight">Your questions</h2>
      {#if view && view.actions.length > 0}
        <ul class="mt-4 divide-y divide-slate-100">
          {#each view.actions as a}
            <li class="flex items-center gap-3 py-3">
              <div class="min-w-0 flex-1">
                <p class="truncate font-medium">{a.title}</p>
                <p class="text-xs text-slate-400">{a.optionCount} options</p>
              </div>
              {#if a.id === view.activeActionId}
                <span class="inline-flex items-center gap-1.5 rounded-full bg-green-100 px-2.5 py-1 text-xs font-semibold text-green-700">
                  <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-green-500"></span> LIVE
                </span>
              {:else}
                <button
                  onclick={() => activate(a.id)}
                  class="rounded-lg border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50"
                >
                  Set live
                </button>
              {/if}
              <button
                onclick={() => loadResults(a.id)}
                class="rounded-lg px-3 py-1.5 text-sm font-medium transition
                  {selectedActionId === a.id ? 'bg-indigo-50 text-indigo-700' : 'text-slate-500 hover:bg-slate-100'}"
              >
                Results
              </button>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="mt-3 text-sm text-slate-400">No questions yet. Create one above.</p>
      {/if}
    </section>

    <!-- Results -->
    {#if results}
      <section class="mt-6 rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
        <div class="flex items-baseline justify-between">
          <h2 class="text-lg font-semibold tracking-tight">{results.title}</h2>
          <span class="text-sm text-slate-400">{results.total} response{results.total === 1 ? '' : 's'}</span>
        </div>
        <div class="mt-4 space-y-3">
          {#each results.options as opt, i}
            <div>
              <div class="mb-1 flex items-center justify-between text-sm">
                <span class="font-medium">{String.fromCharCode(65 + i)}. {opt}</span>
                <span class="text-slate-500">{results.counts[i]} · {pct(results.counts[i], results.total)}%</span>
              </div>
              <div class="h-2.5 w-full overflow-hidden rounded-full bg-slate-100">
                <div
                  class="h-full rounded-full bg-indigo-500 transition-all duration-500"
                  style="width: {pct(results.counts[i], results.total)}%"
                ></div>
              </div>
            </div>
          {/each}
        </div>
      </section>
    {/if}

    <!-- Cluster activity: live, physics-driven canvas of grains inside silos. -->
    <div class="mt-6">
      <SiloGrainCanvas {password} />
    </div>
  {/if}

  {#if error}
    <p class="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
  {/if}
</div>
