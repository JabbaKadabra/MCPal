import { defineConfig, devices } from '@playwright/test';

/**
 * Smoke tests against a running MCPal stack (`docker compose up --build`, or `dotnet run` after `npm run build`).
 * MCPAL_URL points at it; the default is the compose stack.
 */
export default defineConfig({
  testDir: './e2e',
  // Signup and key creation share one database and one company slug space; run one test at a time.
  workers: 1,
  fullyParallel: false,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],
  use: {
    baseURL: process.env.MCPAL_URL ?? 'http://localhost:8080',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
