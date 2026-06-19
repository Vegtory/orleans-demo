<script lang="ts">
  import '../app.css';

  let { children } = $props();
</script>

<div class="min-h-full bg-slate-50 text-slate-900 antialiased">
  {@render children?.()}
</div>
