<script lang="ts">
  import { onDestroy } from 'svelte';
  import { sessionHeaders } from '$lib/session';
  import ChargerSimFleetGrid from '$lib/ChargerSimFleetGrid.svelte';
  import ChargerSimLeaderboard from '$lib/ChargerSimLeaderboard.svelte';
  import ChargerSimAchievements from '$lib/ChargerSimAchievements.svelte';
  import ChargerSimGoal from '$lib/ChargerSimGoal.svelte';

  // The attendee's ChargerSim control room. Shown on the main page when a
  // ChargerSim action is live. It reads aggregate summaries (cheap) and only
  // fetches a single charger's details when one is opened.
  let { actionId, attendeeKey, name }: { actionId: string; attendeeKey: string; name: string } = $props();

  interface FleetSummary {
    totalChargers: number;
    noSessionCount: number;
    activeSessionCount: number;
    pausedWithSessionCount: number;
    killedCount: number;
    chargersWithSessionCount: number;
    totalActivePowerKw: number;
    totalSessionKwh: number;
  }

  interface Charger {
    chargerId: string;
    state: number;
    activeSessionId: string | null;
    activePowerKw: number;
    maxPowerKw: number;
    sessionKwh: number;
    sessionStartedAt: string | null;
    lastUpdatedAt: string;
    killed: boolean;
    version: number;
  }

  interface WorkStatus {
    pendingChargers: number;
    queuedCommands: number;
  }

  interface Cell {
    state: number; // 0 idle, 1 active, 2 paused, 3 killed
    load: number; // active-session load fraction 0..1 (0 for other states)
  }

  interface LeaderboardRow {
    attendeeId: string;
    attendeeName: string;
    totalChargers: number;
    activeSessionCount: number;
    totalActivePowerKw: number;
  }

  interface GoalStatus {
    goalActivePowerKw: number;
    currentActivePowerKw: number;
  }

  // How many points of power history to keep for the sparkline (~2 min at the 2s poll).
  const POWER_HISTORY_MAX = 60;

  const STATE_LABELS = ['No session', 'Active session', 'Paused', 'Killed'];

  let summary = $state<FleetSummary | null>(null);
  let cells = $state<Cell[]>([]);
  let leaderboard = $state<LeaderboardRow[]>([]);
  let goal = $state<GoalStatus | null>(null);
  let powerHistory = $state<number[]>([]);
  let work = $state<WorkStatus>({ pendingChargers: 0, queuedCommands: 0 });
  const working = $derived(work.pendingChargers > 0 || work.queuedCommands > 0);
  let opened = $state<Charger | null>(null);
  let openId = $state('');
  let error = $state<string | null>(null);
  let busy = $state(false);

  const base = $derived(`/api/chargersim/${encodeURIComponent(actionId)}/attendee/${encodeURIComponent(attendeeKey)}`);

  let poll: ReturnType<typeof setInterval> | null = null;
  let registered = false;

  // Register once (records the name + joins the action), then poll the summary.
  $effect(() => {
    if (!registered && actionId && attendeeKey) {
      registered = true;
      register();
    }
  });

  async function register() {
    try {
      await fetch(`${base}/register`, {
        method: 'POST',
        headers: { ...sessionHeaders(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ name })
      });
    } catch {
      /* best-effort; the summary poll will still work */
    }
    refresh();
    poll = setInterval(refresh, 2000);
  }

  // Unpack the one-char-per-cell grid payload (see the /grid endpoint):
  // '.' idle, '0'-'9' active (digit = load bucket), 'p' paused, 'x' killed.
  function decodeCells(packed: string): Cell[] {
    const out: Cell[] = new Array(packed.length);
    for (let i = 0; i < packed.length; i++) {
      const ch = packed[i];
      if (ch === '.') out[i] = { state: 0, load: 0 };
      else if (ch === 'p') out[i] = { state: 2, load: 0 };
      else if (ch === 'x') out[i] = { state: 3, load: 0 };
      else out[i] = { state: 1, load: (ch.charCodeAt(0) - 48) / 9 };
    }
    return out;
  }

  async function refresh() {
    try {
      const [sumRes, workRes, gridRes, lbRes, goalRes] = await Promise.all([
        fetch(`${base}/summary`, { headers: sessionHeaders() }),
        fetch(`${base}/work`, { headers: sessionHeaders() }),
        fetch(`${base}/grid?take=5000`, { headers: sessionHeaders() }),
        fetch(`/api/chargersim/${encodeURIComponent(actionId)}/leaderboard`, { headers: sessionHeaders() }),
        fetch(`/api/chargersim/${encodeURIComponent(actionId)}/goal`, { headers: sessionHeaders() })
      ]);
      if (sumRes.ok) {
        summary = await sumRes.json();
        // Track total active power over time for the sparkline.
        powerHistory = [...powerHistory, summary?.totalActivePowerKw ?? 0].slice(-POWER_HISTORY_MAX);
      }
      if (workRes.ok) work = await workRes.json();
      if (gridRes.ok) cells = decodeCells((await gridRes.json()).cells ?? '');
      if (lbRes.ok) leaderboard = await lbRes.json();
      if (goalRes.ok) goal = await goalRes.json();
      if (opened) await reloadOpened();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  async function post(path: string, body?: unknown) {
    error = null;
    busy = true;
    try {
      const res = await fetch(`${base}${path}`, {
        method: 'POST',
        headers: { ...sessionHeaders(), 'Content-Type': 'application/json' },
        body: body ? JSON.stringify(body) : undefined
      });
      if (res.status === 409) throw new Error('ChargerSim is not active');
      if (!res.ok) throw new Error((await res.json().catch(() => null))?.error ?? `Request failed (${res.status})`);
      await refresh();
      return res;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
      return null;
    } finally {
      busy = false;
    }
  }

  const create = (amount: number) => post('/create', { amount });
  const batch = (command: string, amount = 100) => post('/batch', { command, amount });
  const killMine = () => post('/kill');

  async function openCharger(id: string) {
    if (!id) return;
    error = null;
    try {
      const res = await fetch(`${base}/charger/${encodeURIComponent(id)}`, { headers: sessionHeaders() });
      if (res.status === 404) throw new Error(`No charger ${id}`);
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      opened = await res.json();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  async function openRandom(kind: 'active' | 'paused') {
    error = null;
    try {
      const res = await fetch(`${base}/charger/random/${kind}`, { headers: sessionHeaders() });
      if (res.status === 404) throw new Error(`No ${kind} charger right now`);
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      opened = await res.json();
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  async function reloadOpened() {
    if (!opened) return;
    const res = await fetch(`${base}/charger/${encodeURIComponent(opened.chargerId)}`, { headers: sessionHeaders() });
    if (res.ok) opened = await res.json();
  }

  async function chargerCommand(command: string) {
    if (!opened) return;
    const res = await post(`/charger/${encodeURIComponent(opened.chargerId)}/${command}`);
    if (res?.ok) opened = await res.json();
  }

  function sessionDuration(c: Charger): string {
    if (!c.sessionStartedAt) return '—';
    const secs = Math.max(0, Math.floor((Date.now() - new Date(c.sessionStartedAt).getTime()) / 1000));
    const m = Math.floor(secs / 60);
    const s = secs % 60;
    return `${m}m ${s.toString().padStart(2, '0')}s`;
  }

  const fmt = (n: number, d = 1) => (n ?? 0).toLocaleString(undefined, { maximumFractionDigits: d });

  onDestroy(() => { if (poll) clearInterval(poll); });
</script>

<div class="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
  <div class="flex items-center justify-between">
    <div>
      <div class="flex flex-wrap items-center gap-2">
        <span class="inline-flex items-center gap-1.5 rounded-full bg-green-100 px-2.5 py-1 text-xs font-semibold text-green-700">
          <span class="h-1.5 w-1.5 animate-pulse rounded-full bg-green-500"></span> LIVE
        </span>
        {#if working}
          <span class="inline-flex items-center gap-1.5 rounded-full bg-indigo-100 px-2.5 py-1 text-xs font-semibold text-indigo-700">
            <span class="h-1.5 w-1.5 animate-spin rounded-full border border-indigo-500 border-t-transparent"></span>
            Working…{work.pendingChargers > 0 ? ` ${fmt(work.pendingChargers, 0)} to create` : ''}{work.queuedCommands > 0 ? ` · ${work.queuedCommands} queued` : ''}
          </span>
        {/if}
      </div>
      <h2 class="mt-2 text-xl font-bold tracking-tight">⚡ ChargerSim control room</h2>
      <p class="text-sm text-slate-500">Request chargers and commands — a background worker creates and runs them. Each charger is its own Orleans grain.</p>
    </div>
  </div>

  <!-- Fleet controls — the primary actions, kept at the top so they're the first thing
       you reach for. Grouped by intent: add capacity, command the fleet, inspect one charger. -->
  <div class="mt-5 space-y-4 rounded-xl border border-slate-200 bg-slate-50/70 p-4">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <h3 class="text-sm font-bold tracking-tight text-slate-700">Fleet controls</h3>
      <button disabled={busy} onclick={killMine} class="rounded-lg bg-red-600 px-3 py-1.5 text-sm font-semibold text-white shadow-sm transition hover:bg-red-700 disabled:opacity-50">
        Kill my chargers
      </button>
    </div>

    <!-- Add capacity -->
    <div>
      <h4 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Add chargers <span class="font-normal normal-case text-slate-400">— up to 5,000, created in the background</span></h4>
      <div class="mt-2 flex flex-wrap gap-2">
        <button disabled={busy} onclick={() => create(100)} class="rounded-lg bg-indigo-600 px-3 py-1.5 text-sm font-semibold text-white shadow-sm transition hover:bg-indigo-700 disabled:opacity-50">+100 chargers</button>
      </div>
    </div>

    <!-- Command the whole fleet -->
    <div>
      <h4 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Command the fleet <span class="font-normal normal-case text-slate-400">— 100 chargers, queued &amp; run in the background</span></h4>
      <div class="mt-2 flex flex-wrap gap-2">
        <button disabled={busy} onclick={() => batch('StartSessions')} class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-50">Start sessions</button>
        <button disabled={busy} onclick={() => batch('StopCharging')} class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-50">Stop charging</button>
        <button disabled={busy} onclick={() => batch('StopSessions')} class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-50">Stop sessions</button>
        <button disabled={busy} onclick={() => batch('LowerPowerUsage')} class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-50">Lower power</button>
        <button disabled={busy} onclick={() => batch('IncreasePowerUsage')} class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-50">Increase power</button>
        <button disabled={busy} onclick={() => batch('RandomChaos')} class="rounded-lg border border-purple-300 bg-white px-3 py-1.5 text-sm font-medium text-purple-700 transition hover:bg-purple-50 disabled:opacity-50">Random chaos</button>
      </div>
    </div>

    <!-- Inspect a single charger -->
    <div>
      <h4 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Inspect a single charger</h4>
      <div class="mt-2 flex flex-wrap items-center gap-2">
        <button disabled={busy} onclick={() => openRandom('active')} class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-50">Random active</button>
        <button disabled={busy} onclick={() => openRandom('paused')} class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50 disabled:opacity-50">Random paused</button>
        <form class="flex items-center gap-2" onsubmit={(e) => { e.preventDefault(); openCharger(openId.trim().toUpperCase()); }}>
          <input bind:value={openId} placeholder="CP-000042" class="w-32 rounded-lg border border-slate-300 bg-white px-2 py-1.5 text-sm shadow-sm outline-none focus:border-indigo-500 focus:ring-2 focus:ring-indigo-200" />
          <button type="submit" class="rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 transition hover:bg-slate-50">Open</button>
        </form>
      </div>
    </div>
  </div>

  <!-- Single charger detail panel (sits with the controls that open it) -->
  {#if opened}
    <div class="mt-5 rounded-xl border border-slate-300 bg-slate-50 p-4">
      <div class="flex items-center justify-between">
        <h3 class="text-base font-bold tracking-tight">{opened.chargerId}</h3>
        <button onclick={() => (opened = null)} class="rounded-md px-2 py-1 text-xs font-medium text-slate-500 hover:bg-slate-200">Close ✕</button>
      </div>
      <dl class="mt-3 grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
        <dt class="text-slate-500">State</dt><dd class="font-medium">{STATE_LABELS[opened.state] ?? opened.state}</dd>
        <dt class="text-slate-500">Session id</dt><dd class="font-mono text-xs">{opened.activeSessionId ?? '—'}</dd>
        <dt class="text-slate-500">Active power</dt><dd class="font-medium tabular-nums">{fmt(opened.activePowerKw)} kW</dd>
        <dt class="text-slate-500">Max power</dt><dd class="font-medium tabular-nums">{fmt(opened.maxPowerKw)} kW</dd>
        <dt class="text-slate-500">Session energy</dt><dd class="font-medium tabular-nums">{fmt(opened.sessionKwh, 2)} kWh</dd>
        <dt class="text-slate-500">Session duration</dt><dd class="font-medium tabular-nums">{sessionDuration(opened)}</dd>
        <dt class="text-slate-500">Last updated</dt><dd class="text-xs">{new Date(opened.lastUpdatedAt).toLocaleTimeString()}</dd>
      </dl>
      <div class="mt-3 flex flex-wrap gap-2">
        <button disabled={busy} onclick={() => chargerCommand('StartSession')} class="rounded-lg bg-green-600 px-2.5 py-1 text-xs font-semibold text-white transition hover:bg-green-700 disabled:opacity-50">Start</button>
        <button disabled={busy} onclick={() => chargerCommand('PauseCharging')} class="rounded-lg bg-amber-500 px-2.5 py-1 text-xs font-semibold text-white transition hover:bg-amber-600 disabled:opacity-50">Pause</button>
        <button disabled={busy} onclick={() => chargerCommand('ResumeCharging')} class="rounded-lg bg-green-500 px-2.5 py-1 text-xs font-semibold text-white transition hover:bg-green-600 disabled:opacity-50">Resume</button>
        <button disabled={busy} onclick={() => chargerCommand('StopSession')} class="rounded-lg border border-slate-300 px-2.5 py-1 text-xs font-medium text-slate-700 transition hover:bg-slate-100 disabled:opacity-50">Stop</button>
        <button disabled={busy} onclick={() => chargerCommand('LowerPower')} class="rounded-lg border border-slate-300 px-2.5 py-1 text-xs font-medium text-slate-700 transition hover:bg-slate-100 disabled:opacity-50">Lower power</button>
        <button disabled={busy} onclick={() => chargerCommand('IncreasePower')} class="rounded-lg border border-slate-300 px-2.5 py-1 text-xs font-medium text-slate-700 transition hover:bg-slate-100 disabled:opacity-50">Increase power</button>
        <button disabled={busy} onclick={() => chargerCommand('Kill')} class="rounded-lg bg-red-600 px-2.5 py-1 text-xs font-semibold text-white transition hover:bg-red-700 disabled:opacity-50">Kill</button>
      </div>
    </div>
  {/if}

  <!-- Live fleet grid — the block overview of every charger you own -->
  <ChargerSimFleetGrid {cells} total={summary?.totalChargers ?? 0} />

  <!-- Live leaderboard across all attendees -->
  <ChargerSimLeaderboard rows={leaderboard} {attendeeKey} />

  <!-- Aggregate fleet stats, read from your aggregate grain. -->
  <div class="mt-5 grid grid-cols-2 gap-2 sm:grid-cols-4">
    {#snippet tile(label: string, value: string, tone = 'text-slate-900')}
      <div class="rounded-xl border border-slate-200 bg-slate-50 px-3 py-2">
        <p class="text-[11px] uppercase tracking-wide text-slate-400">{label}</p>
        <p class="text-lg font-bold tabular-nums {tone}">{value}</p>
      </div>
    {/snippet}
    {@render tile('Chargers', fmt(summary?.totalChargers ?? 0, 0))}
    {@render tile('Active', fmt(summary?.activeSessionCount ?? 0, 0), 'text-green-600')}
    {@render tile('Paused', fmt(summary?.pausedWithSessionCount ?? 0, 0), 'text-amber-600')}
    {@render tile('No session', fmt(summary?.noSessionCount ?? 0, 0))}
    {@render tile('Killed', fmt(summary?.killedCount ?? 0, 0), 'text-red-600')}
    {@render tile('With session', fmt(summary?.chargersWithSessionCount ?? 0, 0))}
    {@render tile('Active power', `${fmt(summary?.totalActivePowerKw ?? 0)} kW`, 'text-indigo-600')}
    {@render tile('Session energy', `${fmt(summary?.totalSessionKwh ?? 0)} kWh`)}
  </div>

  <!-- Room-wide collaborative goal (shown only when the presenter has set one) -->
  <ChargerSimGoal {goal} />

  <!-- Power sparkline + achievement badges -->
  <ChargerSimAchievements {summary} {powerHistory} {attendeeKey} />

  {#if error}
    <p class="mt-4 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{error}</p>
  {/if}
</div>
