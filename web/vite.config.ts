import { defineConfig } from 'vite'
import { svelte } from '@sveltejs/vite-plugin-svelte'

// During development the UI runs on Vite (port 5173) and proxies API/hub calls to the backend.
// Point NOVA_BACKEND at the mini PC to develop the UI on your desktop against real hardware.
const backend = process.env.NOVA_BACKEND ?? 'http://localhost:5000'

export default defineConfig({
  plugins: [svelte()],
  build: {
    // The backend serves the built UI, so production needs just one process.
    outDir: '../src/Nova.Server/wwwroot',
    emptyOutDir: true,
    // The offline world map for the site picker is ~760 kB on its own; it only loads when the picker opens.
    chunkSizeWarningLimit: 800,
  },
  server: {
    proxy: {
      '/api': backend,
      '/hub': { target: backend, ws: true },
    },
  },
})
