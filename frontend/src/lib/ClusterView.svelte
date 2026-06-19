<script lang="ts">
  import { onDestroy, onMount } from 'svelte';
  import { sessionHeaders } from '$lib/session';

  // Live visualization of the Orleans cluster: which grains are active, on which
  // silo, and the grain-to-grain calls flowing between them. Data comes from the
  // /api/cluster/live debug endpoint, polled every 500ms; observed calls are then
  // replayed as animated lines, slightly staggered so a burst plays out instead
  // of flashing all at once.

  let { password }: { password: string } = $props();

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

  // --- Which grains we surface ---------------------------------------------
  // Only application grains are interesting here; Orleans system targets and the
  // observability plumbing itself are hidden to keep the picture readable.
  const APP_PREFIX = 'App.Api.Grains.';
  const HIDDEN = new Set(['clustercallrecorder', 'activationinventory']);

  const LABELS: Record<string, string> = {
    presenter: 'Presenter',
    attendee: 'Attendee',
    multiplechoice: 'Multiple choice',
    presentation: 'Presentation'
  };
  const COLORS: Record<string, string> = {
    presenter: '#6366f1',
    attendee: '#10b981',
    multiplechoice: '#f59e0b',
    presentation: '#ec4899'
  };

  function typeKey(grainId: string): string {
    return grainId.split('/')[0];
  }
  function keyPart(grainId: string): string {
    return grainId.slice(grainId.indexOf('/') + 1);
  }
  function label(t: string): string {
    return LABELS[t] ?? t;
  }
  function color(t: string): string {
    return COLORS[t] ?? '#64748b';
  }
  function isAppGrain(g: ActiveGrain): boolean {
    return g.grainType.startsWith(APP_PREFIX) && !HIDDEN.has(typeKey(g.grainId));
  }
  function siloLabel(silo: string): string {
    // "S127.0.0.1:11111:140893564" -> "127.0.0.1:11111"
    const parts = silo.replace(/^S/, '').split(':');
    return parts.length >= 2 ? `${parts[0]}:${parts[1]}` : silo;
  }

  let activations = $state<ActiveGrain[]>([]);
  let error = $state<string | null>(null);
  let tracingEnabled = $state(true);

  let appGrains = $derived(activations.filter(isAppGrain));

  // Active-grain counts per type, most common first.
  let typeCounts = $derived.by(() => {
    const m = new Map<string, number>();
    for (const g of appGrains) {
      const t = typeKey(g.grainId);
      m.set(t, (m.get(t) ?? 0) + 1);
    }
    return [...m.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
  });
  let siloCount = $derived(new Set(appGrains.map((g) => g.siloAddress)).size);

  // --- Layout --------------------------------------------------------------
  const VB_W = 1000;
  const NODE_W = 134;
  const NODE_H = 34;
  const CELL_W = NODE_W + 14;
  const CELL_H = NODE_H + 14;
  const HEADER_H = 34;
  const PAD_X = 16;
  const PAD_Y = 16;
  const SILO_GAP = 18;

  interface Pos {
    x: number;
    y: number;
    type: string;
  }
  interface SiloBox {
    silo: string;
    x: number;
    w: number;
    count: number;
  }

  let layout = $derived.by(() => {
    const bySilo = new Map<string, ActiveGrain[]>();
    for (const g of appGrains) {
      (bySilo.get(g.siloAddress) ?? bySilo.set(g.siloAddress, []).get(g.siloAddress)!).push(g);
    }
    const silos = [...bySilo.entries()];
    const n = Math.max(silos.length, 1);
    const siloW = (VB_W - SILO_GAP * (n + 1)) / n;
    const cols = Math.max(1, Math.floor((siloW - PAD_X * 2) / CELL_W));

    const positions = new Map<string, Pos>();
    let maxRows = 1;

    const boxes: SiloBox[] = silos.map(([silo, gs], si) => {
      gs.sort(
        (a, b) =>
          typeKey(a.grainId).localeCompare(typeKey(b.grainId)) ||
          a.grainId.localeCompare(b.grainId)
      );
      const x0 = SILO_GAP + si * (siloW + SILO_GAP);
      const rows = Math.max(1, Math.ceil(gs.length / cols));
      maxRows = Math.max(maxRows, rows);
      gs.forEach((g, i) => {
        const r = Math.floor(i / cols);
        const c = i % cols;
        const x = x0 + PAD_X + c * CELL_W + NODE_W / 2;
        const y = HEADER_H + PAD_Y + r * CELL_H + NODE_H / 2;
        positions.set(g.grainId, { x, y, type: typeKey(g.grainId) });
      });
      return { silo, x: x0, w: siloW, count: gs.length };
    });

    const height = HEADER_H + PAD_Y * 2 + maxRows * CELL_H;
    return { boxes, positions, width: VB_W, height };
  });

  // --- Call animations -----------------------------------------------------
  interface Anim {
    id: string;
    x1: number;
    y1: number;
    x2: number;
    y2: number;
    color: string;
    start: number;
    success: boolean;
  }
  const TRAVEL_MS = 700; // time a pulse takes to travel an edge
  const FADE_MS = 350; // line fade-out after arrival
  const STAGGER_MS = 130; // gap between replayed calls in a poll batch

  let anims = $state<Anim[]>([]);
  let now = $state(0);
  let seen = new Set<string>();
  let primed = false; // skip animating the history that exists on first poll

  function callKey(c: CallRecord): string {
    return `${c.timestampUtc}|${c.sourceGrainId}|${c.targetGrainId}|${c.methodName}`;
  }

  function ingestCalls(list: CallRecord[]) {
    const fresh: CallRecord[] = [];
    for (const c of list) {
      if (!c.sourceGrainId) continue;
      const k = callKey(c);
      if (!seen.has(k)) {
        seen.add(k);
        fresh.push(c);
      }
    }
    // Keep the seen-set bounded (the recorder only keeps the last 100 calls).
    if (seen.size > 600) seen = new Set([...seen].slice(-300));

    if (!primed) {
      primed = true; // first batch is existing history — record but don't replay
      return;
    }

    const base = performance.now();
    fresh.forEach((c, i) => scheduleAnim(c, base + i * STAGGER_MS));
  }

  function scheduleAnim(c: CallRecord, startAt: number) {
    const from = layout.positions.get(c.sourceGrainId!);
    const to = layout.positions.get(c.targetGrainId);
    if (!from || !to) return; // an endpoint isn't currently visible — skip
    anims = [
      ...anims,
      {
        id: crypto.randomUUID(),
        x1: from.x,
        y1: from.y,
        x2: to.x,
        y2: to.y,
        color: color(to.type),
        start: startAt,
        success: c.success
      }
    ];
  }

  // Per-frame view of in-flight animations.
  let pulses = $derived.by(() => {
    const out: {
      id: string;
      color: string;
      success: boolean;
      x1: number;
      y1: number;
      x2: number;
      y2: number;
      dotX: number;
      dotY: number;
      opacity: number;
    }[] = [];
    for (const a of anims) {
      const t = now - a.start;
      if (t < 0) continue; // not started yet
      const p = Math.min(t / TRAVEL_MS, 1);
      const opacity = t <= TRAVEL_MS ? 1 : Math.max(0, 1 - (t - TRAVEL_MS) / FADE_MS);
      out.push({
        id: a.id,
        color: a.color,
        success: a.success,
        x1: a.x1,
        y1: a.y1,
        x2: a.x2,
        y2: a.y2,
        dotX: a.x1 + (a.x2 - a.x1) * p,
        dotY: a.y1 + (a.y2 - a.y1) * p,
        opacity
      });
    }
    return out;
  });

  // --- Polling + animation loop -------------------------------------------
  let pollTimer: ReturnType<typeof setInterval> | null = null;
  let raf = 0;

  function headers(): HeadersInit {
    return { ...sessionHeaders(), 'X-Presenter-Password': password };
  }

  async function tick() {
    try {
      const res = await fetch('/api/cluster/live', { headers: headers() });
      if (!res.ok) throw new Error('Cluster activity unavailable');
      const data = await res.json();
      activations = data.activations;
      ingestCalls(data.calls);
      // Reflect the cluster-wide toggle (another presenter may have flipped it).
      tracingEnabled = data.tracing.enabled;
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Unknown error';
    }
  }

  async function setTracing(enabled: boolean) {
    tracingEnabled = enabled; // optimistic; the next poll confirms
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

  function frame() {
    now = performance.now();
    // Drop animations that have fully faded.
    if (anims.length) {
      const cutoff = now - (TRAVEL_MS + FADE_MS);
      const live = anims.filter((a) => a.start > cutoff);
      if (live.length !== anims.length) anims = live;
    }
    raf = requestAnimationFrame(frame);
  }

  onMount(() => {
    now = performance.now();
    tick();
    pollTimer = setInterval(tick, 500);
    raf = requestAnimationFrame(frame);
  });

  onDestroy(() => {
    if (pollTimer) clearInterval(pollTimer);
    if (raf) cancelAnimationFrame(raf);
  });
</script>

<!-- Active grains by type -->
<section class="rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
  <div class="flex items-center justify-between">
    <h2 class="text-lg font-semibold tracking-tight">Active grains</h2>
    <span class="text-xs text-slate-400">
      {appGrains.length} grain{appGrains.length === 1 ? '' : 's'} · {siloCount} silo{siloCount === 1
        ? ''
        : 's'}
    </span>
  </div>
  {#if typeCounts.length > 0}
    <ul class="mt-4 space-y-2">
      {#each typeCounts as [type, count] (type)}
        <li class="flex items-center gap-2.5">
          <span class="h-2.5 w-2.5 shrink-0 rounded-full" style="background:{color(type)}"></span>
          <span class="flex-1 text-sm font-medium text-slate-700">{label(type)}</span>
          <span
            class="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold tabular-nums text-slate-600"
            >{count}</span
          >
        </li>
      {/each}
    </ul>
  {:else}
    <p class="mt-3 text-sm text-slate-400">No active grains yet.</p>
  {/if}
</section>

<!-- Silo / communication map -->
<section class="mt-6 rounded-2xl border border-slate-200 bg-white p-6 shadow-sm">
  <div class="flex flex-wrap items-center justify-between gap-3">
    <h2 class="text-lg font-semibold tracking-tight">Cluster activity</h2>
    <div class="flex items-center gap-4">
      <label class="inline-flex cursor-pointer items-center gap-2 text-xs font-medium text-slate-600">
        <input
          type="checkbox"
          checked={tracingEnabled}
          onchange={(e) => setTracing((e.target as HTMLInputElement).checked)}
          class="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-300"
        />
        Record grain-to-grain calls
      </label>
      <span class="inline-flex items-center gap-1.5 text-xs font-medium text-slate-400">
        <span
          class="h-1.5 w-1.5 rounded-full {tracingEnabled
            ? 'animate-pulse bg-indigo-500'
            : 'bg-slate-300'}"
        ></span>
        {tracingEnabled ? 'live · polling 500ms' : 'tracing paused'}
      </span>
    </div>
  </div>

  {#if appGrains.length > 0}
    <div class="mt-4 overflow-x-auto">
      <svg
        viewBox="0 0 {layout.width} {layout.height}"
        class="w-full"
        style="min-width:520px; height:auto"
        role="img"
        aria-label="Grains in their silos with live communication lines"
      >
        <!-- Silo containers -->
        {#each layout.boxes as box (box.silo)}
          <g>
            <rect
              x={box.x}
              y={0}
              width={box.w}
              height={layout.height}
              rx="12"
              class="fill-slate-50 stroke-slate-200"
              stroke-width="1.5"
            />
            <text
              x={box.x + 14}
              y={22}
              class="fill-slate-500"
              font-size="13"
              font-weight="600"
            >
              Silo {siloLabel(box.silo)}
            </text>
            <text
              x={box.x + box.w - 14}
              y={22}
              text-anchor="end"
              class="fill-slate-400"
              font-size="12"
            >
              {box.count}
            </text>
          </g>
        {/each}

        <!-- Communication pulses (drawn beneath the nodes) -->
        {#each pulses as pulse (pulse.id)}
          <g opacity={pulse.opacity}>
            <line
              x1={pulse.x1}
              y1={pulse.y1}
              x2={pulse.dotX}
              y2={pulse.dotY}
              stroke={pulse.color}
              stroke-width="2"
              stroke-linecap="round"
            />
            <circle cx={pulse.dotX} cy={pulse.dotY} r="4.5" fill={pulse.color} />
            <circle cx={pulse.dotX} cy={pulse.dotY} r="8" fill={pulse.color} opacity="0.25" />
          </g>
        {/each}

        <!-- Grain nodes -->
        {#each appGrains as g (g.grainId)}
          {@const p = layout.positions.get(g.grainId)}
          {#if p}
            <g transform="translate({p.x - NODE_W / 2}, {p.y - NODE_H / 2})">
              <rect
                width={NODE_W}
                height={NODE_H}
                rx="9"
                fill="white"
                stroke={color(p.type)}
                stroke-width="1.5"
              />
              <circle cx="13" cy={NODE_H / 2} r="4" fill={color(p.type)} />
              <text x="24" y="15" font-size="11" font-weight="600" class="fill-slate-700">
                {label(p.type)}
              </text>
              <text x="24" y="27" font-size="9.5" class="fill-slate-400">
                {keyPart(g.grainId).length > 16
                  ? keyPart(g.grainId).slice(0, 15) + '…'
                  : keyPart(g.grainId)}
              </text>
            </g>
          {/if}
        {/each}
      </svg>
    </div>

    <!-- Legend -->
    <div class="mt-4 flex flex-wrap gap-x-4 gap-y-1.5">
      {#each typeCounts as [type] (type)}
        <span class="inline-flex items-center gap-1.5 text-xs text-slate-500">
          <span class="h-2 w-2 rounded-full" style="background:{color(type)}"></span>
          {label(type)}
        </span>
      {/each}
    </div>
  {:else}
    <p class="mt-3 text-sm text-slate-400">
      No active grains to show yet. Create a question and set it live, then watch attendees answer.
    </p>
  {/if}

  {#if error}
    <p class="mt-3 text-xs text-amber-600">{error}</p>
  {/if}
</section>
