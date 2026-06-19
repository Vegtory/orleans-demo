import adapter from '@sveltejs/adapter-static';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: vitePreprocess(),
  kit: {
    // Static build emitted directly into the ASP.NET Core app's wwwroot.
    // `fallback` produces the SPA fallback document served for client routes.
    adapter: adapter({
      pages: '../src/App.Api/wwwroot',
      assets: '../src/App.Api/wwwroot',
      fallback: 'index.html',
      precompress: false,
      strict: true
    })
  }
};

export default config;
