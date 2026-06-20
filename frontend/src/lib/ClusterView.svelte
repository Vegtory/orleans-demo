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
    presentation: 'Presentation',
    charger: 'Sim charger',
    attendeechargersim: 'Charger fleet',
    attendeechargeraggregate: 'Fleet aggregate',
    chargersimaction: 'Charger action'
  };
  const COLORS: Record<string, string> = {
    presenter: '#6366f1',
    attendee: '#10b981',
    multiplechoice: '#f59e0b',
    presentation: '#ec4899',
    charger: '#0ea5e9',
    attendeechargersim: '#14b8a6',
    attendeechargeraggregate: '#8b5cf6',
    chargersimaction: '#f97316'
  };

  // High-volume grain types hidden by default to keep the view readable.
  // Click a legend entry to toggle visibility.
  const DEFAULT_HIDDEN = new Set(['charger', 'attendeechargersim', 'attendeechargeraggregate', 'chargersimaction']);
  let hiddenTypes = $state(new Set(DEFAULT_HIDDEN));

  function toggleType(t: string) {
    const next = new Set(hiddenTypes);
    if (next.has(t)) next.delete(t);
    else next.add(t);
    hiddenTypes = next;
  }

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
  // Only grains whose type the user hasn't hidden — used for the web layout.
  let visibleGrains = $derived(appGrains.filter((g) => !hiddenTypes.has(typeKey(g.grainId))));

  // Active-grain counts per type (all types, not just visible), most common first.
  let typeCounts = $derived.by(() => {
    const m = new Map<string, number>();
    for (const g of appGrains) {
      const t = typeKey(g.grainId);
      m.set(t, (m.get(t) ?? 0) + 1);
    }
    return [...m.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
  });
  let siloCount = $derived(new Set(appGrains.map((g) => g.siloAddress)).size);

  // --- Spiderweb layout ----------------------------------------------------
  // Each silo is rendered as its own radial "web": a central hub with grains
  // placed on concentric rings along evenly-spaced spokes. Spoke lines (hub ->
  // grain) and ring lines (grain -> neighbouring grain) form the web strands;
  // each grain is a colored dot labelled with its key. Silos sit side by side,
  // each in its own column, and the web scales down to fit narrow columns.
  const VB_W = 1000;
  const NODE_R = 6.5; // grain dot radius
  const HUB_R = 11; // silo hub radius
  const INNER_R = 54; // radius of the first (innermost) ring
  const RING_GAP = 46; // spacing between concentric rings
  const COL_PAD = 38; // breathing room inside a silo column
  const LABEL_GAP = 18; // room reserved for the outermost key labels
  const MIN_H = 380;

  interface Pos {
    x: number;
    y: number;
    type: string;
  }
  interface WebLine {
    x1: number;
    y1: number;
    x2: number;
    y2: number;
    ring: boolean;
  }
  interface SiloHub {
    silo: string;
    cx: number;
    cy: number;
    radius: number;
    count: number;
  }

  // How many spokes a web of `count` grains uses — grows gently with the count
  // so small silos stay simple and large ones fan out into a denser web.
  function spokesFor(count: number): number {
    if (count <= 1) return 1;
    return Math.min(11, Math.max(5, Math.round(Math.sqrt(count * 1.9))));
  }

  let layout = $derived.by(() => {
    const bySilo = new Map<string, ActiveGrain[]>();
    for (const g of visibleGrains) {
      (bySilo.get(g.siloAddress) ?? bySilo.set(g.siloAddress, []).get(g.siloAddress)!).push(g);
    }
    const silos = [...bySilo.entries()];
    const n = Math.max(silos.length, 1);
    const colW = VB_W / n;
    const colHalf = colW / 2;
    const maxAllowed = colHalf - COL_PAD - LABEL_GAP;

    // First pass: geometry + per-silo scale so each web fits its column.
    const meta = silos.map(([silo, gs]) => {
      gs.sort(
        (a, b) =>
          typeKey(a.grainId).localeCompare(typeKey(b.grainId)) ||
          a.grainId.localeCompare(b.grainId)
      );
      const spokes = spokesFor(gs.length);
      const rings = Math.max(1, Math.ceil(gs.length / spokes));
      const naturalR = INNER_R + (rings - 1) * RING_GAP;
      const scale = naturalR > 0 ? Math.min(1, maxAllowed / naturalR) : 1;
      return { silo, gs, spokes, rings, radius: naturalR * scale, scale };
    });

    const maxRadius = Math.max(0, ...meta.map((m) => m.radius));
    const height = Math.max(MIN_H, 2 * (maxRadius + NODE_R + LABEL_GAP + 34));
    const cy = height / 2;

    const positions = new Map<string, Pos>();
    const web: WebLine[] = [];
    const hubs: SiloHub[] = [];

    meta.forEach((m, si) => {
      const cx = si * colW + colHalf;
      const innerR = INNER_R * m.scale;
      const ringGap = RING_GAP * m.scale;
      const S = m.spokes;

      // Place each grain on (spoke, ring) so spokes line up across rings.
      const bySpoke = new Map<number, { x: number; y: number; r: number }[]>();
      const byRing = new Map<number, { x: number; y: number; s: number }[]>();

      m.gs.forEach((g, i) => {
        const s = i % S;
        const r = Math.floor(i / S);
        const ang = (s / S) * Math.PI * 2 - Math.PI / 2;
        const radius = innerR + r * ringGap;
        const x = cx + radius * Math.cos(ang);
        const y = cy + radius * Math.sin(ang);
        positions.set(g.grainId, { x, y, type: typeKey(g.grainId) });
        (bySpoke.get(s) ?? bySpoke.set(s, []).get(s)!).push({ x, y, r });
        (byRing.get(r) ?? byRing.set(r, []).get(r)!).push({ x, y, s });
      });

      // Spoke strands: hub -> outermost grain on each occupied spoke.
      for (const arr of bySpoke.values()) {
        const tip = arr.reduce((a, b) => (b.r > a.r ? b : a));
        web.push({ x1: cx, y1: cy, x2: tip.x, y2: tip.y, ring: false });
      }

      // Ring strands: connect neighbouring grains on the same ring.
      for (const arr of byRing.values()) {
        const pts = [...arr].sort((a, b) => a.s - b.s);
        const closed = pts.length === S && pts.length > 2;
        const segs = closed ? pts.length : pts.length - 1;
        for (let k = 0; k < segs; k++) {
          const a = pts[k];
          const b = pts[(k + 1) % pts.length];
          web.push({ x1: a.x, y1: a.y, x2: b.x, y2: b.y, ring: true });
        }
      }

      hubs.push({ silo: m.silo, cx, cy, radius: m.radius + NODE_R + 12, count: m.gs.length });
    });

    return { positions, web, hubs, width: VB_W, height };
  });

  // --- Call animations -----------------------------------------------------
  interface Anim {
    id: string;
    sourceId: string;
    targetId: string;
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
    // Endpoints are resolved live (in `pulses`) against the current layout, so
    // a strand keeps tracking its grains even as the web reshapes around them.
    anims = [
      ...anims,
      {
        id: crypto.randomUUID(),
        sourceId: c.sourceGrainId!,
        targetId: c.targetGrainId,
        color: color(typeKey(c.targetGrainId)),
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
      const from = layout.positions.get(a.sourceId);
      const to = layout.positions.get(a.targetId);
      if (!from || !to) continue; // an endpoint isn't currently visible — skip
      const p = Math.min(t / TRAVEL_MS, 1);
      const opacity = t <= TRAVEL_MS ? 1 : Math.max(0, 1 - (t - TRAVEL_MS) / FADE_MS);
      out.push({
        id: a.id,
        color: a.color,
        success: a.success,
        x1: from.x,
        y1: from.y,
        x2: to.x,
        y2: to.y,
        dotX: from.x + (to.x - from.x) * p,
        dotY: from.y + (to.y - from.y) * p,
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
      {#if visibleGrains.length < appGrains.length}
        <span class="text-slate-300">· {appGrains.length - visibleGrains.length} hidden</span>
      {/if}
    </span>
  </div>
  {#if typeCounts.length > 0}
    <ul class="mt-4 space-y-2">
      {#each typeCounts as [type, count] (type)}
        {@const hidden = hiddenTypes.has(type)}
        <li class="flex items-center gap-2.5 transition-opacity {hidden ? 'opacity-50' : ''}">
          <span
            class="h-2.5 w-2.5 shrink-0 rounded-full border"
            style="background:{hidden ? 'transparent' : color(type)}; border-color:{color(type)}"
          ></span>
          <span class="flex-1 text-sm font-medium text-slate-700">{label(type)}</span>
          <span
            class="rounded-full bg-slate-100 px-2 py-0.5 text-xs font-semibold tabular-nums text-slate-600"
            >{count}</span
          >
          <button
            type="button"
            onclick={() => toggleType(type)}
            class="text-xs text-slate-400 hover:text-slate-600"
            title={hidden ? `Show in cluster view` : `Hide from cluster view`}
          >{hidden ? 'show' : 'hide'}</button>
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
        aria-label="Grains arranged as a spiderweb per silo, with live communication lines"
      >

        <!-- Silo clusters: faint boundary + label -->
        {#each layout.hubs as hub (hub.silo)}
          <g>
            <circle
              cx={hub.cx}
              cy={hub.cy}
              r={hub.radius}
              class="fill-slate-50/60 stroke-slate-200"
              stroke-width="1.5"
              stroke-dasharray="4 5"
            />
            <text
              x={hub.cx}
              y={hub.cy - hub.radius - 10}
              text-anchor="middle"
              class="fill-slate-500"
              font-size="13"
              font-weight="600"
            >
              Silo {siloLabel(hub.silo)} · {hub.count}
            </text>
          </g>
        {/each}

        <!-- Web strands: spokes (hub -> grain) and rings (grain -> grain) -->
        {#each layout.web as strand, i (i)}
          <line
            x1={strand.x1}
            y1={strand.y1}
            x2={strand.x2}
            y2={strand.y2}
            class={strand.ring ? 'stroke-slate-300' : 'stroke-slate-200'}
            stroke-width={strand.ring ? 1 : 1.25}
          />
        {/each}

        <!-- Silo hub centers -->
        {#each layout.hubs as hub (hub.silo)}
          <circle cx={hub.cx} cy={hub.cy} r={HUB_R + 5} fill="#0f172a" opacity="0.06" />
          <circle cx={hub.cx} cy={hub.cy} r={HUB_R} class="fill-slate-700" />
          <circle cx={hub.cx} cy={hub.cy} r={HUB_R - 4} class="fill-slate-400" />
        {/each}

        <!-- Communication pulses (drawn beneath the grain dots) -->
        {#each pulses as pulse (pulse.id)}
          <g opacity={pulse.opacity}>
            <line
              x1={pulse.x1}
              y1={pulse.y1}
              x2={pulse.dotX}
              y2={pulse.dotY}
              stroke={pulse.color}
              stroke-width="2.5"
              stroke-linecap="round"
            />
            <circle cx={pulse.dotX} cy={pulse.dotY} r="4.5" fill={pulse.color} />
            <circle cx={pulse.dotX} cy={pulse.dotY} r="9" fill={pulse.color} opacity="0.22" />
          </g>
        {/each}

        <!-- Grain dots, each labelled with its key -->
        {#each visibleGrains as g (g.grainId)}
          {@const p = layout.positions.get(g.grainId)}
          {#if p}
            {@const k = keyPart(g.grainId)}
            <g>
              <circle cx={p.x} cy={p.y} r={NODE_R + 3} fill={color(p.type)} opacity="0.18" />
              <circle
                cx={p.x}
                cy={p.y}
                r={NODE_R}
                fill={color(p.type)}
                stroke="white"
                stroke-width="1.5"
              >
                <title>{label(p.type)} · {k}</title>
              </circle>
              <text
                x={p.x}
                y={p.y + NODE_R + 11}
                text-anchor="middle"
                font-size="9"
                font-weight="600"
                class="fill-slate-600"
                style="paint-order:stroke; stroke:white; stroke-width:2.5px"
              >
                {k.length > 12 ? k.slice(0, 11) + '…' : k}
              </text>
            </g>
          {/if}
        {/each}
      </svg>
    </div>

    <!-- Legend — click any entry to hide/show that grain type -->
    <div class="mt-4 flex flex-wrap gap-x-4 gap-y-1.5">
      {#each typeCounts as [type, count] (type)}
        {@const hidden = hiddenTypes.has(type)}
        <button
          type="button"
          onclick={() => toggleType(type)}
          class="inline-flex items-center gap-1.5 text-xs transition-opacity {hidden
            ? 'opacity-40 hover:opacity-70'
            : 'text-slate-500 hover:opacity-80'}"
          title={hidden ? `Show ${label(type)} grains` : `Hide ${label(type)} grains`}
        >
          <span
            class="h-2 w-2 shrink-0 rounded-full border"
            style="background:{hidden ? 'transparent' : color(type)}; border-color:{color(type)}"
          ></span>
          {label(type)}
          <span class="tabular-nums text-slate-400">· {count}</span>
        </button>
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
