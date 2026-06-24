<script lang="ts">
  // A live grid of the attendee's chargers — one cell per charger, coloured by
  // state and (for active sessions) brightened by load. Cells come from a stable,
  // packed sample of the attendee's aggregate grain, so each cell keeps its
  // position between polls and animates in place as its grain ticks. Presentational
  // only: the parent owns the polling/decoding and passes the latest cells down.
  interface Cell {
    state: number; // 0 No session, 1 Active, 2 Paused, 3 Killed
    load: number; // active-session load fraction 0..1 (0 for other states)
  }

  let { cells, total }: { cells: Cell[]; total: number } = $props();

  // The whole fleet renders at once (up to MaxChargers), so cells shrink as the
  // count climbs — a handful are chunky, thousands form a dense heatmap wall.
  const layout = $derived.by(() => {
    const n = cells.length;
    if (n <= 120) return { size: 16, gap: 3 };
    if (n <= 300) return { size: 13, gap: 3 };
    if (n <= 800) return { size: 10, gap: 2 };
    if (n <= 1500) return { size: 8, gap: 2 };
    if (n <= 3000) return { size: 6, gap: 1 };
    if (n <= 4500) return { size: 5, gap: 1 };
    return { size: 4, gap: 1 }; // densest tier — full ~5,000-charger fleet
  });

  // Per-cell colour transitions are a smooth touch for small fleets, but thousands
  // of simultaneously-transitioning divs repaint expensively on low-end phones —
  // so drop the transition once the grid gets large.
  const animate = $derived(cells.length <= 1000);

  // Active cells fade from a dim to a vivid green by load fraction, so a fleet
  // running flat-out visibly glows. Other states use a flat colour.
  function cellStyle(c: Cell): string {
    if (c.state === 1) {
      const frac = Math.min(1, Math.max(0, c.load));
      const light = 62 - frac * 30; // 62% (dim) → 32% (vivid) lightness
      return `background-color: hsl(145 70% ${light}%)`;
    }
    const colors: Record<number, string> = {
      0: '#cbd5e1', // No session — slate-300
      2: '#f59e0b', // Paused — amber-500
      3: '#dc2626' // Killed — red-600
    };
    return `background-color: ${colors[c.state] ?? '#cbd5e1'}`;
  }
</script>

<div class="mt-5">
  <div class="flex items-baseline justify-between">
    <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Live fleet grid</h3>
    <span class="text-[11px] text-slate-400">{total.toLocaleString()} charger{total === 1 ? '' : 's'}</span>
  </div>

  {#if cells.length === 0}
    <p class="mt-2 text-sm text-slate-500">No chargers yet — request some above to light up the grid.</p>
  {:else}
    <!-- auto-fill so the grid reflows to the screen width; cell size adapts to count. -->
    <div class="mt-2 grid" style="gap: {layout.gap}px; grid-template-columns: repeat(auto-fill, minmax({layout.size}px, 1fr));">
      {#each cells as c, i (i)}
        <div
          class="aspect-square rounded-[2px] {animate ? 'transition-colors duration-500' : ''}"
          style={cellStyle(c)}
          title={['No session', 'Active', 'Paused', 'Killed'][c.state] ?? ''}
        ></div>
      {/each}
    </div>
    <!-- Legend -->
    <div class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-slate-500">
      <span class="flex items-center gap-1"><span class="h-2.5 w-2.5 rounded-[2px]" style="background-color: hsl(145 70% 42%)"></span> Active (brighter = more load)</span>
      <span class="flex items-center gap-1"><span class="h-2.5 w-2.5 rounded-[2px] bg-amber-500"></span> Paused</span>
      <span class="flex items-center gap-1"><span class="h-2.5 w-2.5 rounded-[2px] bg-slate-300"></span> No session</span>
      <span class="flex items-center gap-1"><span class="h-2.5 w-2.5 rounded-[2px] bg-red-600"></span> Killed</span>
    </div>
  {/if}
</div>
