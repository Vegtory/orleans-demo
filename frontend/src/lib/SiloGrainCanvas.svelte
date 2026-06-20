<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { sessionHeaders } from '$lib/session';
  import { GrainsVisualization, type GrainInput, type CallInput } from '$lib/viz/GrainsVisualization';

  // A Canvas-based, physics-driven view of the live Orleans cluster. Silos are
  // drawn as rounded cards; each grain is a particle that lives inside its silo
  // (repelling neighbours, pulled toward centre, kept in bounds) and drifts
  // smoothly when it moves silo. Recent grain-to-grain calls play as fading
  // energy pulses with a dot travelling source -> target.
  //
  // Data comes from the existing /api/cluster/live endpoint, polled every 500ms.
  // The component only reconciles data into the long-lived visualization; all
  // particle/animation state lives in GrainsVisualization across polls.

  let { password }: { password: string } = $props();

  // --- API record shapes (mirror the backend's /cluster/live payload) -------
  interface ActiveGrain {
    grainId: string;
    grainType: string;
    siloAddress: string;
  }
  interface CallRecord {
    timestampUtc: string;
    observedOnSilo: string;
    sourceGrainId: string | null;
    targetGrainId: string;
    interfaceName: string;
    methodName: string;
    durationMs: number;
    success: boolean;
  }

  // --- Which grains we surface (match the existing presenter view) ----------
  const APP_PREFIX = 'App.Api.Grains.';
  const HIDDEN = new Set(['clustercallrecorder', 'activationinventory']);
  const COLORS: Record<string, string> = {
    presenter: '#818cf8',
    attendee: '#34d399',
    multiplechoice: '#fbbf24',
    presentation: '#f472b6'
  };
  const LABELS: Record<string, string> = {
    presenter: 'Presenter',
    attendee: 'Attendee',
    multiplechoice: 'Multiple choice',
    presentation: 'Presentation'
  };

  const typeKey = (grainId: string) => grainId.split('/')[0];
  const keyPart = (grainId: string) => grainId.slice(grainId.indexOf('/') + 1);
  const colorForType = (type: string) => COLORS[type] ?? '#94a3b8';
  const labelForType = (type: string) => LABELS[type] ?? type;
  const isAppGrain = (g: ActiveGrain) =>
    g.grainType.startsWith(APP_PREFIX) && !HIDDEN.has(typeKey(g.grainId));

  function siloLabel(silo: string): string {
    // "S127.0.0.1:11111:140893564" -> "Silo 127.0.0.1:11111"
    const parts = silo.replace(/^S/, '').split(':');
    return `Silo ${parts.length >= 2 ? `${parts[0]}:${parts[1]}` : silo}`;
  }

  // --- Component state ------------------------------------------------------
  let container: HTMLDivElement;
  let canvas: HTMLCanvasElement;
  let viz: GrainsVisualization | null = null;
  let resizeObserver: ResizeObserver | null = null;
  let pollTimer: ReturnType<typeof setInterval> | null = null;

  let error = $state<string | null>(null);
  let tracingEnabled = $state(true);
  let grainCount = $state(0);
  let siloCount = $state(0);
  let legend = $state<{ type: string; label: string; color: string; count: number }[]>([]);

  // De-dupe seen calls; skip replaying the history that exists on first poll.
  let seen = new Set<string>();
  let primed = false;

  const callKey = (c: CallRecord) =>
    `${c.timestampUtc}|${c.sourceGrainId}|${c.targetGrainId}|${c.methodName}`;

  function headers(): HeadersInit {
    return { ...sessionHeaders(), 'X-Presenter-Password': password };
  }

  async function tick() {
    try {
      const res = await fetch('/api/cluster/live', { headers: headers() });
      if (!res.ok) throw new Error('Cluster activity unavailable');
      const data = await res.json();
      applySnapshot(data.activations as ActiveGrain[]);
      ingestCalls(data.calls as CallRecord[]);
      tracingEnabled = data.tracing.enabled;
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  // Reconcile the activation snapshot into the visualization. The viz keeps
  // nodes long-lived; we just hand it the current set.
  function applySnapshot(activations: ActiveGrain[]) {
    const app = activations.filter(isAppGrain);
    const grains: GrainInput[] = app.map((g) => ({
      id: g.grainId,
      type: typeKey(g.grainId),
      silo: g.siloAddress,
      label: keyPart(g.grainId)
    }));
    viz?.setGrains(grains);

    // Header + legend bookkeeping.
    grainCount = app.length;
    siloCount = new Set(app.map((g) => g.siloAddress)).size;
    const counts = new Map<string, number>();
    for (const g of grains) counts.set(g.type, (counts.get(g.type) ?? 0) + 1);
    legend = [...counts.entries()]
      .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
      .map(([type, count]) => ({ type, label: labelForType(type), color: colorForType(type), count }));
  }

  function ingestCalls(list: CallRecord[]) {
    const fresh: CallInput[] = [];
    for (const c of list) {
      if (!c.sourceGrainId) continue;
      const k = callKey(c);
      if (seen.has(k)) continue;
      seen.add(k);
      fresh.push({
        sourceId: c.sourceGrainId,
        targetId: c.targetGrainId,
        color: colorForType(typeKey(c.targetGrainId)),
        success: c.success
      });
    }
    // Keep the seen-set bounded (recorder only keeps the last ~100 calls).
    if (seen.size > 600) seen = new Set([...seen].slice(-300));

    if (!primed) {
      primed = true; // first batch is existing history — record but don't replay
      return;
    }
    if (fresh.length) viz?.emitCalls(fresh);
  }

  async function setTracing(enabled: boolean) {
    tracingEnabled = enabled; // optimistic; next poll confirms
    try {
      await fetch('/api/cluster/tracing', {
        method: 'POST',
        headers: { ...headers(), 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled })
      });
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  onMount(() => {
    viz = new GrainsVisualization(canvas, { colorForType, siloLabel });

    // Resize the canvas with its container.
    resizeObserver = new ResizeObserver(() => {
      const rect = container.getBoundingClientRect();
      viz?.resize(rect.width, rect.height);
    });
    resizeObserver.observe(container);
    const rect = container.getBoundingClientRect();
    viz.resize(rect.width, rect.height);

    viz.start();
    tick();
    pollTimer = setInterval(tick, 500);
  });

  onDestroy(() => {
    if (pollTimer) clearInterval(pollTimer);
    resizeObserver?.disconnect();
    viz?.destroy();
    viz = null;
  });
</script>

<section class="overflow-hidden rounded-2xl border border-slate-800 bg-slate-950 shadow-sm">
  <!-- Header: title, live counts, tracing toggle -->
  <div class="flex flex-wrap items-center justify-between gap-3 border-b border-slate-800 px-5 py-3">
    <div>
      <h2 class="text-sm font-semibold tracking-tight text-slate-100">Cluster grains</h2>
      <p class="text-xs text-slate-400">
        {grainCount} grain{grainCount === 1 ? '' : 's'} · {siloCount} silo{siloCount === 1 ? '' : 's'}
      </p>
    </div>
    <div class="flex items-center gap-4">
      <label class="inline-flex cursor-pointer items-center gap-2 text-xs font-medium text-slate-300">
        <input
          type="checkbox"
          checked={tracingEnabled}
          onchange={(e) => setTracing((e.target as HTMLInputElement).checked)}
          class="h-4 w-4 rounded border-slate-600 bg-slate-800 text-indigo-500 focus:ring-indigo-400"
        />
        Record calls
      </label>
      <span class="inline-flex items-center gap-1.5 text-xs font-medium text-slate-400">
        <span
          class="h-1.5 w-1.5 rounded-full {tracingEnabled ? 'animate-pulse bg-indigo-400' : 'bg-slate-600'}"
        ></span>
        {tracingEnabled ? 'live · 500ms' : 'paused'}
      </span>
    </div>
  </div>

  <!-- Canvas stage. The container drives sizing via ResizeObserver. -->
  <div bind:this={container} class="relative h-[420px] w-full sm:h-[520px]">
    <canvas bind:this={canvas} class="block h-full w-full"></canvas>
    {#if grainCount === 0}
      <div class="pointer-events-none absolute inset-0 flex items-center justify-center">
        <p class="text-sm text-slate-500">Waiting for active grains…</p>
      </div>
    {/if}
  </div>

  <!-- Legend -->
  {#if legend.length > 0}
    <div class="flex flex-wrap gap-x-4 gap-y-1.5 border-t border-slate-800 px-5 py-3">
      {#each legend as item (item.type)}
        <span class="inline-flex items-center gap-1.5 text-xs text-slate-400">
          <span class="h-2 w-2 rounded-full" style="background:{item.color}"></span>
          {item.label}
          <span class="tabular-nums text-slate-500">· {item.count}</span>
        </span>
      {/each}
    </div>
  {/if}

  {#if error}
    <p class="border-t border-slate-800 px-5 py-2 text-xs text-amber-400">{error}</p>
  {/if}
</section>
