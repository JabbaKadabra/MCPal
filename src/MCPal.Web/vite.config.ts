/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

const cloud = 'http://localhost:8080';

export default defineConfig({
  plugins: [react()],
  build: {
    // The cloud host serves the built SPA from its wwwroot.
    outDir: '../MCPal.Cloud/wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': cloud,
      '/oauth/register': cloud,
      '/oauth/token': cloud,
      '/mcp': cloud,
      '/hub': { target: cloud, ws: true },
      '/.well-known': cloud,
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
