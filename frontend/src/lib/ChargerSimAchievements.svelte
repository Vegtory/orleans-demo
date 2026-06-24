<script lang="ts">
  import { browser } from '$app/environment';

  // Light gamification for the attendee: a live sparkline of total active power and
  // a row of milestone badges that pop the moment a threshold is first crossed.
  // Earned badges are persisted per attendee in localStorage so they survive a
  // reload. All derived from the summary the parent already polls — no extra calls.
  interface Summary {
    totalChargers: number;
    activeSessionCount: number;
    totalActivePowerKw: number;
    totalSessionKwh: number;
  }

  let { summary, powerHistory, attendeeKey }: {
    summary: Summary | null;
    powerHistory: number[];
    attendeeKey: string;
  } = $props();

  interface Badge {
    id: string;
    label: string;
    emoji: string;
    test: (s: Summary) => boolean;
  }

  const BADGES: Badge[] = [
    { id: 'first', label: 'First charger', emoji: '⚡', test: (s) => s.totalChargers >= 1 },
    { id: 'century', label: 'Century (100 chargers)', emoji: '💯', test: (s) => s.totalChargers >= 100 },
    { id: 'fleet1k', label: 'Fleet of 1,000', emoji: '🚀', test: (s) => s.totalChargers >= 1000 },
    { id: 'kw100', label: '100 kW', emoji: '🔌', test: (s) => s.totalActivePowerKw >= 100 },
    { id: 'kw500', label: '500 kW', emoji: '⚙️', test: (s) => s.totalActivePowerKw >= 500 },
    { id: 'mw1', label: '1 MW club', emoji: '🏆', test: (s) => s.totalActivePowerKw >= 1000 },
    { id: 'active50', label: '50 live sessions', emoji: '🔥', test: (s) => s.activeSessionCount >= 50 },
    { id: 'active100', label: '100 live sessions', emoji: '🌋', test: (s) => s.activeSessionCount >= 100 },
    { id: 'kwh100', label: '100 kWh delivered', emoji: '🔋', test: (s) => s.totalSessionKwh >= 100 }
  ];

  const storageKey = $derived(`orleans-demo:chargersim-achievements:${attendeeKey}`);

  let earned = $state<Set<string>>(new Set());
  let toast = $state<Badge | null>(null);
  let toastTimer: ReturnType<typeof setTimeout> | null = null;
  let loaded = false;

  function load() {
    if (!browser) return;
    try {
      const raw = localStorage.getItem(storageKey);
      if (raw) earned = new Set(JSON.parse(raw) as string[]);
    } catch {
      /* storage unavailable — start fresh */
    }
  }

  function persist() {
    if (!browser) return;
    try {
      localStorage.setItem(storageKey, JSON.stringify([...earned]));
    } catch {
      /* ignore */
    }
  }

  // Watch the summary; award any newly crossed threshold and pop a toast for it.
  // The first run after load only seeds already-earned badges (no toast spam on a
  // reload of an established fleet).
  $effect(() => {
    if (!loaded) {
      load();
      loaded = true;
    }
    if (!summary) return;

    let changed = false;
    let popped: Badge | null = null;
    for (const b of BADGES) {
      if (!earned.has(b.id) && b.test(summary)) {
        earned.add(b.id);
        changed = true;
        popped = b; // last newly-earned in this pass gets the toast
      }
    }
    if (changed) {
      earned = new Set(earned);
      persist();
      if (popped) showToast(popped);
    }
  });

  function showToast(b: Badge) {
    toast = b;
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (toast = null), 4000);
  }

  // Build an SVG sparkline polyline from the rolling power history.
  const SPARK_W = 240;
  const SPARK_H = 36;
  const sparkPoints = $derived.by(() => {
    const h = powerHistory;
    if (h.length < 2) return '';
    const max = Math.max(...h, 1);
    const stepX = SPARK_W / (h.length - 1);
    return h
      .map((v, i) => `${(i * stepX).toFixed(1)},${(SPARK_H - (v / max) * (SPARK_H - 2) - 1).toFixed(1)}`)
      .join(' ');
  });
  const currentPower = $derived(powerHistory.length ? powerHistory[powerHistory.length - 1] : 0);
</script>

<div class="mt-5 rounded-xl border border-slate-200 bg-gradient-to-br from-indigo-50/60 to-white p-4">
  <div class="flex flex-wrap items-center justify-between gap-x-2 gap-y-1">
    <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Power & achievements</h3>
    <span class="text-xs font-semibold tabular-nums text-indigo-600">{currentPower.toLocaleString(undefined, { maximumFractionDigits: 1 })} kW now</span>
  </div>

  <!-- Power sparkline -->
  <svg viewBox="0 0 {SPARK_W} {SPARK_H}" preserveAspectRatio="none" class="mt-2 h-10 w-full" role="img" aria-label="Total active power over time">
    {#if sparkPoints}
      <polyline points={sparkPoints} fill="none" stroke="#4f46e5" stroke-width="1.5" stroke-linejoin="round" stroke-linecap="round" vector-effect="non-scaling-stroke" />
    {:else}
      <line x1="0" y1={SPARK_H - 1} x2={SPARK_W} y2={SPARK_H - 1} stroke="#e2e8f0" stroke-width="1" vector-effect="non-scaling-stroke" />
    {/if}
  </svg>

  <!-- Badges -->
  <div class="mt-3 flex flex-wrap gap-1.5">
    {#each BADGES as b (b.id)}
      {@const got = earned.has(b.id)}
      <span
        class="inline-flex items-center gap-1 rounded-full px-2 py-0.5 text-[11px] font-medium transition {got ? 'bg-amber-100 text-amber-800 ring-1 ring-amber-300' : 'bg-slate-100 text-slate-400'}"
        title={got ? 'Unlocked!' : 'Locked'}
      >
        <span class={got ? '' : 'opacity-40 grayscale'}>{b.emoji}</span>{b.label}
      </span>
    {/each}
  </div>
</div>

<!-- Achievement pop -->
{#if toast}
  <div class="pointer-events-none fixed bottom-6 left-1/2 z-50 w-[calc(100vw-1.5rem)] max-w-sm -translate-x-1/2">
    <div class="mx-auto flex w-fit max-w-full items-center gap-2 rounded-full bg-slate-900 px-4 py-2 text-sm font-semibold text-white shadow-lg ring-1 ring-amber-400/50">
      <span class="shrink-0 text-lg">{toast.emoji}</span>
      <span class="truncate">Achievement unlocked — {toast.label}!</span>
    </div>
  </div>
{/if}
