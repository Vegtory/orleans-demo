<script lang="ts">
  import '../app.css';
  import Toaster from '$lib/Toaster.svelte';

  let { children } = $props();
</script>

<div class="min-h-full bg-slate-50 text-slate-900 antialiased">
  {@render children?.()}
</div>

<Toaster />
