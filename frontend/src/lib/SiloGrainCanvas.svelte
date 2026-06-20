<script lang="ts">
  import { onDestroy } from 'svelte';
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
    presentation: '#f472b6',
    charger: '#38bdf8',
    attendeechargersim: '#2dd4bf',
    attendeechargeraggregate: '#a78bfa',
    chargersimaction: '#fb923c'
  };
  const LABELS: Record<string, string> = {
    presenter: 'Presenter',
    attendee: 'Attendee',
    multiplechoice: 'Multiple choice',
    presentation: 'Presentation',
    charger: 'Sim charger',
    attendeechargersim: 'Charger fleet',
    attendeechargeraggregate: 'Fleet aggregate',
    chargersimaction: 'Charger action'
  };

  // High-volume grain types hidden by default to keep the canvas readable.
  // Click a legend entry to toggle visibility.
  const DEFAULT_HIDDEN = new Set(['charger', 'attendeechargersim', 'attendeechargeraggregate', 'chargersimaction']);
  let hiddenTypes = $state(new Set(DEFAULT_HIDDEN));

  // Max grains rendered per type. When exceeded, we pick a stable random subset
  // equally distributed across silos. The selection is sticky: grains stay
  // selected until they leave the cluster, so the canvas doesn't churn.
  const MAX_PER_TYPE = 100;
  // Persistent selection per grain type: Map<type, Set<grainId>>
  const selectedByType = new Map<string, Set<string>>();

  function shuffle<T>(arr: T[]): T[] {
    const a = [...arr];
    for (let i = a.length - 1; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      [a[i], a[j]] = [a[j], a[i]];
    }
    return a;
  }

  // Returns a subset of appGrains capped to MAX_PER_TYPE per type, and a set
  // of type keys that were capped.
  function applyPerTypeLimit(
    appGrains: ActiveGrain[]
  ): { limited: ActiveGrain[]; cappedTypes: Set<string> } {
    // Index all active grains by type then by id.
    const byType = new Map<string, Map<string, ActiveGrain>>();
    for (const g of appGrains) {
      const t = typeKey(g.grainId);
      if (!byType.has(t)) byType.set(t, new Map());
      byType.get(t)!.set(g.grainId, g);
    }

    // Drop persistent selections for types that vanished entirely.
    for (const t of selectedByType.keys()) {
      if (!byType.has(t)) selectedByType.delete(t);
    }

    const cappedTypes = new Set<string>();
    const limited: ActiveGrain[] = [];

    for (const [type, typeMap] of byType) {
      if (typeMap.size <= MAX_PER_TYPE) {
        selectedByType.delete(type);
        for (const g of typeMap.values()) limited.push(g);
        continue;
      }

      cappedTypes.add(type);

      if (!selectedByType.has(type)) selectedByType.set(type, new Set());
      const selected = selectedByType.get(type)!;

      // Evict grains that left the cluster.
      for (const id of selected) {
        if (!typeMap.has(id)) selected.delete(id);
      }

      // Fill remaining slots from unselected grains, round-robining across silos
      // to keep distribution even.
      if (selected.size < MAX_PER_TYPE) {
        const candidates = [...typeMap.keys()].filter((id) => !selected.has(id));
        const bySilo = new Map<string, string[]>();
        for (const id of candidates) {
          const silo = typeMap.get(id)!.siloAddress;
          if (!bySilo.has(silo)) bySilo.set(silo, []);
          bySilo.get(silo)!.push(id);
        }
        const queues = [...bySilo.values()].map(shuffle);
        let needed = MAX_PER_TYPE - selected.size;
        for (let round = 0; needed > 0 && queues.some((q) => round < q.length); round++) {
          for (const queue of queues) {
            if (round < queue.length && needed > 0) {
              selected.add(queue[round]);
              needed--;
            }
          }
        }
      }

      for (const id of selected) {
        const g = typeMap.get(id);
        if (g) limited.push(g);
      }
    }

    return { limited, cappedTypes };
  }

  function toggleType(t: string) {
    const next = new Set(hiddenTypes);
    if (next.has(t)) next.delete(t);
    else next.add(t);
    hiddenTypes = next;
  }

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
  let container = $state<HTMLDivElement>();
  let canvas = $state<HTMLCanvasElement>();
  let viz: GrainsVisualization | null = null;
  let resizeObserver: ResizeObserver | null = null;
  let pollTimer: ReturnType<typeof setInterval> | null = null;

  let error = $state<string | null>(null);
  // Tracing (call recording) is off by default; the first poll confirms.
  let tracingEnabled = $state(false);
  // Collapsed by default: the cluster view is opt-in, and we avoid polling
  // /api/cluster/live entirely until the presenter expands it.
  let collapsed = $state(true);
  let grainCount = $state(0);
  let siloCount = $state(0);
  let legend = $state<{ type: string; label: string; color: string; count: number; hidden: boolean; capped: boolean }[]>([]);

  // Last activation snapshot — reactive so the $effect below re-runs on changes.
  let lastActivations = $state<ActiveGrain[]>([]);

  // Re-sync the visualization and legend whenever the snapshot or hidden-types set changes.
  $effect(() => {
    const app = lastActivations.filter(isAppGrain);

    // Raw counts before capping (used for the legend so users see the real number).
    const rawCounts = new Map<string, number>();
    for (const g of app) {
      const t = typeKey(g.grainId);
      rawCounts.set(t, (rawCounts.get(t) ?? 0) + 1);
    }

    // Apply per-type cap to all app grains before filtering by visibility, so
    // the selection stays stable even when a type is toggled off and back on.
    const { limited, cappedTypes } = applyPerTypeLimit(app);
    const visible = limited.filter((g) => !hiddenTypes.has(typeKey(g.grainId)));
    const grains: GrainInput[] = visible.map((g) => ({
      id: g.grainId,
      type: typeKey(g.grainId),
      silo: g.siloAddress,
      label: keyPart(g.grainId)
    }));
    viz?.setGrains(grains);

    grainCount = visible.length;
    siloCount = new Set(app.map((g) => g.siloAddress)).size;
    legend = [...rawCounts.entries()]
      .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
      .map(([type, count]) => ({
        type,
        label: labelForType(type),
        color: colorForType(type),
        count,
        hidden: hiddenTypes.has(type),
        capped: cappedTypes.has(type)
      }));
  });

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

  // Store the latest activation snapshot; the $effect above handles reconciliation.
  function applySnapshot(activations: ActiveGrain[]) {
    lastActivations = activations;
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

  // Spin up the canvas + polling once the section is expanded (canvas/container
  // are only in the DOM then). Idempotent: a no-op if already running.
  function startViz() {
    if (viz || !canvas || !container) return;
    const el = container;
    viz = new GrainsVisualization(canvas, { colorForType, siloLabel });

    // Resize the canvas with its container.
    resizeObserver = new ResizeObserver(() => {
      const rect = el.getBoundingClientRect();
      viz?.resize(rect.width, rect.height);
    });
    resizeObserver.observe(el);
    const rect = el.getBoundingClientRect();
    viz.resize(rect.width, rect.height);

    viz.start();
    tick();
    pollTimer = setInterval(tick, 500);
  }

  // Tear down the canvas + polling when collapsed (or unmounted) so no API
  // calls happen while hidden. Reset priming so reopening doesn't replay stale
  // call history.
  function stopViz() {
    if (pollTimer) clearInterval(pollTimer);
    pollTimer = null;
    resizeObserver?.disconnect();
    resizeObserver = null;
    viz?.destroy();
    viz = null;
    primed = false;
    seen = new Set();
    selectedByType.clear();
  }

  // React to expand/collapse. Runs after DOM updates, so when expanding the
  // canvas/container bindings are already in place.
  $effect(() => {
    if (collapsed) stopViz();
    else startViz();
  });

  onDestroy(() => stopViz());
</script>

<section class="overflow-hidden rounded-2xl border border-slate-800 bg-slate-950 shadow-sm">
  <!-- Header: collapse toggle, live counts, tracing toggle -->
  <div class="flex flex-wrap items-center justify-between gap-3 border-b border-slate-800 px-5 py-3">
    <button
      type="button"
      onclick={() => (collapsed = !collapsed)}
      aria-expanded={!collapsed}
      class="-mx-2 flex items-center gap-2 rounded-lg px-2 py-1 text-left transition hover:bg-slate-900"
    >
      <span
        class="text-slate-400 transition-transform {collapsed ? '' : 'rotate-90'}"
        aria-hidden="true"
      >▶</span>
      <span>
        <span class="block text-sm font-semibold tracking-tight text-slate-100">Cluster grains</span>
        <span class="block text-xs text-slate-400">
          {#if collapsed}
            Show live cluster view
          {:else}
            {@const total = legend.reduce((s, l) => s + l.count, 0)}
            {grainCount} grain{grainCount === 1 ? '' : 's'} · {siloCount} silo{siloCount === 1 ? '' : 's'}{#if grainCount < total} · {total - grainCount} hidden{/if}
          {/if}
        </span>
      </span>
    </button>
    {#if !collapsed}
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
    {/if}
  </div>

  {#if !collapsed}
    <!-- Canvas stage. The container drives sizing via ResizeObserver. -->
    <div bind:this={container} class="relative h-[420px] w-full sm:h-[520px]">
      <canvas bind:this={canvas} class="block h-full w-full"></canvas>
      {#if grainCount === 0}
        <div class="pointer-events-none absolute inset-0 flex items-center justify-center">
          <p class="text-sm text-slate-500">Waiting for active grains…</p>
        </div>
      {/if}
    </div>

    <!-- Legend — click any entry to hide/show that grain type in the canvas -->
    {#if legend.length > 0}
      <div class="flex flex-wrap gap-x-4 gap-y-1.5 border-t border-slate-800 px-5 py-3">
        {#each legend as item (item.type)}
          <button
            type="button"
            onclick={() => toggleType(item.type)}
            class="inline-flex items-center gap-1.5 text-xs transition-opacity {item.hidden
              ? 'opacity-40 hover:opacity-70'
              : 'text-slate-400 hover:opacity-80'}"
            title={item.hidden
              ? `Show ${item.label} grains`
              : item.capped
                ? `Hide ${item.label} grains (showing ${MAX_PER_TYPE} of ${item.count})`
                : `Hide ${item.label} grains`}
          >
            <span
              class="h-2 w-2 shrink-0 rounded-full border"
              style="background:{item.hidden ? 'transparent' : item.color}; border-color:{item.color}"
            ></span>
            {item.label}
            <span class="tabular-nums text-slate-500">· {item.count}</span>
            {#if item.capped}
              <span
                class="text-orange-400"
                title="Showing {MAX_PER_TYPE} of {item.count} grains"
                aria-label="Display capped at {MAX_PER_TYPE}"
              >⬤</span>
            {/if}
          </button>
        {/each}
      </div>
    {/if}

    {#if error}
      <p class="border-t border-slate-800 px-5 py-2 text-xs text-amber-400">{error}</p>
    {/if}
  {/if}
</section>
