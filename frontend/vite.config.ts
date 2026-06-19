import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [sveltekit()],
  server: {
    // Proxy API calls to the locally running ASP.NET Core app during `vite dev`.
    proxy: {
      '/api': 'http://localhost:5000'
    }
  }
});
