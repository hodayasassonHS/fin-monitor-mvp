import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

/**
 * The dev server proxies `/api` and `/hubs` to the backend so the browser only ever talks to one
 * origin. That mirrors the production topology — nginx serving the bundle and reverse-proxying
 * the same two paths — which means CORS and SignalR credentials behave identically in
 * development and in the cluster, rather than being a class of bug that only appears on deploy.
 */
const backendOrigin = process.env.VITE_BACKEND_ORIGIN ?? 'http://localhost:5080';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: backendOrigin, changeOrigin: true },
      // ws: true is what lets the SignalR WebSocket upgrade through the dev proxy.
      '/hubs': { target: backendOrigin, changeOrigin: true, ws: true },
    },
  },
  build: {
    sourcemap: true,
  },
  test: {
    globals: true,
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      exclude: ['src/test/**', 'src/**/*.test.{ts,tsx}', 'src/main.tsx'],
    },
  },
});
