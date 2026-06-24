<script lang="ts">
  import { fly } from 'svelte/transition';
  import { toasts } from '$lib/toast.svelte';

  // Per-kind styling. Errors are the common case but the store supports
  // info/success too, so they get distinct colours.
  const styles: Record<string, string> = {
    error: 'border-red-200 bg-red-50 text-red-700',
    info: 'border-slate-200 bg-white text-slate-700',
    success: 'border-green-200 bg-green-50 text-green-700'
  };
</script>

<div
  class="pointer-events-none fixed inset-x-0 top-0 z-50 flex flex-col items-center gap-2 px-4 py-4 sm:items-end sm:px-6"
>
  {#each toasts.toasts as toast (toast.id)}
    <div
      role="alert"
      transition:fly={{ y: -16, duration: 200 }}
      class="pointer-events-auto flex w-full max-w-sm items-start gap-2 rounded-lg border px-3 py-2 text-sm shadow-md {styles[toast.kind] ?? styles.info}"
    >
      <span class="flex-1 break-words">{toast.message}</span>
      <button
        type="button"
        aria-label="Dismiss"
        onclick={() => toasts.dismiss(toast.id)}
        class="-mr-1 shrink-0 rounded p-0.5 text-current opacity-60 transition hover:opacity-100"
      >
        <svg class="h-4 w-4" viewBox="0 0 20 20" fill="currentColor"><path fill-rule="evenodd" d="M4.3 4.3a1 1 0 0 1 1.4 0L10 8.6l4.3-4.3a1 1 0 1 1 1.4 1.4L11.4 10l4.3 4.3a1 1 0 0 1-1.4 1.4L10 11.4l-4.3 4.3a1 1 0 0 1-1.4-1.4L8.6 10 4.3 5.7a1 1 0 0 1 0-1.4Z" clip-rule="evenodd"/></svg>
      </button>
    </div>
  {/each}
</div>
