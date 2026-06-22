<script lang="ts">
  import { onDestroy } from 'svelte';
  import { sessionHeaders } from '$lib/session';

  // Polls the global reaction feed and floats each new attendee reaction up the
  // screen as an emoji, then fades it out. A fixed, full-screen, click-through
  // overlay so it layers over whatever page embeds it — the presenter console
  // and the standalone cluster/overview displays. The feed endpoint is
  // presenter-password protected, so a password is required to poll it.
  let { password, intervalMs = 1500 }: { password: string; intervalMs?: number } = $props();

  interface ReactionEvent { seq: number; kind: string; }
  interface ReactionFeed { lastSeq: number; events: ReactionEvent[]; }
  interface Floater { id: number; emoji: string; left: number; drift: number; duration: number; }

  const reactionEmoji: Record<string, string> = {
    heart: '❤️',
    thumbs: '👍',
    question: '❓'
  };

  // Cursor of the last event we've already shown (null until the first poll
  // establishes it). Plain closure state — no reactivity needed.
  let since: number | null = null;
  let floaters = $state<Floater[]>([]);
  let floaterId = 0;
  const timers = new Set<ReturnType<typeof setTimeout>>();

  // Spawn one floating emoji per press. Randomized horizontal start, sideways
  // drift and duration so a burst of identical reactions still reads as many
  // distinct emoji rather than one stack.
  function spawnFloater(kind: string) {
    const emoji = reactionEmoji[kind];
    if (!emoji) return;
    const id = ++floaterId;
    const left = 8 + Math.random() * 84; // vw, keep clear of the edges
    const drift = (Math.random() - 0.5) * 80; // px sideways sway
    const duration = 6000 + Math.random() * 3000; // ms float-up time
    floaters = [...floaters, { id, emoji, left, drift, duration }];
    const timer = setTimeout(() => {
      floaters = floaters.filter((f) => f.id !== id);
      timers.delete(timer);
    }, duration);
    timers.add(timer);
  }

  async function load() {
    if (!password) return;
    try {
      const url = since === null ? '/api/presenter/reactions' : `/api/presenter/reactions?since=${since}`;
      const res = await fetch(url, { headers: { ...sessionHeaders(), 'X-Presenter-Password': password } });
      if (!res.ok) return;
      const feed: ReactionFeed = await res.json();
      // On the first poll we only adopt the cursor — no backlog replay.
      if (since !== null) {
        for (const ev of feed.events) spawnFloater(ev.kind);
      }
      since = feed.lastSeq;
    } catch {
      /* reactions are best-effort; keep the last cursor */
    }
  }

  // Poll while a password is available; restart cleanly if it changes.
  $effect(() => {
    if (!password) return;
    void intervalMs; // re-run the effect if the interval changes
    load();
    const poll = setInterval(load, intervalMs);
    return () => clearInterval(poll);
  });

  onDestroy(() => {
    for (const t of timers) clearTimeout(t);
    timers.clear();
  });
</script>

<!-- Fixed, full-screen, click-through overlay so emoji float over the whole
     view regardless of scroll position. -->
<div class="reaction-overlay" aria-hidden="true">
  {#each floaters as f (f.id)}
    <span
      class="reaction-floater"
      style="left: {f.left}vw; --drift: {f.drift}px; animation-duration: {f.duration}ms;"
    >
      {f.emoji}
    </span>
  {/each}
</div>

<style>
  .reaction-overlay {
    position: fixed;
    inset: 0;
    overflow: hidden;
    pointer-events: none;
    z-index: 50;
  }

  .reaction-floater {
    position: absolute;
    bottom: 6rem;
    font-size: 2.25rem;
    line-height: 1;
    will-change: transform, opacity;
    animation-name: reaction-float;
    animation-timing-function: ease-out;
    animation-fill-mode: forwards;
  }

  @keyframes reaction-float {
    0% {
      transform: translate(0, 0) scale(0.6);
      opacity: 0;
    }
    15% {
      transform: translate(calc(var(--drift) * 0.2), -10vh) scale(1.1);
      opacity: 1;
    }
    70% {
      opacity: 1;
    }
    100% {
      transform: translate(var(--drift), -80vh) scale(1);
      opacity: 0;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .reaction-floater {
      animation-name: reaction-fade;
    }
    @keyframes reaction-fade {
      0% { opacity: 0; transform: translateY(0); }
      20% { opacity: 1; }
      100% { opacity: 0; transform: translateY(-20vh); }
    }
  }
</style>
