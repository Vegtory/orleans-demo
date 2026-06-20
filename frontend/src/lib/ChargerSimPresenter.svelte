<script lang="ts">
  import { sessionHeaders } from '$lib/session';

  // The presenter's main-stage ChargerSim dashboard: global totals, per-attendee
  // cards and a big Kill all button. It polls a single grain call (the action
  // grain's dashboard) which rolls up aggregate grains — never the chargers.
  let { presenterKey, actionId, password, title = 'ChargerSim', defaultOpen = false }: {
    presenterKey: string;
    actionId: string;
    password: string;
    title?: string;
    // Start expanded (e.g. on a dedicated overview screen) instead of the
    // default collapsed state used inside the presenter's action list.
    defaultOpen?: boolean;
  } = $props();

  interface FleetSummary {
    attendeeId: string;
    attendeeName: string;
    totalChargers: number;
    noSessionCount: number;
    activeSessionCount: number;
    pausedWithSessionCount: number;
    killedCount: number;
    chargersWithSessionCount: number;
    totalActivePowerKw: number;
    totalSessionKwh: number;
  }

  interface Dashboard {
    active: boolean;
    global: FleetSummary;
    attendees: FleetSummary[];
    recentEvents: string[];
    killSwitchEnabled: boolean;
  }

  let dash = $state<Dashboard | null>(null);
  let error = $state<string | null>(null);
  let loading = $state(true);
  let togglePending = $state(false);
  // svelte-ignore state_referenced_locally -- intentional: seed once from the prop
  let open = $state(defaultOpen);

  const base = $derived(`/api/presenter/${encodeURIComponent(presenterKey)}/chargersim/${encodeURIComponent(actionId)}`);

  function authHeaders(json = false): HeadersInit {
    const h: Record<string, string> = { ...sessionHeaders(), 'X-Presenter-Password': password };
    if (json) h['Content-Type'] = 'application/json';
    return h;
  }

  let poll: ReturnType<typeof setInterval> | null = null;

  // Load the latest snapshot immediately on mount (and whenever the action id
  // changes), so a page reload shows fresh data right away instead of staying
  // blank until the panel is expanded. The dashboard is served from a cached
  // snapshot on the backend, so this is a single cheap grain call.
  $effect(() => {
    actionId; // re-run if the action this component renders changes
    refresh();
  });

  // Live-poll only while expanded — no point hammering the endpoint for a
  // collapsed panel nobody is looking at.
  $effect(() => {
    if (open) {
      refresh();
      poll = setInterval(refresh, 1500);
    } else if (poll) {
      clearInterval(poll);
      poll = null;
    }
    return () => { if (poll) { clearInterval(poll); poll = null; } };
  });

  async function refresh() {
    try {
      const res = await fetch(`${base}/dashboard`, { headers: authHeaders() });
      if (res.ok) {
        dash = await res.json();
        error = null;
      }
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      loading = false;
    }
  }

  // Optimistic toggle: flip the UI immediately, send the request in the
  // background, and roll back if it fails. The backend kills chargers
  // asynchronously and the next poll reconciles the authoritative flag.
  async function setKillSwitch(enabled: boolean) {
    if (togglePending) return;
    const previous = dash?.killSwitchEnabled ?? false;
    if (dash) dash = { ...dash, killSwitchEnabled: enabled };
    togglePending = true;
    error = null;
    try {
      const res = await fetch(`${base}/killswitch`, {
        method: 'POST',
        headers: authHeaders(true),
        body: JSON.stringify({ enabled })
      });
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      // Reconcile sooner if we're not actively polling (panel collapsed).
      if (!open) refresh();
    } catch (e) {
      if (dash) dash = { ...dash, killSwitchEnabled: previous };
      error = e instanceof Error ? e.message : 'Unknown error';
    } finally {
      togglePending = false;
    }
  }

  const fmt = (n: number, d = 1) => (n ?? 0).toLocaleString(undefined, { maximumFractionDigits: d });
</script>

