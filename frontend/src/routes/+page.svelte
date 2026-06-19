<script lang="ts">
  // Landing page: pick a role. Client-side routing handles the two routes.
</script>

<main>
  <h1>Live multiple-choice polling</h1>
  <p>
    An ASP.NET Core minimal API co-hosting an Orleans silo, with this static
    Svelte frontend served from <code>wwwroot</code>. Presenters create questions
    and put one in focus; attendees answer the one in focus.
  </p>

  <div class="roles">
    <a class="card" href="/presenter">
      <h2>I'm a presenter</h2>
      <p>Create questions, choose which is live, watch results.</p>
    </a>
    <a class="card" href="/attendee">
      <h2>I'm an attendee</h2>
      <p>Join with your name and answer the live question.</p>
    </a>
  </div>
</main>

<style>
  main {
    max-width: 36rem;
    margin: 3rem auto;
    padding: 0 1rem;
    font-family: system-ui, sans-serif;
    line-height: 1.5;
  }
  h1 {
    font-size: 1.6rem;
  }
  .roles {
    display: flex;
    gap: 1rem;
    margin-top: 2rem;
  }
  .card {
    flex: 1;
    display: block;
    padding: 1rem 1.2rem;
    border: 1px solid #ddd;
    border-radius: 0.5rem;
    text-decoration: none;
    color: inherit;
  }
  .card:hover {
    border-color: #888;
    background: #fafafa;
  }
  .card h2 {
    font-size: 1.1rem;
    margin: 0 0 0.3rem;
  }
  .card p {
    margin: 0;
    color: #555;
    font-size: 0.95rem;
  }
  code {
    background: #f0f0f0;
    padding: 0.1rem 0.3rem;
    border-radius: 0.2rem;
  }
</style>
