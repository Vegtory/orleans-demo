<script lang="ts">
  // A live, head-to-head leaderboard across every attendee, ranked by total active
  // power (the metric that swings most as fleets start, pause and surge). The
  // attendee's own row is always shown and highlighted, even when it falls outside
  // the visible top slice. Presentational only — the parent polls and passes rows.
  interface Row {
    attendeeId: string;
    attendeeName: string;
    totalChargers: number;
    activeSessionCount: number;
    totalActivePowerKw: number;
  }

  let { rows, attendeeKey }: { rows: Row[]; attendeeKey: string } = $props();

  const TOP_N = 6;

  // Rank by active power, then break ties by fleet size. Returns ranked rows with a
  // 1-based rank attached.
  const ranked = $derived(
    [...rows]
      .sort((a, b) => b.totalActivePowerKw - a.totalActivePowerKw || b.totalChargers - a.totalChargers)
      .map((r, i) => ({ ...r, rank: i + 1 }))
  );

  const me = $derived(ranked.find((r) => r.attendeeId === attendeeKey) ?? null);
  const top = $derived(ranked.slice(0, TOP_N));
  // Show our own row beneath the top slice when we're not already in it.
  const showMeSeparately = $derived(me !== null && me.rank > TOP_N);

  const fmt = (n: number, d = 1) => (n ?? 0).toLocaleString(undefined, { maximumFractionDigits: d });
  const medal = (rank: number) => (rank === 1 ? '🥇' : rank === 2 ? '🥈' : rank === 3 ? '🥉' : `#${rank}`);
</script>

<div class="mt-5">
  <div class="flex items-baseline justify-between">
    <h3 class="text-xs font-semibold uppercase tracking-wide text-slate-400">Leaderboard — by active power</h3>
    {#if me}
      <span class="text-[11px] font-semibold text-indigo-600">You're #{me.rank} of {ranked.length}</span>
    {/if}
  </div>

  {#if ranked.length === 0}
    <p class="mt-2 text-sm text-slate-500">No fleets yet. Be the first to power up.</p>
  {:else}
    {#snippet row(r: (typeof ranked)[number], mine: boolean)}
      <div class="flex items-center gap-3 rounded-lg px-3 py-1.5 text-sm {mine ? 'bg-indigo-50 ring-1 ring-indigo-200' : ''}">
        <span class="w-8 shrink-0 text-center font-bold tabular-nums {r.rank <= 3 ? 'text-base' : 'text-slate-400'}">{medal(r.rank)}</span>
        <span class="min-w-0 flex-1 truncate font-medium {mine ? 'text-indigo-700' : 'text-slate-700'}">{r.attendeeName || r.attendeeId}{mine ? ' (you)' : ''}</span>
        <span class="hidden shrink-0 text-xs tabular-nums text-green-600 min-[380px]:inline">{fmt(r.activeSessionCount, 0)} active</span>
        <span class="w-16 shrink-0 text-right font-semibold tabular-nums text-indigo-600 sm:w-20">{fmt(r.totalActivePowerKw)} kW</span>
      </div>
    {/snippet}

    <div class="mt-2 space-y-0.5">
      {#each top as r (r.attendeeId)}
        {@render row(r, r.attendeeId === attendeeKey)}
      {/each}
      {#if showMeSeparately && me}
        <div class="px-3 py-0.5 text-center text-xs text-slate-400">⋯</div>
        {@render row(me, true)}
      {/if}
    </div>
  {/if}
</div>
