<script lang="ts">
  // A live grid of the attendee's chargers — one cell per sampled charger, coloured
  // by state and (for active sessions) brightened by load. The cells are a stable
  // sample from the attendee's aggregate grain, so each cell keeps its position
  // between polls and animates in place as its grain ticks. Presentational only:
  // the parent owns the polling and passes the latest sample down.
  interface Cell {
    state: number; // ChargerSimState: 0 No session, 1 Active, 2 Paused, 3 Killed
    activePowerKw: number;
    maxPowerKw: number;
  }

  let { cells, total }: { cells: Cell[]; total: number } = $props();

  // Active cells fade from a dim to a vivid green by load fraction, so a fleet
  // running flat-out visibly glows. Other states use a flat colour.
  function cellStyle(c: Cell): string {
    if (c.state === 1) {
      const frac = c.maxPowerKw > 0 ? Math.min(1, Math.max(0, c.activePowerKw / c.maxPowerKw)) : 0.5;
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
    {#if total > cells.length}
      <span class="text-[11px] text-slate-400">showing {cells.length.toLocaleString()} of {total.toLocaleString()}</span>
    {/if}
  </div>

  {#if cells.length === 0}
    <p class="mt-2 text-sm text-slate-500">No chargers yet — request some above to light up the grid.</p>
  {:else}
    <!-- auto-fill so the grid reflows to the screen: ~13px cells on a phone,
         many more columns on a wide screen, with no ragged right edge. -->
    <div class="mt-2 grid gap-[3px]" style="grid-template-columns: repeat(auto-fill, minmax(13px, 1fr));">
      {#each cells as c, i (i)}
        <div
          class="aspect-square rounded-[2px] transition-colors duration-500"
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
