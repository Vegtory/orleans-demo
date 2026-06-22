<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { presenterSession, sessionHeaders } from '$lib/session';
  import SiloGrainCanvas from '$lib/SiloGrainCanvas.svelte';
  import ChargerSimPresenter from '$lib/ChargerSimPresenter.svelte';

  // ActionKind, serialized as a number: 0 = MultipleChoice, 1 = ChargerSim.
  interface ActionSummary { id: string; title: string; optionCount: number; kind: number; }
  interface PresenterView { name: string; actions: ActionSummary[]; activeActionId: string | null; }
  interface ResultsView { actionId: string; title: string; options: string[]; counts: number[]; total: number; }
  interface AttendeePresence { name: string; lastSeen: string; }
  interface AttendeeRosterView { count: number; attendees: AttendeePresence[]; }

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

  // Live attendee roster (everyone who called the presentation in the last 10
  // minutes). Collapsed by default — the header just shows the count.
  let attendees = $state<AttendeeRosterView | null>(null);
  let attendeesOpen = $state(false);

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

  // --- Floating reactions -----------------------------------------------------
  // Attendees tap reaction buttons; we poll the global feed and float each new
  // press up the screen, then fade it away. `reactionsSince` is the cursor of the
  // last event we've already shown (null until the first poll establishes it).
  interface ReactionEvent { seq: number; kind: string; }
  interface ReactionFeed { lastSeq: number; events: ReactionEvent[]; }
  interface Floater { id: number; emoji: string; left: number; drift: number; duration: number; }

  const reactionEmoji: Record<string, string> = {
    heart: '❤️',
    thumbs: '👍',
    question: '❓'
  };

  let reactionsSince = $state<number | null>(null);
  let floaters = $state<Floater[]>([]);
  let floaterId = 0;
  const floaterTimers = new Set<ReturnType<typeof setTimeout>>();

  // Spawn one floating emoji per press. Randomized horizontal start, sideways
  // drift and duration so a burst of identical reactions still reads as many
  // distinct emoji rather than one stack.
  function spawnFloater(kind: string) {
    const emoji = reactionEmoji[kind];
    if (!emoji) return;
    const id = ++floaterId;
    const left = 8 + Math.random() * 84; // vw, keep clear of the edges
    const drift = (Math.random() - 0.5) * 80; // px sideways sway
    const duration = 6000 + Math.random() * 3000; // ms float-up time
    floaters = [...floaters, { id, emoji, left, drift, duration }];
    const timer = setTimeout(() => {
      floaters = floaters.filter((f) => f.id !== id);
      floaterTimers.delete(timer);
    }, duration);
    floaterTimers.add(timer);
  }

  async function loadReactions() {
    if (!key) return;
    try {
      const url =
        reactionsSince === null
          ? '/api/presenter/reactions'
          : `/api/presenter/reactions?since=${reactionsSince}`;
      const res = await fetch(url, { headers: authHeaders() });
      if (!res.ok) return;
      const feed: ReactionFeed = await res.json();
      // On the first poll we only adopt the cursor — no backlog replay.
      if (reactionsSince !== null) {
        for (const ev of feed.events) spawnFloater(ev.kind);
      }
      reactionsSince = feed.lastSeq;
    } catch {
      /* reactions are best-effort; keep the last cursor */
    }
  }

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
    await loadAttendees();
    await loadReactions();
    poll = setInterval(refresh, 2000);
  }

  // Poll the live attendee roster. Failures here are non-fatal — the roster is a
  // secondary panel, so we leave the last known list in place rather than
  // surfacing an error over the main presenter flow.
  async function loadAttendees() {
    if (!key) return;
    try {
      const res = await fetch('/api/presenter/attendees', { headers: authHeaders() });
      if (res.ok) attendees = await res.json();
    } catch {
      /* keep the last roster */
    }
  }

  // Human-readable "last seen" label for a roster entry.
  function lastSeenLabel(iso: string): string {
    const secs = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 1000));
    if (secs < 10) return 'just now';
    if (secs < 60) return `${secs}s ago`;
    return `${Math.floor(secs / 60)}m ago`;
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
      await loadAttendees();
      await loadReactions();
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

  async function createChargerSim() {
    if (!key) return;
    error = null;
    busy = true;
    try {
      const res = await fetch(`/api/presenter/${encodeURIComponent(key)}/chargersim`, {
        method: 'POST',
        headers: authHeaders(true),
        body: JSON.stringify({ title: title.trim() || 'Charger fleet simulation' })
      });
      if (!res.ok) throw new Error((await res.json().catch(() => null))?.error ?? `Request failed (${res.status})`);
      title = '';
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

  async function removeAction(actionId: string) {
    if (!key) return;
    if (!confirm('Remove this action? This will kill all its state and cannot be undone.')) return;
    error = null;
    busy = true;
    try {
      const res = await fetch(`/api/presenter/${encodeURIComponent(key)}/actions/${actionId}`, {
        method: 'DELETE',
        headers: authHeaders()
      });
      if (!res.ok) throw new Error((await res.json().catch(() => null))?.error ?? `Request failed (${res.status})`);
      if (selectedActionId === actionId) { selectedActionId = null; results = null; }
      await refresh();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      busy = false;
    }
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
    reactionsSince = null;
    floaters = [];
    for (const t of floaterTimers) clearTimeout(t);
    floaterTimers.clear();
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

  onDestroy(() => {
    if (poll) clearInterval(poll);
    for (const t of floaterTimers) clearTimeout(t);
    floaterTimers.clear();
  });
</script>

<div class="mx-auto flex min-h-screen w-full max-w-2xl flex-col px-4 py-8">
  <header class="mb-8 flex items-center justify-between">
    <div class="flex items-center gap-2">
      <span class="flex h-9 w-9 items-center justify-center rounded-lg bg-indigo-600 text-lg font-bold text-white">P</span>
      <span class="text-lg font-semibold tracking-tight">Live Poll <span class="text-slate-400">· Presenter</span></span>
    </div>
    <nav class="flex items-center gap-1">
      <a
        href="/cluster"
        class="rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-200 hover:text-slate-900"
      >
        Cluster
      </a>
      <a
        href="/overview"
        class="rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-200 hover:text-slate-900"
      >
        Overview
      </a>
      <a
        href="/"
        class="rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-200 hover:text-slate-900"
      >
        ← Attendee view
      </a>
    </nav>
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

    <!-- Attendees: collapsed by default, the header shows the live count. -->
    <section class="mb-6 overflow-hidden rounded-2xl border border-slate-200 bg-white shadow-sm">
      <button
        type="button"
        onclick={() => (attendeesOpen = !attendeesOpen)}
        aria-expanded={attendeesOpen}
        class="flex w-full items-center justify-between px-6 py-4 text-left transition hover:bg-slate-50"
      >
        <div class="flex items-center gap-2.5">
          <h2 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Attendees</h2>
          <span class="inline-flex min-w-6 items-center justify-center rounded-full bg-indigo-100 px-2 py-0.5 text-xs font-semibold text-indigo-700">
            {attendees?.count ?? 0}
          </span>
        </div>
        <svg
          class="h-4 w-4 text-slate-400 transition-transform {attendeesOpen ? 'rotate-180' : ''}"
          viewBox="0 0 20 20" fill="currentColor" aria-hidden="true"
        >
          <path fill-rule="evenodd" d="M5.3 7.3a1 1 0 0 1 1.4 0L10 10.6l3.3-3.3a1 1 0 1 1 1.4 1.4l-4 4a1 1 0 0 1-1.4 0l-4-4a1 1 0 0 1 0-1.4Z" clip-rule="evenodd"/>
        </svg>
      </button>
      {#if attendeesOpen}
        <div class="border-t border-slate-100 px-6 py-4">
          {#if attendees && attendees.count > 0}
            <ul class="space-y-2.5">
              {#each attendees.attendees as a}
                <li class="flex items-center justify-between gap-3 text-sm">
                  <span class="flex min-w-0 items-center gap-2">
                    <span class="h-1.5 w-1.5 shrink-0 rounded-full bg-green-500"></span>
                    <span class="truncate font-medium text-slate-700">{a.name}</span>
                  </span>
                  <span class="shrink-0 text-xs text-slate-400">{lastSeenLabel(a.lastSeen)}</span>
                </li>
              {/each}
            </ul>
          {:else}
            <p class="text-sm text-slate-400">No attendees in the last 10 minutes.</p>
          {/if}
          <p class="mt-3 text-xs text-slate-400">Active in the last 10 minutes.</p>
        </div>
      {/if}
    </section>

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
      <div class="flex items-center justify-between">
        <h2 class="text-lg font-semibold tracking-tight">Create a question</h2>
        <button
          type="button"
          disabled={busy}
          onclick={createChargerSim}
          class="rounded-lg border border-indigo-300 bg-indigo-50 px-3 py-1.5 text-sm font-semibold text-indigo-700 transition hover:bg-indigo-100 disabled:opacity-50"
          title="Create a ChargerSim action (uses the title above, or a default)"
        >
          ⚡ New ChargerSim
        </button>
      </div>
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
            <li class="py-3">
              <div class="flex items-center gap-3">
                <div class="min-w-0 flex-1">
                  <p class="truncate font-medium">
                    {#if a.kind === 1}<span class="mr-1">⚡</span>{/if}{a.title}
                  </p>
                  <p class="text-xs text-slate-400">{a.kind === 1 ? 'ChargerSim action' : `${a.optionCount} options`}</p>
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
                {#if a.kind !== 1}
                  <button
                    onclick={() => loadResults(a.id)}
                    class="rounded-lg px-3 py-1.5 text-sm font-medium transition
                      {selectedActionId === a.id ? 'bg-indigo-50 text-indigo-700' : 'text-slate-500 hover:bg-slate-100'}"
                  >
                    Results
                  </button>
                {/if}
                <button
                  onclick={() => removeAction(a.id)}
                  disabled={busy}
                  aria-label="Remove action"
                  class="rounded-lg px-2 py-1.5 text-sm font-medium text-slate-400 transition hover:bg-red-50 hover:text-red-600 disabled:opacity-40"
                >✕</button>
              </div>
              {#if a.kind === 1 && key}
                <div class="mt-2">
                  <ChargerSimPresenter presenterKey={key} actionId={a.id} {password} title={a.title} />
                </div>
              {/if}
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

<!-- Floating attendee reactions. Fixed, full-screen, click-through overlay so
     emoji float over the whole presenter view regardless of scroll position. -->
{#if connected}
  <div class="reaction-overlay" aria-hidden="true">
    {#each floaters as f (f.id)}
      <span
        class="reaction-floater"
        style="left: {f.left}vw; --drift: {f.drift}px; animation-duration: {f.duration}ms;"
      >
        {f.emoji}
      </span>
    {/each}
  </div>
{/if}

<style>
  .reaction-overlay {
    position: fixed;
    inset: 0;
    overflow: hidden;
    pointer-events: none;
    z-index: 50;
  }

  .reaction-floater {
    position: absolute;
    bottom: 6rem;
    font-size: 2.25rem;
    line-height: 1;
    will-change: transform, opacity;
    animation-name: reaction-float;
    animation-timing-function: ease-out;
    animation-fill-mode: forwards;
  }

  @keyframes reaction-float {
    0% {
      transform: translate(0, 0) scale(0.6);
      opacity: 0;
    }
    15% {
      transform: translate(calc(var(--drift) * 0.2), -10vh) scale(1.1);
      opacity: 1;
    }
    70% {
      opacity: 1;
    }
    100% {
      transform: translate(var(--drift), -80vh) scale(1);
      opacity: 0;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .reaction-floater {
      animation-name: reaction-fade;
    }
    @keyframes reaction-fade {
      0% { opacity: 0; transform: translateY(0); }
      20% { opacity: 1; }
      100% { opacity: 0; transform: translateY(-20vh); }
    }
  }
</style>
