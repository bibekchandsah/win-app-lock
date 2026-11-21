import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

// Relative asset URLs so the bundle works from the virtual host
// (https://island.appassets/index.html) with no network access.
export default defineConfig({
  plugins: [svelte()],
  base: './',
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'chrome110',
    assetsInlineLimit: 0,
    sourcemap: false,
    cssCodeSplit: false,
  },
});
