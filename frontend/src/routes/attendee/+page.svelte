<script lang="ts">
  // The attendee experience moved to the main page ("/"). Keep this route as a
  // client-side redirect so existing links/bookmarks to /attendee still work.
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';

  onMount(() => { goto('/', { replaceState: true }); });
</script>

<p class="p-8 text-sm text-slate-500">Redirecting…</p>
