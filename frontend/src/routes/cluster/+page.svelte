<script lang="ts">
  import { onMount } from 'svelte';
  import { presenterSession, sessionHeaders } from '$lib/session';
  import SiloGrainCanvas from '$lib/SiloGrainCanvas.svelte';
  import ReactionOverlay from '$lib/ReactionOverlay.svelte';

  // A standalone, password-gated display of the live Orleans cluster
  // visualization — and nothing else. Handy to project on a second screen
  // during a talk. The cluster endpoints are presenter-password protected, so
  // we reuse that password; if a presenter session is already saved in this
  // browser we unlock automatically without re-prompting.

  let password = $state('');
  let unlocked = $state(false);
  let busy = $state(false);
  let error = $state<string | null>(null);

  onMount(() => {
    const saved = presenterSession.load();
    if (saved?.password) {
      password = saved.password;
      unlock();
    }
  });

  // Verify the password against the cluster endpoint before showing the view,
  // so a wrong password fails here with a clear message instead of leaving the
  // visualization silently empty.
  async function unlock() {
    error = null;
    busy = true;
    try {
      const res = await fetch('/api/cluster/live', {
        headers: { ...sessionHeaders(), 'X-Presenter-Password': password }
      });
      if (res.status === 401) throw new Error('Wrong presenter password');
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      unlocked = true;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      busy = false;
    }
  }
</script>

<div class="mx-auto flex min-h-screen w-full max-w-7xl flex-col px-4 py-6">
  <header class="mb-6 flex items-center justify-between">
    <div class="flex items-center gap-2">
      <span class="flex h-9 w-9 items-center justify-center rounded-lg bg-indigo-600 text-lg font-bold text-white">P</span>
      <span class="text-lg font-semibold tracking-tight">Live Poll <span class="text-slate-400">· Cluster</span></span>
    </div>
    <a
      href="/presenter"
      class="rounded-md px-3 py-1.5 text-sm font-medium text-slate-500 transition hover:bg-slate-200 hover:text-slate-900"
    >
      ← Presenter
    </a>
  </header>

  {#if unlocked}
    <SiloGrainCanvas {password} defaultExpanded showAllTypes tall />
  {:else}
    <div class="flex flex-1 flex-col justify-center">
      <div class="mx-auto w-full max-w-md rounded-2xl border border-slate-200 bg-white p-8 shadow-sm">
        <h1 class="text-2xl font-bold tracking-tight">Cluster visualization</h1>
        <p class="mt-1 text-sm text-slate-500">
          Enter the presenter password to watch the live Orleans cluster.
        </p>

        <form class="mt-6 space-y-4" onsubmit={(e) => { e.preventDefault(); unlock(); }}>
          <div>
            <label for="c-pwd" class="mb-1 block text-sm font-medium text-slate-700">Presenter password</label>
            <input
              id="c-pwd"
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
            {busy ? 'Unlocking…' : 'Show cluster'}
          </button>
        </form>
      </div>
    </div>
  {/if}

  {#if error}
    <p class="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
  {/if}
</div>

<!-- Floating attendee reactions over the cluster display. -->
{#if unlocked}
  <ReactionOverlay {password} />
{/if}
