<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { presenterSession, sessionHeaders } from '$lib/session';
  import SiloGrainCanvas from '$lib/SiloGrainCanvas.svelte';
  import ChargerSimPresenter from '$lib/ChargerSimPresenter.svelte';
  import ReactionOverlay from '$lib/ReactionOverlay.svelte';

  // A standalone "overview" display: the live cluster visualization plus every
  // ChargerSim dashboard, expanded and ready to project. Both halves are
  // presenter-password protected, and discovering which actions are ChargerSim
  // needs a presenter key, so this page attaches to a presenter grain just like
  // the presenter console (reusing the saved session when there is one).

  // ActionKind, serialized as a number: 0 = MultipleChoice, 1 = ChargerSim.
  interface ActionSummary { id: string; title: string; optionCount: number; kind: number; }
  interface PresenterView { name: string; actions: ActionSummary[]; activeActionId: string | null; }

  let name = $state('');
  let password = $state('');
  let key = $state<string | null>(null);
  let connected = $state(false);
  let busy = $state(false);
  let error = $state<string | null>(null);
  let view = $state<PresenterView | null>(null);

  // Only the ChargerSim actions (kind === 1) get a dashboard.
  let chargerSims = $derived(view?.actions.filter((a) => a.kind === 1) ?? []);

  let poll: ReturnType<typeof setInterval> | null = null;

  // Resume a saved presenter session if one exists. With a stored password we
  // reconnect straight away; otherwise we keep the key/name and prompt for the
  // password before connecting.
  onMount(() => {
    const saved = presenterSession.load();
    if (saved?.key) {
      key = saved.key;
      name = saved.name;
      if (saved.password) {
        password = saved.password;
        connect();
      }
    }
  });

  function authHeaders(json = false): HeadersInit {
    const h: Record<string, string> = { ...sessionHeaders(), 'X-Presenter-Password': password };
    if (json) h['Content-Type'] = 'application/json';
    return h;
  }

  // Create a presenter grain (idempotent for a given name) to obtain a key, then
  // connect. Used when there is no saved session to resume.
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

  // Verify the password against the grain, then start polling for actions.
  async function connect() {
    if (!key) return;
    error = null;
    busy = true;
    try {
      const res = await fetch(`/api/presenter/${encodeURIComponent(key)}`, { headers: authHeaders() });
      if (res.status === 401) throw new Error('Wrong presenter password');
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      view = await res.json();
      presenterSession.save({ key, name, password });
      connected = true;
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
      const res = await fetch(`/api/presenter/${encodeURIComponent(key)}`, { headers: authHeaders() });
      if (res.status === 401) {
        if (poll) clearInterval(poll);
        poll = null;
        connected = false;
        throw new Error('Wrong presenter password');
      }
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      view = await res.json();
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  function startFresh() {
    presenterSession.clear();
    key = null;
    password = '';
    name = '';
    error = null;
  }

  onDestroy(() => { if (poll) clearInterval(poll); });
</script>

<div class="mx-auto flex min-h-screen w-full max-w-screen-2xl flex-col px-4 py-6">
  <header class="mb-6 flex items-center justify-between">
    <div class="flex items-center gap-2">
      <span class="flex h-9 w-9 items-center justify-center rounded-lg bg-indigo-600 text-lg font-bold text-white">P</span>
      <span class="text-lg font-semibold tracking-tight">Live Poll <span class="text-slate-400">· Overview</span></span>
    </div>
    <a
      href="/presenter"
      class="rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-200 hover:text-slate-900"
    >
      ← Presenter
    </a>
  </header>

  {#if !connected && key}
    <!-- Restored a saved session: re-enter the password to reconnect. -->
    <div class="flex flex-1 flex-col justify-center">
      <div class="mx-auto w-full max-w-md rounded-2xl border border-slate-200 bg-white p-8 shadow-sm">
        <h1 class="text-2xl font-bold tracking-tight">Welcome back{name ? `, ${name}` : ''}</h1>
        <p class="mt-1 text-sm text-slate-500">Enter the presenter password to load the overview.</p>

        <form class="mt-6 space-y-4" onsubmit={(e) => { e.preventDefault(); connect(); }}>
          <div>
            <label for="o-pwd-resume" class="mb-1 block text-sm font-medium text-slate-700">Presenter password</label>
            <input
              id="o-pwd-resume"
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
            {busy ? 'Loading…' : 'Load overview'}
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
      <div class="mx-auto w-full max-w-md rounded-2xl border border-slate-200 bg-white p-8 shadow-sm">
        <h1 class="text-2xl font-bold tracking-tight">Cluster overview</h1>
        <p class="mt-1 text-sm text-slate-500">
          The live cluster visualization plus your ChargerSim dashboards.
        </p>

        <form class="mt-6 space-y-4" onsubmit={(e) => { e.preventDefault(); create(); }}>
          <div>
            <label for="o-name" class="mb-1 block text-sm font-medium text-slate-700">Your name</label>
            <input
              id="o-name"
              bind:value={name}
              placeholder="bob"
              class="w-full rounded-lg border border-slate-300 px-3 py-2 text-base shadow-sm outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200"
            />
          </div>
          <div>
            <label for="o-pwd" class="mb-1 block text-sm font-medium text-slate-700">Presenter password</label>
            <input
              id="o-pwd"
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
            {busy ? 'Loading…' : 'Load overview'}
          </button>
        </form>
      </div>
    </div>
  {:else}
    <!-- Fleet aggregate (ChargerSim dashboards) and the cluster overview sit
         stacked on small screens and side by side from the lg breakpoint up. -->
    <div class="grid gap-6 lg:grid-cols-2 lg:items-start">
      <div class="space-y-4">
        {#if chargerSims.length > 0}
          {#each chargerSims as a (a.id)}
            {#if key}
              <ChargerSimPresenter presenterKey={key} actionId={a.id} {password} title={a.title} defaultOpen />
            {/if}
          {/each}
        {:else}
          <div class="rounded-2xl border border-dashed border-slate-300 bg-white/50 p-6 text-center text-sm text-slate-500">
            No ChargerSim simulations yet. Create one from the
            <a href="/presenter" class="font-medium text-indigo-600 hover:underline">presenter console</a>
            to see its dashboard here.
          </div>
        {/if}
      </div>

      <SiloGrainCanvas {password} defaultExpanded showAllTypes />
    </div>
  {/if}

  {#if error}
    <p class="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
  {/if}
</div>

<!-- Floating attendee reactions over the overview display. -->
{#if connected}
  <ReactionOverlay {password} />
{/if}
