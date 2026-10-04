/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const server = 'http://localhost:8080';

export default defineConfig({
  plugins: [react()],
  build: {
    // The server host serves the built SPA from its wwwroot.
    outDir: '../MCPal.Server/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': server,
      '/oauth/register': server,
      '/oauth/token': server,
      '/mcp': server,
      '/hub': { target: server, ws: true },
      '/.well-known': server,
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    globals: false,
    // Playwright specs run against a live stack (npm run e2e), not in jsdom.
    exclude: ['e2e/**', 'node_modules/**'],
  },
});
