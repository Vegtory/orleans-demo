<script lang="ts">
  // The room-wide collaborative goal: a shared progress bar toward the presenter's
  // total-power target. Every attendee sees the same bar, so the whole room pushes
  // for it together. A confetti burst fires the moment the fleet first reaches 100%.
  // Presentational only — the parent polls /goal and passes the status down.
  interface GoalStatus {
    goalActivePowerKw: number;
    currentActivePowerKw: number;
  }

  let { goal }: { goal: GoalStatus | null } = $props();

  const target = $derived(goal?.goalActivePowerKw ?? 0);
  const current = $derived(goal?.currentActivePowerKw ?? 0);
  const hasGoal = $derived(target > 0);
  const pct = $derived(hasGoal ? Math.min(100, (current / target) * 100) : 0);
  const reached = $derived(hasGoal && current >= target);

  const fmt = (n: number) => (n ?? 0).toLocaleString(undefined, { maximumFractionDigits: 0 });

  // Fire confetti once per crossing into "reached", and re-arm only after the fleet
  // drops back below the target — so a fleet hovering at the goal doesn't spam it.
  let celebrated = false;
  let pieces = $state<{ id: number; left: number; delay: number; hue: number; dur: number }[]>([]);
  let nextId = 0;

  $effect(() => {
    if (reached && !celebrated) {
      celebrated = true;
      burst();
    } else if (!reached && hasGoal) {
      celebrated = false;
    }
  });

  function burst() {
    const batch = Array.from({ length: 80 }, () => ({
      id: nextId++,
      left: Math.random() * 100,
      delay: Math.random() * 0.4,
      hue: Math.floor(Math.random() * 360),
      dur: 1.6 + Math.random() * 1.2
    }));
    pieces = batch;
    // Clear after the longest animation so the DOM doesn't accumulate confetti.
    setTimeout(() => (pieces = []), 3200);
  }
</script>

{#if hasGoal}
  <div class="mt-5 rounded-xl border p-4 transition-colors {reached ? 'border-green-300 bg-green-50' : 'border-indigo-200 bg-indigo-50/50'}">
    <div class="flex flex-wrap items-baseline justify-between gap-x-2 gap-y-0.5">
      <h3 class="text-xs font-semibold uppercase tracking-wide {reached ? 'text-green-600' : 'text-indigo-500'}">
        🎯 Room goal {reached ? '— reached!' : ''}
      </h3>
      <span class="text-sm font-bold tabular-nums {reached ? 'text-green-700' : 'text-indigo-700'}">
        {fmt(current)} / {fmt(target)} kW
      </span>
    </div>
    <div class="mt-2 h-4 w-full overflow-hidden rounded-full bg-white ring-1 ring-inset ring-slate-200">
      <div
        class="h-full rounded-full transition-[width] duration-700 ease-out {reached ? 'bg-green-500' : 'bg-indigo-500'}"
        style="width: {pct}%"
      ></div>
    </div>
    <p class="mt-1.5 text-right text-[11px] font-medium tabular-nums {reached ? 'text-green-600' : 'text-slate-500'}">
      {pct.toFixed(0)}% — everyone's fleets combined
    </p>
  </div>
{/if}

<!-- Confetti overlay -->
{#if pieces.length}
  <div class="pointer-events-none fixed inset-0 z-50 overflow-hidden">
    {#each pieces as p (p.id)}
      <span
        class="confetti"
        style="left: {p.left}%; animation-delay: {p.delay}s; animation-duration: {p.dur}s; background-color: hsl({p.hue} 90% 55%);"
      ></span>
    {/each}
  </div>
{/if}

<style>
  .confetti {
    position: absolute;
    top: -12px;
    width: 8px;
    height: 14px;
    border-radius: 1px;
    opacity: 0.9;
    animation-name: confetti-fall;
    animation-timing-function: linear;
    animation-iteration-count: 1;
  }
  @keyframes confetti-fall {
    0% {
      transform: translateY(-10vh) rotate(0deg);
      opacity: 1;
    }
    100% {
      transform: translateY(105vh) rotate(720deg);
      opacity: 0.9;
    }
  }
</style>