<section class="rounded-2xl border border-slate-800 bg-slate-900 text-white shadow-lg">
  <!-- Collapsible header — always visible -->
  <button
    class="flex w-full items-center justify-between px-6 py-4 text-left"
    onclick={() => (open = !open)}
    aria-expanded={open}
  >
    <div>
      <span class="text-base font-bold tracking-tight">⚡ {title}</span>
      <span class="ml-2 text-sm text-slate-400">ChargerSim dashboard</span>
    </div>
    <span class="text-slate-400 transition-transform duration-200 {open ? 'rotate-180' : ''}">▾</span>
  </button>

  {#if open}
    <div class="border-t border-slate-700 px-6 pb-6 pt-5">
      {#if loading && !dash}
        <p class="py-4 text-sm text-slate-400">Loading latest fleet data…</p>
      {/if}
      <div class="flex items-center justify-between">
        <p class="text-sm text-slate-400">Live fleet across all attendees</p>
        <!-- Kill switch toggle -->
        <button
          role="switch"
          aria-checked={dash?.killSwitchEnabled ?? false}
          aria-label="Kill switch"
          onclick={() => setKillSwitch(!(dash?.killSwitchEnabled ?? false))}
          class="flex items-center gap-3 transition-opacity {togglePending ? 'opacity-70' : ''}"
        >
          <span class="text-sm font-semibold {dash?.killSwitchEnabled ? 'text-red-300' : 'text-slate-400'}">Kill switch</span>
          <span
            class="relative inline-flex h-7 w-14 shrink-0 items-center rounded-full border-2 transition-colors duration-200 {dash?.killSwitchEnabled ? 'border-red-500 bg-red-600' : 'border-slate-600 bg-slate-700'}"
          >
            <span
              class="inline-block h-5 w-5 rounded-full bg-white shadow transition-transform duration-200 {dash?.killSwitchEnabled ? 'translate-x-7' : 'translate-x-1'}"
            ></span>
          </span>
        </button>
      </div>

      <!-- Big global numbers -->
      <div class="mt-5 grid grid-cols-2 gap-3 md:grid-cols-3">
        <div class="rounded-xl bg-indigo-600/20 px-4 py-3 ring-1 ring-indigo-500/30">
          <p class="text-xs uppercase tracking-wide text-indigo-300">Total active power</p>
          <p class="text-3xl font-black tabular-nums text-indigo-200">{fmt(dash?.global.totalActivePowerKw ?? 0)} <span class="text-lg">kW</span></p>
        </div>
        <div class="rounded-xl bg-slate-800 px-4 py-3">
          <p class="text-xs uppercase tracking-wide text-slate-400">Total chargers</p>
          <p class="text-3xl font-black tabular-nums">{fmt(dash?.global.totalChargers ?? 0, 0)}</p>
        </div>
        <div class="rounded-xl bg-slate-800 px-4 py-3">
          <p class="text-xs uppercase tracking-wide text-slate-400">Total session energy</p>
          <p class="text-3xl font-black tabular-nums">{fmt(dash?.global.totalSessionKwh ?? 0)} <span class="text-lg">kWh</span></p>
        </div>
        <div class="rounded-xl bg-green-600/20 px-4 py-3 ring-1 ring-green-500/30">
          <p class="text-xs uppercase tracking-wide text-green-300">Active sessions</p>
          <p class="text-2xl font-black tabular-nums text-green-200">{fmt(dash?.global.activeSessionCount ?? 0, 0)}</p>
        </div>
        <div class="rounded-xl bg-amber-600/20 px-4 py-3 ring-1 ring-amber-500/30">
          <p class="text-xs uppercase tracking-wide text-amber-300">Paused sessions</p>
          <p class="text-2xl font-black tabular-nums text-amber-200">{fmt(dash?.global.pausedWithSessionCount ?? 0, 0)}</p>
        </div>
        <div class="rounded-xl bg-red-600/20 px-4 py-3 ring-1 ring-red-500/30">
          <p class="text-xs uppercase tracking-wide text-red-300">Killed chargers</p>
          <p class="text-2xl font-black tabular-nums text-red-200">{fmt(dash?.global.killedCount ?? 0, 0)}</p>
        </div>
      </div>

      <!-- Event ticker -->
      {#if dash?.recentEvents?.length}
        <div class="mt-5">
          <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Recent activity</h3>
          <ul class="mt-2 space-y-1 text-sm text-slate-300">
            {#each dash.recentEvents.slice(0, 6) as ev}
              <li class="truncate">• {ev}</li>
            {/each}
          </ul>
        </div>
      {/if}

      <!-- Per-attendee cards -->
      <div class="mt-5">
        <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Per-attendee fleets</h3>
        {#if dash?.attendees?.length}
          <div class="mt-2 grid grid-cols-1 gap-2 sm:grid-cols-2">
            {#each dash.attendees as a}
              <div class="rounded-xl bg-slate-800 px-4 py-3">
                <div class="flex items-center justify-between">
                  <p class="font-semibold">{a.attendeeName || a.attendeeId}</p>
                  <p class="text-sm tabular-nums text-slate-400">{fmt(a.totalChargers, 0)} chargers</p>
                </div>
                <div class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-300">
                  <span class="text-green-300">{fmt(a.activeSessionCount, 0)} active</span>
                  <span class="text-amber-300">{fmt(a.pausedWithSessionCount, 0)} paused</span>
                  <span>{fmt(a.noSessionCount, 0)} idle</span>
                  <span class="text-red-300">{fmt(a.killedCount, 0)} killed</span>
                  <span class="text-indigo-300">{fmt(a.totalActivePowerKw)} kW</span>
                  <span>{fmt(a.totalSessionKwh)} kWh</span>
                </div>
              </div>
            {/each}
          </div>
        {:else}
          <p class="mt-2 text-sm text-slate-500">No attendees have joined yet.</p>
        {/if}
      </div>

      {#if error}
        <p class="mt-4 rounded-lg border border-red-500/40 bg-red-500/10 px-3 py-2 text-sm text-red-300">{error}</p>
      {/if}
    </div>
  {/if}
</section>
